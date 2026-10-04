param([string]$RunId=[guid]::NewGuid().ToString('N'))
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$stage=Get-Stage
$trusted=$false;$lifecycle=$null;$lifecycleLocked=$false
try {
 Assert-Administrator
 Assert-StageTrusted $stage
 $trusted=$true
 $lifecycle=[Threading.Mutex]::new($false,'Global\MagicKeyboardBridgeInstallV1')
 try{$lifecycleLocked=$lifecycle.WaitOne(0)}catch [Threading.AbandonedMutexException]{$lifecycleLocked=$true}
 if(-not $lifecycleLocked){throw 'Another installation or recovery is running.'}
 & (Join-Path $PSScriptRoot 'Recover.ps1')
 if($LASTEXITCODE-ne 0){throw 'Keyboard recovery failed; application files were retained.'}
 Invoke-App @('remove-virtual','--stage',$stage) -TimeoutSeconds 90|Out-Null
 foreach($name in @('MagicKeyboardBridgeWorker','MagicKeyboardBridgeHealth','MagicKeyboardBridgeRollback')) {
  Assert-TaskOwner $name
  Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction SilentlyContinue
 }
 # Retain only the small result and recovery log so the launcher can report
 # completion. Resolve and validate every owned subtree before deleting it.
 foreach($name in @('app','scripts','drivers','driver-tools')) {
  $target=[IO.Path]::GetFullPath((Join-Path $stage $name))
  if(-not $target.StartsWith($stage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe cleanup path.'}
  if(Test-Path -LiteralPath $target){Assert-SafeDirectory $target;foreach($item in Get-ChildItem -LiteralPath $target -Force -Recurse){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected link during cleanup.'}};Remove-Item -LiteralPath $target -Recurse -Force}
 }
 foreach($name in @('settings.json','armed','stop','status.json','health.json','installing.json')){Remove-Item -LiteralPath (Join-Path $stage $name) -ErrorAction SilentlyContinue}
 [ordered]@{Status='Uninstalled';RunId=$RunId;Time=(Get-Date).ToString('o');PhysicalKeyboardRestored=$true;BootSettingsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'uninstall-result.json') -Encoding UTF8
 exit 0
}catch{
 [Console]::Error.WriteLine(($_|Out-String))
 if($trusted){[ordered]@{Status='UninstallNeedsReview';RunId=$RunId;Time=(Get-Date).ToString('o');Message=($_|Out-String);BootSettingsChanged=$false}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'uninstall-result.json') -Encoding UTF8}
 exit 1
}
finally{if($lifecycleLocked){$lifecycle.ReleaseMutex()};if($lifecycle){$lifecycle.Dispose()}}
