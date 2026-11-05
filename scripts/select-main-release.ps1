#requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$TargetSha,
    [Parameter(Mandatory)][string]$OutputPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleaseVersion.psm1') -Force

git fetch origin '+refs/heads/main:refs/remotes/origin/main' --tags
if ($LASTEXITCODE -ne 0) { throw 'Cannot fetch main and its release tags.' }
$anchor = Get-VdMainReleaseAnchor
$anchorTagSha = git rev-parse --verify "$($anchor.Tag)^{commit}"
if ($LASTEXITCODE -ne 0 -or $anchorTagSha -cne $anchor.Commit) {
    throw 'The immutable release migration anchor no longer matches its Git tag.'
}
$firstParents = @(git rev-list --first-parent origin/main)
if ($LASTEXITCODE -ne 0) { throw 'Cannot read the first-parent main history.' }
$anchorIndex = [Array]::IndexOf($firstParents, $anchor.Commit)
if ($anchorIndex -lt 0) { throw 'The migration anchor is not on the first-parent main history.' }
$commits = @($firstParents | Select-Object -First $anchorIndex)
[Array]::Reverse($commits)
if ($TargetSha -cne $anchor.Commit -and $TargetSha -cnotin $commits) {
    throw 'The requested commit is outside the managed first-parent main history.'
}
$history = @(
    foreach ($sha in @($anchor.Commit) + $commits) {
        $baseVersion = $null
        $projectText = @(git show "${sha}:src/VideoDownloader/VideoDownloader.csproj" 2>$null)
        if ($LASTEXITCODE -eq 0) {
            try {
                [xml]$project = $projectText -join "`n"
                $baseVersion = [string]$project.Project.PropertyGroup.Version
            } catch {
                if ($sha -ceq $TargetSha) { throw "Cannot read the project version at $sha." }
            }
        } elseif ($sha -ceq $TargetSha) {
            throw "Cannot read the project version at $sha."
        }
        [pscustomobject]@{ Sha = $sha; BaseVersion = $baseVersion }
        if ($sha -ceq $TargetSha) { break }
    }
)
$plan = Get-VdMainReleasePlan -History $history -TargetSha $TargetSha
"tag=$($plan.Tag)" | Out-File -FilePath $OutputPath -Encoding utf8 -Append
"version=$($plan.Version)" | Out-File -FilePath $OutputPath -Encoding utf8 -Append
Write-Host "Main revision $($plan.Revision): $($plan.Sha) -> $($plan.Tag)"
exit 0
