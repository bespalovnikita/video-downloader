#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Version,
    [string]$OutputPath = 'dist/VideoDownloader-Android.apk'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path $PSScriptRoot -Parent
Import-Module (Join-Path $root '.github/scripts/ReleaseVersion.psm1') -Force
$versionCode = Get-VdAndroidVersionCode -Version $Version

foreach ($name in @('ANDROID_KEYSTORE_BASE64', 'ANDROID_KEYSTORE_PASSWORD', 'ANDROID_KEY_ALIAS', 'ANDROID_KEY_PASSWORD')) {
    if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
        throw "Missing GitHub Actions secret: $name. Configure the permanent Android signing key before publishing."
    }
}
if ([string]::IsNullOrWhiteSpace($env:ANDROID_HOME)) { throw 'ANDROID_HOME must point to Android SDK.' }
$buildTools = Join-Path $env:ANDROID_HOME 'build-tools/35.0.0'
$zipalign = Join-Path $buildTools $(if ($IsWindows) { 'zipalign.exe' } else { 'zipalign' })
$apksigner = Join-Path $buildTools $(if ($IsWindows) { 'apksigner.bat' } else { 'apksigner' })
$aapt = Join-Path $buildTools $(if ($IsWindows) { 'aapt.exe' } else { 'aapt' })
foreach ($tool in @($zipalign, $apksigner, $aapt)) {
    if (-not (Test-Path -LiteralPath $tool -PathType Leaf)) { throw "Missing Android build tool: $tool" }
}

$output = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
$scratch = Join-Path $temporaryRoot "vd-android-signing-$([guid]::NewGuid().ToString('N'))"
[void](New-Item -ItemType Directory -Path $scratch)
try {
    $keystore = Join-Path $scratch 'release.keystore'
    [IO.File]::WriteAllBytes($keystore, [Convert]::FromBase64String($env:ANDROID_KEYSTORE_BASE64))
    # Keep the key outside the checkout and Gradle cache. Passwords stay in env,
    # never in Gradle properties, command-line arguments or repository files.
    Push-Location (Join-Path $root 'android')
    try {
        $gradle = if ($IsWindows) { '.\gradlew.bat' } else { './gradlew' }
        & $gradle --no-daemon "-PvdVersionName=$Version" "-PvdVersionCode=$versionCode" :app:lintRelease :app:assembleRelease
        if ($LASTEXITCODE -ne 0) { throw 'Android release build failed.' }
    } finally { Pop-Location }

    $unsigned = Join-Path $root 'android/app/build/outputs/apk/release/app-release-unsigned.apk'
    $aligned = Join-Path $scratch 'aligned.apk'
    $signed = Join-Path $scratch 'signed.apk'
    & $zipalign -f 4 $unsigned $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK alignment failed.' }
    & $apksigner sign --ks $keystore --ks-key-alias $env:ANDROID_KEY_ALIAS --ks-pass env:ANDROID_KEYSTORE_PASSWORD --key-pass env:ANDROID_KEY_PASSWORD --out $signed $aligned
    if ($LASTEXITCODE -ne 0) { throw 'APK signing failed.' }
    & $apksigner verify --verbose --print-certs $signed
    if ($LASTEXITCODE -ne 0) { throw 'APK signature verification failed.' }
    & $zipalign -c 4 $signed
    if ($LASTEXITCODE -ne 0) { throw 'Signed APK alignment verification failed.' }
    $badging = (& $aapt dump badging $signed) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw 'Cannot read signed APK metadata.' }
    $package = "package: name='ru.bespalov.videodownloader' versionCode='$versionCode' versionName='$Version'"
    if (-not $badging.StartsWith($package) -or $badging -match '(?m)^application-debuggable') {
        throw 'APK must be a non-debuggable release with the selected package and version.'
    }
    [void](New-Item -ItemType Directory -Path (Split-Path $output -Parent) -Force)
    Move-Item -LiteralPath $signed -Destination $output -Force
    Write-Host "Verified Android release $Version (versionCode $versionCode): $output"
} finally {
    Remove-Item -LiteralPath $scratch -Recurse -Force
}
