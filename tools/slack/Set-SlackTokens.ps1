param([Parameter(Mandatory=$true)][string]$SecretDirectory)
$ErrorActionPreference = 'Stop'
$target = [IO.Path]::GetFullPath($SecretDirectory)
if (-not [IO.Path]::IsPathFullyQualified($SecretDirectory) -or $target.StartsWith('\\')) { throw 'Use a private local absolute directory.' }
$ancestor = $target
while ($ancestor) {
    if ((Test-Path -LiteralPath $ancestor) -and ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked secret path denied.' }
    if (Test-Path -LiteralPath (Join-Path $ancestor '.git')) { throw 'Secrets cannot be saved inside a Git repository.' }
    $ancestor = [IO.Path]::GetDirectoryName($ancestor)
}
if (Test-Path -LiteralPath $target) {
    if ((Get-Item -LiteralPath $target -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked secret directory denied.' }
} else { New-Item -ItemType Directory -Path $target | Out-Null }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().User
$acl = [Security.AccessControl.DirectorySecurity]::new()
$acl.SetOwner($identity)
$acl.SetAccessRuleProtection($true, $false)
$inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit'
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity,'FullControl',$inherit,'None','Allow'))
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-18'),'FullControl',$inherit,'None','Allow'))
Set-Acl -LiteralPath $target -AclObject $acl
foreach ($entry in @(@('app.token','xapp-','App-level token (connections:write)'),@('bot.token','xoxb-','Bot token'))) {
    $path = Join-Path $target $entry[0]
    if (Test-Path -LiteralPath $path) { throw 'An existing token file is preserved. Inspect it before replacement.' }
    $secure = Read-Host -Prompt $entry[2] -AsSecureString
    $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr)
        if (-not $token.StartsWith($entry[1]) -or $token.Length -lt 20 -or $token -match '\s') { throw 'Token format rejected.' }
        $file = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $bytes = [Text.Encoding]::UTF8.GetBytes($token); $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
    } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr); $token = $null; $secure.Dispose() }
}
Write-Output 'Tokens saved locally. Do not paste them into chat or Git.'
