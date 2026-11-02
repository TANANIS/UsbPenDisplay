#requires -Version 7.0
param([Parameter(Mandatory)][string]$Directory,[string]$OutputPrefix='')
$ErrorActionPreference='Stop'
$culture=[System.Globalization.CultureInfo]::InvariantCulture
function Percentile($Rows,[string]$Field,[double]$Fraction){
    $values=@($Rows | ForEach-Object {[double]::Parse($_.$Field,$culture)} | Sort-Object)
    if($values.Count -eq 0){return 0.0}
    $index=($values.Count-1)*$Fraction;$low=[int][Math]::Floor($index);$high=[int][Math]::Ceiling($index)
    return $values[$low]+($values[$high]-$values[$low])*($index-$low)
}
$summary=@()
foreach($file in Get-ChildItem -LiteralPath $Directory -Filter '*.frames.csv' -Recurse){
    $all=@(Import-Csv -LiteralPath $file.FullName)
    $hostLog=Join-Path $file.Directory.FullName 'host.log'
    $valid=(Test-Path -LiteralPath $hostLog) -and ((Get-Content -Raw -LiteralPath $hostLog) -match 'BENCH_COMPLETE') -and ((Get-Content -Raw -LiteralPath $hostLog) -notmatch 'ACK_STOP|CAPTURE_RESTART|GPU_DISABLED')
    foreach($phase in @('stroke','transform','idle')){
        $rows=@($all | Where-Object {$_.phase -eq $phase -and $_.keyframe -eq 'False' -and $_.verified -eq 'False'} | Select-Object -Skip 3)
        if($rows.Count -lt 2){continue}
        $span=[double]$rows[-1].host_elapsed_ms-[double]$rows[0].host_elapsed_ms
        $summary += [pscustomobject]@{
            run=$file.Directory.Name;phase=$phase;valid=$valid;frames=$rows.Count
            update_fps=[Math]::Round(($rows.Count-1)*1000/$span,2)
            capture_ack_p50_ms=[Math]::Round((Percentile $rows 'capture_to_ack_ms' .5),3)
            capture_ack_p95_ms=[Math]::Round((Percentile $rows 'capture_to_ack_ms' .95),3)
            capture_process_p50_ms=[Math]::Round((Percentile $rows 'capture_process_ms' .5),3)
            compare_p50_ms=[Math]::Round((Percentile $rows 'compare_ms' .5),3)
            upload_p50_ms=[Math]::Round((Percentile $rows 'upload_ms' .5),3)
            render_queue_p95_ms=[Math]::Round((Percentile $rows 'render_queue_ms' .95),3)
            swap_p50_ms=[Math]::Round((Percentile $rows 'swap_ms' .5),3)
            readback_p50_bytes=[Math]::Round((Percentile $rows 'readback_bytes' .5),0)
            input_echo_sample_p50_ms=[Math]::Round((Percentile $rows 'input_rtt_ms' .5),3)
            input_echo_sample_p95_ms=[Math]::Round((Percentile $rows 'input_rtt_ms' .95),3)
        }
    }
}
$summary | Format-Table run,phase,valid,update_fps,capture_ack_p50_ms,capture_ack_p95_ms,upload_p50_ms -AutoSize
if($OutputPrefix){
    $parent=Split-Path -Parent $OutputPrefix;if($parent){New-Item -ItemType Directory -Force -Path $parent | Out-Null}
    $summary | Export-Csv -LiteralPath "$OutputPrefix.csv" -NoTypeInformation -Encoding utf8
    $summary | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$OutputPrefix.json" -Encoding utf8
}
