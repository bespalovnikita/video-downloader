#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Directory,
    [Parameter(Mandatory)][string]$Version
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path -LiteralPath $Directory -PathType Container)) { throw "Missing package folder: $Directory" }
foreach ($file in Get-ChildItem -LiteralPath $Directory -Recurse -File) {
    if ($file.Extension -in @('.cmd','.bat','.ps1','.psm1') -or $file.Name -ieq 'pwsh.exe' -or
        $file.Name -match '(?i)(launcher|NativeTests|UiTests)\.(exe|dll)$') {
        throw "Package contains an obsolete entry point, test host or PowerShell file: $($file.FullName)"
    }
}
foreach ($required in @('VideoDownloader.exe','yt-dlp.exe','ui/MainWindow.xaml','ui/DuplicateDownloadDialog.xaml',
    'ui/SubscriptionsWindow.xaml','assets/app.ico','collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py')) {
    $path = Join-Path $Directory $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty required package file: $required"
    }
}
$exe = Join-Path $Directory 'VideoDownloader.exe'
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$productVersion = ($metadata.ProductVersion -split '\+', 2)[0]
if ($productVersion -cne $Version -or $metadata.OriginalFilename -cne 'VideoDownloader.dll') {
    throw "The package must contain the native WPF application at version $Version; found '$($metadata.ProductVersion)' / '$($metadata.OriginalFilename)'."
}
Write-Host "Native UI package and EXE version $Version`: PASS"
