#requires -Version 7.0
param([string]$Version='0.3.0',[string]$RuntimeVersion='8.0.30',
    [string]$AndroidSdk="$env:LOCALAPPDATA\Android\Sdk",[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
if(-not $OutputDirectory){$OutputDirectory=Join-Path $PSScriptRoot "dist\v$Version"}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$OutputDirectory=(Resolve-Path $OutputDirectory).Path
$bundle=Join-Path $OutputDirectory "UsbPenDisplay-$Version-win-x64"
if(Test-Path $bundle){throw 'Use a new output directory to avoid mixing release files.'}
New-Item -ItemType Directory -Force -Path $bundle,(Join-Path $bundle 'tools'),(Join-Path $bundle 'licenses') | Out-Null
& dotnet publish (Join-Path $PSScriptRoot 'host\UsbPenDisplay.csproj') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None "-p:RuntimeFrameworkVersion=$RuntimeVersion" -o $bundle
if($LASTEXITCODE -ne 0){throw 'Self-contained Windows publish failed'}
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'release\UsbPenDisplay.apk') -Destination (Join-Path $bundle 'UsbPenDisplay.apk')
$platformTools=Join-Path $AndroidSdk 'platform-tools'
foreach($name in @('adb.exe','AdbWinApi.dll','AdbWinUsbApi.dll')){Copy-Item -LiteralPath (Join-Path $platformTools $name) -Destination (Join-Path $bundle 'tools')}
Copy-Item -LiteralPath (Join-Path $platformTools 'NOTICE.txt') -Destination (Join-Path $bundle 'licenses\Android-platform-tools-NOTICE.txt')
Get-Content (Join-Path $platformTools 'source.properties') | Set-Content (Join-Path $bundle 'tools\source.properties')
Copy-Item -Path (Join-Path $PSScriptRoot 'licenses\*') -Destination (Join-Path $bundle 'licenses') -Recurse
$nuget=if($env:NUGET_PACKAGES){$env:NUGET_PACKAGES}else{Join-Path $env:USERPROFILE '.nuget\packages'}
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$RuntimeVersion\LICENSE.TXT") -Destination (Join-Path $bundle 'licenses\dotnet-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.netcore.app.runtime.win-x64\$RuntimeVersion\THIRD-PARTY-NOTICES.TXT") -Destination (Join-Path $bundle 'licenses\dotnet-THIRD-PARTY-NOTICES.txt')
Copy-Item -LiteralPath (Join-Path $nuget "microsoft.windowsdesktop.app.runtime.win-x64\$RuntimeVersion\LICENSE") -Destination (Join-Path $bundle 'licenses\WindowsDesktop-LICENSE.txt')
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'README.md'),(Join-Path $PSScriptRoot 'THIRD_PARTY.md') -Destination $bundle
& (Join-Path $bundle 'UsbPenDisplay.exe') --verify
if($LASTEXITCODE -ne 0){throw 'Packaged protocol checks failed'}
& (Join-Path $bundle 'UsbPenDisplay.exe') --verify-reconnect
if($LASTEXITCODE -ne 0){throw 'Packaged reconnect checks failed'}
Get-ChildItem $bundle -Recurse -File | ForEach-Object {
    $hash=Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256
    "$($hash.Hash.ToLowerInvariant())  $([IO.Path]::GetRelativePath($bundle,$_.FullName).Replace('\','/'))"
} | Set-Content -LiteralPath (Join-Path $bundle 'SHA256SUMS.txt') -Encoding ascii
$zip=Join-Path $OutputDirectory "UsbPenDisplay-$Version-win-x64.zip"
Compress-Archive -LiteralPath $bundle -DestinationPath $zip -CompressionLevel Optimal
$apk=Join-Path $OutputDirectory "UsbPenDisplay-$Version-arm64.apk"
Copy-Item -LiteralPath (Join-Path $bundle 'UsbPenDisplay.apk') -Destination $apk
Get-Item $zip,$apk | ForEach-Object { $hash=Get-FileHash -LiteralPath $_.FullName;"$($hash.Hash.ToLowerInvariant())  $($_.Name)" } | Set-Content (Join-Path $OutputDirectory 'SHA256SUMS.txt') -Encoding ascii
Write-Output "Packaged: $OutputDirectory"
