$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'Common.ps1')
$stage=Get-Stage
if(-not(Test-Path -LiteralPath (Join-Path $stage 'armed'))){exit 0}
try {
 $health=Invoke-App @('health','--stage',$stage) -AllowFailure
 if($health.ExitCode-eq 0){exit 0}
 Start-Sleep -Seconds 2
 $health=Invoke-App @('health','--stage',$stage) -AllowFailure
 if($health.ExitCode-eq 0){exit 0}
}catch{($_|Out-String)|Add-Content -LiteralPath (Join-Path $stage 'watchdog-errors.log')}
& (Join-Path $PSScriptRoot 'Recover.ps1')
exit $LASTEXITCODE
