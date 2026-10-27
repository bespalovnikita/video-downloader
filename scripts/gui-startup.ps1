#requires -Version 7.0
[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src/VideoDownloader/bin/$Configuration/net8.0-windows/win-x64/VideoDownloader.exe"
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Native apphost was not built: $exe" }
$testProfileDirectory = Join-Path ([IO.Path]::GetTempPath()) ('vd-startup-' + [guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testProfileDirectory)
$process = $null
try {
    $start = [Diagnostics.ProcessStartInfo]::new($exe)
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WorkingDirectory = Split-Path -Parent $exe
    # The real entry point uses a separate data directory, mutex and pipe for
    # this profile. An installed user's queue and running instance are untouched.
    $start.Environment['VD_DATA_DIRECTORY'] = $testProfileDirectory
    $process = [Diagnostics.Process]::Start($start)
    $deadline = [DateTime]::UtcNow.AddSeconds(25)
    while ([DateTime]::UtcNow -lt $deadline) {
        $process.Refresh()
        if ($process.HasExited) { throw "Native GUI exited during startup with code $($process.ExitCode)" }
        if ($process.MainWindowHandle -ne [IntPtr]::Zero -and (Test-Path -LiteralPath (Join-Path $testProfileDirectory 'session.json'))) { break }
        Start-Sleep -Milliseconds 200
    }
    $process.Refresh()
    if ($process.MainWindowHandle -eq [IntPtr]::Zero) { throw 'Native WPF GUI did not open a window' }
    if ($process.MainWindowTitle -cne 'Video Downloader') { throw "Native GUI opened an unexpected window: $($process.MainWindowTitle)" }
    $checkpoint = Join-Path $testProfileDirectory 'session.json'
    if (-not (Test-Path -LiteralPath $checkpoint)) { throw 'The startup smoke did not create its isolated profile checkpoint' }
    $session = Get-Content -LiteralPath $checkpoint -Raw | ConvertFrom-Json
    if ($session.Version -ne 1 -or $session.Queue.Count -ne 0) { throw 'A fresh isolated startup must save an empty v1 queue' }
    Write-Host 'Native WPF entry point, window title and isolated profile: PASS'
}
finally {
    if ($null -ne $process) {
        try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
        finally { $process.Dispose() }
    }
    if (Test-Path -LiteralPath $testProfileDirectory) { Remove-Item -LiteralPath $testProfileDirectory -Recurse -Force }
}
