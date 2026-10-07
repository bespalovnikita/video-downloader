$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$gui = Join-Path $root "ytdl-manager-gui.ps1"
$engine = Join-Path $root "ytdl-manager-v8.ps1"
$core = Join-Path $root "lib\VideoDownloader.Core.psm1"
$xaml = Join-Path $root "ui\MainWindow.xaml"
$icon = Join-Path $root "assets\app.ico"
$launcher = Join-Path $root "launcher\VideoDownloader.csproj"

foreach ($file in @($gui,$engine,$core)) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
    if ($errors.Count -gt 0) {
        $text = ($errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) -join [Environment]::NewLine
        throw ("PowerShell parse errors in {0}:{1}{2}" -f $file,[Environment]::NewLine,$text)
    }
}

[xml]$xamlDoc = Get-Content $xaml -Raw -Encoding UTF8
if ($xamlDoc.DocumentElement.LocalName -ne "Window") { throw "MainWindow.xaml root is not Window" }

Add-Type -AssemblyName System.Drawing
$ico = [System.Drawing.Icon]::new($icon)
$ico.Dispose()

if (-not (Test-Path $launcher)) { throw "Missing launcher project" }

$guiText = Get-Content $gui -Raw
$engineText = Get-Content $engine -Raw
$xamlText = Get-Content $xaml -Raw

$guiMarkers = @(
    "PresentationFramework",
    "ObservableCollection",
    "DoDragDrop",
    "Suspend-ProcessTree",
    "Resume-ProcessTree",
    "WatchClipboardCheck",
    "Start-DependencyJob",
    "Register-Protocol",
    "videodownloader://",
    "Apply-Profile",
    "SessionStats",
    "Start-Preview",
    "DynamicRange",
    "ContextMenu"
)
foreach ($marker in $guiMarkers) {
    if (-not $guiText.Contains($marker)) { throw "Missing GUI feature marker: $marker" }
}

$xamlMarkers = @(
    'x:Name="QueueGrid"',
    'x:Name="PauseButton"',
    'x:Name="CodecCombo"',
    'x:Name="ContainerCombo"',
    'x:Name="WriteSubsCheck"',
    'x:Name="FilenameTemplateBox"',
    'x:Name="DependencyStatus"',
    '<Setter Property="IsReadOnly" Value="True"/>',
    '<Style TargetType="DataGridRow">',
    '<Setter Property="AlternatingRowBackground" Value="#20242D"/>',
    '<Style x:Key="GridTextStyle" TargetType="TextBlock">'
)
foreach ($marker in $xamlMarkers) {
    if (-not $xamlText.Contains($marker)) { throw "Missing XAML control marker: $marker" }
}

$engineMarkers = @(
    'ValidateSet("Auto","MP4","MKV","WebM")',
    'ValidateSet("Auto","H264","VP9","AV1")',
    "WriteSubtitles",
    "EmbedThumbnail",
    "EmbedMetadata",
    "EmbedChapters",
    "FilenameTemplate",
    "Retries",
    "--progress-template",
    "__VD_PROGRESS__",
    "Test-TransientDownloadError"
)
foreach ($marker in $engineMarkers) {
    if (-not $engineText.Contains($marker)) { throw "Missing engine feature marker: $marker" }
}

$temp = Join-Path ([IO.Path]::GetTempPath()) ("video-downloader-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    $queue = Join-Path $temp "queue.txt"
    Set-Content -Path $queue -Value "https://example.com/video" -Encoding UTF8

    & $engine -In $queue -Out $temp -DryRun -Quality 1080 -RateLimit 5M -ResultDir $temp -Container MP4 -VideoCodec H264 -WriteSubtitles -SubtitleLangs "ru.*,en.*" -EmbedMetadata -EmbedChapters -FilenameTemplate "%(uploader)s - %(title)s.%(ext)s" -NoProgress
    if ($LASTEXITCODE -ne 0) { throw "Engine dry-run failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "WPF/engine smoke checks passed."
