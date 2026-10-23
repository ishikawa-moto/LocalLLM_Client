#Requires -Version 7.0
param([string]$ControlRoot=(Join-Path $env:LOCALAPPDATA 'LocalBrain\control-plane\slack'))
$ErrorActionPreference='Stop'
$p=[IO.Path]::GetFullPath($ControlRoot)
function Hash($f){(Get-FileHash -LiteralPath $f -Algorithm SHA256).Hash.ToLowerInvariant()}
function Save($value,$path){$tmp=$path+'.pending';$bytes=[Text.UTF8Encoding]::new($false).GetBytes(($value|ConvertTo-Json -Depth 14)+[char]10);$f=[IO.FileStream]::new($tmp,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$f.Write($bytes);$f.Flush($true)}finally{$f.Dispose()};if(Test-Path $path){[IO.File]::Replace($tmp,$path,[NullString]::Value)}else{[IO.File]::Move($tmp,$path)}}
$r=Get-Content (Join-Path $p 'receiver-password-files-install.json') -Raw|ConvertFrom-Json
if($r.status -ne 'installed' -or (Hash $PSCommandPath) -ne $r.launcher_sha256){throw 'INSTALLED_LAUNCHER_DRIFT'}
foreach($f in $r.manifest.PSObject.Properties){if((Hash (Join-Path $r.runtime $f.Name)) -ne $f.Value){throw 'RUNTIME_DRIFT'}}
if((Hash $r.config) -ne $r.config_sha256 -or (Hash $r.web_config) -ne $r.web_sha256){throw 'CONFIG_DRIFT'}
$lease=[IO.File]::Open((Join-Path $p 'launcher.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
try{
 $store=Join-Path $p 'data';$probe=[IO.File]::Open((Join-Path $store 'host.lock'),[IO.FileMode]::OpenOrCreate,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None);$probe.Dispose()
 $path=Join-Path $p 'host-password-files-launch-intent.json'
 $boot=(Get-CimInstance Win32_OperatingSystem).LastBootUpTime.ToUniversalTime().ToFileTimeUtc().ToString()
 if(Test-Path ($path+'.pending')){throw 'PENDING_LAUNCH_OBSERVE_ONLY'}
 if(Test-Path $path){$old=Get-Content $path -Raw|ConvertFrom-Json;if($old.schema -ne 'localbrain.slack-launch.v3' -or $old.attempt -notmatch '^[a-f0-9]{32}$'){throw 'UNKNOWN_LAUNCH_KEEP'};$live=if($old.pid){Get-Process -Id $old.pid -ErrorAction SilentlyContinue}else{$null};if($live){throw 'LIVE_PROCESS_OR_IDENTITY_KEEP'};if($old.status -ne 'process_exited' -and -not($old.boot_filetime -and [long]$old.boot_filetime -lt [long]$boot)){throw 'UNCERTAIN_LAUNCH_KEEP'};[IO.File]::Move($path,(Join-Path $p ('host-password-files-history-'+$old.attempt+'.json')),$false)}
 $intent=[ordered]@{schema='localbrain.slack-launch.v3';status='launch_requested';attempt=[guid]::NewGuid().ToString('N');boot_filetime=$boot;executable=(Join-Path $r.runtime 'localbrain.exe');runtime_dll_sha256=$r.manifest.'localbrain.dll';config_sha256=$r.config_sha256;web_sha256=$r.web_sha256;created_utc=[DateTimeOffset]::UtcNow.ToString('o')}
 Save $intent $path
 if((Hash $r.client_config) -ne $r.client_config_sha256){throw 'CLIENT_CONFIG_DRIFT'}
 $env:LOCALBRAIN_CLIENT_CONFIG=$r.client_config
 $env:LOCALBRAIN_OCR_CONFIG=$r.ocr_config
 $child=Start-Process -FilePath $intent.executable -ArgumentList @('slack','serve','--config',('"'+$r.config+'"'),'--web-config',('"'+$r.web_config+'"')) -WorkingDirectory $p -WindowStyle Hidden -RedirectStandardOutput (Join-Path $p ('host-'+$intent.attempt+'.stdout.log')) -RedirectStandardError (Join-Path $p ('host-'+$intent.attempt+'.stderr.log')) -PassThru
 $intent.pid=$child.Id;$intent.birth_utc=$child.StartTime.ToUniversalTime().ToString('o');$intent.status='running';Save $intent $path
 $child.WaitForExit();$intent.status='process_exited';$intent.exit_code=$child.ExitCode;$intent.exited_utc=[DateTimeOffset]::UtcNow.ToString('o');Save $intent $path
}finally{$lease.Dispose()}
