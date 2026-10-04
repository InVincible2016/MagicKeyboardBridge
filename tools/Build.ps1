param([ValidateSet('win-x64','win-arm64')][string]$Runtime='win-x64',[string]$DotNet)
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'scripts\Dependencies.ps1')
$dependency=Get-CoreDependency
Push-Location -LiteralPath $root
try {
$cache=Join-Path $root '.cache'
New-Item -ItemType Directory -Path $cache -Force|Out-Null
[Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
function Download-Checked([string]$Url,[string]$Path,[string]$Hash,[string]$Algorithm='SHA256') {
 $length=if($Algorithm-eq'SHA512'){128}else{64}
 if($Hash-notmatch ('^[A-Fa-f0-9]{'+$length+'}$')){throw 'Invalid dependency checksum format.'}
 if((Test-Path -LiteralPath $Path)-and(Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash-eq$Hash){return}
 $client=New-Object Net.WebClient
 try{$client.DownloadFile($Url,$Path)}finally{$client.Dispose()}
 $actual=(Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash
 if($actual-ne$Hash){Remove-Item -LiteralPath $Path;throw ('Dependency checksum mismatch for '+[IO.Path]::GetFileName($Path)+': expected '+$Hash+', got '+$actual)}
}
$vendor=Join-Path $root 'vendor\HIDMaestro.Core.dll'
$coreHash=$dependency.CoreHash
if(-not(Test-Path -LiteralPath $vendor)) {
 $zip=Join-Path $cache 'HIDMaestro-v1.10.0.zip'
 Download-Checked $dependency.Url $zip $dependency.ArchiveHash
 $expanded=Join-Path $cache 'hidmaestro'
 Expand-Archive -LiteralPath $zip -DestinationPath $expanded -Force
 $candidate=@(Get-ChildItem -LiteralPath $expanded -Recurse -Filter HIDMaestro.Core.dll|Where-Object{(Get-FileHash -LiteralPath $_.FullName).Hash-eq$coreHash})|Select-Object -First 1
 if(-not $candidate){throw 'Pinned HIDMaestro DLL missing from archive.'}
 New-Item -ItemType Directory -Path (Split-Path -Parent $vendor) -Force|Out-Null
 Copy-Item -LiteralPath $candidate.FullName -Destination $vendor
}
if((Get-FileHash -LiteralPath $vendor).Hash-ne$coreHash){throw 'Pinned HIDMaestro DLL hash mismatch.'}
if(-not $DotNet) {
 $command=Get-Command dotnet -ErrorAction SilentlyContinue
 if($command){$sdks=& $command.Source --list-sdks 2>$null;if($LASTEXITCODE-eq 0-and@($sdks|Where-Object{$_-match '^10\.0\.4\d\d\s'}).Count){$DotNet=$command.Source}}
 if(-not $DotNet) {
  $sdkArch=if($env:PROCESSOR_ARCHITECTURE-eq'ARM64'){'arm64'}else{'x64'}
  $hash=if($sdkArch-eq'arm64'){'8272EAAB6F06AD658B1976E19D88BEED287A601F968B71D5C26B75D10587CF087665C599D2E136A911002F955C97F59AA8692581BBF6B8E7AF5F82604C810256'}else{'24B670AD3D923BFCF47DF6C3B034152398B42F6DBC388E10D783AEE1CFB5E5817D399FC0AE2A12CFA822A55E61D34830CCB15C50EF6EFEE437AB874BB7C79430'}
  $zip=Join-Path $cache ('dotnet-sdk-10.0.401-win-'+$sdkArch+'.zip')
  Download-Checked ('https://builds.dotnet.microsoft.com/dotnet/Sdk/10.0.401/dotnet-sdk-10.0.401-win-'+$sdkArch+'.zip') $zip $hash 'SHA512'
  $sdk=Join-Path $cache ('sdk-'+$sdkArch)
  if(-not(Test-Path -LiteralPath (Join-Path $sdk 'dotnet.exe'))){Expand-Archive -LiteralPath $zip -DestinationPath $sdk -Force}
  $DotNet=Join-Path $sdk 'dotnet.exe'
 }
}
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1';$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false';$env:DOTNET_CLI_HOME=Join-Path $cache 'dotnet-home'
$destination=Join-Path $root ('dist\MagicKeyboardBridge-'+$Runtime)
# Only the generated output for this selected runtime may be replaced.
. (Join-Path $root 'scripts\Common.ps1')
$absolute=[IO.Path]::GetFullPath($destination)
$distPrefix=[IO.Path]::GetFullPath((Join-Path $root 'dist'))+'\'
if(-not $absolute.StartsWith($distPrefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe build destination.'}
if(Test-Path -LiteralPath $absolute){Assert-SafeDirectory $absolute;foreach($item in Get-ChildItem -LiteralPath $absolute -Force -Recurse){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected build-output link.'}};Remove-Item -LiteralPath $absolute -Recurse -Force}
New-Item -ItemType Directory -Path $absolute|Out-Null
& "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Test-Scripts.ps1')
if($LASTEXITCODE-ne 0){throw 'Windows PowerShell script tests failed.'}
& "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Test-InstallerFlows.ps1')
if($LASTEXITCODE-ne 0){throw 'Isolated installer flow tests failed.'}
& "$env:windir\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tests\Test-DependencyBootstrap.ps1')
if($LASTEXITCODE-ne 0){throw 'Dependency bootstrap tests failed.'}
& $DotNet run --project (Join-Path $root 'tests\MagicKeyboardBridge.Tests.csproj') -c Release
if($LASTEXITCODE-ne 0){throw 'Mapping and ownership regression tests failed.'}
& $DotNet publish (Join-Path $root 'src\MagicKeyboardBridge\MagicKeyboardBridge.csproj') -c Release -r $Runtime --self-contained true -o (Join-Path $destination 'app') --nologo
if($LASTEXITCODE-ne 0){throw 'Publish failed.'}
# The public archive includes our app and .NET runtime. Fetch the upstream
# aggregate DLL directly from its publisher at first install, before UAC.
$payload=Join-Path $destination 'app\HIDMaestro.Core.dll'
if(Test-Path -LiteralPath $payload){Remove-Item -LiteralPath $payload -Force}
foreach($folder in @('scripts','licenses','docs')){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $destination -Recurse -Force}
foreach($name in @('Install.cmd','Restore.cmd','Uninstall.cmd','Status.cmd','LICENSE','README.md','README.zh-CN.md')){Copy-Item -LiteralPath (Join-Path $root $name) -Destination $destination -Force}
$files=@(Get-ChildItem -LiteralPath $destination -Recurse -File|Where-Object{$_.Name-ne'package-manifest.json'}|ForEach-Object{[ordered]@{Path=$_.FullName.Substring($destination.Length+1);SHA256=(Get-FileHash -LiteralPath $_.FullName).Hash}})
[ordered]@{Product='MagicKeyboardBridge';Version='0.4.0';Runtime=$Runtime;Files=$files}|ConvertTo-Json -Depth 5|Set-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Encoding UTF8
$zip=$destination+'.zip'
Compress-Archive -Path (Join-Path $destination '*') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $zip|Format-List
Write-Output $zip

}finally{Pop-Location}
