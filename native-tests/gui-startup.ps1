$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot '..\native\bin\Release\net8.0-windows\win-x64\VideoDownloader.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Native apphost was not built: $exe" }
$profileDirectory = Join-Path ([IO.Path]::GetTempPath()) ('vd-startup-' + [guid]::NewGuid().ToString('N'))
$previousDataDirectory = $env:VD_DATA_DIRECTORY
$p = $null
try {
    $env:VD_DATA_DIRECTORY = $profileDirectory
    $p = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
    $deadline = (Get-Date).AddSeconds(25)
    while ((Get-Date) -lt $deadline) {
        $p.Refresh()
        if ($p.HasExited) { throw "Native GUI exited during startup with code $($p.ExitCode)" }
        if ($p.MainWindowHandle -ne [IntPtr]::Zero -and (Test-Path -LiteralPath (Join-Path $profileDirectory 'session.json'))) { break }
        Start-Sleep -Milliseconds 200
    }
    $p.Refresh()
    if ($p.MainWindowHandle -eq [IntPtr]::Zero) { throw "Native WPF GUI did not open a window" }
    if (-not (Test-Path -LiteralPath (Join-Path $profileDirectory 'session.json'))) { throw 'Native startup did not use the isolated profile' }
    if ($p.MainWindowTitle -ne 'Video Downloader') {
        throw "Native GUI opened unexpected window (startup error?): $($p.MainWindowTitle)"
    }
    Write-Host "Native C# WPF GUI startup and window title smoke passed. pid=$($p.Id)"
}
finally {
    $env:VD_DATA_DIRECTORY = $previousDataDirectory
    if ($null -ne $p) {
        try { if (-not $p.HasExited) { $p.Kill($true); [void]$p.WaitForExit(5000) } } catch {}
        $p.Dispose()
    }
    if (Test-Path -LiteralPath $profileDirectory) { Remove-Item -LiteralPath $profileDirectory -Recurse -Force }
}
