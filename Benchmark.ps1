#requires -Version 7.0
param([string]$Serial='',[int]$Width=1280,[string]$OutputDirectory='',
    [string[]]$Cases=@(),[switch]$Desktop)
$ErrorActionPreference='Stop'
$Serial = & (Join-Path $PSScriptRoot 'Resolve-Tablet.ps1') -Serial $Serial
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('logs\matrix-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$runs=@(
    @{Name='legacy-canvas-w1';Capture='legacy';Renderer='canvas';Window=1},
    @{Name='legacy-canvas-w2';Capture='legacy';Renderer='canvas';Window=2},
    @{Name='gpu-canvas-w1';Capture='gpu';Renderer='canvas';Window=1},
    @{Name='gpu-canvas-w2';Capture='gpu';Renderer='canvas';Window=2},
    @{Name='gpu-gl-w2';Capture='gpu';Renderer='gl';Window=2},
    @{Name='gpu-gl-w1';Capture='gpu';Renderer='gl';Window=1}
)
if(-not $Desktop){
    $runs=@(
        @{Name='synthetic-canvas-w1';Capture='gpu';Renderer='canvas';Window=1},
        @{Name='synthetic-canvas-w2';Capture='gpu';Renderer='canvas';Window=2},
        @{Name='synthetic-gl-w1';Capture='gpu';Renderer='gl';Window=1},
        @{Name='synthetic-gl-w2';Capture='gpu';Renderer='gl';Window=2}
    )
}
if($Cases.Count -eq 0){$Cases=@($runs | ForEach-Object Name)}
foreach($run in $runs | Where-Object {$_.Name -in $Cases}){
    $directory=Join-Path $OutputDirectory $run.Name;New-Item -ItemType Directory -Force -Path $directory | Out-Null
    $stdout=Join-Path $directory 'host.log';$stderr=Join-Path $directory 'host.err'
    $arguments=@('--serial',$Serial,'--width',"$Width",'--diagnostic','--benchmark','--window',"$($run.Window)",'--capture',$run.Capture,'--renderer',$run.Renderer,'--logs',$directory)
    if(-not $Desktop){$arguments += '--headless-benchmark'}
    $startInfo=[System.Diagnostics.ProcessStartInfo]::new((Join-Path $PSScriptRoot 'release\host\UsbPenDisplay.exe'))
    $startInfo.UseShellExecute=$false;$startInfo.CreateNoWindow=$true;$startInfo.RedirectStandardOutput=$true;$startInfo.RedirectStandardError=$true
    foreach($argument in $arguments){$startInfo.ArgumentList.Add($argument)}
    Write-Output "RUN $($run.Name)"
    $process=[System.Diagnostics.Process]::Start($startInfo)
    $outTask=$process.StandardOutput.ReadToEndAsync();$errTask=$process.StandardError.ReadToEndAsync()
    if(-not $process.WaitForExit(45000)){
        & (Join-Path $PSScriptRoot 'Stop.ps1') -Serial $Serial
        $process.WaitForExit(8000) | Out-Null
        throw "Benchmark timed out: $($run.Name)"
    }
    [System.IO.File]::WriteAllText($stdout,$outTask.Result);[System.IO.File]::WriteAllText($stderr,$errTask.Result)
    if($process.ExitCode -ne 0 -or -not $outTask.Result.Contains('BENCH_COMPLETE')){throw "Benchmark stopped before completion: $($run.Name). Remaining runs cancelled."}
    $frames=@(Get-ChildItem -LiteralPath $directory -Filter '*.frames.csv' | ForEach-Object {Import-Csv -LiteralPath $_.FullName})
    if(@($frames | Where-Object phase -eq 'transform').Count -lt 50 -or $outTask.Result -match 'ACK_STOP|CAPTURE_RESTART|GPU_DISABLED'){
        throw "Benchmark failed frame/connection validation: $($run.Name). See $stdout. Remaining runs cancelled."
    }
    Write-Output "DONE $($run.Name)"
}
Write-Output "Matrix saved: $OutputDirectory"
