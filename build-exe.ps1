param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"

$gui = Join-Path $PSScriptRoot "ytdl-manager-gui.ps1"
$engine = Join-Path $PSScriptRoot "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $PSScriptRoot "yt-dlp.exe"
$icon = Join-Path $PSScriptRoot "assets\app.ico"
$launcher = Join-Path $PSScriptRoot "start-gui.cmd"
$docs = Join-Path $PSScriptRoot "GUI.md"

foreach ($path in @($gui,$engine,$ytDlp,$icon)) {
    if (-not (Test-Path $path)) { throw "Missing required file: $path" }
}

if (-not (Get-Module -ListAvailable -Name ps2exe)) {
    Set-PSRepository -Name PSGallery -InstallationPolicy Trusted
    Install-Module ps2exe -Scope CurrentUser -Force
}

Import-Module ps2exe -Force

if (Test-Path $OutputDir) {
    Remove-Item $OutputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

$exe = Join-Path $OutputDir "VideoDownloader.exe"

Invoke-ps2exe     -inputFile $gui     -outputFile $exe     -noConsole     -iconFile $icon     -title "Video Downloader"     -product "Video Downloader"     -company "bespalovnikita"     -version "1.0.0.0"

Copy-Item $engine $OutputDir
Copy-Item $ytDlp $OutputDir
Copy-Item $icon $OutputDir
if (Test-Path $launcher) { Copy-Item $launcher $OutputDir }
if (Test-Path $docs) { Copy-Item $docs $OutputDir }

$zip = Join-Path $OutputDir "VideoDownloader-portable.zip"
$files = Get-ChildItem $OutputDir | Where-Object { $_.FullName -ne $zip }
Compress-Archive -Path $files.FullName -DestinationPath $zip -Force

Write-Host "Built: $exe"
Write-Host "Package: $zip"
