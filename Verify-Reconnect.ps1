#requires -Version 7.0
param([string]$Serial='',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$Serial = & (Join-Path $PSScriptRoot 'Resolve-Tablet.ps1') -Serial $Serial
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot ('logs\reconnect-'+(Get-Date -Format yyyyMMdd-HHmmss))}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$adb=Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
$exe=Join-Path $PSScriptRoot 'release\host\UsbPenDisplay.exe'
$apk=Join-Path $PSScriptRoot 'release\UsbPenDisplay.apk'
function DeviceCommand([string[]]$Command){
    $result=& $adb -s $Serial @Command 2>&1
    if($LASTEXITCODE -ne 0){throw "Device command failed: $result"}
    return "$result"
}
function BeginHost([string]$Name,[string]$Target){
    $script:directory=Join-Path $OutputDirectory $Name;New-Item -ItemType Directory -Force -Path $script:directory | Out-Null
    $script:stdout=Join-Path $script:directory 'host.log'
    $script:process=Start-Process -FilePath $exe -ArgumentList @('--serial',$Target,'--apk',('"'+$apk+'"'),'--logs',('"'+$script:directory+'"')) -WindowStyle Hidden -PassThru -RedirectStandardOutput $script:stdout -RedirectStandardError (Join-Path $script:directory 'host.err')
    $script:hostSerial=$Target
    Start-Sleep -Milliseconds 300
}
function Text { $value=Get-Content -LiteralPath $script:stdout -Raw -ErrorAction SilentlyContinue;return [string]$value }
function WaitFor([scriptblock]$Condition,[int]$Seconds=20){
    $timer=[Diagnostics.Stopwatch]::StartNew()
    while(-not (& $Condition)){
        if($script:process.HasExited){throw "Host exited early: $(Text)"}
        if($timer.Elapsed.TotalSeconds -gt $Seconds){throw "Condition timed out: $(Text)"}
        Start-Sleep -Milliseconds 100
    }
}
function Count([string]$Pattern){ return [regex]::Matches((Text),$Pattern).Count }
function EndHost {
    if($script:process -and -not $script:process.HasExited){
        Write-Output "Stopping test host $script:hostSerial (PID $($script:process.Id))"
        $signal=$null;$deadline=[Diagnostics.Stopwatch]::StartNew()
        while(-not $signal -and -not $script:process.HasExited){
            try {$signal=[System.Threading.EventWaitHandle]::OpenExisting("Local\UsbPenDisplay-Stop-$script:hostSerial")}
            catch [System.Threading.WaitHandleCannotBeOpenedException] {
                if($deadline.Elapsed.TotalSeconds -gt 5){throw}
                Start-Sleep -Milliseconds 100
            }
        }
        if(-not $signal){return}
        try {$signal.Set() | Out-Null} finally {$signal.Dispose()}
        if(-not $script:process.WaitForExit(7000)){throw 'Stop did not finish within seven seconds'}
        if($script:process.ExitCode -ne 0){throw 'Host did not stop cleanly'}
    }
}
try {
    BeginHost 'absent-start' 'USBPEN-NOT-CONNECTED'
    WaitFor { (Text).Contains('CONNECTION WaitingForDevice') } 10
    if((Text) -match 'StartingApp|InstallingApp'){throw 'Disconnected start attempted install/launch'}
    EndHost
    Write-Output 'PASS disconnected startup remains alive; stop cancels waiting'
    BeginHost 'first-session' $Serial
    WaitFor { (Count 'CONNECTION Ready') -ge 1 } 35
    # Simulated loss of this app's rules and process. No global adb restart/disconnect.
    DeviceCommand @('reverse','--remove','tcp:37491') | Out-Null
    DeviceCommand @('reverse','--remove','tcp:37492') | Out-Null
    DeviceCommand @('shell','am','force-stop','dev.usbpendisplay') | Out-Null
    WaitFor { (Count 'CONNECTION Ready') -ge 2 -and (Count 'VIDEO connected') -ge 2 }
    Write-Output 'PASS app death + missing rules recover without a manual launch'
    $launches=Count 'CONNECTION StartingApp'
    DeviceCommand @('shell','input','keyevent','KEYCODE_HOME') | Out-Null
    WaitFor { (Count 'CONNECTION Recovering') -ge 2 }
    Start-Sleep -Seconds 6
    if((Count 'CONNECTION StartingApp') -ne $launches){throw 'Background Activity was pulled to foreground'}
    DeviceCommand @('shell','am','start','-n','dev.usbpendisplay/.MainActivity') | Out-Null
    WaitFor { (Count 'CONNECTION Ready') -ge 3 }
    Write-Output 'PASS background app stays in background; reopening without extras retains session'
    EndHost
    # Old Android token remains in memory; a new host must replace it automatically.
    BeginHost 'host-restart' $Serial
    WaitFor { (Count 'CONNECTION Ready') -ge 1 } 35
    Write-Output 'PASS host restart replaces previous session and acknowledges full state'
    EndHost
    Write-Output "PASS real-device reconnect suite: $OutputDirectory"
} catch { Write-Output "SUITE FAILURE: $_";throw } finally {EndHost}
