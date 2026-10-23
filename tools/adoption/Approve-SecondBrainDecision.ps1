param([string]$ReceiptPath,[string]$Workspace,[string]$RequestId,[switch]$Inspect)
$ErrorActionPreference='Stop'
# This operator preserves the Host's interactive approval. It never supplies an answer.
$base=Join-Path $env:LOCALAPPDATA 'LocalBrain'
$installation=Get-Content -LiteralPath (Join-Path $base 'control-plane\installation.json') -Raw|ConvertFrom-Json
if($installation.schema -cne 'localbrain.client-installation.v1'){throw 'INSTALLED_RELEASE_RECEIPT_REQUIRED'}
$release=[IO.Path]::GetFullPath($installation.release)
if(-not $release.StartsWith((Join-Path $base 'releases')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'INSTALLED_RELEASE_PATH_DIFFERS'}
foreach($path in @($release,$installation.configs.secondbrain)){
    for($cursor=$path;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
        if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_DENIED'}
    }
}
$manifestPath=Join-Path $release 'build-manifest.json'
if((Get-FileHash -LiteralPath $manifestPath).Hash.ToLowerInvariant() -cne $installation.manifest_sha256){throw 'INSTALLED_MANIFEST_DIFFERS'}
$manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
if($manifest.source_head -cne $installation.source_commit){throw 'INSTALLED_SOURCE_DIFFERS'}
$operator=Join-Path $release 'localbrain.exe'
if((Get-FileHash -LiteralPath $operator).Hash.ToLowerInvariant() -cne $manifest.files.'localbrain.exe' -or (Get-FileHash -LiteralPath (Join-Path $release 'localbrain.dll')).Hash.ToLowerInvariant() -cne $installation.product_dll_sha256){throw 'INSTALLED_HOST_DIFFERS'}
$config=$installation.configs.secondbrain
if(-not $installation.configuration_hashes.secondbrain -or (Get-FileHash -LiteralPath $config).Hash.ToLowerInvariant() -cne $installation.configuration_hashes.secondbrain){throw 'SECOND_BRAIN_CONFIGURATION_DIFFERS'}
if($Inspect){@{status='VERIFIED_OPERATOR_BINDING_ONLY';release=$release;source_commit=$manifest.source_head;private_values_exported=$false;approval_created=$false;network_calls=0}|ConvertTo-Json;return}
if(-not $ReceiptPath -or -not $Workspace -or -not $RequestId){throw 'RECEIPT_WORKSPACE_AND_EXACT_REQUEST_ID_REQUIRED'}
# Run in an interactive Windows terminal. Never pipe a model-produced approval here.
$previous=$env:LOCALBRAIN_CLIENT_CONFIG
try{
    $env:LOCALBRAIN_CLIENT_CONFIG=$config
    & $operator agent-v2 adoption-review $Workspace $ReceiptPath $RequestId
    if($LASTEXITCODE -ne 0){throw 'APPROVAL_NOT_COMPLETE_PRESERVE_RECEIPT_AND_REQUEST_ID'}
}finally{$env:LOCALBRAIN_CLIENT_CONFIG=$previous}
