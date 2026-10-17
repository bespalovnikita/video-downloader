#requires -Version 7.0
$ErrorActionPreference = "Stop"

if (-not $IsWindows) { throw "Tray lifecycle test is Windows-only." }
if ([Threading.Thread]::CurrentThread.GetApartmentState() -ne [Threading.ApartmentState]::STA) {
    throw "Tray lifecycle test must run in STA: pwsh -STA -File tests/tray-lifecycle-tests.ps1"
}

$root = Split-Path -Parent $PSScriptRoot
$guiText = Get-Content (Join-Path $root "ytdl-manager-gui.ps1") -Raw -Encoding UTF8
if ($guiText -match '\$window\.ShowDialog\s*\(') {
    throw "Modal ShowDialog terminates the GUI script when the window is hidden."
}
if (-not $guiText.Contains('Start-VdGuiMessageLoop -Window $window')) {
    throw "The GUI must use the tested, non-modal message loop."
}

Add-Type -AssemblyName PresentationFramework
Import-Module (Join-Path $root "lib/VideoDownloader.WindowLifecycle.psm1") -Force

$window = [System.Windows.Window]::new()
$window.Title = "Video Downloader tray lifecycle test"
$window.Width = 240
$window.Height = 120
$window.ShowInTaskbar = $false

$script:phase = 0
$script:closed = $false
$script:failure = $null

# Exercise the same minimize-to-tray / restore / exit sequence as the GUI.
$window.Add_StateChanged({
    if ($window.WindowState -eq [System.Windows.WindowState]::Minimized) {
        $window.Hide()
    }
})
$window.Add_Closing({ $script:closed = $true })

$timer = [System.Windows.Threading.DispatcherTimer]::new()
$timer.Interval = [TimeSpan]::FromMilliseconds(100)
$timer.Add_Tick({
    try {
        switch ($script:phase) {
            0 {
                $window.WindowState = [System.Windows.WindowState]::Minimized
                $script:phase = 1
            }
            1 {
                if ($window.IsVisible) { throw "Minimize did not hide the window." }
                # Reaching this phase proves the application stayed alive in tray.
                $window.Show()
                $window.WindowState = [System.Windows.WindowState]::Normal
                $window.Activate() | Out-Null
                $script:phase = 2
            }
            2 {
                if (-not $window.IsVisible) { throw "Restore did not show the window." }
                if ($window.WindowState -ne [System.Windows.WindowState]::Normal) {
                    throw "Restore did not reset WindowState."
                }
                $script:phase = 3
                $window.Close()
            }
            default {
                throw "Unexpected timer tick after the close request."
            }
        }
    }
    catch {
        $script:failure = $_.Exception.Message
        $window.Close()
    }
})

try {
    $timer.Start()
    Start-VdGuiMessageLoop -Window $window
}
finally {
    $timer.Stop()
}

if ($script:failure) { throw $script:failure }
if ($script:phase -ne 3 -or -not $script:closed) {
    throw "GUI message loop returned before the normal Close/Exit lifecycle completed."
}
Write-Host "Tray minimize, restore and exit lifecycle tests passed."
