param(
    [Parameter(Mandatory)][ValidateSet('capabilities','submit','status','result','cancel','resume')][string]$Operation,
    [string]$InputFile,[string]$RequestId,[string]$JobId,
    [ValidateSet('AUTO','PRINTED_TEXT','HANDWRITING','TABLE_LAYOUT','PDF_DOCUMENT')][string]$Profile='PRINTED_TEXT',
    [switch]$Experimental,[switch]$RetainOriginal,[string]$OutputFile,
    [string]$HostExe=(Join-Path $env:LOCALAPPDATA 'LocalBrain\agent-v14-ocr-20261018\localbrain.exe')
)
$ErrorActionPreference='Stop'
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
