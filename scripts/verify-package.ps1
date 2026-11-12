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
        $file.Name -match '(?i)(launcher|NativeTests|Tests|UiTests)\.(exe|dll)$') {
        throw "Package contains an obsolete entry point, test host or PowerShell file: $($file.FullName)"
    }
}
foreach ($required in @('VideoDownloader.exe','yt-dlp.exe','windows-package.json','ui/MainWindow.xaml','ui/DuplicateDownloadDialog.xaml',
    'ui/SubscriptionsWindow.xaml','assets/app.ico','collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py',
    'collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub_preview.py')) {
    $path = Join-Path $Directory $required
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -eq 0) {
        throw "Missing or empty required package file: $required"
    }
}
$manifest = Get-Content -LiteralPath (Join-Path $Directory 'windows-package.json') -Raw | ConvertFrom-Json
if ($manifest.schema -ne 1 -or $manifest.version -cne $Version -or @($manifest.files).Count -lt 3 -or @($manifest.files).Count -gt 2048) {
    throw 'Invalid Windows package ownership manifest.'
}
$ownedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $manifest.files) {
    $name = [string]$entry.path
    if ($name -match '(^/|\\|:|(^|/)\.\.?(/|$))' -or $name -ceq 'windows-package.json' -or
        $name -match '(?i)(^|/)(ffmpeg|ffprobe)\.exe$' -or -not $ownedNames.Add($name)) {
        throw "Unsafe or duplicate package ownership path: $name"
    }
    $path = Join-Path $Directory $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -ne $entry.size -or
        (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $entry.sha256) {
        throw "Package ownership checksum mismatch: $name"
    }
}
$actualNames = @(Get-ChildItem -LiteralPath $Directory -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath([IO.Path]::GetFullPath($Directory), $_.FullName).Replace('\', '/')
} | Where-Object { $_ -cne 'windows-package.json' })
if ($actualNames.Count -ne $ownedNames.Count -or @($actualNames | Where-Object { -not $ownedNames.Contains($_) }).Count -gt 0) {
    throw 'Windows package contains files outside its ownership manifest.'
}
$exe = Join-Path $Directory 'VideoDownloader.exe'
$metadata = [Diagnostics.FileVersionInfo]::GetVersionInfo($exe)
$productVersion = ($metadata.ProductVersion -split '\+', 2)[0]
if ($productVersion -cne $Version -or $metadata.OriginalFilename -cne 'VideoDownloader.dll' -or
    $metadata.FileDescription -cne 'Video Downloader WPF') {
    throw "The package must contain the native WPF application at version $Version; found '$($metadata.ProductVersion)' / '$($metadata.OriginalFilename)'."
}
Write-Host "Native UI package and EXE version $Version`: PASS"
