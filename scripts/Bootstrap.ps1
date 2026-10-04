param([ValidateSet('Install','Restore','Uninstall','Status')][string]$Action='Install',[switch]$KeepOptionCommand)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$stage=Join-Path $env:ProgramData 'MagicKeyboardBridge'
try {
 if($Action-eq'Status') {
  $path=Join-Path $stage 'status.json'
  if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path}else{Write-Output 'Not installed.'}
  exit 0
 }
 if($Action-eq'Install'-and-not(Test-Path -LiteralPath (Join-Path $root 'app\MagicKeyboardBridge.exe'))) {
  Write-Host 'Building the source package. The first build downloads pinned dependencies.'
  $runtime=if($env:PROCESSOR_ARCHITECTURE-eq'ARM64'){'win-arm64'}else{'win-x64'}
  & (Join-Path $root 'tools\Build.ps1') -Runtime $runtime
  if($LASTEXITCODE-ne 0){throw 'Source package build failed.'}
  $cachedDependency=Join-Path $root 'vendor\HIDMaestro.Core.dll'
  $root=Join-Path $root ('dist\MagicKeyboardBridge-'+$runtime)
  Copy-Item -LiteralPath $cachedDependency -Destination (Join-Path $root 'app\HIDMaestro.Core.dll') -Force
 }
 if($Action-eq'Install'-and(Test-Path -LiteralPath $stage)-and-not(Test-Path -LiteralPath (Join-Path $stage 'package-owner.txt'))){throw 'An older or unrelated bridge occupies the installation directory. It was left unchanged. This package cannot upgrade the early prototype.'}
 if($Action-eq'Install') {
  . (Join-Path $PSScriptRoot 'Common.ps1')
  . (Join-Path $PSScriptRoot 'Dependencies.ps1')
  Ensure-PinnedDependency $root
 }
 $script=if($Action-eq'Install'){Join-Path $root 'scripts\Install.ps1'}elseif($Action-eq'Restore'){Join-Path $stage 'scripts\Recover.ps1'}else{Join-Path $stage 'scripts\Uninstall.ps1'}
 if(-not(Test-Path -LiteralPath $script)){throw 'Installed recovery files are unavailable.'}
 $windowsPS="$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe"
 # Encode the invocation so spaces and apostrophes in the extraction directory
 # cannot become PowerShell or CMD syntax. No credentials are included.
 $literal="'"+$script.Replace("'","''")+"'"
 $runId=[guid]::NewGuid().ToString('N')
 $invoke='& '+$literal+' -RunId '+$runId
 if($Action-eq'Install'){$invoke+=' -PackageRoot '+("'"+$root.Replace("'","''")+"'");if($KeepOptionCommand){$invoke+=' -KeepOptionCommand'}}
 $invoke+='; exit $LASTEXITCODE'
 $encoded=[Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($invoke))
 $start=Get-Date
 Write-Host 'Accept the Windows administrator prompt. Keep the keyboard connected.'
 $child=Start-Process -FilePath $windowsPS -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -EncodedCommand '+$encoded) -Verb RunAs -WindowStyle Hidden -PassThru
 $previous='';$lastNotice=Get-Date
 while(-not $child.WaitForExit(1000)) {
  $progress=Get-Content -LiteralPath (Join-Path $stage 'progress.txt') -Raw -ErrorAction SilentlyContinue
  if($progress-and$progress-ne$previous){Write-Host $progress;$previous=$progress;$lastNotice=Get-Date}
  if(((Get-Date)-$lastNotice).TotalSeconds-ge 15){Write-Host 'Setup is still running; waiting for its current step...';$lastNotice=Get-Date}
  if(((Get-Date)-$start).TotalSeconds-gt 420){throw 'Setup has exceeded its time limit. The independent recovery task protects typing after keyboard takeover. See Status.cmd and Restore.cmd.'}
 }
 if($Action-eq'Install') {
  $resultPath=Join-Path $stage 'install-result.json'
  $result=Get-Content -LiteralPath $resultPath -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json
  if($result-and$result.RunId-eq$runId){Write-Output ($result|ConvertTo-Json);if($result.Status-in@('Installed','AlreadyInstalled')){exit 0}}
 }else{
  $name=if($Action-eq'Restore'){'recovery.json'}else{'uninstall-result.json'}
  $result=Get-Content -LiteralPath (Join-Path $stage $name) -Raw -ErrorAction SilentlyContinue|ConvertFrom-Json
  if($result-and$result.RunId-eq$runId){Write-Output ($result|ConvertTo-Json);if($result.Status-in@('MicrosoftKeyboardRestored','Uninstalled')){exit 0}}
 }
 throw 'Setup did not return a successful result. See the message above.'
}catch{[Console]::Error.WriteLine(($_|Out-String));exit 1}
