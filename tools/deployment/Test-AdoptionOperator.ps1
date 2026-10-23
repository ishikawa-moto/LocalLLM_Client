#Requires -Version 7.2
param([Parameter(Mandatory)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
if(Test-Path -LiteralPath $FixtureRoot){throw 'NEW_FIXTURE_ROOT_REQUIRED'}
$base=Join-Path $FixtureRoot 'LocalBrain';$release=Join-Path $base 'releases\public';$control=Join-Path $base 'control-plane';$null=New-Item -ItemType Directory $release -Force;$null=New-Item -ItemType Directory $control
function Sha([string]$Path){(Get-FileHash -LiteralPath $Path).Hash.ToLowerInvariant()}
foreach($name in @('localbrain.exe','localbrain.dll','secondbrain.json')){[IO.File]::WriteAllText((Join-Path $release $name),'PUBLIC_FIXTURE_NOT_EXECUTABLE')}
$manifest=@{source_head=('a'*40);files=@{'localbrain.exe'=(Sha (Join-Path $release 'localbrain.exe'))}}
$manifest|ConvertTo-Json -Depth 6|Set-Content (Join-Path $release 'build-manifest.json') -Encoding utf8NoBOM
$installation=@{schema='localbrain.client-installation.v1';release=$release;source_commit=('a'*40);manifest_sha256=(Sha (Join-Path $release 'build-manifest.json'));product_dll_sha256=(Sha (Join-Path $release 'localbrain.dll'));configs=@{secondbrain=(Join-Path $release 'secondbrain.json')};configuration_hashes=@{secondbrain=(Sha (Join-Path $release 'secondbrain.json'))}}
$installationPath=Join-Path $control 'installation.json'
function Save-Installation {$installation|ConvertTo-Json -Depth 10|Set-Content $installationPath -Encoding utf8NoBOM}
$operator=Join-Path $PSScriptRoot '..\adoption\Approve-SecondBrainDecision.ps1';$checks=0
function Reject([string]$Code){$caught=$false;try{$null=& $operator -Inspect}catch{if($_.Exception.Message -cne $Code){throw};$caught=$true};if(-not $caught){throw 'EXPECTED_REJECTION_MISSING'};$script:checks++}
$prior=$env:LOCALAPPDATA
try{
    $env:LOCALAPPDATA=$FixtureRoot;Save-Installation
    $r=& $operator -Inspect|ConvertFrom-Json;if($r.status -cne 'VERIFIED_OPERATOR_BINDING_ONLY' -or $r.approval_created -cne $false){throw 'INSPECTION_FAILED'};$checks++
    [IO.File]::WriteAllText((Join-Path $release 'secondbrain.json'),'tampered');Reject 'SECOND_BRAIN_CONFIGURATION_DIFFERS'
    [IO.File]::WriteAllText((Join-Path $release 'secondbrain.json'),'PUBLIC_FIXTURE_NOT_EXECUTABLE')
    [IO.File]::WriteAllText((Join-Path $release 'localbrain.dll'),'tampered');Reject 'INSTALLED_HOST_DIFFERS'
    [IO.File]::WriteAllText((Join-Path $release 'localbrain.dll'),'PUBLIC_FIXTURE_NOT_EXECUTABLE')
    $installation.configuration_hashes=@{};Save-Installation;Reject 'SECOND_BRAIN_CONFIGURATION_DIFFERS'
    $installation.configuration_hashes=@{secondbrain=(Sha (Join-Path $release 'secondbrain.json'))};$installation.manifest_sha256=('b'*64);Save-Installation;Reject 'INSTALLED_MANIFEST_DIFFERS'
    $installation.release=$control;Save-Installation;Reject 'INSTALLED_RELEASE_PATH_DIFFERS'
}finally{$env:LOCALAPPDATA=$prior}
@{status='PASS';checks=$checks;public_only=$true;host_launches=0;approval_changes=0;network_calls=0}|ConvertTo-Json
