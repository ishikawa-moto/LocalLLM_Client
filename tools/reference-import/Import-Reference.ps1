param(
 [Parameter(Mandatory=$true)][string]$PdfPath,
 [Parameter(Mandatory=$true)][string]$Workspace,
 [Parameter(Mandatory=$true)][string]$SourceUri,
 [string]$RetrievedAt=([DateTimeOffset]::Now.ToString('o'))
)
$ErrorActionPreference='Stop'
$settings=Join-Path $PSScriptRoot 'tool-settings.json'
$config=Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
# Running the operator explicitly does not create another console. Python's Host child is hidden.
& $config.python '-B' (Join-Path $PSScriptRoot 'import_reference.py') '--pdf' $PdfPath '--workspace' $Workspace '--source-uri' $SourceUri '--retrieved-at' $RetrievedAt '--settings' $settings
if($LASTEXITCODE-ne 0){throw 'PDF reference import did not complete; prepared bytes are retained for inspection'}