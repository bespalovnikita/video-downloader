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
$managedStacksPath = $ResultPath + '.managed-stacks.txt'
$stackTool = $env:VD_NATIVE_UI_STACK_TOOL
try { if (Test-Path -LiteralPath $managedStacksPath) { Remove-Item -LiteralPath $managedStacksPath -Force } }
catch { Write-Host "Cannot reset optional WPF stacks: $($_.Exception.GetType().Name)"; $stackTool = $null }
$stackReports = @{ Active = $null; AttemptedStage = $null; AttemptedHeartbeat = [DateTime]::MinValue; Saved = $false }
function Stop-WpfStackReporter {
    $active = $stackReports.Active
    if ($null -eq $active) { return }
    $stackReports.Active = $null
    try {
        # The diagnostic tool attaches to the UI; it does not own that process.
        # Kill only this reporter, without waiting or extending the UI deadline.
        if (-not $active.Process.HasExited) { $active.Process.Kill() }
    } catch { Write-Host "Cannot stop WPF stack reporter: $($_.Exception.GetType().Name)" }
    finally {
        try { $active.Process.Dispose() }
        catch { Write-Host "Cannot dispose WPF stack reporter: $($_.Exception.GetType().Name)" }
    }
}
function Save-WpfManagedStacks([string]$text) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($text)
    $limit = 256 * 1024
    if ($bytes.Length -gt $limit) {
        $suffix = [Text.Encoding]::UTF8.GetBytes("`n[Managed stacks truncated at 256 KiB]`n")
        $keep = $limit - $suffix.Length
        # End on a UTF-8 boundary rather than splitting a multibyte method name.
        while ($keep -gt 0 -and ($bytes[$keep] -band 0xC0) -eq 0x80) { $keep-- }
        [Array]::Resize([ref]$bytes, $keep + $suffix.Length)
        [Array]::Copy($suffix, 0, $bytes, $keep, $suffix.Length)
    }
    [IO.File]::WriteAllBytes($managedStacksPath, $bytes)
    $stackReports.Saved = $true
}
function Update-WpfStackReporter([DateTime]$now, [bool]$allowStart = $true) {
    try {
        $active = $stackReports.Active
        if ($null -ne $active) {
            if ($active.Process.HasExited -and $active.StandardOutput.IsCompleted -and $active.StandardError.IsCompleted) {
                # These tasks are already complete: neither pipe can block the
                # parent's existing process/stage polling loop.
                $stackOutput = $active.StandardOutput.GetAwaiter().GetResult()
                $stackError = $active.StandardError.GetAwaiter().GetResult()
                if ($active.Process.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($stackOutput)) {
                    Save-WpfManagedStacks ("UTC=$($now.ToString('O')); UI process=$($process.Id); stage=$($active.Stage)`n" +
                        $stackOutput + "`nReporter stderr:`n" + $stackError)
                    Write-Host 'Native WPF managed stacks captured from the parent.'
                } else {
                    $detail = "$stackError`n$stackOutput"
                    Write-Host "WPF stack reporter exited $($active.Process.ExitCode): $($detail.Substring(0, [Math]::Min(4096, $detail.Length)))"
                }
                Stop-WpfStackReporter
            } elseif ($now -ge $active.Deadline) {
                Write-Host 'WPF stack reporter exceeded its 5-second diagnostic deadline.'
                Stop-WpfStackReporter
            }
        }
        if (-not $allowStart -or $null -ne $stackReports.Active -or [string]::IsNullOrWhiteSpace($stackTool)) { return }
        if (-not (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf)) { return }
        $heartbeat = (Get-Item -LiteralPath $diagnosticsPath).LastWriteTimeUtc
        if (($now - $heartbeat).TotalSeconds -lt 6 -or
            ($stackReports.AttemptedStage -ceq $lastStage -and $stackReports.AttemptedHeartbeat -eq $heartbeat)) { return }
        $stackReports.AttemptedStage = $lastStage
        $stackReports.AttemptedHeartbeat = $heartbeat
        $reportStart = [Diagnostics.ProcessStartInfo]::new($stackTool)
        $reportStart.UseShellExecute = $false
        $reportStart.CreateNoWindow = $true
        $reportStart.RedirectStandardOutput = $true
        $reportStart.RedirectStandardError = $true
        $reportStart.ArgumentList.Add('report')
        $reportStart.ArgumentList.Add('--process-id')
        $reportStart.ArgumentList.Add([string]$process.Id)
        $reporter = [Diagnostics.Process]::Start($reportStart)
        $stackReports.Active = @{
            Process = $reporter
            StandardOutput = $null
            StandardError = $null
            Deadline = [DateTime]::UtcNow.AddSeconds(5)
            Stage = $lastStage
        }
        $stackReports.Active.StandardOutput = $reporter.StandardOutput.ReadToEndAsync()
        $stackReports.Active.StandardError = $reporter.StandardError.ReadToEndAsync()
        Write-Host 'Native WPF heartbeat is stale; requesting parent-managed stacks.'
    } catch {
        Write-Host "Optional WPF stack reporter failed: $($_.Exception.GetType().Name)"
        Stop-WpfStackReporter
    }
}
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
        if ($stackReports.Saved -and (Test-Path -LiteralPath $managedStacksPath)) {
            try { Write-Host ([IO.File]::ReadAllText($managedStacksPath)) }
            catch { Write-Host "Cannot read parent-managed WPF stacks: $($_.Exception.GetType().Name)" }
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
        Update-WpfStackReporter $now
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
    Update-WpfStackReporter ([DateTime]::UtcNow) $false
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
    Stop-WpfStackReporter
    try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
    finally { $process.Dispose() }
}
