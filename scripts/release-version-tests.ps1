#requires -Version 7.0
$ErrorActionPreference = "Stop"
Import-Module (Join-Path $PSScriptRoot "../.github/scripts/ReleaseVersion.psm1") -Force

# Versions belong to main revisions, not to the order in which CI finishes.
$anchor = Get-VdMainReleaseAnchor
$commitA = 'a' * 40
$commitB = 'b' * 40
$commitC = 'c' * 40
$commitD = 'd' * 40
$commitE = 'e' * 40
$history = @(
    [pscustomobject]@{ Sha = $anchor.Commit; BaseVersion = '2.0.0' },
    [pscustomobject]@{ Sha = $commitA; BaseVersion = '2.0.0' },
    [pscustomobject]@{ Sha = $commitB; BaseVersion = '2.0.0' },
    [pscustomobject]@{ Sha = $commitC; BaseVersion = '2.1.0' },
    [pscustomobject]@{ Sha = $commitD; BaseVersion = '2.1.0' },
    [pscustomobject]@{ Sha = $commitE; BaseVersion = '2.0.0' }
)
# B passes first while A is delayed/failed; its unused slot remains reserved.
$b = Get-VdMainReleasePlan -History $history -TargetSha $commitB
$a = Get-VdMainReleasePlan -History $history -TargetSha $commitA
if ($b.Tag -cne 'v2.0.37' -or $a.Tag -cne 'v2.0.36' -or $a.AndroidVersionCode -ge $b.AndroidVersionCode) {
    throw 'Reordered CI must preserve main and Android update order.'
}
$retryA = Get-VdMainReleasePlan -History $history -TargetSha $commitA
if ($retryA.Tag -cne $a.Tag) { throw 'A delayed old retry must retain its original version.' }
$d = Get-VdMainReleasePlan -History $history -TargetSha $commitD
$c = Get-VdMainReleasePlan -History $history -TargetSha $commitC
$e = Get-VdMainReleasePlan -History $history -TargetSha $commitE
if ($c.Tag -cne 'v2.1.0' -or $d.Tag -cne 'v2.1.1' -or $e.Tag -cne 'v2.1.2') {
    throw 'A baseline bump must reserve ordered slots even when CI is reordered or the project baseline is later lowered.'
}
# The final 3.0.0 baseline merge starts the major line at exactly .0. Its
# descendant reserves .1, while retrying any earlier 2.x revision retains its
# historical assignment even when the selector sees the newer main history.
$commitF = 'f' * 40
$commitG = '1' * 40
$majorHistory = @($history) + @(
    [pscustomobject]@{ Sha = $commitF; BaseVersion = '3.0.0' },
    [pscustomobject]@{ Sha = $commitG; BaseVersion = '3.0.0' }
)
$nextMajor = Get-VdMainReleasePlan -History $majorHistory -TargetSha $commitG
$major = Get-VdMainReleasePlan -History $majorHistory -TargetSha $commitF
if ($major.Tag -cne 'v3.0.0' -or $major.Version -cne '3.0.0' -or $major.AndroidVersionCode -ne 3000000 -or
    $nextMajor.Tag -cne 'v3.0.1' -or $nextMajor.Version -cne '3.0.1' -or $nextMajor.AndroidVersionCode -ne 3000001) {
    throw 'A major baseline bump must produce exactly v3.0.0 then v3.0.1 with matching Android update codes, independently of CI completion order.'
}
foreach ($old in @($a, $b, $c, $d, $e)) {
    $retried = Get-VdMainReleasePlan -History $majorHistory -TargetSha $old.Sha
    if ($retried.Tag -cne $old.Tag -or $retried.Version -cne $old.Version -or $retried.AndroidVersionCode -ne $old.AndroidVersionCode) {
        throw "The 3.0.0 baseline must not renumber historical 2.x revision $($old.Sha)."
    }
}
$brokenHistory = @(
    $history[0],
    [pscustomobject]@{ Sha = $commitA; BaseVersion = $null },
    [pscustomobject]@{ Sha = $commitB; BaseVersion = '2.0.0' }
)
if ((Get-VdMainReleasePlan -History $brokenHistory -TargetSha $commitB).Tag -cne 'v2.0.37') {
    throw 'A corrected descendant must release after a broken historical project version.'
}
$invalidAndroidHistory = @(
    $history[0],
    [pscustomobject]@{ Sha = $commitA; BaseVersion = '2.100.0' },
    [pscustomobject]@{ Sha = $commitB; BaseVersion = '2.1.0' }
)
if ((Get-VdMainReleasePlan -History $invalidAndroidHistory -TargetSha $commitB).Tag -cne 'v2.1.0') {
    throw 'An invalid historical Android baseline must not poison corrected descendants.'
}
foreach ($invalidPlan in @(
    @{ History = $history; TargetSha = ('f' * 40) },
    @{ History = @($history[1], $history[2]); TargetSha = $commitB },
    @{ History = $brokenHistory; TargetSha = $commitA },
    @{ History = $invalidAndroidHistory; TargetSha = $commitA },
    @{ History = @($history[0], $history[1], $history[1]); TargetSha = $commitB }
)) {
    $refused = $false
    try { $null = Get-VdMainReleasePlan @invalidPlan } catch { $refused = $true }
    if (-not $refused) { throw 'Invalid main release history must fail closed.' }
}

foreach ($case in @(
    @{ Version = '2.0.0'; Code = 2000000 },
    @{ Version = '2.0.42'; Code = 2000042 },
    @{ Version = '2.0.9999'; Code = 2009999 },
    @{ Version = '2.1.0'; Code = 2010000 },
    @{ Version = '3.0.0'; Code = 3000000 },
    @{ Version = '3.0.1'; Code = 3000001 },
    @{ Version = '2100.0.0'; Code = 2100000000 }
)) {
    if ((Get-VdAndroidVersionCode -Version $case.Version) -ne $case.Code) {
        throw "Incorrect Android versionCode for $($case.Version)."
    }
}
foreach ($invalid in @('0.0.0', '2.100.0', '2.0.10000', '2100.0.1', '2.0', 'v2.0.0', '2.00.0', '2.0.0-rc.1')) {
    $refused = $false
    try { [void](Get-VdAndroidVersionCode -Version $invalid) }
    catch { $refused = $true }
    if (-not $refused) { throw "Invalid Android version must fail closed: $invalid" }
}

Write-Host "Automatic main-release version assignment tests passed."

& (Join-Path $PSScriptRoot 'release-history-tests.ps1')
& (Join-Path $PSScriptRoot 'release-publication-tests.ps1')
