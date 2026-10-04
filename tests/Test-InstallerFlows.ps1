param([string]$Root=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$parent=Join-Path $Root ('.cache\flow-tests\'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $parent -Force|Out-Null
$checks=0;$passed=0
function Assert([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
$cases=@(
 @{Name='install-success';Action='Install';Fault='';Success=$true},
 @{Name='install-already';Action='Install';Fault='';Existing=$true;Success=$true},
 @{Name='install-disabled';Action='Install';Fault='task-disabled';Existing=$true;Success=$true},
 @{Name='install-missing';Action='Install';Fault='task-missing';Existing=$true;Success=$true},
 @{Name='install-no-startup';Action='Install';Fault='task-no-startup';Existing=$true;Success=$true},
 @{Name='install-foreign-task';Action='Install';Fault='task-foreign';Existing=$true},
 @{Name='install-trigger-disabled';Action='Install';Fault='trigger-disabled';Existing=$true;Success=$true},
 @{Name='install-user';Action='Install';Fault='task-user';Existing=$true;Success=$true},
 @{Name='install-preflight';Action='Install';Fault='preflight'},
 @{Name='install-foreign';Action='Install';Fault='foreign-driver'},
 @{Name='install-initialize';Action='Install';Fault='initialize'},
 @{Name='install-prepare';Action='Install';Fault='prepare-drivers'},
 @{Name='install-create';Action='Install';Fault='create-virtual'},
 @{Name='install-gate';Action='Install';Fault='gate'},
 @{Name='install-task';Action='Install';Fault='register-worker'},
 @{Name='install-bind';Action='Install';Fault='bind';Restore=$true},
 @{Name='install-worker';Action='Install';Fault='worker-start';Restore=$true},
 @{Name='install-observe';Action='Install';Fault='observe';Restore=$true},
 @{Name='install-restart';Action='Install';Fault='restart-hang';Restore=$true},
 @{Name='install-observe-second';Action='Install';Fault='observe-second';Restore=$true},
 @{Name='install-health';Action='Install';Fault='health';Restore=$true},
 @{Name='install-watchdog';Action='Install';Fault='watchdog-unhealthy';Restore=$true},
 @{Name='install-watchdog-missing';Action='Install';Fault='watchdog-missing';Restore=$true},
 @{Name='install-recovery-failure';Action='Install';Fault='recovery-failure';KeepGuard=$true},
 @{Name='uninstall-success';Action='Uninstall';Fault='';Existing=$true;Success=$true;Restore=$true},
 @{Name='uninstall-recovery-failure';Action='Uninstall';Fault='restore-device';Existing=$true;KeepFiles=$true},
 @{Name='uninstall-virtual-failure';Action='Uninstall';Fault='remove-virtual';Existing=$true;KeepFiles=$true;Restore=$true},
 @{Name='restore-success';Action='Recover';Fault='';Existing=$true;Success=$true;Restore=$true},
 @{Name='restore-untrusted';Action='Recover';Fault='untrusted';Existing=$true;KeepFiles=$true},
 @{Name='deadline-early';Action='Recover';Fault='';Existing=$true;Deadline=10;Success=$true},
 @{Name='deadline-expired';Action='Recover';Fault='';Existing=$true;Deadline=-10;Success=$true;Restore=$true},
 @{Name='launcher-success';Action='Bootstrap';Fault='';Success=$true},
 @{Name='launcher-stale';Action='Bootstrap';Fault='bootstrap-stale'},
 @{Name='launcher-failure';Action='Bootstrap';Fault='bootstrap-failure'},
 @{Name='launcher-no-result';Action='Bootstrap';Fault='bootstrap-no-result'}
)
try {
 foreach($case in $cases) {
  $fixture=Join-Path $parent $case.Name;$package=Join-Path $fixture ("package with space and 'quote'"+[char]0x4e2d);$stage=Join-Path $fixture 'ProgramData\MagicKeyboardBridge'
  New-Item -ItemType Directory -Path (Join-Path $package 'scripts'),(Join-Path $package 'app') -Force|Out-Null
  foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'scripts') -Filter '*.ps1') {
   $source=[IO.File]::ReadAllText($file.FullName).Replace('Global\MagicKeyboardBridge',('Local\MkbFlowTest'+[IO.Path]::GetFileName($parent)))
   if($file.Name-in@('Common.ps1','Dependencies.ps1')){$source+=[Environment]::NewLine+'. (Join-Path $env:MKB_FLOW_ROOT ''InstallerPlatform.ps1'')'+[Environment]::NewLine}
   [IO.File]::WriteAllText((Join-Path $package ('scripts\'+$file.Name)),$source)
  }
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'InstallerPlatform.ps1') -Destination $fixture
  [IO.File]::WriteAllText((Join-Path $package 'app\MagicKeyboardBridge.exe'),'mock-only; never execute')
  $files=@(Get-ChildItem -LiteralPath $package -Recurse -File|ForEach-Object{@{Path=$_.FullName.Substring($package.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
  @{Product='MagicKeyboardBridge';Files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $package 'package-manifest.json') -Encoding ASCII
  $service=if($case.Existing){'WinUSB'}else{'HidUsb'}
  @{Fault=$case.Fault;Service=$service;Existing=[bool]$case.Existing}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $fixture 'scenario.json') -Encoding ASCII
  if($case.Existing-or$case.Action-eq'Bootstrap') {
   New-Item -ItemType Directory -Path $stage -Force|Out-Null
   Copy-Item -LiteralPath (Join-Path $package 'scripts'),(Join-Path $package 'app') -Destination $stage -Recurse
   [IO.File]::WriteAllText((Join-Path $stage 'package-owner.txt'),'MagicKeyboardBridge/1')
   @{Owner='MagicKeyboardBridge'}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'settings.json') -Encoding ASCII
   [IO.File]::WriteAllText((Join-Path $stage 'armed'),'test')
   [IO.File]::WriteAllText((Join-Path $stage 'progress.txt'),'Already installed and running.')
  }
  if($case.Deadline){@{Deadline=(Get-Date).AddMinutes($case.Deadline).ToString('o');InstallerPid=2147483600;InstallerPath='not-a-real-process';InstallerStarted=(Get-Date).ToString('o')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $stage 'installing.json') -Encoding ASCII}
  $invoke=if($case.Action-eq'Install'){'& '+("'"+(Join-Path $package 'scripts\Install.ps1').Replace("'","''")+"'")+' -PackageRoot '+("'"+$package.Replace("'","''")+"'")+' -RunId test-run'}elseif($case.Action-eq'Bootstrap'){'& '+("'"+(Join-Path $package 'scripts\Bootstrap.ps1').Replace("'","''")+"'")+' -Action Install'}else{'& '+("'"+(Join-Path $stage ('scripts\'+$case.Action+'.ps1')).Replace("'","''")+"'")+' -RunId test-run'+$(if($case.Deadline){' -Deadline'})}
  $wrapper='$env:MKB_FLOW_ROOT='+ ("'"+$fixture.Replace("'","''")+"'")+[Environment]::NewLine+'$env:ProgramData=Join-Path $env:MKB_FLOW_ROOT ''ProgramData'''+[Environment]::NewLine+'. (Join-Path $env:MKB_FLOW_ROOT ''InstallerPlatform.ps1'')'+[Environment]::NewLine+'try {'+[Environment]::NewLine+$invoke+[Environment]::NewLine+'$code=$LASTEXITCODE'+[Environment]::NewLine+'} finally {'+[Environment]::NewLine+'[ordered]@{Service=$global:MkbFlow.Service;Tasks=@($global:MkbTasks.Keys);Starts=$global:MkbStarts;Observes=$global:MkbObserve}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $env:MKB_FLOW_ROOT ''observed.json'') -Encoding ASCII'+[Environment]::NewLine+'}'+[Environment]::NewLine+'exit $code'
  $entry=Join-Path $fixture 'Run.ps1';[IO.File]::WriteAllText($entry,('$ErrorActionPreference=''Stop'''+[Environment]::NewLine+$wrapper),[Text.UTF8Encoding]::new($true))
  $start=New-Object Diagnostics.ProcessStartInfo
  $start.FileName="$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
  $start.Arguments='-NoProfile -ExecutionPolicy Bypass -File "'+$entry+'"'
  $start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
  $process=New-Object Diagnostics.Process;$process.StartInfo=$start
  try {
   [void]$process.Start();$out=$process.StandardOutput.ReadToEndAsync();$err=$process.StandardError.ReadToEndAsync()
   if(-not$process.WaitForExit(15000)){try{$process.Kill()}catch{};throw ('Test process timeout: '+$case.Name)}
   [void]$out.Wait(5000);[void]$err.Wait(5000)
   $code=$process.ExitCode;$details=$out.Result+$err.Result
  }finally{$process.Dispose()}
  if(-not(Test-Path -LiteralPath (Join-Path $fixture 'observed.json'))){throw ($case.Name+': test host failed before recording: '+$details)}
  $observed=Get-Content -LiteralPath (Join-Path $fixture 'observed.json') -Raw|ConvertFrom-Json
  $events=@(Get-Content -LiteralPath (Join-Path $fixture 'events.txt') -ErrorAction SilentlyContinue)
  Assert (($code-eq 0)-eq[bool]$case.Success) ($case.Name+': unexpected exit '+$code+' '+$details)
  if($case.Restore){Assert ($observed.Service-eq'HidUsb') ($case.Name+': ordinary typing must be restored.')}
  if($case.KeepFiles){Assert (Test-Path -LiteralPath (Join-Path $stage 'app\MagicKeyboardBridge.exe')) ($case.Name+': preserve recovery executable on failure.')}
  if($case.Action-eq'Install') {
   $result=Get-Content -LiteralPath (Join-Path $stage 'install-result.json') -Raw|ConvertFrom-Json
   Assert ($result.RunId-eq'test-run') ($case.Name+': result must correlate to this invocation.')
   if($case.KeepGuard){Assert (@($observed.Tasks)-contains'MagicKeyboardBridgeRollback') ($case.Name+': failed recovery must retain independent rollback.')}
   else{Assert (@($observed.Tasks)-notcontains'MagicKeyboardBridgeRollback') ($case.Name+': completed/failed pre-takeover operation must not retain a deadline guard.')}
   if($case.Name-eq'install-success') {
    Assert ($result.Status-eq'Installed'-and$result.WatchdogVerified) 'Success must include a scheduled watchdog check.'
    Assert ($observed.Starts-eq 2-and$observed.Observes-eq 2) 'Installation verifies input before and after restarting the worker.'
    Assert ([array]::IndexOf($events,'app:gate')-lt[array]::IndexOf($events,'app:bind')) 'Virtual gate precedes physical takeover.'
   }elseif($case.Name-in@('install-already','install-foreign-task')){Assert ($events-notcontains'app:bind'-and$events-notcontains'app:restore-device') 'Already-installed success preserves the working bridge.'}
   elseif(-not$case.Existing-and-not$case.Restore-and-not$case.KeepGuard){Assert ($events-notcontains'app:bind') ($case.Name+': failed preflight must not bind the physical keyboard.')}
  }
  if($case.Name-in@('install-disabled','install-missing','install-no-startup','install-trigger-disabled','install-user')){Assert ($events-contains'app:restore-device'-and$events-contains'app:bind'-and$observed.Starts-eq 2) ($case.Name+': incomplete startup configuration must be repaired rather than accepted.')}
  if($case.Name-eq'uninstall-success'){Assert (-not(Test-Path -LiteralPath (Join-Path $stage 'app'))) 'Uninstall removes app only after restoration.';Assert ([array]::IndexOf($events,'app:restore-device')-lt[array]::IndexOf($events,'app:remove-virtual')) 'Restore precedes virtual driver removal.'}
  if($case.Name-eq'deadline-early'){Assert ($events-notcontains'app:restore-device') 'A deadline guard must not restore before its deadline.'}
  if($case.Name-eq'restore-untrusted'){Assert (-not(Test-Path -LiteralPath (Join-Path $stage 'recovery.json'))) 'Untrusted stages must not be written even in the error handler.'}
  $passed++
 }
 [ordered]@{Status='Passed';Scenarios=$passed;Checks=$checks;PhysicalDeviceChanged=$false;TasksAndDrivers='Mocked';PowerShellVersion=$PSVersionTable.PSVersion.ToString()}|ConvertTo-Json -Compress
}finally{
 # Keep failed fixtures for diagnosis; all are inside the ignored .cache tree.
 if($passed-eq$cases.Count) {
  $absolute=[IO.Path]::GetFullPath($parent);$prefix=[IO.Path]::GetFullPath((Join-Path $Root '.cache\flow-tests'))+'\'
  if(-not$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup path.'}
  foreach($item in Get-ChildItem -LiteralPath $absolute -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected fixture link.'}}
  Remove-Item -LiteralPath $absolute -Recurse -Force
 }
}
