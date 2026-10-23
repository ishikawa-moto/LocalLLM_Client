# Compatible with the existing Windows PowerShell task wrapper.
$ErrorActionPreference='Stop'
$base=Join-Path $env:LOCALAPPDATA 'LocalBrain'
$installation=Get-Content -LiteralPath (Join-Path $base 'control-plane\installation.json') -Raw|ConvertFrom-Json
if($installation.schema -ne 'localbrain.client-installation.v1'){throw 'INSTALLATION_SCHEMA_UNKNOWN'}
$release=[IO.Path]::GetFullPath($installation.release)
if(-not $release.StartsWith((Join-Path $base 'releases')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'RELEASE_PATH_UNKNOWN'}
$build=Get-Content -LiteralPath (Join-Path $release 'build-manifest.json') -Raw|ConvertFrom-Json
if($build.source_head -ne $installation.source_commit -or (Get-FileHash -LiteralPath (Join-Path $release 'build-manifest.json')).Hash.ToLowerInvariant() -ne $installation.manifest_sha256){throw 'RELEASE_MANIFEST_DRIFT'}
foreach($entry in $build.files.PSObject.Properties){if((Get-FileHash -LiteralPath (Join-Path $release $entry.Name)).Hash.ToLowerInvariant() -ne $entry.Value){throw 'RELEASE_FILE_DRIFT'}}
$config=$installation.configs.host
if((Get-FileHash -LiteralPath $config).Hash.ToLowerInvariant() -ne $installation.installed_hashes.'clientsettings.json'){throw 'CLIENT_CONFIG_DRIFT'}
$env:LOCALBRAIN_CLIENT_CONFIG=$config
$psi=New-Object System.Diagnostics.ProcessStartInfo
$psi.FileName=Join-Path $release 'localbrain.exe';$psi.Arguments='serve';$psi.WorkingDirectory=$release
$psi.UseShellExecute=$false;$psi.CreateNoWindow=$true;$psi.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
$process=[Diagnostics.Process]::Start($psi)
$process.WaitForExit()
exit $process.ExitCode
