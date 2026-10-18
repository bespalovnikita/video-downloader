#requires -Version 7.0
$ErrorActionPreference = "Stop"
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.DownloadHistory.psm1") -Force

function Assert([bool]$Condition,[string]$Description) {
    if (-not $Condition) { throw $Description }
}
function Expect-Throw([scriptblock]$Block,[string]$Description) {
    $failed = $false
    try { & $Block | Out-Null } catch { $failed = $true }
    Assert $failed $Description
}

$dir = Join-Path ([IO.Path]::GetTempPath()) ("vd-history-test-" + [guid]::NewGuid().ToString("N"))
$path = Join-Path $dir "success.jsonl"
[void][IO.Directory]::CreateDirectory($dir)
try {
    $known = Read-VdDownloadHistory -Path $path
    Assert ($known.Count -eq 0) "Missing history should start empty"

    # Regression: a brand-new installation has no success-history file.
    # GUI creates an empty HashSet and passes an empty timestamp dictionary.
    # PowerShell must bind both values without rejecting the new download.
    $script:unexpectedHistoryPrompts = 0
    $freshUrls = @("https://example.com/first", "https://example.com/second")
    $emptyRecords = Read-VdDownloadHistory -Path $path -IncludeTimestamps
    Assert ($emptyRecords.Count -eq 0) "Missing timestamp history should be empty"
    $firstRun = Resolve-VdDuplicateDownloadPlan -Urls $freshUrls -Known $known -LastDownloads $emptyRecords -Prompt {
        $script:unexpectedHistoryPrompts++
        throw "The first download must not prompt for duplicates"
    }
    Assert (-not $firstRun.Cancelled -and $firstRun.Selected.Count -eq 2 -and $firstRun.Remove.Count -eq 0) "Fresh installation must allow all downloads"
    Assert ($firstRun.Selected[0] -eq $freshUrls[0] -and $firstRun.Selected[1] -eq $freshUrls[1]) "First download must preserve URL order"
    Assert ($script:unexpectedHistoryPrompts -eq 0) "Empty history must never show a duplicate confirmation"
    $emptyFirstRun = Resolve-VdDuplicateDownloadPlan -Urls @() -Known $known -LastDownloads $emptyRecords -Prompt {
        throw "An empty queue must not trigger a duplicate confirmation"
    }
    Assert (-not $emptyFirstRun.Cancelled -and $emptyFirstRun.Selected.Count -eq 0) "Empty history and empty queue must be accepted"

    # Existing but empty history file exercises the same GUI call path.
    [IO.File]::WriteAllText($path, "")
    $knownFromEmptyFile = Read-VdDownloadHistory -Path $path
    $recordsFromEmptyFile = Read-VdDownloadHistory -Path $path -IncludeTimestamps
    $withEmptyFile = Resolve-VdDuplicateDownloadPlan -Urls @("https://example.com/fresh") -Known $knownFromEmptyFile -LastDownloads $recordsFromEmptyFile -Prompt {
        throw "Empty history file must not prompt"
    }
    Assert ($withEmptyFile.Selected.Count -eq 1 -and $withEmptyFile.Remove.Count -eq 0) "Empty history file must allow a download"
    $upper = "https://example.com/Video?q=a%20b"
    $lower = "https://example.com/video?q=a%20b"
    $another = "https://example.com/another"
    $fresh = "https://example.com/new"
    Add-VdSuccessfulDownloadUrl -Path $path -Url $upper
    Add-VdSuccessfulDownloadUrl -Path $path -Url $another
    Add-VdSuccessfulDownloadUrl -Path $path -Url $upper
    $known = Read-VdDownloadHistory -Path $path
    Assert ($known.Count -eq 2 -and $known.Contains($upper) -and $known.Contains($another)) "Success history must persist all unique URLs across multiple writes"
    Assert (-not $known.Contains($lower)) "History URL matching must be ordinal/case-sensitive"
    Assert ((Get-Content $path).Count -eq 3) "History must append, not overwrite, successful attempts"

    $queue = @($fresh,$upper,$another,$lower)
    $script:promptCount = 0
    $plan = Resolve-VdDuplicateDownloadPlan -Urls $queue -Known $known -Prompt {
        param($url,$i,$n)
        $script:promptCount++
        Assert ($i -eq $script:promptCount -and $n -eq 2) "Prompt should receive correct duplicate index and total"
        if ($url -eq $upper) { return "Yes" }
        return "No"
    }
    Assert (-not $plan.Cancelled -and $script:promptCount -eq 2) "Yes/No must prompt per matching URL"
    Assert ($plan.Selected.Count -eq 3 -and $plan.Remove.Count -eq 1 -and $plan.Remove[0] -eq $another) "No must remove only the selected matching URL"
    Assert ($plan.Selected[0] -eq $fresh -and $plan.Selected[1] -eq $upper -and $plan.Selected[2] -eq $lower) "Yes must keep matched URL without dropping other items"
    Assert ($queue.Count -eq 4) "Planner must not mutate the input queue"

    foreach ($mode in @("YesAll","NoAll")) {
        $script:calls = 0
        $result = Resolve-VdDuplicateDownloadPlan -Urls $queue -Known $known -Prompt {
            param($url,$index,$total)
            $script:calls++
            return $mode
        }
        Assert ($script:calls -eq 1) "$mode should prompt only once and apply to later matches"
        if ($mode -eq "YesAll") {
            Assert ($result.Selected.Count -eq 4 -and $result.Remove.Count -eq 0) "YesAll must retain every URL"
        } else {
            Assert ($result.Selected.Count -eq 2 -and $result.Remove.Count -eq 2) "NoAll must remove both matches only"
            Assert ($result.Selected[0] -eq $fresh -and $result.Selected[1] -eq $lower) "NoAll must retain non-matches"
        }
    }

    $script:calls = 0
    $cancelled = Resolve-VdDuplicateDownloadPlan -Urls $queue -Known $known -Prompt {
        param($url)
        $script:calls++
        if ($script:calls -eq 1) { "No" } else { "Cancel" }
    }
    Assert ($cancelled.Cancelled -and $cancelled.Selected.Count -eq 0 -and $cancelled.Remove.Count -eq 0) "Cancel must roll back all earlier decisions"
    $none = Resolve-VdDuplicateDownloadPlan -Urls @($fresh) -Known $known -Prompt { throw "Non-matches must not prompt" }
    Assert ($none.Selected.Count -eq 1 -and $none.Remove.Count -eq 0) "No duplicate case must not prompt"
    $empty = Resolve-VdDuplicateDownloadPlan -Urls @() -Known $known -Prompt { throw "Empty queue must not prompt" }
    Assert (-not $empty.Cancelled -and $empty.Selected.Count -eq 0) "Empty selection must do nothing"
    Expect-Throw { Resolve-VdDuplicateDownloadPlan -Urls @($upper) -Known $known -Prompt { "Unknown" } } "Invalid decision must fail closed"

    # Independent runspaces concurrently append to the same long-lived history.
    $moduleFile = Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.DownloadHistory.psm1"
    $jobs = @(
        1..3 | ForEach-Object {
            Start-ThreadJob -ArgumentList $moduleFile,$path,$_ -ScriptBlock {
                param($module,$historyFile,$worker)
                Import-Module $module -Force
                foreach ($n in 1..8) {
                    Add-VdSuccessfulDownloadUrl -Path $historyFile -Url "https://example.com/worker-$worker/item-$n"
                }
            }
        }
    )
    try {
        $finished = @($jobs | Wait-Job -Timeout 45)
        Assert ($finished.Count -eq $jobs.Count) "Concurrent append timed out"
        foreach ($job in $jobs) {
            Assert ($job.State -eq "Completed") "Concurrent append job failed"
            Receive-Job $job -ErrorAction Stop | Out-Null
        }
        $concurrentHistory = Read-VdDownloadHistory -Path $path
        Assert ($concurrentHistory.Count -eq 26) "Concurrent append lost or corrupted successful URLs"
    } finally {
        foreach ($job in $jobs) { Remove-Job $job -Force -ErrorAction SilentlyContinue }
    }

    $sizeBefore = (Get-Item $path).Length
    Expect-Throw { Add-VdSuccessfulDownloadUrl -Path $path -Url "file:///oops" } "History must reject non-HTTP entries"
    Assert ((Get-Item $path).Length -eq $sizeBefore) "Invalid entry must never alter history"
    [IO.File]::AppendAllText($path,'{"Version":1,"Url":')
    Expect-Throw { Read-VdDownloadHistory -Path $path } "Malformed history must refuse reads instead of losing duplicates"
    Assert ((Get-Item $path).Length -gt $sizeBefore) "Corrupt history must not be deleted"
    [IO.File]::WriteAllText($path,'{"Version":9,"Url":"https://example.com/x"}')
    Expect-Throw { Read-VdDownloadHistory -Path $path } "Unsupported history version must refuse reads"
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Global success history and duplicate decision tests passed."
