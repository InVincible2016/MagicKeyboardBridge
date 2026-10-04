$ErrorActionPreference='Stop'
function Get-Stage { Join-Path $env:ProgramData 'MagicKeyboardBridge' }
function Assert-Administrator {
 if(-not([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)){throw 'Administrator authorization is required.'}
}
function Invoke-App {
 param([string[]]$Arguments,[int]$TimeoutSeconds=40,[switch]$AllowFailure)
 $start=New-Object Diagnostics.ProcessStartInfo
 $start.FileName=Join-Path (Get-Stage) 'app\MagicKeyboardBridge.exe'
 $quoted=@($Arguments|ForEach-Object { if($_.Contains('"')){throw 'Invalid command argument.'};'"'+$_+'"' })
 $start.Arguments=$quoted -join ' '
 $start.UseShellExecute=$false;$start.CreateNoWindow=$true
 $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
 $process=New-Object Diagnostics.Process;$process.StartInfo=$start
 try {
  if(-not $process.Start()){throw 'Cannot start bridge command.'}
  $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
  if(-not $process.WaitForExit($TimeoutSeconds*1000)){try{$process.Kill()}catch{};throw ('Command timed out: '+$Arguments[0])}
  if(-not $stdout.Wait(5000)-or-not $stderr.Wait(5000)){throw 'Command output did not close.'}
  $record=[pscustomobject]@{ExitCode=[int]$process.ExitCode;Output=$stdout.Result;Error=$stderr.Result}
  if(-not $AllowFailure -and $record.ExitCode-ne 0){throw ($Arguments[0]+' failed: '+$record.Output+$record.Error)}
  return $record
 }finally{$process.Dispose()}
}
function Assert-SafeDirectory([string]$Path) {
 $current=[IO.Path]::GetFullPath($Path)
 while($current) {
  if(Test-Path -LiteralPath $current){if((Get-Item -LiteralPath $current -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw ('Directory must not be a link: '+$current)}}
  $parent=Split-Path -Parent $current;if($parent-eq$current){break};$current=$parent
 }
}
function Protect-Stage([string]$Path) {
 Assert-SafeDirectory $Path
 $acl=New-Object Security.AccessControl.DirectorySecurity
 $acl.SetAccessRuleProtection($true,$false)
 foreach($sid in @('S-1-5-18','S-1-5-32-544')){$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid),'FullControl','ContainerInherit,ObjectInherit','None','Allow'))}
 $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-5-32-545'),'ReadAndExecute','ContainerInherit,ObjectInherit','None','Allow'))
 $acl.SetOwner([Security.Principal.SecurityIdentifier]::new('S-1-5-32-544'))
 Set-Acl -LiteralPath $Path -AclObject $acl
}
function Assert-TaskOwner([string]$Name) {
 $task=Get-ScheduledTask -TaskName $Name -ErrorAction SilentlyContinue
 if(-not $task){return}
 $stage=Get-Stage
 $exe="$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
 switch($Name) {
  'MagicKeyboardBridgeWorker'{$exe=Join-Path $stage 'app\MagicKeyboardBridge.exe';$arguments='worker --stage "'+$stage+'"'}
  'MagicKeyboardBridgeHealth'{$arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $stage 'scripts\Watchdog.ps1')+'"'}
  'MagicKeyboardBridgeRollback'{$arguments='-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $stage 'scripts\Recover.ps1')+'" -Deadline'}
  default{throw 'Unknown task name.'}
 }
 if(@($task.Actions).Count-ne 1-or$task.Actions[0].Execute-ne$exe-or$task.Actions[0].Arguments-ne$arguments){throw ('Task belongs to another application: '+$Name)}
}
function Test-BackgroundTasks {
 foreach($name in @('MagicKeyboardBridgeWorker','MagicKeyboardBridgeHealth')) {
  Assert-TaskOwner $name
  $task=Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
  if(-not $task-or$task.State-eq'Disabled'){return $false}
  try{$identity=[Security.Principal.NTAccount]::new($task.Principal.UserId).Translate([Security.Principal.SecurityIdentifier]).Value}catch{$identity=$task.Principal.UserId}
  if($identity-ne'S-1-5-18'){return $false}
  if(-not@($task.Triggers|Where-Object{$_.CimClass.CimClassName-eq'MSFT_TaskBootTrigger'-and$_.Enabled-ne$false}).Count){return $false}
 }
 $worker=Get-ScheduledTask -TaskName MagicKeyboardBridgeWorker
 return ($worker.State-eq'Running'-and(Test-Path -LiteralPath (Join-Path (Get-Stage) 'armed')))
}
function Set-Progress([string]$Message) {
 [IO.File]::WriteAllText((Join-Path (Get-Stage) 'progress.txt'),$Message)
}
function Wait-Worker {
 for($i=0;$i-lt 60;$i++) {
  Start-Sleep -Milliseconds 500
  try{$status=Get-Content -LiteralPath (Join-Path (Get-Stage) 'status.json') -Raw|ConvertFrom-Json}catch{continue}
  if($status.Status-eq'Failed'){throw $status.Message}
  if($status.Status-eq'Running'-and $status.SessionId-eq 0-and-not $status.TestSigningActive){return $status}
 }
 throw 'Background keyboard process did not become ready.'
}

function Assert-StageTrusted([string]$Path) {
 Assert-SafeDirectory $Path
 $acl=Get-Acl -LiteralPath $Path
 $owner=$acl.GetOwner([Security.Principal.SecurityIdentifier]).Value
 if($owner-notin@('S-1-5-18','S-1-5-32-544')-or-not $acl.AreAccessRulesProtected){throw 'Existing installation is not protected by administrators.'}
 $write=[Security.AccessControl.FileSystemRights]::Write-bor[Security.AccessControl.FileSystemRights]::Delete-bor[Security.AccessControl.FileSystemRights]::ChangePermissions-bor[Security.AccessControl.FileSystemRights]::TakeOwnership
 foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
  if($rule.AccessControlType-eq'Allow'-and $rule.IdentityReference.Value-notin@('S-1-5-18','S-1-5-32-544')-and($rule.FileSystemRights-band$write)){throw 'Existing installation allows non-administrator writes.'}
 }
}
