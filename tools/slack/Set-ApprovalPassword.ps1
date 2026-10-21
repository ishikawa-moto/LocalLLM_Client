#Requires -Version 7.2
param([Parameter(Mandatory)][string]$ExistingWebConfig,
      [Parameter(Mandatory)][string]$ExpectedConfigHash,
      [Parameter(Mandatory)][string]$CredentialFile,
      [Parameter(Mandatory)][string]$PreparedWebConfig,
      [Parameter(Mandatory)][string]$Receipt)
$ErrorActionPreference='Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
function Guard([string]$p){
 if(-not [IO.Path]::IsPathFullyQualified($p) -or $p.StartsWith('\\')){throw 'LOCAL_PATH_REQUIRED'}
 $part=[IO.Path]::GetFullPath($p)
 while($part){if(Test-Path -LiteralPath $part){$item=Get-Item -LiteralPath $part -Force;if($item.Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'LINK_DENIED'}};if(Test-Path -LiteralPath (Join-Path $part '.git')){throw 'PRIVATE_NON_GIT_PATH_REQUIRED'};$part=Split-Path $part -Parent}
}
foreach($p in @($ExistingWebConfig,$CredentialFile,$PreparedWebConfig,$Receipt)){Guard $p}
if((Get-FileHash -LiteralPath $ExistingWebConfig -Algorithm SHA256).Hash -ne $ExpectedConfigHash){throw 'WEB_CONFIG_CHANGED_PRESERVED'}
if((Test-Path -LiteralPath $CredentialFile) -or (Test-Path -LiteralPath $PreparedWebConfig) -or (Test-Path -LiteralPath $Receipt)){throw 'EXISTING_PASSWORD_SETUP_OBSERVE_ONLY'}
$form=[Windows.Forms.Form]::new();$form.Text='LocalBrain Slack：承認パスワード設定';$form.ClientSize=[Drawing.Size]::new(650,345);$form.StartPosition='CenterScreen';$form.TopMost=$true
$note=[Windows.Forms.Label]::new();$note.Text="知識・Ruleの承認画面で使う管理者パスワードを設定します。`n4～256文字。チャットには入力しないでください。`nこのPC内にはソルト付きハッシュだけを保存します。`nHTTPSと内部通信の認証は維持します。";$note.SetBounds(20,15,610,85);$form.Controls.Add($note)
$fields=@();$y=115
foreach($caption in @('パスワード','確認入力')){$label=[Windows.Forms.Label]::new();$label.Text=$caption;$label.SetBounds(20,$y,120,25);$form.Controls.Add($label);$box=[Windows.Forms.TextBox]::new();$box.UseSystemPasswordChar=$true;$box.MaxLength=256;$box.SetBounds(150,$y,475,30);$form.Controls.Add($box);$fields+=,$box;$y+=50}
$status=[Windows.Forms.Label]::new();$status.SetBounds(20,225,610,40);$form.Controls.Add($status)
$save=[Windows.Forms.Button]::new();$save.Text='このPC内だけに保存';$save.SetBounds(20,275,350,45);$form.Controls.Add($save)
$cancel=[Windows.Forms.Button]::new();$cancel.Text='キャンセル';$cancel.SetBounds(400,275,225,45);$cancel.Add_Click({$form.Close()});$form.Controls.Add($cancel)
$save.Add_Click({
 try{
  $password=$fields[0].Text
  if($password.Length -lt 4 -or $password.Length -gt 256 -or $password -cne $fields[1].Text -or @($password.ToCharArray()|Where-Object {[char]::IsControl($_)}).Count){$status.Text='4文字以上で、2つの入力を一致させてください。';return}
  if((Get-FileHash -LiteralPath $ExistingWebConfig -Algorithm SHA256).Hash -ne $ExpectedConfigHash){throw 'WEB_CONFIG_CHANGED_PRESERVED'}
  foreach($p in @($CredentialFile,$PreparedWebConfig,$Receipt)){if(Test-Path -LiteralPath $p){throw 'EXISTING_FILE_PRESERVED'}}
  $salt=[Security.Cryptography.RandomNumberGenerator]::GetBytes(32)
  $hash=[Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2($password,$salt,600000,[Security.Cryptography.HashAlgorithmName]::SHA256,32)
  $record=[ordered]@{schema='localbrain.slack-password.v1';actor='localbrain-password-admin';revision=[guid]::NewGuid().ToString('N');iterations=600000;salt=[Convert]::ToBase64String($salt);hash=[Convert]::ToBase64String($hash)}
  $parent=Split-Path $CredentialFile -Parent;[void][IO.Directory]::CreateDirectory($parent)
  $sid=[Security.Principal.WindowsIdentity]::GetCurrent().User
  $acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetAccessRuleProtection($true,$false)
  foreach($owner in @($sid,[Security.Principal.SecurityIdentifier]::new('S-1-5-18'))){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($owner,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
  Set-Acl -LiteralPath $parent -AclObject $acl
  $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Depth 8));$f=[IO.FileStream]::new($CredentialFile,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$f.Write($bytes);$f.Flush($true)}finally{$f.Dispose()}
  $config=Get-Content -LiteralPath $ExistingWebConfig -Raw|ConvertFrom-Json -AsHashtable;$config.Remove('adminCertificateSha256');$config['passwordCredentialFile']=$CredentialFile
  [IO.File]::WriteAllText($PreparedWebConfig,($config|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
  [IO.File]::WriteAllText($Receipt,(@{status='prepared';authentication='password';plaintext_stored=$false;current_runtime_changed=$false;created_utc=[DateTimeOffset]::UtcNow.ToString('o')}|ConvertTo-Json),[Text.UTF8Encoding]::new($false))
  $fields[0].Clear();$fields[1].Clear();$password=$null;[Security.Cryptography.CryptographicOperations]::ZeroMemory($hash)
  [void][Windows.Forms.MessageBox]::Show('保存しました。試験済みruntimeの切替後に、新しいパスワードでログインできます。','LocalBrain');$form.Close()
 }catch{$fields[0].Clear();$fields[1].Clear();$status.Text='設定を完了できませんでした。既存ファイルは保持します。';}
})
$form.Add_Shown({$form.Activate();$fields[0].Focus()});[void]$form.ShowDialog()
