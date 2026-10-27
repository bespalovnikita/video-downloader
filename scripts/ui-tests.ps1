#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug','Release')][string]$Configuration = 'Release',
    [string]$ResultPath = ''
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "native/bin/$Configuration/net8.0-windows/win-x64/VideoDownloader.exe"
$fixture = Join-Path $root "native-tests/bin/$Configuration/net8.0-windows/VideoDownloader.NativeTests.exe"
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
$screenshot = Join-Path (Split-Path -Parent $ResultPath) 'subscription-progress.png'
if (Test-Path -LiteralPath $screenshot) { Remove-Item -LiteralPath $screenshot -Force }
$start = [Diagnostics.ProcessStartInfo]::new($exe)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.WorkingDirectory = Split-Path -Parent $exe
$start.ArgumentList.Add('--ui-self-test')
$start.Environment['VD_NATIVE_UI_TEST_RESULT'] = $ResultPath
$start.Environment['VD_NATIVE_TITLE_FIXTURE'] = $fixture
$process = [Diagnostics.Process]::Start($start)
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    $lastStage = ''
    while (-not $process.WaitForExit(250)) {
        if (Test-Path -LiteralPath $ResultPath) {
            $stage = Get-Content -LiteralPath $ResultPath -Raw
            if ($stage -ne $lastStage) { Write-Host "Native UI stage: $stage"; $lastStage = $stage }
        }
        if ([DateTime]::UtcNow -gt $deadline) { throw "Native WPF tests timed out; last stage=$lastStage" }
    }
    $process.WaitForExit()
    $lastStage = if (Test-Path -LiteralPath $ResultPath) { Get-Content -LiteralPath $ResultPath -Raw } else { '(no stage marker written)' }
    if ($process.ExitCode -ne 0 -or $lastStage.Trim() -cne 'PASS') {
        throw "Native WPF tests failed: exit code $($process.ExitCode); last stage=$lastStage"
    }
    if (-not (Test-Path -LiteralPath $screenshot -PathType Leaf)) { throw 'The UI tests did not save the subscription progress screenshot' }
    Write-Host "Native WPF interaction, recovery and checkpoint tests: PASS ($ResultPath)"
}
finally {
    try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) } }
    finally { $process.Dispose() }
}
