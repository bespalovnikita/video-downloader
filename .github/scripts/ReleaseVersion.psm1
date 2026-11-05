#requires -Version 7.0
Set-StrictMode -Version Latest

# Only stable, three-component SemVer tags participate in the automatic
# release sequence. Pre-releases, dangling draft tags, and unrelated tags
# never advance the public version number.
function Get-VdNextReleaseTag {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$BaseVersion,
        [AllowEmptyCollection()][string[]]$ExistingTags = @(),
        [AllowEmptyCollection()][string[]]$HeadTags = @()
    )

    if ($BaseVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Expected project Version in major.minor.patch format; got: $BaseVersion"
    }
    $baseline = [version]$BaseVersion
    $parseTag = {
        param([string]$Name)
        if ($Name -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { return $null }
        try { return [version]$Name.Substring(1) }
        catch { return $null }
    }

    # A rerun of a workflow on the same SHA must target its existing release,
    # not create a second tag and release for the same commit.
    # Force array type after sorting: PowerShell unwraps empty/single pipelines
    # into $null or a scalar, which breaks .Count under StrictMode.
    $sameCommit = @(
        @(
            foreach ($tag in $HeadTags) {
                $parsed = & $parseTag $tag
                if ($null -ne $parsed -and $parsed -ge $baseline) {
                    [pscustomobject]@{ Version = $parsed; Tag = $tag }
                }
            }
        ) | Sort-Object Version -Descending
    )
    if ($sameCommit.Count -gt 0) { return [string]$sameCommit[0].Tag }

    $highest = $null
    foreach ($tag in $ExistingTags) {
        $parsed = & $parseTag $tag
        if ($null -ne $parsed -and ($null -eq $highest -or $parsed -gt $highest)) {
            $highest = $parsed
        }
    }

    if ($null -eq $highest -or $highest -lt $baseline) {
        return "v$($baseline.Major).$($baseline.Minor).$($baseline.Build)"
    }
    if ($highest.Build -ge [int]::MaxValue) {
        throw "Cannot automatically increment patch component of $highest"
    }
    return "v$($highest.Major).$($highest.Minor).$($highest.Build + 1)"
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

Export-ModuleMember -Function Get-VdNextReleaseTag, Get-VdAndroidVersionCode
