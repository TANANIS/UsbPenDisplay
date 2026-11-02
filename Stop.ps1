param([string]$Serial='')
$ErrorActionPreference='Stop'
$Serial = & (Join-Path $PSScriptRoot 'Resolve-Tablet.ps1') -Serial $Serial
try {
    $stopSignal=[System.Threading.EventWaitHandle]::OpenExisting("Local\UsbPenDisplay-Stop-$Serial")
    try { $stopSignal.Set() | Out-Null } finally { $stopSignal.Dispose() }
    Write-Output 'Stop requested. The host will release the pen and remove its USB port rules.'
} catch [System.Threading.WaitHandleCannotBeOpenedException] {
    Write-Output 'No running host was found.'
}
