param([string]$Root=(Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference='Stop'
. (Join-Path $Root 'scripts\Common.ps1')
. (Join-Path $Root 'scripts\Dependencies.ps1')
$spec=Get-CoreDependency;$checks=0;$downloads=0
function Assert([bool]$Value,[string]$Message){$script:checks++;if(-not$Value){throw $Message}}
Assert ($spec.ArchiveHash-match'^[A-Fa-f0-9]{64}$') 'Production archive pin must be a valid SHA256.'
Assert ($spec.CoreHash-match'^[A-Fa-f0-9]{64}$') 'Production DLL pin must be a valid SHA256.'
$fixture=Join-Path $Root ('.cache\dependency-tests\'+[guid]::NewGuid().ToString('N'))
$package=Join-Path $fixture 'package';$app=Join-Path $package 'app';$cache=Join-Path $package '.download-cache'
New-Item -ItemType Directory -Path $app,$cache -Force|Out-Null
$manifestPath=Join-Path $package 'package-manifest.json'
@{Product='MagicKeyboardBridge';Version='test';Files=@()}|ConvertTo-Json|Set-Content -LiteralPath $manifestPath -Encoding ASCII
$core=Join-Path $app 'HIDMaestro.Core.dll';$vendor=Join-Path $Root 'vendor\HIDMaestro.Core.dll'
Assert ((Get-FileHash -LiteralPath $vendor).Hash-eq$spec.CoreHash) 'Tests require the genuinely pinned upstream DLL.'
function Receive-Dependency([string]$Url,[string]$Destination) {
 if($Url-ne$spec.Url){throw 'Unexpected dependency URL.'}
 $script:downloads++;Copy-Item -LiteralPath $script:downloadSource -Destination $Destination
}
try {
 Copy-Item -LiteralPath $vendor -Destination $core
 Ensure-PinnedDependency $package
 Assert ($downloads-eq 0) 'Valid existing dependency must not download.'
 Ensure-PinnedDependency $package
 $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 Assert (@($manifest.Files).Count-eq 1) 'Repeated setup must not duplicate the manifest dependency.'
 Assert ($manifest.Version-eq'test'-and$manifest.Files[0].SHA256-eq$spec.CoreHash) 'Existing manifest data is preserved with the pinned hash.'
 # Generate a small fixture archive around the authentic DLL. Override only
 # the expected archive hash; production URL and DLL identity remain pinned.
 Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
 $sourceZip=Join-Path $fixture 'upstream-fixture.zip'
 $zip=[IO.Compression.ZipFile]::Open($sourceZip,[IO.Compression.ZipArchiveMode]::Create)
 try{[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$vendor,'../../HIDMaestro.Core.dll',[IO.Compression.CompressionLevel]::NoCompression)|Out-Null}finally{$zip.Dispose()}
 $script:testSpec=[pscustomobject]@{Url=$spec.Url;CoreHash=$spec.CoreHash;ArchiveHash=(Get-FileHash -LiteralPath $sourceZip).Hash}
 function Get-CoreDependency {$script:testSpec}
 $script:testSpec.ArchiveHash+='160'
 $rejected=$false;try{Ensure-PinnedDependency $package}catch{$rejected=$true}
 Assert ($rejected-and$downloads-eq 0) 'Malformed pin must be rejected before any download or cached DLL use.'
 $script:testSpec.ArchiveHash=(Get-FileHash -LiteralPath $sourceZip).Hash
 $script:downloadSource=$sourceZip
 Remove-Item -LiteralPath $core
 Ensure-PinnedDependency $package
 Assert ($downloads-eq 1) 'Missing dependency downloads exactly once.'
 Assert ((Get-FileHash -LiteralPath $core).Hash-eq$spec.CoreHash) 'Extracted dependency must match the independent DLL hash.'
 Assert (-not(Test-Path -LiteralPath (Join-Path $fixture 'HIDMaestro.Core.dll'))) 'Archive paths cannot write outside the app directory.'
 [IO.File]::WriteAllText($core,'corrupted')
 Ensure-PinnedDependency $package
 Assert ($downloads-eq 1-and(Get-FileHash -LiteralPath $core).Hash-eq$spec.CoreHash) 'A valid cache repairs a corrupted DLL without another download.'
 $archive=Join-Path $cache 'HIDMaestro-v1.10.0.zip'
 [IO.File]::WriteAllText($archive,'bad archive')
 Remove-Item -LiteralPath $core
 $bad=Join-Path $fixture 'bad-download.zip';[IO.File]::WriteAllText($bad,'bad download');$script:downloadSource=$bad
 $rejected=$false;try{Ensure-PinnedDependency $package}catch{$rejected=$true}
 Assert $rejected 'Checksum mismatch must stop setup.'
 Assert (-not(Test-Path -LiteralPath $core)) 'Checksum failure must never publish the dependency DLL.'
 Assert (@(Get-ChildItem -LiteralPath $cache -Filter '*.part').Count-eq 0) 'Failed downloads must not leave partial files.'
 $badZip=Join-Path $fixture 'bad-payload.zip'
 $zip=[IO.Compression.ZipFile]::Open($badZip,[IO.Compression.ZipArchiveMode]::Create)
 try{[IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,$bad,'HIDMaestro.Core.dll')|Out-Null}finally{$zip.Dispose()}
 $script:testSpec.ArchiveHash=(Get-FileHash -LiteralPath $badZip).Hash;$script:downloadSource=$badZip
 $rejected=$false;try{Ensure-PinnedDependency $package}catch{$rejected=$true}
 Assert $rejected 'A matching archive hash cannot bypass the independent DLL hash.'
 Assert (-not(Test-Path -LiteralPath $core)) 'Invalid inner payload must not become executable.'
 Assert (@(Get-ChildItem -LiteralPath $app -Filter '*.tmp').Count-eq 0) 'Failed extraction must clean its temporary DLL.'
 [ordered]@{Status='Passed';Checks=$checks;Network='Mocked';PhysicalDeviceChanged=$false}|ConvertTo-Json -Compress
}finally{
 $absolute=[IO.Path]::GetFullPath($fixture);$prefix=[IO.Path]::GetFullPath((Join-Path $Root '.cache\dependency-tests'))+'\'
 if(-not$absolute.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase)){throw 'Unsafe fixture cleanup.'}
 foreach($item in Get-ChildItem -LiteralPath $absolute -Recurse -Force){if($item.Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Unexpected fixture link.'}}
 Remove-Item -LiteralPath $absolute -Recurse -Force
}
