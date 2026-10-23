#Requires -Version 7.2
param([Parameter(Mandatory)][string]$FixtureRoot)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($FixtureRoot)
if(Test-Path -LiteralPath $root){throw 'NEW_FIXTURE_ROOT_REQUIRED'}
New-Item -ItemType Directory -Path $root | Out-Null
$script=Join-Path $PSScriptRoot 'Stage-ClientRelease.ps1'
$checks=0
function New-Fixture([string]$Name){
    $path=Join-Path $root $Name;New-Item -ItemType Directory -Path $path | Out-Null
    [IO.File]::WriteAllText((Join-Path $path 'public.bin'),'public fixture')
    $hash=(Get-FileHash -LiteralPath (Join-Path $path 'public.bin')).Hash.ToLowerInvariant()
    $manifest=[ordered]@{schema='localbrain.reproducible-build.v1';source_head=('a'*40);private_configuration_copied=$false;files=@{'public.bin'=$hash}}
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $path 'build-manifest.json') -Encoding utf8NoBOM
    return $path
}
function Expect-Rejected([string]$Source,[string]$Code){
    $destination=$Source+'-release';$caught=$false
    try{$null=& $script -PublishDirectory $Source -ReleaseDirectory $destination}catch{if($_.Exception.Message -cne $Code){throw};$caught=$true}
    if(-not $caught -or (Test-Path -LiteralPath $destination)){throw 'INVALID_BUILD_WAS_STAGED'}
    $script:checks++
}
$good=New-Fixture 'good';$release=$good+'-release';$receipt=& $script -PublishDirectory $good -ReleaseDirectory $release | ConvertFrom-Json
if($receipt.status -cne 'STAGED_NOT_ACTIVATED' -or (Get-FileHash -LiteralPath (Join-Path $good 'public.bin')).Hash -cne (Get-FileHash -LiteralPath (Join-Path $release 'public.bin')).Hash){throw 'VALID_COPY_FAILED'};$checks++
$caught=$false;try{$null=& $script -PublishDirectory $good -ReleaseDirectory $release}catch{if($_.Exception.Message -cne 'NEW_RELEASE_DIRECTORY_REQUIRED'){throw};$caught=$true};if(-not $caught){throw 'EXISTING_RELEASE_OVERWRITTEN'};$checks++
$bad=New-Fixture 'tampered';[IO.File]::WriteAllText((Join-Path $bad 'public.bin'),'changed');Expect-Rejected $bad 'BUILD_HASH_MISMATCH'
$bad=New-Fixture 'undeclared';[IO.File]::WriteAllText((Join-Path $bad 'extra.txt'),'extra');Expect-Rejected $bad 'UNDECLARED_BUILD_FILE'
$bad=New-Fixture 'private';$manifest=Get-Content -LiteralPath (Join-Path $bad 'build-manifest.json') -Raw | ConvertFrom-Json;$manifest.files=[pscustomobject]@{'clientsettings.json'=('b'*64)};$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bad 'build-manifest.json');Expect-Rejected $bad 'BUILD_ENTRY_INVALID'
$bad=New-Fixture 'traversal';$manifest=Get-Content -LiteralPath (Join-Path $bad 'build-manifest.json') -Raw | ConvertFrom-Json;$manifest.files=[pscustomobject]@{'../outside.bin'=('b'*64)};$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bad 'build-manifest.json');Expect-Rejected $bad 'BUILD_ENTRY_INVALID'
$bad=New-Fixture 'private-flag';$manifest=Get-Content -LiteralPath (Join-Path $bad 'build-manifest.json') -Raw | ConvertFrom-Json;$manifest.private_configuration_copied=$true;$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $bad 'build-manifest.json');Expect-Rejected $bad 'BUILD_MANIFEST_INVALID'
$bad=New-Fixture 'junction';$null=New-Item -ItemType Junction -Path (Join-Path $bad 'redirect') -Target $good;Expect-Rejected $bad 'REPARSE_POINT_DENIED'
[ordered]@{status='PASS';checks=$checks;public_only=$true;activation=$false;database_changes=$false} | ConvertTo-Json
