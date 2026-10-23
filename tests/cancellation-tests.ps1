#requires -Version 7.0
$ErrorActionPreference = "Stop"
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.Cancellation.psm1") -Force
function Assert([bool]$Test,[string]$Message) { if (-not $Test) { throw $Message } }
function Expect-Throw([scriptblock]$Action,[string]$Message) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true }
    Assert $caught $Message
}
$dir=Join-Path ([IO.Path]::GetTempPath()) ("vd-cancel-tests-"+[guid]::NewGuid().ToString("N"))
$cancelDir=Join-Path $dir "signals"
$output=Join-Path $dir "output"
$stage=Join-Path $dir "staging"
[void][IO.Directory]::CreateDirectory($stage)
[void][IO.Directory]::CreateDirectory($output)
try {
    $u1="https://example.org/Video"
    $u2="https://example.org/video"
    Assert (-not (Test-VdCancellation -Directory $cancelDir -Url $u1)) "No cancellation before marker"
    Request-VdCancellation -Directory $cancelDir -Url $u1
    Assert (Test-VdCancellation -Directory $cancelDir -Url $u1) "Cancelled URL must be signalled"
    Assert (-not (Test-VdCancellation -Directory $cancelDir -Url $u2)) "URL cancellation must be ordinal case-sensitive"
    Request-VdCancellation -Directory $cancelDir -Url $u2
    Assert (Test-VdCancellation -Directory $cancelDir -Url $u2) "Second URL should cancel separately"
    Request-VdCancellation -Directory $cancelDir -Url $u1
    Assert (@(Get-ChildItem -LiteralPath $cancelDir -Filter "*.cancel").Count -eq 2) "Duplicate signal must be idempotent"

    $previousFile=Join-Path $output "previous.mp4"
    [IO.File]::WriteAllText($previousFile,"PREVIOUS")
    $stageMedia=Join-Path $stage "new.mp4"
    $stageSubtitle=Join-Path $stage "new.en.srt"
    [IO.File]::WriteAllText($stageMedia,"NEW")
    [IO.File]::WriteAllText($stageSubtitle,"subtitle")
    $published=Publish-VdStagedFiles -StageDir $stage -OutputRoot $output -MediaPath $stageMedia
    Assert ($published.OwnedPaths.Count -eq 2) "Publishing should track media and sidecars"
    Assert ($published.Path -eq (Join-Path $output "new.mp4")) "Published media path must be absolute output path"
    Assert ((Test-Path $published.Path) -and (Test-Path $previousFile)) "Publishing must retain new and existing downloads"
    [void](Remove-VdOwnedFiles -OutputRoot $output -Paths @($published.OwnedPaths))
    Assert (-not (Test-Path $published.Path)) "Remove must delete newly created output"
    Assert (-not (Test-Path (Join-Path $output "new.en.srt"))) "Remove must delete sidecar files"
    Assert (Test-Path $previousFile) "Unrelated file must not be deleted"

    # Even if yt-dlp creates a filename that already exists, never claim it as
    # owned by the new download, and never overwrite or delete the original.
    [IO.File]::WriteAllText($stageMedia,"REPLACEMENT")
    $duplicate=Publish-VdStagedFiles -StageDir $stage -OutputRoot $output -MediaPath $stageMedia
    Assert ($duplicate.OwnedPaths.Count -eq 1) "Stage should publish only the new unique media file"
    Assert (([IO.File]::ReadAllText($previousFile)) -eq "PREVIOUS") "Existing outputs must remain untouched"
    [IO.File]::WriteAllText($stageMedia,"MUST-NOT-OVERWRITE")
    [IO.File]::WriteAllText((Join-Path $output "new.mp4"),"ALREADY HERE")
    Expect-Throw { Publish-VdStagedFiles -StageDir $stage -OutputRoot $output -MediaPath $stageMedia } "Collision must fail closed: never claim a pre-existing file as owned"
    Assert (([IO.File]::ReadAllText((Join-Path $output "new.mp4"))) -eq "ALREADY HERE") "Existing media must never be overwritten"
    [IO.File]::WriteAllText($stageSubtitle,'COPIED SUBTITLE')
    $copy=Publish-VdStagedFiles -StageDir $stage -OutputRoot $output -MediaPath $stageMedia -RenameCollisions
    Assert ($copy.Path -eq (Join-Path $output 'new (2).mp4')) 'Approved repeat must choose a free media name'
    Assert ($copy.OwnedPaths -contains (Join-Path $output 'new (2).en.srt')) 'Repeated media and subtitles must retain a shared stem'
    [void](Remove-VdOwnedFiles -OutputRoot $output -Paths $copy.OwnedPaths)
    Assert (([IO.File]::ReadAllText((Join-Path $output 'new.mp4'))) -eq 'ALREADY HERE') 'Removing a repeated download must preserve the original'

    $outside=Join-Path $dir "unrelated.mp4"
    [IO.File]::WriteAllText($outside,"DON'T TOUCH")
    Expect-Throw { Remove-VdOwnedFiles -OutputRoot $output -Paths @($outside) } "Refuse deleting outside root"
    Assert (Test-Path $outside) "Outside root file survives"
    $pathTraversal=Join-Path $output "..\unrelated.mp4"
    Expect-Throw { Remove-VdOwnedFiles -OutputRoot $output -Paths @($pathTraversal) } "Refuse traversal path"
    Assert (Test-Path $outside) "Traversal cannot delete outside file"
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host "Per-URL cancellation and owned-file safety tests passed."
