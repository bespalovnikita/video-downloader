#requires -Version 7.0
# Independent safety regression: never delete/overwrite anything outside the
# explicit output root, even via NTFS junction or symlink directory segments.
$ErrorActionPreference="Stop"
Import-Module (Join-Path (Split-Path -Parent $PSScriptRoot) "lib/VideoDownloader.Cancellation.psm1") -Force

function Assert([bool]$Value,[string]$Message) {
    if (-not $Value) { throw $Message }
}
function Expect-Refusal([scriptblock]$Action,[string]$Message) {
    $refused=$false
    try { & $Action | Out-Null } catch { $refused=$true }
    Assert $refused $Message
}
$testRoot=Join-Path ([IO.Path]::GetTempPath()) ("vd-file-boundary-"+[guid]::NewGuid().ToString("N"))
$output=Join-Path $testRoot "downloads"
$outside=Join-Path $testRoot "protected-outside"
$staging=Join-Path $testRoot "stage"
[void][IO.Directory]::CreateDirectory($output)
[void][IO.Directory]::CreateDirectory($outside)
[void][IO.Directory]::CreateDirectory($staging)
$outsideSentinel=Join-Path $outside "system-like-sentinel.bin"
[IO.File]::WriteAllText($outsideSentinel,"KEEP THIS DATA: "+[guid]::NewGuid())
$sentinel=[IO.File]::ReadAllText($outsideSentinel)
$preexisting=Join-Path $output "unrelated-existing.mp4"
[IO.File]::WriteAllText($preexisting,"PREEXISTING")
$caseCount=0

function Assert-Sentinels {
    Assert ([IO.File]::Exists($outsideSentinel)) "Protected file outside root has been deleted"
    Assert (([IO.File]::ReadAllText($outsideSentinel)) -ceq $sentinel) "Protected file outside root was modified"
    Assert ([IO.File]::Exists($preexisting)) "Pre-existing, unrelated in-root file was deleted"
    Assert (([IO.File]::ReadAllText($preexisting)) -ceq "PREEXISTING") "Pre-existing file was modified"
}

try {
    # Paths that are not underneath the exact destination folder.
    foreach ($candidate in @(
        $outsideSentinel,
        (Join-Path $output "../protected-outside/system-like-sentinel.bin"),
        (Join-Path $testRoot "downloads-trick/sentinel.bin"),
        (Join-Path $output "../../outside.bin")
    )) {
        Expect-Refusal { Remove-VdOwnedFiles -OutputRoot $output -Paths @($candidate) } "Deletion must refuse an escaped or sibling path: $candidate"
        $caseCount++
        Assert-Sentinels
    }
    # The test must work with absolute and mixed separator canonicalization.
    $valid=Join-Path $output "owned.mp4"
    [IO.File]::WriteAllText($valid,"OWNED")
    [void](Remove-VdOwnedFiles -OutputRoot $output -Paths @($valid))
    Assert (-not [IO.File]::Exists($valid)) "Deletion of truly owned in-root file must still work"
    Assert-Sentinels

    if ($IsWindows) {
        # Junctions do not require elevated symlink creation privileges on CI.
        $junction=Join-Path $output "outside-junction"
        New-Item -Path $junction -ItemType Junction -Target $outside -ErrorAction Stop | Out-Null
        $viaJunction=Join-Path $junction "system-like-sentinel.bin"
        Assert (Test-Path -LiteralPath $viaJunction) "NTFS junction test fixture is not operational"
        Expect-Refusal { Remove-VdOwnedFiles -OutputRoot $output -Paths @($viaJunction) } "Deletion through a junction must be forbidden"
        Assert-Sentinels
        $caseCount++
        $subfolder=Join-Path $junction "new-subfolder"
        $stageFile=Join-Path $staging "new-asset.mp4"
        [IO.File]::WriteAllText($stageFile,"STAGED")
        Expect-Refusal { Publish-VdStagedFiles -StageDir $staging -OutputRoot $subfolder -MediaPath $stageFile } "Publishing through a junction must be forbidden"
        Assert (-not (Test-Path (Join-Path $outside "new-subfolder/new-asset.mp4"))) "Staged output escaped through junction"
        Assert-Sentinels
        $caseCount++

        # A symlinked root itself must also be rejected, not just inner dirs.
        $rootJunction=Join-Path $testRoot "root-is-junction"
        New-Item -Path $rootJunction -ItemType Junction -Target $outside -ErrorAction Stop | Out-Null
        Expect-Refusal { Remove-VdOwnedFiles -OutputRoot $rootJunction -Paths @((Join-Path $rootJunction "system-like-sentinel.bin")) } "Symlinked output root must be forbidden"
        Assert-Sentinels
        $caseCount++

        # A junction in the staging tree must not import outside data.
        $unsafeStage=Join-Path $staging "nested"
        New-Item -Path $unsafeStage -ItemType Junction -Target $outside -ErrorAction Stop | Out-Null
        Expect-Refusal { Publish-VdStagedFiles -StageDir $staging -OutputRoot $output -MediaPath $stageFile } "Staging with reparse directories must be refused"
        Assert-Sentinels
        $caseCount++

        # File symbolic links are also forbidden if creation is permitted by
        # runner privilege / developer mode (junction checks are mandatory).
        $symbolic=Join-Path $output "symlink-to-sentinel.bin"
        try {
            New-Item -Path $symbolic -ItemType SymbolicLink -Target $outsideSentinel -ErrorAction Stop | Out-Null
            Expect-Refusal { Remove-VdOwnedFiles -OutputRoot $output -Paths @($symbolic) } "File symbolic link must be forbidden"
            Assert-Sentinels
            $caseCount++
        } catch [System.UnauthorizedAccessException] {
            Write-Host "File-symlink fixture skipped: Windows runner lacks symlink privilege."
        }
    }

    # A file collision must not be treated as new media ownership; otherwise
    # an ordinary Remove in the GUI could delete a pre-existing file.
    $collisionStage=Join-Path $testRoot "collision-stage"
    [void][IO.Directory]::CreateDirectory($collisionStage)
    $collisionMedia=Join-Path $collisionStage "unrelated-existing.mp4"
    [IO.File]::WriteAllText($collisionMedia,"NEW DOWNLOAD")
    Expect-Refusal { Publish-VdStagedFiles -StageDir $collisionStage -OutputRoot $output -MediaPath $collisionMedia } "Collision with existing file must refuse publishing"
    Assert-Sentinels
    $caseCount++

    # Mixed-batch removal: the dangerous escaped file must never be reached.
    $safe=Join-Path $output "owned-second.mp4"
    [IO.File]::WriteAllText($safe,"OWNED")
    Expect-Refusal { Remove-VdOwnedFiles -OutputRoot $output -Paths @($outsideSentinel) } "Mixed removal must refuse outside path"
    Assert-Sentinels
    $caseCount++
    Write-Host "Boundary deletion safety: $caseCount rejected escape cases; outside and pre-existing sentinels unchanged."
} finally {
    # Remove junctions as links, not their targets. Preserve the protected
    # sentinel until all assertions have completed.
    foreach ($link in @((Join-Path $output "outside-junction"),(Join-Path $testRoot "root-is-junction"),(Join-Path $staging "nested"))) {
        if (Test-Path -LiteralPath $link) {
            try { [IO.Directory]::Delete($link) } catch {}
        }
    }
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
