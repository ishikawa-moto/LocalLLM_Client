param(
    [Parameter(Mandatory)][string]$BaseVsix,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot '..\out\stable'),
    [switch]$RunTests
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$package = Split-Path -Parent $PSScriptRoot
$base = (Resolve-Path -LiteralPath $BaseVsix).Path
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
if (Test-Path -LiteralPath $root) { throw 'Choose a new output directory; existing files are preserved.' }
$null = New-Item -ItemType Directory -Path $root
$inputHash = (Get-FileHash -LiteralPath $base).Hash
if ($inputHash -ceq '6A3B5D14010B9983A133D0C69AEB966FD1C9D619B2ED2B8A404002036CF5A468') {
    $input = [IO.File]::OpenRead($base)
    $base = Join-Path $root 'marketplace.vsix'
    $normalized = [IO.File]::Open($base, [IO.FileMode]::CreateNew)
    $gzip = [IO.Compression.GZipStream]::new($input, [IO.Compression.CompressionMode]::Decompress)
    try { $gzip.CopyTo($normalized) }
    finally { $gzip.Dispose(); $normalized.Dispose(); $input.Dispose() }
}
if ((Get-FileHash -LiteralPath $base).Hash -cne 'D681ACA70F24E1A3B9ADEA8D9BBE4D24D954AE74F71C7B648ED589E53DC8354B') {
    throw 'Input differs from the pinned Continue 2.0.0 win32-x64 Marketplace VSIX.'
}
$stage = Join-Path $root 'stage'
[IO.Compression.ZipFile]::ExtractToDirectory($base, $stage)
[IO.File]::Copy((Join-Path $stage 'extension.vsixmanifest'), (Join-Path $stage 'extension\.vsixmanifest'))
[IO.File]::Copy((Join-Path $package 'packaging\Content_Types.xml'), (Join-Path $stage '[Content_Types].xml'), $true)
$js = Join-Path $stage 'extension\out\extension.js'
& node (Join-Path $PSScriptRoot 'patch-bundle.cjs') $stage $js
if ($LASTEXITCODE -ne 0) { throw 'Bundle patch failed' }
& node --check $js
if ($LASTEXITCODE -ne 0) { throw 'Generated JavaScript failed syntax check' }
& node --check (Join-Path $stage 'extension\gui\assets\index.js')
if ($LASTEXITCODE -ne 0) { throw 'Generated GUI failed syntax check' }
& node (Join-Path $PSScriptRoot 'normalize-manifest.cjs') (Join-Path $stage 'extension\package.json')
if ($LASTEXITCODE -ne 0) { throw 'Manifest normalization failed' }
if ($RunTests) {
    $previousStage = $env:PC73_STAGE_DIR
    try {
        $env:PC73_STAGE_DIR = $stage
        & node (Join-Path $package 'tests\run_all.mjs')
        if ($LASTEXITCODE -ne 0) { throw 'Focused tests failed' }
    } finally { $env:PC73_STAGE_DIR = $previousStage }
}
$rows = Get-Content -LiteralPath (Join-Path $package 'packaging\stable-layout.tsv')
if ($rows.Count -ne 400) { throw 'Unexpected stable manifest count' }
$layout = foreach ($row in $rows) {
    $parts = $row -split "\t"
    if ($parts.Count -ne 3 -or $parts[0] -notmatch '^(?:extension/[^\\]+|extension\.vsixmanifest|\[Content_Types\]\.xml)$' -or
        ($parts[0].Split('/') -contains '..')) { throw 'Invalid stable manifest row' }
    $path = Join-Path $stage $parts[0]
    if ((Get-FileHash -LiteralPath $path).Hash -cne $parts[2]) { throw "Payload hash mismatch at $($parts[0])" }
    [pscustomobject]@{
        Entry = $parts[0]; Path = $path
        Time = [DateTimeOffset]::ParseExact($parts[1], 'yyyy-MM-ddTHH:mm:sszzz', [Globalization.CultureInfo]::InvariantCulture)
    }
}
$target = Join-Path $root 'continue-2.0.0-localbrain-pc73-automatic.vsix'
$archive = [IO.Compression.ZipFile]::Open($target, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($item in $layout) {
        $entry = $archive.CreateEntry($item.Entry, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = $item.Time
        $src = [IO.File]::OpenRead($item.Path)
        try {
            $dest = $entry.Open()
            try { $src.CopyTo($dest) } finally { $dest.Dispose() }
        } finally { $src.Dispose() }
    }
} finally { $archive.Dispose() }
$hash = (Get-FileHash -LiteralPath $target).Hash
[pscustomobject]@{ Path = $target; Bytes = (Get-Item -LiteralPath $target).Length; SHA256 = $hash } | Format-List
if ($hash -cne '18780DEF9A775B14EE4981DACD1448B9DF9C7F62975D3304DB4790593963B54F') {
    throw 'Payload matched; ZIP encoding differs from the reference build. Use the documented PowerShell/.NET version.'
}
