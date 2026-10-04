# Loaded ONLY into copied scripts by Test-InstallerFlows.ps1.
# Production scripts have no test switch or dependency on this file.
if(-not $env:MKB_FLOW_ROOT){throw 'Isolated test root is required.'}
if(-not $global:MkbFlow){
 $global:MkbFlow=Get-Content -LiteralPath (Join-Path $env:MKB_FLOW_ROOT 'scenario.json') -Raw|ConvertFrom-Json
 $global:MkbTasks=@{};$global:MkbObserve=0;$global:MkbStarts=0;$global:MkbSession=1
}
function Trace-Flow([string]$Event){[IO.File]::AppendAllText((Join-Path $env:MKB_FLOW_ROOT 'events.txt'),$Event+[Environment]::NewLine)}
function Get-Stage {Join-Path (Join-Path $env:MKB_FLOW_ROOT 'ProgramData') 'MagicKeyboardBridge'}
function Assert-Fixture([string]$Path){if(-not[IO.Path]::GetFullPath($Path).StartsWith([IO.Path]::GetFullPath($env:MKB_FLOW_ROOT)+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Test path escaped fixture.'}}
function Assert-Administrator {Trace-Flow 'administrator';if($global:MkbFlow.Fault-eq'admin'){throw 'Test administrator denial.'}}
function Assert-StageTrusted([string]$Path){Assert-Fixture $Path;if($global:MkbFlow.Fault-eq'untrusted'){throw 'Test untrusted stage.'}}
function Protect-Stage([string]$Path){Assert-Fixture $Path;Trace-Flow 'protect-stage'}
function Ensure-PinnedDependency([string]$PackageRoot){Assert-Fixture $PackageRoot;Trace-Flow 'dependency-mocked'}
function Stop-Process {param($Id,[switch]$Force);throw 'The isolated test must never terminate a host process.'}
function New-ScheduledTaskAction {param($Execute,$Argument,$WorkingDirectory);[pscustomobject]@{Execute=$Execute;Arguments=$Argument}}
function New-ScheduledTaskSettingsSet { [pscustomobject]@{TestOnly=$true} }
function New-ScheduledTaskTrigger {param([switch]$AtStartup,[switch]$Once,$At,$RepetitionInterval);[pscustomobject]@{Delay='';At=$At;Enabled=$true;CimClass=[pscustomobject]@{CimClassName=$(if($AtStartup){'MSFT_TaskBootTrigger'}else{'MSFT_TaskTimeTrigger'})}}}
function Register-ScheduledTask {
 param($TaskName,$Action,$Trigger,$Settings,$User,$RunLevel,[switch]$Force)
 Trace-Flow ('register:'+ $TaskName)
 if($global:MkbFlow.Fault-eq'register-worker'-and$TaskName-eq'MagicKeyboardBridgeWorker'){throw 'Injected task registration failure.'}
 $global:MkbTasks[$TaskName]=[pscustomobject]@{TaskName=$TaskName;Actions=@($Action);Triggers=@($Trigger);State='Ready';Principal=[pscustomobject]@{UserId=$User}}
}
function Get-ScheduledTask {param($TaskName,$ErrorAction);$global:MkbTasks[$TaskName]}
function Unregister-ScheduledTask {param($TaskName,$Confirm,$ErrorAction);Trace-Flow ('unregister:'+ $TaskName);$global:MkbTasks.Remove($TaskName)}
function Disable-ScheduledTask {param($TaskName,$ErrorAction);Trace-Flow ('disable:'+ $TaskName);if($global:MkbTasks.ContainsKey($TaskName)){$global:MkbTasks[$TaskName].State='Disabled'}}
function Enable-ScheduledTask {param($TaskName,$ErrorAction);Trace-Flow ('enable:'+ $TaskName);$global:MkbTasks[$TaskName].State='Ready'}
function Stop-ScheduledTask {param($TaskName,$ErrorAction);Trace-Flow ('stop:'+ $TaskName);if($global:MkbTasks.ContainsKey($TaskName)){$global:MkbTasks[$TaskName].State=$(if($global:MkbTasks[$TaskName].State-eq'Disabled'){'Disabled'}else{'Ready'})}}
function Start-Sleep {
 param($Milliseconds,$Seconds)
 if((Test-Path -LiteralPath (Join-Path (Get-Stage) 'stop'))-and$global:MkbTasks.ContainsKey('MagicKeyboardBridgeWorker')-and$global:MkbFlow.Fault-ne'restart-hang'){$global:MkbTasks['MagicKeyboardBridgeWorker'].State='Ready'}
 [Threading.Thread]::Sleep(1)
}
function Save-FlowJson([string]$Name,$Value){$Value|ConvertTo-Json -Depth 8|Set-Content -LiteralPath (Join-Path (Get-Stage) $Name) -Encoding UTF8}
function Start-ScheduledTask {
 param($TaskName)
 Trace-Flow ('start:'+ $TaskName)
 if($TaskName-eq'MagicKeyboardBridgeWorker') {
  $global:MkbStarts++;$global:MkbTasks[$TaskName].State='Running'
  $status=if($global:MkbFlow.Fault-eq'worker-start'){'Failed'}else{'Running'}
  Save-FlowJson 'status.json' @{Status=$status;Time=(Get-Date).ToString('o');SessionId=0;TestSigningActive=$false;ProcessId=(1000+$global:MkbStarts);Message='Injected worker state.'}
 }elseif($TaskName-eq'MagicKeyboardBridgeHealth') {
  if($global:MkbFlow.Fault-eq'watchdog-missing'){return}
  $global:MkbSession=0
  try{& (Join-Path (Get-Stage) 'scripts\Watchdog.ps1')}finally{$global:MkbSession=1}
 }
}
function Invoke-App {
 param([string[]]$Arguments,[int]$TimeoutSeconds=40,[switch]$AllowFailure)
 $command=$Arguments[0];Trace-Flow ('app:'+ $command)
 $failed=($global:MkbFlow.Fault-eq$command)-or($global:MkbFlow.Fault-eq'recovery-failure'-and$command-in@('observe','restore-device'))
 $value=@{Status='MockSuccess'}
 switch($command) {
  'preflight' {$service=if($global:MkbFlow.Fault-eq'foreign-driver'){'Foreign'}else{$global:MkbFlow.Service};$value=@{Devices=@(@{Service=$service;LowerFilters=@();Started=$true})}}
  'initialize' {Save-FlowJson 'settings.json' @{Owner='MagicKeyboardBridge';InstallationId='11111111111111111111111111111111';ControllerIndex=2048}}
  'prepare-drivers' {}
  'create-virtual' {}
  'gate' {}
  'bind' {$global:MkbFlow.Service='WinUSB'}
  'observe' {$global:MkbObserve++;if($global:MkbFlow.Fault-eq'observe-second'-and$global:MkbObserve-eq 2){$failed=$true}}
  'health' {
   $failed=$failed-or($global:MkbFlow.Fault-eq'watchdog-unhealthy'-and$global:MkbSession-eq 0)
   if($global:MkbFlow.Service-ne'WinUSB'){$failed=$true}
   $value=@{Status=$(if($failed){'Unhealthy'}else{'Healthy'});Time=(Get-Date).ToString('o');CheckerSessionId=$global:MkbSession;TestSigningActive=$false}
   Save-FlowJson 'health.json' $value
  }
  'restore-device' {if(-not$failed){$global:MkbFlow.Service='HidUsb'}}
  'remove-usb-package' {}
  'remove-virtual' {}
  default {throw ('Unexpected mock application command: '+$command)}
 }
 if($failed-and-not$AllowFailure){throw ('Injected failure: '+$command)}
 [pscustomobject]@{ExitCode=$(if($failed){1}else{0});Output=($value|ConvertTo-Json -Depth 6 -Compress);Error=''}
}
function Start-Process {
 param($FilePath,$ArgumentList,$Verb,$WindowStyle,[switch]$PassThru)
 if($Verb-ne'RunAs'-or$WindowStyle-ne'Hidden'){throw 'Unexpected test process launch.'}
 $match=[regex]::Match($ArgumentList,'-EncodedCommand\s+([A-Za-z0-9+/=]+)')
 if(-not$match.Success){throw 'Expected encoded launcher invocation.'}
 $decoded=[Text.Encoding]::Unicode.GetString([Convert]::FromBase64String($match.Groups[1].Value))
 $tokens=$null;$errors=$null;[Management.Automation.Language.Parser]::ParseInput($decoded,[ref]$tokens,[ref]$errors)|Out-Null
 if(@($errors).Count){throw 'Encoded launcher invocation does not parse.'}
 $run=[regex]::Match($decoded,'-RunId\s+([a-f0-9]{32})')
 if(-not$run.Success){throw 'Expected correlated launcher run ID.'}
 Trace-Flow 'elevation-mocked'
 if($global:MkbFlow.Fault-ne'bootstrap-no-result'){
  $id=if($global:MkbFlow.Fault-eq'bootstrap-stale'){'old-run'}else{$run.Groups[1].Value}
  $status=if($global:MkbFlow.Fault-eq'bootstrap-failure'){'InstallNeedsReview'}else{'Installed'}
  Save-FlowJson 'install-result.json' @{Status=$status;RunId=$id;Time=(Get-Date).ToString('o')}
 }
 $process=New-Object psobject
 $process|Add-Member ScriptMethod WaitForExit {param($timeout);$true}
 $process
}
if($global:MkbFlow.Existing-and-not$global:MkbTasks.ContainsKey('MagicKeyboardBridgeWorker')) {
 $stage=Get-Stage;$windowsPS="$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
 $boot=[pscustomobject]@{Enabled=$true;CimClass=[pscustomobject]@{CimClassName='MSFT_TaskBootTrigger'}}
 $global:MkbTasks['MagicKeyboardBridgeWorker']=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=(Join-Path $stage 'app\MagicKeyboardBridge.exe');Arguments=('worker --stage "'+$stage+'"')});State='Running';Triggers=@($boot);Principal=[pscustomobject]@{UserId='S-1-5-18'}}
 $global:MkbTasks['MagicKeyboardBridgeHealth']=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=$windowsPS;Arguments=('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "'+(Join-Path $stage 'scripts\Watchdog.ps1')+'"')});State='Ready';Triggers=@($boot);Principal=[pscustomobject]@{UserId='S-1-5-18'}}
 if($global:MkbFlow.Fault-eq'trigger-disabled'){$boot.Enabled=$false}
 if($global:MkbFlow.Fault-eq'task-user'){$global:MkbTasks['MagicKeyboardBridgeWorker'].Principal.UserId='S-1-5-19'}
 if($global:MkbFlow.Fault-eq'task-disabled'){$global:MkbTasks['MagicKeyboardBridgeHealth'].State='Disabled'}
 if($global:MkbFlow.Fault-eq'task-missing'){$global:MkbTasks.Remove('MagicKeyboardBridgeHealth')}
 if($global:MkbFlow.Fault-eq'task-no-startup'){$global:MkbTasks['MagicKeyboardBridgeWorker'].Triggers=@()}
 if($global:MkbFlow.Fault-eq'task-foreign'){$global:MkbTasks['MagicKeyboardBridgeWorker'].Actions[0].Execute='foreign.exe'}
}
