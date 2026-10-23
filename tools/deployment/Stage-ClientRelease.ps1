#Requires -Version 7.2
param([Parameter(Mandatory)][string]$PublishDirectory,[Parameter(Mandatory)][string]$ReleaseDirectory)
$ErrorActionPreference='Stop'
function Assert-LocalPath([string]$Path) {
    if(-not [IO.Path]::IsPathFullyQualified($Path) -or $Path.StartsWith('\\')){throw 'LOCAL_ABSOLUTE_PATH_REQUIRED'}
    $full=[IO.Path]::GetFullPath($Path)
    for($cursor=$full; $cursor; $cursor=[IO.Path]::GetDirectoryName($cursor)) {
        if(Test-Path -LiteralPath $cursor){if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_DENIED'}}
    }
    return $full
}
$source=Assert-LocalPath $PublishDirectory
$target=Assert-LocalPath $ReleaseDirectory
if(Test-Path -LiteralPath $target){throw 'NEW_RELEASE_DIRECTORY_REQUIRED'}
if($target.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'RELEASE_INSIDE_SOURCE_DENIED'}
$manifestPath=Join-Path $source 'build-manifest.json'
$manifestBytes=[IO.File]::ReadAllBytes($manifestPath)
$manifest=[Text.Encoding]::UTF8.GetString($manifestBytes) | ConvertFrom-Json
if($manifest.schema -cne 'localbrain.reproducible-build.v1' -or $manifest.source_head -cnotmatch '^[a-f0-9]{40}$' -or $manifest.private_configuration_copied -ne $false){throw 'BUILD_MANIFEST_INVALID'}
$pairs=@($manifest.files.PSObject.Properties)
if($pairs.Count -lt 1 -or $pairs.Count -gt 1000){throw 'BUILD_INVENTORY_INVALID'}
$inventory=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach($pair in $pairs){
    if($pair.Name -match '(^[\\/]|:|(^|[\\/])\.{1,2}([\\/]|$)|(^|[\\/])$)' -or $pair.Name -match '(^|[\\/])(clientsettings|private[^\\/]*)' -or $pair.Value -cnotmatch '^[a-f0-9]{64}$' -or -not $inventory.Add($pair.Name.Replace('\','/'))){throw 'BUILD_ENTRY_INVALID'}
    $file=Assert-LocalPath (Join-Path $source $pair.Name)
    if(-not $file.StartsWith($source+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.Value){throw 'BUILD_HASH_MISMATCH'}
}
foreach($entry in Get-ChildItem -LiteralPath $source -Recurse -Force){
    $null=Assert-LocalPath $entry.FullName
    if(-not $entry.PSIsContainer){$relative=[IO.Path]::GetRelativePath($source,$entry.FullName).Replace('\','/');if($relative -cne 'build-manifest.json' -and -not $inventory.Contains($relative)){throw 'UNDECLARED_BUILD_FILE'}}
}
# Every input is validated before creating the target. A failed copy stays staged for inspection;
# this tool neither activates a release, runs a process, changes a DB, nor removes old files.
New-Item -ItemType Directory -Path $target | Out-Null
foreach($pair in $pairs){
    $destination=Join-Path $target $pair.Name
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
    [IO.File]::Copy((Join-Path $source $pair.Name),$destination,$false)
    if((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() -cne $pair.Value){throw 'STAGED_HASH_MISMATCH'}
}
$manifestStream=[IO.File]::Open((Join-Path $target 'build-manifest.json'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$manifestStream.Write($manifestBytes);$manifestStream.Flush($true)}finally{$manifestStream.Dispose()}
[ordered]@{status='STAGED_NOT_ACTIVATED';source_head=$manifest.source_head;release=$target;product_files=$pairs.Count;manifest_sha256=(Get-FileHash -LiteralPath (Join-Path $target 'build-manifest.json')).Hash.ToLowerInvariant()} | ConvertTo-Json
