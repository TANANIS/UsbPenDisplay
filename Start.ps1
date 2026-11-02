param(
    [string]$Serial = '',
    [int]$Width = 1280,
    [int]$Fps = 60,
    [switch]$Diagnostic,
    [switch]$Benchmark,
    [switch]$VerifyFrames,
    [ValidateRange(1,2)][int]$Window = 2,
    [ValidateSet('gpu','legacy')][string]$Capture = 'gpu',
    [ValidateSet('gl','canvas')][string]$Renderer = 'canvas'
)
$ErrorActionPreference = 'Stop'
$hostExe = Join-Path $PSScriptRoot 'release\host\UsbPenDisplay.exe'
$apk = Join-Path $PSScriptRoot 'release\UsbPenDisplay.apk'
if (-not (Test-Path -LiteralPath $hostExe)) { throw 'Run Build.ps1 first.' }
if(-not $Serial -and -not $Diagnostic -and -not $Benchmark){& $hostExe --desktop;return}
$Serial = & (Join-Path $PSScriptRoot 'Resolve-Tablet.ps1') -Serial $Serial
$hostArgs = @('--serial',$Serial,'--width',"$Width",'--fps',"$Fps",'--apk',$apk,'--logs',(Join-Path $PSScriptRoot 'logs'))
$hostArgs += @('--window',"$Window",'--capture',$Capture,'--renderer',$Renderer)
if ($Diagnostic) { $hostArgs += '--diagnostic' }
if ($Benchmark) { $hostArgs += @('--diagnostic','--benchmark') }
if ($VerifyFrames) { $hostArgs += '--verify-frames' }
& $hostExe @hostArgs
if($LASTEXITCODE -ne 0){throw "USB Pen Display exited with code $LASTEXITCODE. Read the message above."}
