param([switch]$Deadline,[string]$RunId=[guid]::NewGuid().ToString('N'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$stage=Get-Stage
$trusted=$false
$lifecycle=$null;$lifecycleLocked=$false
try {
 Assert-Administrator
 Assert-StageTrusted $stage
 $trusted=$true
 $settings=Get-Content -LiteralPath (Join-Path $stage 'settings.json') -Raw|ConvertFrom-Json
 if($settings.Owner-ne'MagicKeyboardBridge'){throw 'Unexpected installation owner.'}
 if($Deadline) {
  if(-not(Test-Path -LiteralPath (Join-Path $stage 'installing.json'))){exit 0}
  $install=Get-Content -LiteralPath (Join-Path $stage 'installing.json') -Raw|ConvertFrom-Json
  if(([datetime]$install.Deadline).ToUniversalTime()-gt[datetime]::UtcNow){exit 0}
  $installer=Get-Process -Id ([int]$install.InstallerPid) -ErrorAction SilentlyContinue
  if($installer-and $installer.Path-eq$install.InstallerPath-and[math]::Abs(($installer.StartTime.ToUniversalTime()-([datetime]$install.InstallerStarted).ToUniversalTime()).TotalSeconds)-lt 1){Stop-Process -Id $installer.Id -Force}
 }
 $lifecycle=[Threading.Mutex]::new($false,'Global\MagicKeyboardBridgeInstallV1')
 try{$lifecycleLocked=$lifecycle.WaitOne(20000)}catch [Threading.AbandonedMutexException]{$lifecycleLocked=$true}
 if(-not $lifecycleLocked){throw 'Installation is still running. Its independent rollback remains armed.'}
 [IO.File]::WriteAllText((Join-Path $stage 'stop'),'recovery')
 foreach($name in @('MagicKeyboardBridgeWorker','MagicKeyboardBridgeHealth')){Assert-TaskOwner $name;Disable-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue|Out-Null}
 Start-Sleep -Seconds 2
 Stop-ScheduledTask -TaskName MagicKeyboardBridgeWorker -ErrorAction SilentlyContinue
 Invoke-App @('restore-device','--stage',$stage)|Out-Null
 Invoke-App @('remove-usb-package','--stage',$stage) -TimeoutSeconds 90|Out-Null
 Remove-Item -LiteralPath (Join-Path $stage 'armed') -ErrorAction SilentlyContinue
 [ordered]@{Status='MicrosoftKeyboardRestored';RunId=$RunId;Time=(Get-Date).ToString('o');BootSettingsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'recovery.json') -Encoding UTF8
 exit 0
}catch{
 [Console]::Error.WriteLine(($_|Out-String))
 if($trusted){[ordered]@{Status='RecoveryNeedsAttention';RunId=$RunId;Time=(Get-Date).ToString('o');Message=($_|Out-String)}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'recovery.json') -Encoding UTF8}
 exit 1
}finally{if($lifecycleLocked){$lifecycle.ReleaseMutex()};if($lifecycle){$lifecycle.Dispose()}}
