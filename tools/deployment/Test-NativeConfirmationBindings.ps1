#Requires -Version 7.2
param([Parameter(Mandatory)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($FixtureRoot)
if(Test-Path -LiteralPath $root){throw 'NEW_FIXTURE_ROOT_REQUIRED'}
$null=New-Item -ItemType Directory -Path $root
$initializer=Join-Path $PSScriptRoot 'Initialize-NativeConfirmationBindings.ps1'
$stager=Join-Path $PSScriptRoot 'Stage-ClientRelease.ps1'
$checks=0
function New-Fixture([string]$Name){
    $dir=Join-Path $root $Name;$null=New-Item -ItemType Directory $dir
    [IO.File]::WriteAllText((Join-Path $dir 'Confirm-TaskCommands.ps1'),"throw 'PUBLIC_FIXTURE_NEVER_EXECUTE'")
    [IO.File]::WriteAllText((Join-Path $dir 'Confirm-DecisionReaffirmation.ps1'),"throw 'PUBLIC_FIXTURE_NEVER_EXECUTE'")
    return $dir
}
function Write-Manifest([string]$Dir){
    $files=[ordered]@{}
    foreach($file in Get-ChildItem $Dir -File | Where-Object Name -ne 'build-manifest.json'){$files[$file.Name]=(Get-FileHash $file.FullName).Hash.ToLowerInvariant()}
    @{schema='localbrain.reproducible-build.v1';source_head=('a'*40);private_configuration_copied=$false;files=$files}|ConvertTo-Json -Depth 5|Set-Content (Join-Path $Dir 'build-manifest.json') -Encoding utf8NoBOM
}
function Expect-Failure([scriptblock]$Action,[string]$Code){
    $caught=$false;try{& $Action}catch{if($_.Exception.Message -cne $Code){throw};$caught=$true};if(-not $caught){throw 'EXPECTED_REJECTION_MISSING'};$script:checks++
}
$good=New-Fixture 'good';& $initializer -PublishDirectory $good;Write-Manifest $good
$r=& $stager -PublishDirectory $good -ReleaseDirectory ($good+'-release') | ConvertFrom-Json
if($r.product_files -ne 4){throw 'BINDINGS_NOT_STAGED'};$checks++
$before=@(Get-ChildItem $good -File|Get-FileHash|Select-Object Path,Hash)|ConvertTo-Json -Compress
& $initializer -PublishDirectory $good
$after=@(Get-ChildItem $good -File|Get-FileHash|Select-Object Path,Hash)|ConvertTo-Json -Compress
if($before -cne $after){throw 'VALID_BINDINGS_REWRITTEN'};$checks++
$bad=New-Fixture 'missing';Remove-Item (Join-Path $bad 'Confirm-DecisionReaffirmation.ps1')
Expect-Failure {& $initializer -PublishDirectory $bad} 'NATIVE_CONFIRMATION_SCRIPT_MISSING'
if(Test-Path (Join-Path $bad 'task-command-native-code.json')){throw 'PARTIAL_METADATA_WRITTEN'};$checks++
$bad=New-Fixture 'changed';& $initializer -PublishDirectory $bad
[IO.File]::WriteAllText((Join-Path $bad 'Confirm-DecisionReaffirmation.ps1'),'changed public fixture')
Expect-Failure {& $initializer -PublishDirectory $bad} 'NATIVE_CONFIRMATION_BINDING_MISMATCH'
Write-Manifest $bad
Expect-Failure {& $stager -PublishDirectory $bad -ReleaseDirectory ($bad+'-release')} 'NATIVE_CONFIRMATION_BINDING_MISMATCH'
if(Test-Path ($bad+'-release')){throw 'INVALID_RELEASE_CREATED'};$checks++
$bad=New-Fixture 'omitted';Write-Manifest $bad
Expect-Failure {& $stager -PublishDirectory $bad -ReleaseDirectory ($bad+'-release')} 'NATIVE_CONFIRMATION_BINDING_REQUIRED'
$bad=New-Fixture 'junction';$null=New-Item -ItemType Junction -Path (Join-Path $root 'redirect') -Target $bad
Expect-Failure {& $initializer -PublishDirectory (Join-Path $root 'redirect')} 'REPARSE_POINT_DENIED'
@{status='PASS';checks=$checks;public_only=$true;dialogs_launched=0;grants_created=0;production_changes=0}|ConvertTo-Json
