#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleaseVersion.psm1') -Force

$source = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$selector = Join-Path $PSScriptRoot 'select-main-release.ps1'
$anchor = Get-VdMainReleaseAnchor
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { Join-Path $source 'work' }
$scratch = Join-Path $temporaryRoot "release-history-tests-$([guid]::NewGuid().ToString('N'))"
$origin = Join-Path $scratch 'origin.git'
$checkout = Join-Path $scratch 'checkout'
$projectRelative = 'src/VideoDownloader/VideoDownloader.csproj'
$projectPath = Join-Path $checkout $projectRelative
$outputPath = Join-Path $scratch 'outputs.txt'
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$powerShell = (Get-Process -Id $PID).Path

function Invoke-ReleaseHistoryCommand {
    param(
        [string]$Executable = $git,
        [Parameter(Mandatory)][string[]]$Arguments,
        [string]$Directory = $source,
        [switch]$ExpectFailure,
        [string]$FailurePattern
    )
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Executable
    $start.WorkingDirectory = $Directory
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    # The fixture uses only local paths and should not inherit signing, hooks or
    # interactive credential configuration from the developer's Git settings.
    $start.Environment['GIT_CONFIG_GLOBAL'] = Join-Path $scratch 'empty-gitconfig'
    $start.Environment['GIT_CONFIG_NOSYSTEM'] = '1'
    $start.Environment['GIT_CONFIG_COUNT'] = '0'
    $start.Environment['GIT_TERMINAL_PROMPT'] = '0'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    try {
        if (-not $process.Start()) { throw "Cannot start release history command: $Executable" }
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Release history command timed out: $Executable"
        }
        $output = $stdout.GetAwaiter().GetResult()
        $errorOutput = $stderr.GetAwaiter().GetResult()
        if (($process.ExitCode -eq 0) -eq $ExpectFailure.IsPresent) {
            throw "Release history command exited $($process.ExitCode): $Executable $($Arguments -join ' ')`n$output`n$errorOutput"
        }
        if ($ExpectFailure -and $FailurePattern -and "$output`n$errorOutput" -notmatch $FailurePattern) {
            throw "Unexpected release history failure; wanted $FailurePattern, got: $output`n$errorOutput"
        }
        return $output.Trim()
    } finally {
        $process.Dispose()
    }
}

function New-ReleaseHistoryCommit {
    param([AllowNull()][string]$Version)
    if ([string]::IsNullOrEmpty($Version)) {
        $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('rm', '-f', $projectRelative)
    } else {
        $null = New-Item -ItemType Directory -Path (Split-Path -Parent $projectPath) -Force
        [IO.File]::WriteAllText($projectPath, "<Project><PropertyGroup><Version>$Version</Version></PropertyGroup></Project>", [Text.UTF8Encoding]::new($false))
        $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('add', $projectRelative)
    }
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('commit', '--quiet', '--allow-empty', '-m', "Fixture version $Version")
    $sha = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('rev-parse', 'HEAD')
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('push', '--quiet', 'origin', 'HEAD:refs/heads/main')
    return $sha
}

function Assert-ReleaseHistorySelection {
    param([string]$CommitSha, [AllowNull()][string]$ExpectedTag, [string]$FailurePattern)
    if (Test-Path -LiteralPath $outputPath) { Remove-Item -LiteralPath $outputPath -Force }
    $arguments = @('-NoProfile', '-File', $selector, '-TargetSha', $CommitSha, '-OutputPath', $outputPath)
    if ($ExpectedTag) {
        $null = Invoke-ReleaseHistoryCommand -Executable $powerShell -Directory $checkout -Arguments $arguments
        $lines = @(Get-Content -LiteralPath $outputPath)
        if ("tag=$ExpectedTag" -cnotin $lines -or "version=$($ExpectedTag.Substring(1))" -cnotin $lines) {
            throw "Wrong release for $CommitSha; wanted $ExpectedTag, got: $($lines -join ', ')"
        }
    } else {
        # Capture the expected failing child process separately so its error can
        # be checked without making a PowerShell failure look like a test pass.
        $null = Invoke-ReleaseHistoryCommand -Executable $powerShell -Directory $checkout -Arguments $arguments -ExpectFailure -FailurePattern $FailurePattern
        if (Test-Path -LiteralPath $outputPath) {
            throw 'A rejected history revision must not produce release outputs.'
        }
    }
}

try {
    $null = New-Item -ItemType Directory -Path $scratch -Force
    [IO.File]::WriteAllText((Join-Path $scratch 'empty-gitconfig'), '')
    # All refs, commits and pushes below belong to local fixture repositories.
    # --shared reuses source objects without checking out the large application.
    $null = Invoke-ReleaseHistoryCommand -Arguments @('clone', '--quiet', '--bare', '--shared', $source, $origin)
    $null = Invoke-ReleaseHistoryCommand -Arguments @('--git-dir', $origin, 'update-ref', 'refs/heads/main', $anchor.Commit)
    $null = Invoke-ReleaseHistoryCommand -Arguments @('--git-dir', $origin, 'update-ref', "refs/tags/$($anchor.Tag)", $anchor.Commit)
    $null = Invoke-ReleaseHistoryCommand -Arguments @('--git-dir', $origin, 'symbolic-ref', 'HEAD', 'refs/heads/main')
    $null = Invoke-ReleaseHistoryCommand -Arguments @('clone', '--quiet', '--shared', '--no-checkout', $origin, $checkout)
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('read-tree', 'HEAD')
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('config', 'user.name', 'Release fixture')
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('config', 'user.email', 'release-fixture@example.invalid')
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('config', 'commit.gpgsign', 'false')

    $commitA = New-ReleaseHistoryCommit '2.0.0'
    $commitB = New-ReleaseHistoryCommit '2.0.0'
    Assert-ReleaseHistorySelection $commitB 'v2.0.37'
    Assert-ReleaseHistorySelection $commitA 'v2.0.36'

    # Failed main commits reserve their patch without permanently blocking a
    # fixed descendant. Only the selected revision must have a valid baseline.
    $commitC = New-ReleaseHistoryCommit $null
    Assert-ReleaseHistorySelection $commitC $null -FailurePattern 'Cannot read the project version'
    $commitD = New-ReleaseHistoryCommit '2.1.0'
    Assert-ReleaseHistorySelection $commitD 'v2.1.0'
    Assert-ReleaseHistorySelection $commitA 'v2.0.36'

    # An ordinary ancestry check would accept this merge, but the migration
    # anchor exists only through its second parent and must be rejected.
    $tree = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('rev-parse', 'HEAD^{tree}')
    $orphan = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('commit-tree', $tree, '-m', 'Foreign first-parent root')
    $merge = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('commit-tree', $tree, '-p', $orphan, '-p', $commitD, '-m', 'Anchor only on second parent')
    $null = Invoke-ReleaseHistoryCommand -Directory $checkout -Arguments @('push', '--quiet', '--force', 'origin', "${merge}:refs/heads/main")
    Assert-ReleaseHistorySelection $merge $null -FailurePattern 'migration anchor is not on the first-parent'

    Write-Host 'Real Git release history tests passed: reordered CI, old retry, broken ancestor recovery, baseline bump and first-parent anchor guard.'
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
