#requires -Version 7.0
$ErrorActionPreference = "Stop"
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.LiveQueue.psm1") -Force

function Assert([bool]$value,[string]$message) { if (-not $value) { throw $message } }
function Assert-Throws([scriptblock]$action,[string]$message) {
    $thrown = $false
    try { & $action | Out-Null } catch { $thrown = $true }
    Assert $thrown $message
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("vd-live-queue-" + [guid]::NewGuid().ToString("N"))
$path = Join-Path $dir "incoming.txt"
[void][IO.Directory]::CreateDirectory($dir)
try {
    Assert (@(Read-VdLiveQueueUrls -Path $path).Count -eq 0) "Missing live queue must be empty"
    $upper = "https://example.com/Video?source=clipboard"
    $lower = "https://example.com/video?source=clipboard"
    $another = "https://example.com/watch?v=123"
    Add-VdLiveQueueUrls -Path $path -Urls @($upper,$upper,$lower)
    Add-VdLiveQueueUrls -Path $path -Urls @($another)
    $values = @(Read-VdLiveQueueUrls -Path $path)
    Assert ($values.Count -eq 3) "Batch duplicates should be collapsed, separate append retained"
    Assert ($values[0] -ceq $upper -and $values[1] -ceq $lower -and $values[2] -eq $another) "Live ingress must retain URL order and case"
    $oldSize = (Get-Item -LiteralPath $path).Length
    Assert-Throws { Add-VdLiveQueueUrls -Path $path -Urls @("https://example.com/ok","file:///bad") } "Invalid entry must reject entire batch"
    Assert ((Get-Item -LiteralPath $path).Length -eq $oldSize) "Invalid entry must not partly write"
    Assert-Throws { Add-VdLiveQueueUrls -Path $path -Urls @("https://example.com/a`nb") } "Multiline URLs must be rejected"
    Add-VdLiveQueueUrls -Path $path -Urls @()
    Assert ((Get-Item -LiteralPath $path).Length -eq $oldSize) "Empty append must be a no-op"

    # Multiple independent runspaces simulate GUI writes while engine polls.
    $module = Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.LiveQueue.psm1"
    $jobs = @(
        1..3 | ForEach-Object {
            Start-ThreadJob -ArgumentList $module,$path,$_ -ScriptBlock {
                param($moduleFile,$file,$worker)
                Import-Module $moduleFile -Force
                foreach ($i in 1..15) {
                    Add-VdLiveQueueUrls -Path $file -Urls @("https://example.com/$worker/$i")
                    # Read under the same lock to catch torn writes.
                    [void]@(Read-VdLiveQueueUrls -Path $file)
                }
            }
        }
    )
    try {
        [void]($jobs | Wait-Job -Timeout 45)
        foreach ($j in $jobs) {
            Assert ($j.State -eq "Completed") "Live queue writer job failed or timed out"
            Receive-Job $j -ErrorAction Stop | Out-Null
        }
        $values = @(Read-VdLiveQueueUrls -Path $path)
        Assert ($values.Count -eq 48) "Concurrent appended URLs must never be lost or torn"
        $uniqueUrls = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($value in $values) { [void]$uniqueUrls.Add($value) }
        Assert ($uniqueUrls.Count -eq 48) "Concurrent writes must not duplicate URL content"
    } finally {
        foreach ($j in $jobs) { Remove-Job $j -Force -ErrorAction SilentlyContinue }
    }

    [IO.File]::AppendAllText($path, "not-a-url`n")
    Assert-Throws { Read-VdLiveQueueUrls -Path $path } "Invalid ingress data must fail rather than silently issue a bad download"
    [IO.File]::WriteAllText($path, "https://example.com/recovered`n")
    $restored = @(Read-VdLiveQueueUrls -Path $path)
    Assert ($restored.Count -eq 1 -and $restored[0] -eq "https://example.com/recovered") "Ingress must remain usable after a bad record is fixed"
} finally {
    Remove-Item -LiteralPath $dir -Force -Recurse -ErrorAction SilentlyContinue
}
Write-Host "Live queue append/poll/concurrency tests passed."
