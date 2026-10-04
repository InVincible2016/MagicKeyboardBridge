param([string]$Root=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
$checks=0
function Assert([bool]$Value,[string]$Message){$script:checks++;if(-not $Value){throw $Message}}
foreach($file in Get-ChildItem -LiteralPath (Join-Path $Root 'scripts'),(Join-Path $Root 'tools') -Filter '*.ps1' -Recurse) {
 $tokens=$null;$errors=$null
 [Management.Automation.Language.Parser]::ParseFile($file.FullName,[ref]$tokens,[ref]$errors)|Out-Null
 Assert (@($errors).Count-eq 0) ('PowerShell syntax: '+$file.Name+' '+($errors|Out-String))
 Assert (-not([IO.File]::ReadAllBytes($file.FullName)|Where-Object{$_-gt 127})) ('PowerShell 5 source must remain ASCII: '+$file.Name)
}
. (Join-Path $Root 'scripts\Common.ps1')
$fixture=Join-Path ([IO.Path]::GetTempPath()) ('MagicKeyboardBridge-script-tests-'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture|Out-Null
try {
 function Get-Stage {$fixture}
 function Get-ScheduledTask {param($TaskName,$ErrorAction);$script:fakeTask}
 $script:fakeTask=$null;Assert-TaskOwner MagicKeyboardBridgeWorker;Assert $true 'Missing task allowed.'
 $script:fakeTask=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=(Join-Path $fixture 'app\MagicKeyboardBridge.exe');Arguments=('worker --stage "'+$fixture+'"')})}
 Assert-TaskOwner MagicKeyboardBridgeWorker;Assert $true 'Exact owned worker accepted.'
 $script:fakeTask.Actions[0].Execute='unrelated.exe'
 $rejected=$false;try{Assert-TaskOwner MagicKeyboardBridgeWorker}catch{$rejected=$true}
 Assert $rejected 'A task merely mentioning the stage must not count as owned.'
 $script:fakeTask=[pscustomobject]@{Actions=@([pscustomobject]@{Execute=(Join-Path $fixture 'app\MagicKeyboardBridge.exe');Arguments=('worker --stage "'+$fixture+'"')},[pscustomobject]@{Execute='other.exe';Arguments=''})}
 $rejected=$false;try{Assert-TaskOwner MagicKeyboardBridgeWorker}catch{$rejected=$true}
 Assert $rejected 'A task with an extra action must be rejected.'
 Assert-SafeDirectory $fixture;Assert $true 'Ordinary fixture directory accepted.'
 $target=Join-Path $fixture 'real';New-Item -ItemType Directory -Path $target|Out-Null
 $link=Join-Path $fixture 'link';New-Item -ItemType Junction -Path $link -Target $target|Out-Null
 $rejected=$false;try{Assert-SafeDirectory (Join-Path $link 'missing')}catch{$rejected=$true}
 Assert $rejected 'A junction ancestor must be rejected, including for absent children.'
 # Remove only the junction itself; never recursively traverse a reparse point.
 [IO.Directory]::Delete($link)
 Set-Progress 'Step completed.'
 Assert ((Get-Content -LiteralPath (Join-Path $fixture 'progress.txt') -Raw)-eq'Step completed.') 'Progress preserves exact completion text.'
}finally{
 if(Test-Path -LiteralPath (Join-Path $fixture 'link')){[IO.Directory]::Delete((Join-Path $fixture 'link'))}
 $absolute=[IO.Path]::GetFullPath($fixture)
 if(-not $absolute.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe test cleanup path.'}
 foreach($item in Get-ChildItem -LiteralPath $absolute -Force -Recurse){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected test link.'}}
 Remove-Item -LiteralPath $absolute -Recurse -Force
}
[ordered]@{Status='Passed';Checks=$checks;PowerShellVersion=$PSVersionTable.PSVersion.ToString();PhysicalDeviceChanged=$false}|ConvertTo-Json -Compress
