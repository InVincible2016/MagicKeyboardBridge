param([Parameter(Mandatory)][string]$PackageRoot,[switch]$KeepOptionCommand,[string]$RunId=[guid]::NewGuid().ToString('N'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$stage=Get-Stage
$changed=$false;$guard=$false;$trusted=$false;$installMutex=$null;$installLocked=$false
try {
 Assert-Administrator
 $installMutex=[Threading.Mutex]::new($false,'Global\MagicKeyboardBridgeInstallV1')
 try{$installLocked=$installMutex.WaitOne(0)}catch [Threading.AbandonedMutexException]{$installLocked=$true}
 if(-not $installLocked){throw 'Another installation is running. Leave its window open.'}
 Assert-SafeDirectory $stage
 if(Test-Path -LiteralPath $stage){Assert-StageTrusted $stage;$trusted=$true}
 if(Test-Path -LiteralPath (Join-Path $stage 'settings.json')) {
  $existing=Invoke-App @('health','--stage',$stage) -AllowFailure
  if($existing.ExitCode-eq 0-and(Test-BackgroundTasks)){[ordered]@{Status='AlreadyInstalled';RunId=$RunId;Time=(Get-Date).ToString('o');PhysicalDriverChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'install-result.json') -Encoding UTF8;Set-Progress 'Already installed and running.';exit 0}
  # Restore an unhealthy owned installation before replacing executable files.
  & (Join-Path $stage 'scripts\Recover.ps1')
  if($LASTEXITCODE-ne 0){throw 'Existing installation recovery failed. Run Restore.cmd; its files were retained.'}
 }elseif((Test-Path -LiteralPath $stage)-and-not(Test-Path -LiteralPath (Join-Path $stage 'package-owner.txt'))){throw 'An older or unrelated installation occupies the destination. It was left unchanged.'}
 $manifest=Get-Content -LiteralPath (Join-Path $PackageRoot 'package-manifest.json') -Raw|ConvertFrom-Json
 if($manifest.Product-ne'MagicKeyboardBridge'){throw 'Wrong package manifest.'}
 $packagePrefix=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')+'\'
 foreach($entry in $manifest.Files) {
  $source=[IO.Path]::GetFullPath((Join-Path $PackageRoot $entry.Path))
  if(-not $source.StartsWith($packagePrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid manifest path.'}
  Assert-SafeDirectory (Split-Path -Parent $source)
  if((Get-Item -LiteralPath $source -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Package contains a link.'}
  if((Get-FileHash -LiteralPath $source).Hash-ne$entry.SHA256){throw ('Package integrity failed: '+$entry.Path)}
 }
 # No driver changes occur before dependency and preflight checks complete.
 if(-not(Test-Path -LiteralPath $stage)){New-Item -ItemType Directory -Path $stage|Out-Null}
 Protect-Stage $stage
 $trusted=$true
 [IO.File]::WriteAllText((Join-Path $stage 'package-owner.txt'),'MagicKeyboardBridge/1')
 foreach($entry in $manifest.Files) {
  $destination=[IO.Path]::GetFullPath((Join-Path $stage $entry.Path))
  if(-not $destination.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid destination.'}
  Assert-SafeDirectory (Split-Path -Parent $destination)
  New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
  Copy-Item -LiteralPath (Join-Path $PackageRoot $entry.Path) -Destination $destination -Force
  if((Get-FileHash -LiteralPath $destination).Hash-ne$entry.SHA256){throw 'Staged file hash mismatch.'}
 }
 Set-Progress 'Checking Windows and the connected Apple keyboard...'
 $preflight=(Invoke-App @('preflight')).Output|ConvertFrom-Json
 if(@($preflight.Devices).Count-ne 1){throw 'Connect exactly one A1644 Magic Keyboard by Lightning USB.'}
 if($preflight.Devices[0].Service-ne'HidUsb'-or @($preflight.Devices[0].LowerFilters).Count-ne 0){throw 'Another driver currently owns the keyboard. Its configuration was left unchanged.'}
 if(-not(Test-Path -LiteralPath (Join-Path $stage 'settings.json'))) {
  $arguments=@('initialize','--stage',$stage);if($KeepOptionCommand){$arguments+='--keep-option-command'}
  Invoke-App $arguments|Out-Null
 }
 Set-Progress 'Preparing machine-local driver signatures. Test signing remains OFF...'
 Invoke-App @('prepare-drivers','--stage',$stage) -TimeoutSeconds 150|Out-Null
 Set-Progress 'Creating the persistent virtual keyboard...'
 Invoke-App @('create-virtual','--stage',$stage) -TimeoutSeconds 45|Out-Null
 Set-Progress 'Testing virtual input and reconnection before switching the physical keyboard...'
 Invoke-App @('gate','--stage',$stage) -TimeoutSeconds 45|Out-Null
 $windowsPS="$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
 $settings=New-ScheduledTaskSettingsSet -StartWhenAvailable -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -MultipleInstances IgnoreNew -ExecutionTimeLimit ([timespan]::Zero)
 foreach($name in @('MagicKeyboardBridgeWorker','MagicKeyboardBridgeHealth','MagicKeyboardBridgeRollback')){Assert-TaskOwner $name}
 $deadline=(Get-Date).AddMinutes(3)
 [ordered]@{Deadline=$deadline.ToString('o');InstallerPid=$PID;InstallerPath=(Get-Process -Id $PID).Path;InstallerStarted=(Get-Process -Id $PID).StartTime.ToUniversalTime().ToString('o');Time=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'installing.json') -Encoding UTF8
 $recoverAction=New-ScheduledTaskAction -Execute $windowsPS -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}" -Deadline' -f (Join-Path $stage 'scripts\Recover.ps1'))
 Register-ScheduledTask -TaskName MagicKeyboardBridgeRollback -Action $recoverAction -Trigger (New-ScheduledTaskTrigger -Once -At $deadline) -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
 $guard=$true
 $workerAction=New-ScheduledTaskAction -Execute (Join-Path $stage 'app\MagicKeyboardBridge.exe') -Argument ('worker --stage "{0}"' -f $stage) -WorkingDirectory $stage
 Register-ScheduledTask -TaskName MagicKeyboardBridgeWorker -Action $workerAction -Trigger (New-ScheduledTaskTrigger -AtStartup) -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
 $healthAction=New-ScheduledTaskAction -Execute $windowsPS -Argument ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "{0}"' -f (Join-Path $stage 'scripts\Watchdog.ps1'))
 $boot=New-ScheduledTaskTrigger -AtStartup;$boot.Delay='PT60S'
 $repeat=New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 1)
 Register-ScheduledTask -TaskName MagicKeyboardBridgeHealth -Action $healthAction -Trigger @($boot,$repeat) -Settings $settings -User SYSTEM -RunLevel Highest -Force|Out-Null
 Disable-ScheduledTask -TaskName MagicKeyboardBridgeHealth|Out-Null
 foreach($name in @('stop','status.json','probe.request')){Remove-Item -LiteralPath (Join-Path $stage $name) -ErrorAction SilentlyContinue}
 Set-Progress 'Switching the Apple keyboard and verifying background input...'
 $changed=$true
 Invoke-App @('bind','--stage',$stage)|Out-Null
 [IO.File]::WriteAllText((Join-Path $stage 'armed'),'armed')
 Start-ScheduledTask -TaskName MagicKeyboardBridgeWorker
 $first=Wait-Worker
 Invoke-App @('observe','--stage',$stage)|Out-Null
 Set-Progress 'Testing a background-process restart...'
 [IO.File]::WriteAllText((Join-Path $stage 'stop'),'restart test')
 for($i=0;$i-lt 40;$i++){if((Get-ScheduledTask -TaskName MagicKeyboardBridgeWorker).State-ne'Running'){break};Start-Sleep -Milliseconds 250}
 if((Get-ScheduledTask -TaskName MagicKeyboardBridgeWorker).State-eq'Running'){throw 'Worker did not stop for the restart test.'}
 foreach($name in @('stop','status.json')){Remove-Item -LiteralPath (Join-Path $stage $name) -ErrorAction SilentlyContinue}
 Start-ScheduledTask -TaskName MagicKeyboardBridgeWorker
 $second=Wait-Worker
 Invoke-App @('observe','--stage',$stage)|Out-Null
 Invoke-App @('health','--stage',$stage)|Out-Null
 $watchdogStarted=Get-Date
 Enable-ScheduledTask -TaskName MagicKeyboardBridgeHealth|Out-Null
 Start-ScheduledTask -TaskName MagicKeyboardBridgeHealth
 $watchdogPassed=$false
 for($i=0;$i-lt 30;$i++) {
  Start-Sleep -Milliseconds 500
  try{$health=Get-Content -LiteralPath (Join-Path $stage 'health.json') -Raw|ConvertFrom-Json}catch{continue}
  if(([datetime]$health.Time)-ge$watchdogStarted-and$health.CheckerSessionId-eq 0){
   if($health.Status-ne'Healthy'){throw 'The scheduled watchdog rejected the running keyboard.'}
   $watchdogPassed=$true;break
  }
 }
 if(-not $watchdogPassed-or-not(Test-Path -LiteralPath (Join-Path $stage 'armed'))){throw 'The scheduled watchdog did not verify the keyboard.'}
 Unregister-ScheduledTask -TaskName MagicKeyboardBridgeRollback -Confirm:$false
 $guard=$false
 Remove-Item -LiteralPath (Join-Path $stage 'installing.json')
 [ordered]@{Status='Installed';RunId=$RunId;WatchdogVerified=$true;Time=(Get-Date).ToString('o');TestSigningActive=$false;VirtualInputVerified=$true;SystemInputVerified=$true;ProcessRestartVerified=$true;RebootRequired=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'install-result.json') -Encoding UTF8
 Set-Progress 'Installed. Fn is Control. No restart is required.'
 exit 0
}catch{
 $message=$_|Out-String
 if($changed) {
  & (Join-Path $stage 'scripts\Recover.ps1')
  if($LASTEXITCODE-ne 0){$message+=' Automatic recovery needs attention. Run Restore.cmd with the mouse.'}
  elseif($guard){Unregister-ScheduledTask -TaskName MagicKeyboardBridgeRollback -Confirm:$false -ErrorAction SilentlyContinue;$guard=$false;Remove-Item -LiteralPath (Join-Path $stage 'installing.json') -ErrorAction SilentlyContinue}
 }
 if($guard-and-not$changed) {
  try {
   foreach($name in @('MagicKeyboardBridgeWorker','MagicKeyboardBridgeHealth')){Assert-TaskOwner $name;Disable-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue|Out-Null}
   Assert-TaskOwner 'MagicKeyboardBridgeRollback'
   Unregister-ScheduledTask -TaskName MagicKeyboardBridgeRollback -Confirm:$false
   $guard=$false
   Remove-Item -LiteralPath (Join-Path $stage 'installing.json') -ErrorAction SilentlyContinue
  }catch{$message+=' Pre-takeover task cleanup needs review: '+($_|Out-String)}
 }
 if($trusted){[ordered]@{Status='InstallNeedsReview';RunId=$RunId;Time=(Get-Date).ToString('o');Message=$message;BootSettingsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'install-result.json') -Encoding UTF8;Set-Progress $message}
 [Console]::Error.WriteLine($message)
 exit 1
}finally{if($installLocked){$installMutex.ReleaseMutex()};if($installMutex){$installMutex.Dispose()}}
