#Requires -Version 7.2
param([Parameter(Mandatory)][string]$OutputDirectory,[string]$SdkVersion='10.0.303')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$output=[IO.Path]::GetFullPath($OutputDirectory)
if((Test-Path -LiteralPath $output) -or $output.StartsWith($repo+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'NEW_EXTERNAL_OUTPUT_DIRECTORY_REQUIRED'}
$dirty=@(& git -C $repo status --porcelain=v1);if($LASTEXITCODE -or $dirty.Count){throw 'CLEAN_EXACT_SOURCE_REQUIRED'}
$head=(& git -C $repo rev-parse HEAD);if($LASTEXITCODE){throw 'GIT_HEAD_REQUIRED'}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_NOLOGO='1'
$actual=(& dotnet --version);if($LASTEXITCODE -or $actual.Trim() -cne $SdkVersion){throw 'EXACT_SDK_REQUIRED_NO_AUTO_INSTALL'}
# Restore is a separate explicit setup step. Model/production configuration is never copied.
& dotnet publish (Join-Path $repo 'dotnet/LocalBrain.ClientHost') -c Release --no-restore -o $output -p:ContinuousIntegrationBuild=true -p:Deterministic=true "-p:PathMap=$repo=/_/localbrain"
if($LASTEXITCODE){throw 'PUBLISH_FAILED_PRESERVE_OUTPUT_FOR_INSPECTION'}
if((& git -C $repo rev-parse HEAD) -cne $head -or @(& git -C $repo status --porcelain=v1).Count){throw 'SOURCE_DRIFT_PRESERVE_OUTPUT'}
& (Join-Path $PSScriptRoot 'deployment/Initialize-NativeConfirmationBindings.ps1') -PublishDirectory $output
$files=[ordered]@{}
foreach($file in Get-ChildItem -LiteralPath $output -File -Recurse){$relative=[IO.Path]::GetRelativePath($output,$file.FullName).Replace('\','/');$files[$relative]=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
[ordered]@{schema='localbrain.reproducible-build.v1';source_head=$head;sdk=$actual.Trim();configuration='Release';path_map='/_/localbrain';private_configuration_copied=$false;files=$files}|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path $output 'build-manifest.json') -Encoding utf8NoBOM
