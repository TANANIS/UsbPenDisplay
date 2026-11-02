#requires -Version 7.0
param([string]$Serial='',[switch]$Build,[switch]$Matrix,[string]$OutputDirectory='',
    [ValidateSet('gl','canvas')][string]$Renderer='canvas',[ValidateRange(1,2)][int]$Window=2,[switch]$Recovery)
$ErrorActionPreference='Stop'
$Serial = & (Join-Path $PSScriptRoot 'Resolve-Tablet.ps1') -Serial $Serial
if($Build){& (Join-Path $PSScriptRoot 'Build.ps1')}
$hostExe=Join-Path $PSScriptRoot 'release\host\UsbPenDisplay.exe'
$apk=Join-Path $PSScriptRoot 'release\UsbPenDisplay.apk'
if(-not (Test-Path -LiteralPath $hostExe)){throw 'Build the project first.'}
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('logs\verification-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
& $hostExe --verify 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'protocol.log')
if($LASTEXITCODE -ne 0){throw 'Protocol verification failed'}
& $hostExe --verify-reconnect 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'reconnect.log')
if($LASTEXITCODE -ne 0){throw 'Reconnect verification failed'}
& $hostExe --verify-pipeline --logs (Join-Path $OutputDirectory 'loopback') 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'pipeline.log')
if($LASTEXITCODE -ne 0){throw 'Pipeline verification failed'}
$adb=Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
& $adb -s $Serial install -r $apk
if($LASTEXITCODE -ne 0){throw 'APK installation failed'}
$integrity=Join-Path $OutputDirectory 'integrity';New-Item -ItemType Directory -Force -Path $integrity | Out-Null
$start=[System.Diagnostics.ProcessStartInfo]::new($hostExe)
$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
foreach($argument in @('--serial',$Serial,'--diagnostic','--benchmark','--verify-frames','--window',"$Window",'--capture','gpu','--renderer',$Renderer,'--logs',$integrity)){$start.ArgumentList.Add($argument)}
if($Recovery){$start.ArgumentList.Add('--recovery-test')}
$process=[System.Diagnostics.Process]::Start($start);$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
if(-not $process.WaitForExit(45000)){
    & (Join-Path $PSScriptRoot 'Stop.ps1') -Serial $Serial
    $process.WaitForExit(8000) | Out-Null
    throw 'Integrity run timed out'
}
[System.IO.File]::WriteAllText((Join-Path $integrity 'host.log'),$stdout.Result)
[System.IO.File]::WriteAllText((Join-Path $integrity 'host.err'),$stderr.Result)
$frames=@(Get-ChildItem $integrity -Filter '*.frames.csv' | ForEach-Object {Import-Csv $_.FullName})
if($process.ExitCode -ne 0 -or -not $stdout.Result.Contains('BENCH_COMPLETE') -or $stdout.Result -match 'CAPTURE_RESTART|GPU_DISABLED' -or (-not $Recovery -and $stdout.Result.Contains('ACK_STOP')) -or @($frames | Where-Object {$_.verified -eq 'True' -and $_.phase -eq 'transform'}).Count -lt 50){
    throw "Integrity run failed or was interrupted. See $integrity"
}
Write-Output "PASS real-device GPU source validation + ordered texture reconstruction: $($frames.Count) acknowledged verified frames"
if($Recovery -and -not $stdout.Result.Contains('RECOVERY_PASS')){throw 'USB/app restart recovery did not pass'}
if($stdout.Result -notmatch 'CANVAS_RESULT events=[1-9]'){Write-Warning 'The diagnostic canvas did not receive pen contacts while foreground; pen acceptance remains separate.'}
if($Matrix){
    $matrixDirectory=Join-Path $OutputDirectory 'matrix'
    & (Join-Path $PSScriptRoot 'Benchmark.ps1') -Serial $Serial -OutputDirectory $matrixDirectory
    & (Join-Path $PSScriptRoot 'Summarize.ps1') -Directory $matrixDirectory -OutputPrefix (Join-Path $OutputDirectory 'summary')
}
Write-Output "Verification evidence: $OutputDirectory"
