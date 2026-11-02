param(
    [string]$AndroidSdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$JavaHome = 'C:\Program Files\Eclipse Adoptium\jdk-17.0.19.10-hotspot'
)
$ErrorActionPreference = 'Stop'
$projectRoot = $PSScriptRoot
$taskBuildRoot = Join-Path $projectRoot 'build'
$classes = Join-Path $taskBuildRoot 'classes'
$dex = Join-Path $taskBuildRoot 'dex'
$release = Join-Path $projectRoot 'release'
New-Item -ItemType Directory -Force -Path $classes,$dex,$release | Out-Null
$toolsPath = Join-Path $AndroidSdk 'build-tools\35.0.1'
$androidJar = Join-Path $AndroidSdk 'platforms\android-35\android.jar'
function Check-Exit([string]$step) { if ($LASTEXITCODE -ne 0) { throw "$step failed ($LASTEXITCODE)" } }
$native = Join-Path $taskBuildRoot 'native'
New-Item -ItemType Directory -Force -Path $native | Out-Null
$clang = Join-Path $AndroidSdk 'ndk\28.1.13356709\toolchains\llvm\prebuilt\windows-x86_64\bin\aarch64-linux-android26-clang.cmd'
& $clang -shared -fPIC -O3 -Wall -Wextra (Join-Path $projectRoot 'android\native\usbpen.c') (Join-Path $projectRoot 'android\native\vendor\lz4.c') -ljnigraphics -lGLESv2 -o (Join-Path $native 'libusbpen.so')
Check-Exit 'ARM64 native pixel pipeline'
& dotnet publish (Join-Path $projectRoot 'host\UsbPenDisplay.csproj') -c Release -o (Join-Path $release 'host')
Check-Exit 'Windows publish'
$sources = @(Get-ChildItem (Join-Path $projectRoot 'android\src') -Recurse -Filter '*.java' | ForEach-Object FullName)
& (Join-Path $JavaHome 'bin\javac.exe') -encoding UTF-8 -source 8 -target 8 -bootclasspath "$androidJar;$toolsPath\core-lambda-stubs.jar" -d $classes @sources
Check-Exit 'Java compile'
$classFiles = @(Get-ChildItem $classes -Recurse -Filter '*.class' | ForEach-Object FullName)
$env:JAVA_HOME = $JavaHome
& (Join-Path $toolsPath 'd8.bat') --min-api 26 --lib $androidJar --output $dex @classFiles
Check-Exit 'DEX compile'
$unsigned = Join-Path $taskBuildRoot 'unsigned.apk'
& (Join-Path $toolsPath 'aapt2.exe') link -o $unsigned --manifest (Join-Path $projectRoot 'android\AndroidManifest.xml') -I $androidJar
Check-Exit 'APK link'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($unsigned,[System.IO.Compression.ZipArchiveMode]::Update)
try {
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $dex 'classes.dex'),'classes.dex') | Out-Null
    [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive,(Join-Path $native 'libusbpen.so'),'lib/arm64-v8a/libusbpen.so') | Out-Null
} finally { $archive.Dispose() }
$aligned = Join-Path $taskBuildRoot 'aligned.apk'
& (Join-Path $toolsPath 'zipalign.exe') -f 4 $unsigned $aligned
Check-Exit 'APK align'
$keystore = Join-Path $taskBuildRoot 'prototype.keystore'
if (-not (Test-Path -LiteralPath $keystore)) {
    & (Join-Path $JavaHome 'bin\keytool.exe') -genkeypair -keystore $keystore -storepass android -keypass android -alias prototype -keyalg RSA -keysize 2048 -validity 3650 -dname 'CN=USB Pen Display Local Prototype'
    Check-Exit 'Local signing key'
}
$apk = Join-Path $release 'UsbPenDisplay.apk'
& (Join-Path $toolsPath 'apksigner.bat') sign --ks $keystore --ks-pass pass:android --key-pass pass:android --out $apk $aligned
Check-Exit 'APK sign'
& (Join-Path $toolsPath 'apksigner.bat') verify $apk
Check-Exit 'APK verification'
& (Join-Path $release 'host\UsbPenDisplay.exe') --verify
Check-Exit 'Protocol/native interface verification'
& (Join-Path $release 'host\UsbPenDisplay.exe') --verify-reconnect
Check-Exit 'Connection lifecycle verification'
Write-Output "Built: $release"
