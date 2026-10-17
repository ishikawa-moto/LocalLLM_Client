param([Parameter(Mandatory=$true)][string]$InputPath,[int]$OwnerPid=0,[long]$OwnerBirth=0,[switch]$LayoutOnly)
$ErrorActionPreference='Stop'
if(-not [Environment]::UserInteractive){throw 'Windows desktop is required'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$bytes=[IO.File]::ReadAllBytes($InputPath)
$hash=[BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash($bytes)).Replace('-','').ToLowerInvariant()
$data=[Text.Encoding]::UTF8.GetString($bytes)|ConvertFrom-Json
if($data.source -ne 'windows_task_command_grant_request' -or $data.nonce -notmatch '^[a-f0-9]{32}$'){throw 'Invalid Host task grant request'}
$form=New-Object Windows.Forms.Form
$form.Text='LocalBrain：この依頼のコマンド実行を一括許可';$form.Size=New-Object Drawing.Size(920,780);$form.StartPosition='CenterScreen'
$form.MaximizeBox=$false
$label=New-Object Windows.Forms.Label;$label.Location=New-Object Drawing.Point(16,12);$label.Size=New-Object Drawing.Size(870,95)
$label.Text="この依頼と同じタスクの再開について、下記範囲での編集・コマンド実行を一括許可します。`r`n許可後はコマンドごとの確認を挟まず、テスト・レビューまで継続します。UACや再認証は別です。`r`n信頼する個人用プロジェクトが対象です。任意プログラムの全副作用を隔離する仕組みではありません。"
$text=New-Object Windows.Forms.TextBox;$text.Location=New-Object Drawing.Point(16,112);$text.Size=New-Object Drawing.Size(870,540)
$text.Multiline=$true;$text.ReadOnly=$true;$text.ScrollBars='Both';$text.WordWrap=$false
$text.Text="対象: $($data.root)`r`nTask: $($data.task_id)`r`nGit HEAD: $($data.git_head)`r`nRequest hash: $($data.request_hash)`r`n`r`n依頼:`r`n$($data.requirement)`r`n`r`n完了条件:`r`n$($data.acceptance_criteria -join "`r`n")`r`n`r`n編集対象:`r`n$($data.allowed_files -join "`r`n")`r`n`r`nコマンド範囲・予算:`r`n$($data.contract|ConvertTo-Json -Depth 8)"
$yes=New-Object Windows.Forms.Button;$yes.Location=New-Object Drawing.Point(16,672);$yes.Size=New-Object Drawing.Size(540,45);$yes.Text='この依頼の範囲で、完了までの実行を一括許可';$yes.DialogResult='Yes'
$no=New-Object Windows.Forms.Button;$no.Location=New-Object Drawing.Point(650,672);$no.Size=New-Object Drawing.Size(236,45);$no.Text='許可しない';$no.DialogResult='Cancel'
$form.Controls.AddRange(@($label,$text,$yes,$no));$form.AcceptButton=$no;$form.CancelButton=$no
if($LayoutOnly){[ordered]@{status='PASS_TASK_GRANT_LAYOUT';content_readonly=$text.ReadOnly;receipt_written=$false;window_shown=$false}|ConvertTo-Json;$form.Dispose();exit 0}
$form.TopMost=$true;$form.Add_Shown({$form.Activate()})
$ownerTimer=New-Object Windows.Forms.Timer;$ownerTimer.Interval=1000
$ownerTimer.Add_Tick({
    $ownerValid=$true
    if($OwnerPid -gt 0){try{$taskOwner=[Diagnostics.Process]::GetProcessById($OwnerPid);$ownerValid=(-not $taskOwner.HasExited -and $taskOwner.StartTime.ToUniversalTime().Ticks -eq $OwnerBirth);$taskOwner.Dispose()}catch{$ownerValid=$false}}
    if(-not $ownerValid -or (Test-Path -LiteralPath (Join-Path (Split-Path -Parent $InputPath) 'cancel.json'))){$form.DialogResult='Cancel';$form.Close()}
})
$ownerTimer.Start()
$confirmed=$form.ShowDialog() -eq [Windows.Forms.DialogResult]::Yes
$ownerTimer.Stop();$ownerTimer.Dispose()
if(([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($InputPath))).Replace('-','').ToLowerInvariant()) -ne $hash){throw 'Grant input changed while displayed'}
$result=[ordered]@{source='windows_native_task_command_grant';confirmed=$confirmed;nonce=$data.nonce;input_hash=$hash;task_id=$data.task_id;request_hash=$data.request_hash;confirmed_at=[DateTimeOffset]::UtcNow.ToString('o')}
$pending=[string]$data.output_path+'.pending'
$stream=[IO.File]::Open($pending,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try{$encoded=[Text.Encoding]::UTF8.GetBytes(($result|ConvertTo-Json -Depth 5));$stream.Write($encoded,0,$encoded.Length);$stream.Flush($true)}finally{$stream.Dispose()}
if(Test-Path -LiteralPath $data.output_path){throw 'Existing grant receipt: do not overwrite'}
[IO.File]::Move($pending,[string]$data.output_path);$form.Dispose()
