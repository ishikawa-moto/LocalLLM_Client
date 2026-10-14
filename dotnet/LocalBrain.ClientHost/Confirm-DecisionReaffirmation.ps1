param([Parameter(Mandatory=$true)][string]$InputPath,[switch]$LayoutOnly)
$ErrorActionPreference='Stop'
if(-not[Environment]::UserInteractive){throw 'An interactive Windows desktop is required'}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
$inputData=Get-Content -LiteralPath $InputPath -Raw -Encoding UTF8|ConvertFrom-Json
if($inputData.source-ne'windows_native_dialog_request'-or$inputData.nonce-notmatch'^[a-f0-9]{32}$'){throw 'Invalid native confirmation input'}
$form=New-Object System.Windows.Forms.Form
$form.Text='SecondBrain：差し戻し指摘を了承して保存する確認'
$form.Size=New-Object System.Drawing.Size(900,820);$form.StartPosition='CenterScreen'
$form.MinimizeBox=$false;$form.MaximizeBox=$false
$summary=New-Object System.Windows.Forms.Label
$summary.Location=New-Object System.Drawing.Point(16,12);$summary.Size=New-Object System.Drawing.Size(850,60)
$summary.Text="Reviewerは不備を指摘しました。下記の指摘を了承し、人間の再依頼としてこの内容を保存しますか？`r`n合格・検証済みの事実としては登録しません。"
$issues=New-Object System.Windows.Forms.TextBox
$issues.Location=New-Object System.Drawing.Point(16,76);$issues.Size=New-Object System.Drawing.Size(850,180)
$issues.Multiline=$true;$issues.ReadOnly=$true;$issues.ScrollBars='Vertical'
$issues.Text=($inputData.issues|ForEach-Object {"[$($_.code)] $($_.message)"})-join"`r`n"
$content=New-Object System.Windows.Forms.TextBox
$content.Location=New-Object System.Drawing.Point(16,270);$content.Size=New-Object System.Drawing.Size(850,440)
$content.Multiline=$true;$content.ReadOnly=$true;$content.ScrollBars='Both';$content.WordWrap=$false
$content.Text="request_id: $($inputData.request_id)`r`nproject: $($inputData.project)`r`ntitle: $($inputData.title)`r`nintent_hash: $($inputData.intent_hash)`r`n`r`n$($inputData.content)"
$yes=New-Object System.Windows.Forms.Button
$yes.Text='指摘を了承して、この内容を保存';$yes.Location=New-Object System.Drawing.Point(16,730);$yes.Size=New-Object System.Drawing.Size(450,40);$yes.DialogResult='Yes'
$no=New-Object System.Windows.Forms.Button
$no.Text='保存しない';$no.Location=New-Object System.Drawing.Point(650,730);$no.Size=New-Object System.Drawing.Size(216,40);$no.DialogResult='Cancel'
$form.Controls.AddRange(@($summary,$issues,$content,$yes,$no));$form.CancelButton=$no;$form.AcceptButton=$no
if($LayoutOnly){[ordered]@{status='PASS_NATIVE_LAYOUT_ONLY';control_count=$form.Controls.Count;content_readonly=$content.ReadOnly;issues_readonly=$issues.ReadOnly;contains_exact_content=$content.Text.Contains([string]$inputData.content);approval_receipt_created=$false;window_shown=$false}|ConvertTo-Json;$form.Dispose();exit 0}
$confirmed=($form.ShowDialog()-eq[System.Windows.Forms.DialogResult]::Yes)
$receipt=[ordered]@{source='windows_native_dialog';confirmed=$confirmed;nonce=$inputData.nonce;intent_hash=$inputData.intent_hash;review_receipt_hash=$inputData.review_receipt_hash;request_id=$inputData.request_id;confirmed_at=[DateTimeOffset]::UtcNow.ToUnixTimeSeconds();actor=[Environment]::UserName}
$temp=[string]$inputData.output_path+'.pending'
if(Test-Path -LiteralPath $temp){throw 'Confirmation output already pending'}
if(Test-Path -LiteralPath $inputData.output_path){throw 'Confirmation output already exists'}
$receipt|ConvertTo-Json -Depth 4|Set-Content -LiteralPath $temp -Encoding UTF8
[IO.File]::Move($temp,[string]$inputData.output_path)
$form.Dispose()
