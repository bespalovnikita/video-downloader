#requires -Version 7.0
Set-StrictMode -Version Latest

# Migration boundary: all earlier public releases keep their original versions.
# New versions count first-parent main revisions after this immutable release,
# including revisions whose CI has not succeeded yet. CI completion order must
# never determine Windows/Android update order.
$script:MainReleaseAnchor = @{
    Commit = 'e93a78f6b82c04a213ec2eaf9cd93855c84ef691'
    Tag = 'v2.0.35'
}

function Get-VdMainReleaseAnchor {
    return [pscustomobject]$script:MainReleaseAnchor.Clone()
}

function Get-VdMainReleasePlan {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][object[]]$History,
        [Parameter(Mandatory)][string]$TargetSha,
        [string]$AnchorSha = $script:MainReleaseAnchor.Commit,
        [string]$AnchorTag = $script:MainReleaseAnchor.Tag
    )

    if ($History.Count -eq 0 -or $History[0].Sha -cne $AnchorSha) {
        throw 'Main release history must begin at the immutable migration anchor.'
    }
    if ($AnchorTag -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Invalid main release anchor tag: $AnchorTag"
    }
    $version = [version]$AnchorTag.Substring(1)
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    for ($index = 0; $index -lt $History.Count; $index++) {
        $revision = $History[$index]
        if (-not $seen.Add([string]$revision.Sha)) { throw 'Duplicate main release revision.' }
        $baseline = $null
        if ($revision.BaseVersion -match '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
            try {
                $baseline = [version]$revision.BaseVersion
                $null = Get-VdAndroidVersionCode -Version $baseline.ToString()
            } catch { $baseline = $null }
        }
        if ($null -eq $baseline -and $revision.Sha -ceq $TargetSha) {
            throw "Invalid project Version at main revision $($revision.Sha)."
        }
        if ($index -gt 0) {
            # A broken, non-target revision still reserves its patch slot. It
            # must not prevent a corrected descendant from releasing later.
            if ($null -ne $baseline -and $baseline -gt $version) {
                $version = $baseline
            } else {
                if ($version.Build -ge [int]::MaxValue) { throw 'Main release patch version overflow.' }
                $version = [version]::new($version.Major, $version.Minor, $version.Build + 1)
            }
        }
        if ($revision.Sha -ceq $TargetSha) {
            # Validate the Android update number before either platform builds.
            $code = Get-VdAndroidVersionCode -Version $version.ToString()
            return [pscustomobject]@{
                Sha = $TargetSha
                Tag = "v$version"
                Version = $version.ToString()
                AndroidVersionCode = $code
                Revision = $index
            }
        }
    }
    throw "Commit $TargetSha is not on the managed first-parent main history."
}

function Get-VdAndroidVersionCode {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Version)

    if ($Version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Expected Android release version in major.minor.patch format; got: $Version"
    }
    $parsed = [version]$Version
    # Reserve fixed decimal ranges so Android updates preserve SemVer order.
    if ($parsed.Minor -gt 99 -or $parsed.Build -gt 9999) {
        throw "Android release version requires minor <= 99 and patch <= 9999."
    }
    $code = [long]$parsed.Major * 1000000 + [long]$parsed.Minor * 10000 + $parsed.Build
    if ($code -lt 1 -or $code -gt 2100000000) {
        throw "Android versionCode must be between 1 and 2100000000."
    }
    return [int]$code
}

Export-ModuleMember -Function Get-VdAndroidVersionCode, Get-VdMainReleaseAnchor, Get-VdMainReleasePlan
