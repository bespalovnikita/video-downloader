#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$ResultPath = '',
    [ValidateSet('All','SubscriptionInitialActions','WindowsUsability')][string]$Scene = 'All'
)
$ErrorActionPreference = 'Stop'
$Scene = switch ($Scene) {
    'All' { 'All' }
    'WindowsUsability' { 'WindowsUsability' }
    default { 'SubscriptionInitialActions' }
}
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "tests/VideoDownloader.UiTests/bin/$Configuration/net8.0-windows/VideoDownloader.UiTests.exe"
$fixture = Join-Path $root "tests/VideoDownloader.Tests/bin/$Configuration/net8.0-windows/VideoDownloader.Tests.exe"
foreach ($path in @($exe,$fixture)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Build the native application and tests first: $path" }
}
if ($ResultPath.Length -eq 0) {
    $resultDirectory = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else {
        Join-Path ([IO.Path]::GetTempPath()) ('vd-ui-result-' + [guid]::NewGuid().ToString('N'))
    }
    $ResultPath = Join-Path $resultDirectory 'native-ui-result.txt'
}
$ResultPath = [IO.Path]::GetFullPath($ResultPath)
[void][IO.Directory]::CreateDirectory((Split-Path -Parent $ResultPath))
if (Test-Path -LiteralPath $ResultPath) { Remove-Item -LiteralPath $ResultPath -Force }
$diagnosticsPath = $ResultPath + '.diagnostics.txt'
if (Test-Path -LiteralPath $diagnosticsPath) { Remove-Item -LiteralPath $diagnosticsPath -Force }
if (-not ('VdUiWindowDiagnostics' -as [type])) {
    Add-Type -Path (Join-Path $PSScriptRoot 'UiWindowDiagnostics.cs')
}
$screenshot = Join-Path (Split-Path -Parent $ResultPath) 'subscription-progress.png'
if (Test-Path -LiteralPath $screenshot) { Remove-Item -LiteralPath $screenshot -Force }
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = Split-Path -Parent $exe
$start.Environment['VD_NATIVE_UI_TEST_RESULT'] = $ResultPath
$start.Environment['VD_NATIVE_TITLE_FIXTURE'] = $fixture
$start.Environment['VD_NATIVE_UI_TEST_SCENE'] = $Scene
$process = [Diagnostics.Process]::Start($start)
try {
    # This is the whole WPF process budget; individual scenario waits retain
    # their own short deadlines as the number of real windows grows.
    $deadline = [DateTime]::UtcNow.AddSeconds(180)
    $captureAt = $deadline.AddSeconds(-10)
    $lastStage = ''
    $stageSince = [DateTime]::UtcNow
    $capturedStage = $false
    $finalSnapshot = $false
    $windowSnapshot = '(no window snapshot yet)'
    function Write-WpfFailureDiagnostics {
        Write-Host "Last native-window snapshot: $windowSnapshot"
        if (Test-Path -LiteralPath $diagnosticsPath) {
            try {
                $stream = [IO.FileStream]::new($diagnosticsPath,[IO.FileMode]::Open,[IO.FileAccess]::Read,
                    [IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete)
                $reader = [IO.StreamReader]::new($stream)
                try { Write-Host ($reader.ReadToEnd()) } finally { $reader.Dispose() }
            } catch { Write-Host "Cannot read WPF diagnostic heartbeat: $($_.Exception.GetType().Name)" }
        }
    }
    while (-not $process.WaitForExit(250)) {
        if (Test-Path -LiteralPath $ResultPath) {
            $stage = Get-Content -LiteralPath $ResultPath -Raw
            if ($stage -ne $lastStage) {
                Write-Host "Native UI stage: $stage"; $lastStage = $stage
                $stageSince = [DateTime]::UtcNow; $capturedStage = $false
            }
        }
        $now = [DateTime]::UtcNow
        if ((-not $capturedStage -and $now -ge $stageSince.AddSeconds(10)) -or
            (-not $finalSnapshot -and $now -ge $captureAt)) {
            # Capture before the unchanged process deadline. Each title read
            # has a 50 ms bound, including an unresponsive native dialog.
            $windowSnapshot = "$($now.ToString('O'))`n" + [VdUiWindowDiagnostics]::Capture($process.Id)
            $capturedStage = $true
            if ($now -ge $captureAt) { $finalSnapshot = $true }
        }
        if ([DateTime]::UtcNow -gt $deadline) {
            Write-WpfFailureDiagnostics
            throw "Native WPF tests timed out; last stage=$lastStage"
        }
    }
    $process.WaitForExit()
    $lastStage = if (Test-Path -LiteralPath $ResultPath) { Get-Content -LiteralPath $ResultPath -Raw } else { '(no stage marker written)' }
    if ($process.ExitCode -ne 0 -or $lastStage.Trim() -cne 'PASS') {
        Write-WpfFailureDiagnostics
        throw "Native WPF tests failed: exit code $($process.ExitCode); last stage=$lastStage"
    }
    if ($Scene -eq 'All') {
        if (-not (Test-Path -LiteralPath $screenshot -PathType Leaf)) { throw 'The UI tests did not save the subscription progress screenshot' }
        Write-Host "Native WPF interaction, recovery and checkpoint tests: PASS ($ResultPath)"
    }
    else {
        Write-Host "Focused native WPF $Scene scenario: PASS ($ResultPath)"
    }
}
finally {
    try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
    finally { $process.Dispose() }
}
