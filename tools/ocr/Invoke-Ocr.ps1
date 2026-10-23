param(
    [Parameter(Mandatory)][ValidateSet('capabilities','submit','status','result','cancel','resume')][string]$Operation,
    [string]$InputFile,[string]$RequestId,[string]$JobId,
    [ValidateSet('AUTO','PRINTED_TEXT','HANDWRITING','TABLE_LAYOUT','PDF_DOCUMENT')][string]$Profile='PRINTED_TEXT',
    [switch]$Experimental,[switch]$RetainOriginal,[string]$OutputFile,
    [string]$HostExe
)
$ErrorActionPreference='Stop'
if(-not $HostExe){
    $base=Join-Path $env:LOCALAPPDATA 'LocalBrain'
    $installation=Get-Content -LiteralPath (Join-Path $base 'control-plane\installation.json') -Raw|ConvertFrom-Json
    if($installation.schema -ne 'localbrain.client-installation.v1'){throw 'Installed release receipt is required.'}
    $release=[IO.Path]::GetFullPath($installation.release)
    if(-not $release.StartsWith((Join-Path $base 'releases')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Installed release path differs.'}
    $manifest=Join-Path $release 'build-manifest.json'
    if((Get-FileHash -LiteralPath $manifest).Hash.ToLowerInvariant() -ne $installation.manifest_sha256){throw 'Installed release manifest differs.'}
    $build=Get-Content -LiteralPath $manifest -Raw|ConvertFrom-Json
    $HostExe=Join-Path $release 'localbrain.exe'
    if($build.source_head -ne $installation.source_commit -or (Get-FileHash -LiteralPath $HostExe).Hash.ToLowerInvariant() -ne $build.files.'localbrain.exe' -or (Get-FileHash -LiteralPath (Join-Path $release 'localbrain.dll')).Hash.ToLowerInvariant() -ne $installation.product_dll_sha256){throw 'Installed OCR Host differs.'}
    $config=$installation.configs.host
    if((Get-FileHash -LiteralPath $config).Hash.ToLowerInvariant() -ne $installation.installed_hashes.'clientsettings.json'){throw 'Installed client configuration differs.'}
    $env:LOCALBRAIN_CLIENT_CONFIG=$config
}
if(-not (Test-Path -LiteralPath $HostExe -PathType Leaf)){throw 'Qualified OCR Host is not installed. Do not use a generic shell fallback.'}
$arguments=@('ocr',$Operation)
if($Operation -eq 'submit'){
    if(-not $InputFile -or -not $RequestId){throw 'InputFile and RequestId are required. Retries must reuse the same RequestId.'}
    $resolved=(Resolve-Path -LiteralPath $InputFile).Path
    $digest=(Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash.ToLowerInvariant()
    $arguments+=@('--input',$resolved,'--sha256',$digest,'--profile',$Profile,'--request-id',$RequestId)
    if($Experimental){$arguments+='--experimental'}
    if($RetainOriginal){$arguments+='--retain-original'}
}elseif($Operation -ne 'capabilities'){
    if(-not $JobId){throw 'JobId is required.'}
    $arguments+=@('--job-id',$JobId)
}
$json=& $HostExe @arguments
if($LASTEXITCODE -ne 0){$json;exit $LASTEXITCODE}
if($OutputFile){
    if($Operation -ne 'result'){throw 'OutputFile is for explicit result export only.'}
    $path=$ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputFile)
    if(Test-Path -LiteralPath $path){throw 'Output already exists; inspect it instead of overwriting.'}
    [IO.File]::WriteAllText($path,($json -join [Environment]::NewLine),[Text.UTF8Encoding]::new($false))
}else{$json}
