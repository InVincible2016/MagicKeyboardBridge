param([Parameter(Mandatory)][string]$PackageRoot,[switch]$DisposableVM)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\Common.ps1')
. (Join-Path $root 'scripts\Dependencies.ps1')
Assert-Administrator
if(-not$DisposableVM){throw 'This test changes drivers and certificate stores. Use -DisposableVM only on a disposable Windows VM.'}
$model=(Get-CimInstance Win32_ComputerSystem).Model
if($model-notmatch 'Virtual Machine|VirtualBox|VMware|KVM|QEMU'){throw ('Disposable virtual machine required; detected model: '+$model)}
$normalStage=Join-Path $env:ProgramData 'MagicKeyboardBridge'
if(Test-Path -LiteralPath $normalStage){throw 'An existing bridge installation is present; integration test refused.'}
foreach($key in Get-ChildItem -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Enum\ROOT\HIDCLASS' -ErrorAction SilentlyContinue) {
 if(@((Get-ItemProperty -LiteralPath $key.PSPath -ErrorAction SilentlyContinue).HardwareID)-contains'root\MagicKeyboardBridge'){throw 'An existing project virtual keyboard is present; integration test refused.'}
}
$integrationStage=Join-Path $env:ProgramData ('MagicKeyboardBridgeIntegration-'+[guid]::NewGuid().ToString('N'))
function Get-Stage {$script:integrationStage}
$resultPath=Join-Path $PackageRoot 'virtual-driver-validation.json'
$prepared=$false;$cleaned=$false;$passed=$false;$errorText='';$started=Get-Date
try {
 # Build has already validated this local dependency. Reuse it instead of
 # downloading the same archive a second time in a disposable CI runner.
 $vendor=Join-Path $root 'vendor\HIDMaestro.Core.dll'
 if(Test-Path -LiteralPath $vendor){Copy-Item -LiteralPath $vendor -Destination (Join-Path $PackageRoot 'app\HIDMaestro.Core.dll') -Force}
 Ensure-PinnedDependency $PackageRoot
 Assert-SafeDirectory $PackageRoot
 $manifest=Get-Content -LiteralPath (Join-Path $PackageRoot 'package-manifest.json') -Raw|ConvertFrom-Json
 $prefix=[IO.Path]::GetFullPath($PackageRoot).TrimEnd('\')+'\'
 New-Item -ItemType Directory -Path $integrationStage|Out-Null
 Protect-Stage $integrationStage
 foreach($entry in $manifest.Files) {
  $source=[IO.Path]::GetFullPath((Join-Path $PackageRoot $entry.Path))
  $destination=[IO.Path]::GetFullPath((Join-Path $integrationStage $entry.Path))
  if(-not$source.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)-or-not$destination.StartsWith($integrationStage+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'Invalid test package path.'}
  Assert-SafeDirectory (Split-Path -Parent $source)
  if((Get-Item -LiteralPath $source -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Test package link rejected.'}
  if((Get-FileHash -LiteralPath $source).Hash-ne$entry.SHA256){throw 'Test package checksum mismatch.'}
  New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force|Out-Null
  Copy-Item -LiteralPath $source -Destination $destination
 }
 $preflight=Invoke-App @('preflight')
 Invoke-App @('initialize','--stage',$integrationStage)|Out-Null
 $prepared=$true
 Invoke-App @('prepare-drivers','--stage',$integrationStage) -TimeoutSeconds 150|Out-Null
 Invoke-App @('create-virtual','--stage',$integrationStage) -TimeoutSeconds 45|Out-Null
 $gate=(Invoke-App @('gate','--stage',$integrationStage) -TimeoutSeconds 45).Output|ConvertFrom-Json
 if($gate.Status-ne'VirtualInputPassed'){throw 'Virtual input did not pass.'}
 $passed=$true
}catch{$errorText=$_|Out-String}
finally {
 if($prepared) {
  try{Invoke-App @('remove-virtual','--stage',$integrationStage) -TimeoutSeconds 90|Out-Null;$cleaned=$true}
  catch{$errorText+=' Cleanup failed: '+($_|Out-String);$passed=$false}
 }
 $result=[ordered]@{Status=$(if($passed-and$cleaned){'Passed'}else{'Failed'});Started=$started.ToString('o');Finished=(Get-Date).ToString('o');OS=[Environment]::OSVersion.Version.ToString();Model=$model;SessionId=(Get-Process -Id $PID).SessionId;VirtualInputVerified=$passed;CleanupVerified=$cleaned;PhysicalDriverChanged=$false;BootSettingsChanged=$false;GameCompatibilityVerified=$false;Message=$errorText}
 $result|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $resultPath -Encoding UTF8
 $result|ConvertTo-Json -Depth 6
 if($cleaned-and(Test-Path -LiteralPath $integrationStage)) {
  $absolute=[IO.Path]::GetFullPath($integrationStage)
  if(-not$absolute.StartsWith(([IO.Path]::GetFullPath($env:ProgramData)+'\MagicKeyboardBridgeIntegration-'),[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe integration cleanup path.'}
  Assert-SafeDirectory $absolute
  foreach($item in Get-ChildItem -LiteralPath $absolute -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected integration link.'}}
  Remove-Item -LiteralPath $absolute -Recurse -Force
 }
}
if(-not$passed-or-not$cleaned){exit 1}
