param([Parameter(Mandatory=$true)][string]$NotificationFile)
$ErrorActionPreference = 'Stop'
# Run in the logged-in user's session; never from a Windows service/session 0.
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$icon = [System.Windows.Forms.NotifyIcon]::new()
$icon.Icon = [System.Drawing.SystemIcons]::Information
$icon.Visible = $true
$icon.Text = 'LocalBrain 承認管理'
$previous = ''
try {
    while ($true) {
        if (Test-Path -LiteralPath $NotificationFile) {
            $current = (Get-FileHash -LiteralPath $NotificationFile -Algorithm SHA256).Hash
            if ($current -ne $previous) {
                $notice = Get-Content -LiteralPath $NotificationFile -Raw | ConvertFrom-Json
                if ($notice.pending_count -gt 0) {
                    $icon.ShowBalloonTip(10000, 'LocalBrain', '承認待ちがあります。登録済みのHTTPS承認ページで確認してください。', [System.Windows.Forms.ToolTipIcon]::Info)
                }
                $previous = $current
            }
        }
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Seconds 30
    }
} finally { $icon.Visible = $false; $icon.Dispose() }
