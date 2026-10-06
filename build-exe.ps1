#requires -Version 7.0
param(
    [string]$OutputDir = (Join-Path $PSScriptRoot "dist")
)

$ErrorActionPreference = "Stop"

$launcherProject = Join-Path $PSScriptRoot "launcher\VideoDownloader.csproj"
$gui = Join-Path $PSScriptRoot "ytdl-manager-gui.ps1"
$engine = Join-Path $PSScriptRoot "ytdl-manager-v8.ps1"
$ytDlp = Join-Path $PSScriptRoot "yt-dlp.exe"
$icon = Join-Path $PSScriptRoot "assets\app.ico"
$cmd = Join-Path $PSScriptRoot "start-gui.cmd"
$docs = Join-Path $PSScriptRoot "GUI.md"

foreach ($path in @($launcherProject,$gui,$engine,$ytDlp,$icon)) {
    if (-not (Test-Path $path)) { throw "Missing required file: $path" }
}

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET 8 SDK is required to build VideoDownloader.exe."
}

if (Test-Path $OutputDir) { Remove-Item $OutputDir -Recurse -Force }
New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null

dotnet publish $launcherProject -c Release -r win-x64 --self-contained true -o $OutputDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

foreach ($path in @($gui,$engine,$ytDlp,$icon,$cmd,$docs)) {
    if (Test-Path $path) { Copy-Item $path $OutputDir -Force }
}

$zip = Join-Path $OutputDir "VideoDownloader-portable.zip"
$files = Get-ChildItem $OutputDir | Where-Object { $_.FullName -ne $zip }
Compress-Archive -Path $files.FullName -DestinationPath $zip -Force

Write-Host "Built: $(Join-Path $OutputDir 'VideoDownloader.exe')"
Write-Host "Package: $zip"
