$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$gui = Join-Path $root "ytdl-manager-gui.ps1"
$engine = Join-Path $root "ytdl-manager-v8.ps1"
$icon = Join-Path $root "assets\app.ico"

foreach ($file in @($gui,$engine)) {
    $tokens = $null
    $errors = $null
    [void][System.Management.Automation.Language.Parser]::ParseFile($file,[ref]$tokens,[ref]$errors)
    if ($errors.Count -gt 0) {
        $text = ($errors | ForEach-Object { "$($_.Extent.StartLineNumber): $($_.Message)" }) -join [Environment]::NewLine
        throw "PowerShell parse errors in $file:$([Environment]::NewLine)$text"
    }
}

Add-Type -AssemblyName System.Drawing
$ico = [System.Drawing.Icon]::new($icon)
$ico.Dispose()

$temp = Join-Path ([IO.Path]::GetTempPath()) ("video-downloader-smoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp -Force | Out-Null

try {
    $queue = Join-Path $temp "queue.txt"
    Set-Content -Path $queue -Value "https://example.com/video" -Encoding UTF8

    & $engine -In $queue -Out $temp -DryRun -Quality 1080 -RateLimit 5M -ResultDir $temp -NoProgress
    if ($LASTEXITCODE -ne 0) { throw "Engine dry-run failed with exit code $LASTEXITCODE" }
}
finally {
    Remove-Item $temp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Smoke checks passed."
