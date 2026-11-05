#requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Inspect', 'Publish')][string]$Mode,
    [Parameter(Mandatory)][string]$Repository,
    [Parameter(Mandatory)][string]$Tag,
    [Parameter(Mandatory)][string]$CommitSha,
    [string]$AssetDirectory = 'dist',
    [string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleasePublication.psm1') -Force

function Resolve-ReleaseTag {
    param([string]$Name, [bool]$Refresh)
    if ($Name -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') { throw 'Invalid Git release tag.' }
    if ($Refresh) {
        git fetch origin "refs/tags/${Name}:refs/tags/${Name}"
        if ($LASTEXITCODE -ne 0) { throw "Cannot verify the published Git tag $Name." }
    }
    $sha = git rev-parse --verify "${Name}^{commit}" 2>$null
    if ($LASTEXITCODE -eq 0) { return [string]$sha }
    return $null
}
function Test-ReleaseAncestor {
    param([string]$Ancestor, [string]$Descendant)
    git merge-base --is-ancestor $Ancestor $Descendant
    if ($LASTEXITCODE -eq 0) { return $true }
    if ($LASTEXITCODE -eq 1) { return $false }
    throw 'Cannot verify published release ancestry.'
}

if ($Mode -ceq 'Inspect') {
    if (-not $OutputPath) { throw 'Inspect requires a GitHub output path.' }
    $releases = @(Invoke-VdGitHubReleaseTransport 'List' @{ Repository = $Repository })
    $release = Get-VdMatchingRelease -Releases $releases -Tag $Tag
    $sha = Resolve-ReleaseTag $Tag $false
    $state = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $sha
    "published=$(($state -ceq 'Published').ToString().ToLowerInvariant())" | Out-File -FilePath $OutputPath -Encoding utf8 -Append
    Write-Host "$Tag release state: $state"
} else {
    $result = Invoke-VdReleasePublication -Repository $Repository -Tag $Tag -CommitSha $CommitSha -AssetDirectory $AssetDirectory -ResolveTag ${function:Resolve-ReleaseTag} -IsAncestor ${function:Test-ReleaseAncestor}
    Write-Host "$($result.Tag): $($result.Status); Latest promotion: $($result.MakeLatest)"
}
exit 0
