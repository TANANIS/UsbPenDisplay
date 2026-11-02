param([string]$Serial='')
if($Serial){return $Serial}
$adb=Join-Path $env:LOCALAPPDATA 'Android\Sdk\platform-tools\adb.exe'
if(Test-Path (Join-Path $PSScriptRoot 'tools\adb.exe')){$adb=Join-Path $PSScriptRoot 'tools\adb.exe'}
if(-not(Test-Path $adb)){throw 'ADB not found. Use the complete portable package or install Android platform-tools.'}
$lines=@(& $adb devices -l)
if($LASTEXITCODE -ne 0){throw 'Could not enumerate USB devices.'}
$authorized=@($lines | Where-Object {$_ -match '^\S+\s+device\b'})
$preferred=@($authorized | Where-Object {$_ -match '\bmodel:SM_P610\b'})
if($preferred.Count -eq 1){return ($preferred[0] -split '\s+')[0]}
if($authorized.Count -eq 1){return ($authorized[0] -split '\s+')[0]}
throw 'Choose a tablet in the desktop panel, or specify -Serial DEVICE_SERIAL.'
