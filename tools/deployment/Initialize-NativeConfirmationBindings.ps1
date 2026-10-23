param([Parameter(Mandatory=$true)][string]$PublishDirectory)
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath($PublishDirectory)
if($PublishDirectory -notmatch '^[A-Za-z]:[\\/]' -or $root.StartsWith('\\')){throw 'LOCAL_PUBLISH_DIRECTORY_REQUIRED'}
for($cursor=$root;$cursor;$cursor=[IO.Path]::GetDirectoryName($cursor)){
    if((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_DENIED'}
}
$bindings=@(
    @{script='Confirm-TaskCommands.ps1';metadata='task-command-native-code.json'},
    @{script='Confirm-DecisionReaffirmation.ps1';metadata='decision-native-code.json'}
)
$prepared=@()
foreach($binding in $bindings){
    $script=Join-Path $root $binding.script
    if(-not (Test-Path -LiteralPath $script -PathType Leaf)){throw 'NATIVE_CONFIRMATION_SCRIPT_MISSING'}
    if((Get-Item -LiteralPath $script -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_DENIED'}
    $hash=(Get-FileHash -LiteralPath $script -Algorithm SHA256).Hash.ToLowerInvariant()
    $target=Join-Path $root $binding.metadata
    if(Test-Path -LiteralPath $target){
        if((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'REPARSE_POINT_DENIED'}
        $existing=Get-Content -LiteralPath $target -Raw | ConvertFrom-Json
        if($existing.script_sha256 -cne $hash){throw 'NATIVE_CONFIRMATION_BINDING_MISMATCH'}
    }
    $prepared+=@{target=$target;hash=$hash;metadata=$binding.metadata}
}
# Bind installed code only. These files never grant a task or fabricate a human receipt.
# Validate both scripts and existing bindings before writing either metadata file.
foreach($item in $prepared){
    if(-not (Test-Path -LiteralPath $item.target)){
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes(([ordered]@{schema=1;script_sha256=$item.hash}|ConvertTo-Json -Compress))
        $stream=[IO.File]::Open($item.target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    }
}
