$ErrorActionPreference='Stop'
function Get-CoreDependency {
 [pscustomobject]@{
  Url='https://github.com/hifihedgehog/HIDMaestro/releases/download/v1.10.0/HIDMaestro-v1.10.0.zip'
  ArchiveHash='24FAB064FF179917FD4FE6CCD83571783ADFA3CA976B7AD3C3E48A891790E216160'
  CoreHash='DA0BE0B400AE095CA694AADB94CFF390282B43EB318E6349B0BC2358222A6A94'
 }
}
function Receive-Dependency([string]$Url,[string]$Destination) {
 # The payload is obtained directly from its publisher, before elevation.
 # Both network idle time and total download time are bounded.
 [Net.ServicePointManager]::SecurityProtocol=[Net.SecurityProtocolType]::Tls12
 $request=[Net.HttpWebRequest]::Create($Url);$request.Timeout=30000;$request.ReadWriteTimeout=15000
 $response=$null;$input=$null;$output=$null;$clock=[Diagnostics.Stopwatch]::StartNew();$last=-1
 try {
  $response=$request.GetResponse();$input=$response.GetResponseStream();$output=[IO.File]::Create($Destination)
  $buffer=New-Object byte[] 131072;$received=0L
  while(($count=$input.Read($buffer,0,$buffer.Length))-gt 0) {
   $output.Write($buffer,0,$count);$received+=$count
   if($clock.Elapsed.TotalSeconds-gt 300){throw 'Dependency download exceeded five minutes. Run Install.cmd again to retry.'}
   $seconds=[int]$clock.Elapsed.TotalSeconds
   if($seconds-ne$last){Write-Host ('Downloading verified upstream dependency: {0:N1} MiB' -f ($received/1MB));$last=$seconds}
  }
 }finally{if($output){$output.Dispose()};if($input){$input.Dispose()};if($response){$response.Dispose()}}
}
function Ensure-PinnedDependency([string]$PackageRoot) {
 $spec=Get-CoreDependency
 $app=Join-Path $PackageRoot 'app';Assert-SafeDirectory $app
 $core=Join-Path $app 'HIDMaestro.Core.dll'
 if((Test-Path -LiteralPath $core)-and((Get-Item -LiteralPath $core -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Dependency must not be a link.'}
 if(-not(Test-Path -LiteralPath $core)-or(Get-FileHash -LiteralPath $core).Hash-ne$spec.CoreHash) {
  $cache=Join-Path $PackageRoot '.download-cache';Assert-SafeDirectory $cache
  New-Item -ItemType Directory -Path $cache -Force|Out-Null
  $archive=Join-Path $cache 'HIDMaestro-v1.10.0.zip'
  if((Test-Path -LiteralPath $archive)-and((Get-Item -LiteralPath $archive -Force).Attributes-band[IO.FileAttributes]::ReparsePoint)){throw 'Dependency archive must not be a link.'}
  if(-not(Test-Path -LiteralPath $archive)-or(Get-FileHash -LiteralPath $archive).Hash-ne$spec.ArchiveHash) {
   $temporary=$archive+'.'+[guid]::NewGuid().ToString('N')+'.part'
   try {
    Receive-Dependency $spec.Url $temporary
    if((Get-FileHash -LiteralPath $temporary).Hash-ne$spec.ArchiveHash){throw 'Upstream archive checksum mismatch; no dependency was executed.'}
    Move-Item -LiteralPath $temporary -Destination $archive -Force
   }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
  }
  Add-Type -AssemblyName System.IO.Compression,System.IO.Compression.FileSystem
  $zip=[IO.Compression.ZipFile]::OpenRead($archive)
  $temporary=$core+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
  try {
   $entries=@($zip.Entries|Where-Object{$_.Name-eq'HIDMaestro.Core.dll'})
   $found=$false
   foreach($entry in $entries) {
    $input=$entry.Open();$output=[IO.File]::Create($temporary)
    try{$input.CopyTo($output)}finally{$input.Dispose();$output.Dispose()}
    if((Get-FileHash -LiteralPath $temporary).Hash-eq$spec.CoreHash){$found=$true;break}
   }
   if(-not$found){throw 'The archive does not contain the pinned dependency.'}
   Move-Item -LiteralPath $temporary -Destination $core -Force
  }finally{$zip.Dispose();if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary -Force}}
 }
 # Only add the single independently pinned file to the package's copy list.
 $manifestPath=Join-Path $PackageRoot 'package-manifest.json'
 if((Get-Item -LiteralPath $manifestPath -Force).Attributes-band[IO.FileAttributes]::ReparsePoint){throw 'Package manifest must not be a link.'}
 $manifest=Get-Content -LiteralPath $manifestPath -Raw|ConvertFrom-Json
 if($manifest.Product-ne'MagicKeyboardBridge'){throw 'Wrong package manifest.'}
 $manifest.Files=@($manifest.Files|Where-Object{($_.Path-replace'/','\')-ne'app\HIDMaestro.Core.dll'})+@([pscustomobject]@{Path='app\HIDMaestro.Core.dll';SHA256=$spec.CoreHash})
 $manifest|ConvertTo-Json -Depth 6|Set-Content -LiteralPath $manifestPath -Encoding UTF8
}
