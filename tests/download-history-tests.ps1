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
        $calls++
        if ($calls -eq 1) { "No" } else { "Cancel" }
    }
    Assert ($cancelled.Cancelled -and $cancelled.Selected.Count -eq 0 -and $cancelled.Remove.Count -eq 0) "Cancel must roll back all earlier decisions"
    $none = Resolve-VdDuplicateDownloadPlan -Urls @($fresh) -Known $known -Prompt { throw "Non-matches must not prompt" }
    Assert ($none.Selected.Count -eq 1 -and $none.Remove.Count -eq 0) "No duplicate case must not prompt"
    $empty = Resolve-VdDuplicateDownloadPlan -Urls @() -Known $known -Prompt { throw "Empty queue must not prompt" }
    Assert (-not $empty.Cancelled -and $empty.Selected.Count -eq 0) "Empty selection must do nothing"
    Expect-Throw { Resolve-VdDuplicateDownloadPlan -Urls @($upper) -Known $known -Prompt { "Unknown" } } "Invalid decision must fail closed"

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
