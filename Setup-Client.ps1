param(
  [Parameter(Mandatory=$true)][string]$CertificatePfx,
  [Security.SecureString]$CertificatePassword,
  [string]$InitialWorkspace,
  [string]$GatewayBaseUrl,
  [int]$BridgePort,
  [switch]$SkipExtensionInstall,
  [switch]$RemovePfxAfterImport
)
$ErrorActionPreference='Stop'

function Get-ClientConnectionSettings {
  param([string]$GatewayBaseUrl,[int]$BridgePort)
  $gatewayUri=$null
  if(-not [Uri]::TryCreate($GatewayBaseUrl,[UriKind]::Absolute,[ref]$gatewayUri) -or $gatewayUri.Scheme -ne 'https'){ throw 'GatewayBaseUrl must be an HTTPS URL with the actual server host and port.' }
  if($BridgePort -lt 1 -or $BridgePort -gt [UInt16]::MaxValue){ throw 'BridgePort must be a valid local TCP port.' }
  [pscustomobject]@{GatewayBaseUrl=$gatewayUri.AbsoluteUri.TrimEnd('/');BridgePort=$BridgePort;BridgeHost=[Net.IPAddress]::Loopback.ToString()}
}
function New-ClientSettingsJson {
  param([string]$Template,[string]$GatewayBaseUrl,[int]$BridgePort,[string]$Thumbprint)
  $connection=Get-ClientConnectionSettings -GatewayBaseUrl $GatewayBaseUrl -BridgePort $BridgePort
  $settings=$Template|ConvertFrom-Json
  $settings.gatewayBaseUrl=$connection.GatewayBaseUrl
  $settings.bridgePort=$connection.BridgePort
  $settings.clientCertificateThumbprint=$Thumbprint
  $settings|ConvertTo-Json -Depth 10
}
function Expand-ClientContinueTemplate {
  param([string]$Template,[string]$Executable,[int]$BridgePort)
  if($BridgePort -lt 1 -or $BridgePort -gt [UInt16]::MaxValue){ throw 'BridgePort must be a valid local TCP port.' }
  $Template.Replace('__LOCALBRAIN_EXE__',$Executable).Replace('__BRIDGE_HOST__',[Net.IPAddress]::Loopback.ToString()).Replace('__BRIDGE_PORT__',$BridgePort.ToString([Globalization.CultureInfo]::InvariantCulture))
}

$packageRoot=$PSScriptRoot
$installRoot=Join-Path $env:LOCALAPPDATA 'LocalBrain'
$appRoot=Join-Path $installRoot 'app'
$dotnet=(Get-Command dotnet -ErrorAction Stop)
if(-not $InitialWorkspace){ $InitialWorkspace=Read-Host 'Initial Git workspace path (leave empty to register later)' }
if(([version](& $dotnet.Source --version)).Major -lt 10){ throw '.NET 10 or newer is required.' }
if(-not $GatewayBaseUrl){ $GatewayBaseUrl=Read-Host 'Gateway HTTPS URL (server host and port)' }
if(-not $BridgePort){ $BridgePort=[int](Read-Host 'Local Bridge TCP port') }
$connection=Get-ClientConnectionSettings -GatewayBaseUrl $GatewayBaseUrl -BridgePort $BridgePort
if(-not $SkipExtensionInstall){
  $code=(Get-Command code -ErrorAction Stop)
  & $code.Source --install-extension Continue.continue
  if($LASTEXITCODE -ne 0){ throw 'Continue installation failed.' }
  & $code.Source --install-extension openai.chatgpt
  if($LASTEXITCODE -ne 0){ throw 'Codex extension installation failed.' }
}
New-Item -ItemType Directory -Path $appRoot -Force | Out-Null
& $dotnet.Source publish (Join-Path $packageRoot 'dotnet\LocalBrain.ClientHost\LocalBrain.ClientHost.csproj') -c Release -r win-x64 --self-contained false -o $appRoot
if($LASTEXITCODE -ne 0){ throw 'LocalBrain client publish failed.' }
if($null -eq $CertificatePassword){ $CertificatePassword=Read-Host 'Client certificate password' -AsSecureString }
$certificate=Import-PfxCertificate -FilePath ([IO.Path]::GetFullPath($CertificatePfx)) -CertStoreLocation 'Cert:\CurrentUser\My' -Password $CertificatePassword -Exportable:$false
$ca=Join-Path $packageRoot 'certs\ca.pem'
if(-not (Test-Path -LiteralPath $ca)){ throw 'certs\ca.pem is missing from the transfer package.' }
Import-Certificate -FilePath $ca -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
$settings=New-ClientSettingsJson -Template (Get-Content -LiteralPath (Join-Path $packageRoot 'clientsettings.template.json') -Raw) -GatewayBaseUrl $connection.GatewayBaseUrl -BridgePort $connection.BridgePort -Thumbprint $certificate.Thumbprint
[IO.File]::WriteAllText((Join-Path $appRoot 'clientsettings.json'),$settings,[Text.UTF8Encoding]::new($false))
$continueDir=Join-Path $env:USERPROFILE '.continue'; New-Item -ItemType Directory -Path $continueDir -Force | Out-Null
$target=Join-Path $continueDir 'config.yaml'; if(Test-Path $target){ Copy-Item $target ($target+'.backup-'+(Get-Date -Format yyyyMMdd-HHmmss)) }
$exe=(Join-Path $appRoot 'localbrain.exe') -replace '\\','/'
$yaml=Expand-ClientContinueTemplate -Template (Get-Content -LiteralPath (Join-Path $packageRoot 'continue-template.yaml') -Raw) -Executable $exe -BridgePort $connection.BridgePort
[IO.File]::WriteAllText($target,$yaml,[Text.UTF8Encoding]::new($false))
try {
  $action=New-ScheduledTaskAction -Execute (Join-Path $appRoot 'localbrain.exe') -Argument 'serve' -WorkingDirectory $appRoot
  $trigger=New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
  $taskSettings=New-ScheduledTaskSettingsSet -ExecutionTimeLimit ([TimeSpan]::Zero) -RestartCount 3 -RestartInterval (New-TimeSpan -Minutes 1)
  Register-ScheduledTask -TaskName 'LocalBrain Client Host' -Action $action -Trigger $trigger -Settings $taskSettings -Description 'VS Code bridge, heartbeat, SecondBrain MCP, and Codex review runner' -Force | Out-Null
  Start-ScheduledTask -TaskName 'LocalBrain Client Host'
} catch {
  $startup=Join-Path ([Environment]::GetFolderPath('Startup')) 'LocalBrain-Client.cmd'
  ('@echo off'+[Environment]::NewLine+'start "" /min "'+(Join-Path $appRoot 'localbrain.exe')+'" serve') | Set-Content -LiteralPath $startup -Encoding ascii
  Start-Process -FilePath (Join-Path $appRoot 'localbrain.exe') -ArgumentList 'serve' -WorkingDirectory $appRoot -WindowStyle Hidden
  Write-Warning 'Task Scheduler was unavailable; installed an equivalent per-user Startup entry.'
}
if($InitialWorkspace){
  & (Join-Path $appRoot 'localbrain.exe') register-project $InitialWorkspace
  if($LASTEXITCODE -ne 0){ throw 'Initial Git workspace registration failed.' }
}
if($RemovePfxAfterImport){ Remove-Item -LiteralPath ([IO.Path]::GetFullPath($CertificatePfx)) -Force }
Write-Host 'LocalBrain client is installed. Restart VS Code, open Continue, and select Agent mode.'
