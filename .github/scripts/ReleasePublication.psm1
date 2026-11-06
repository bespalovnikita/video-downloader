#requires -Version 7.0
Set-StrictMode -Version Latest

$script:Assets = @(
    @{ Name = 'VideoDownloader-Windows-x64.zip'; Label = 'Windows x64 portable package' },
    @{ Name = 'VideoDownloader-Android.apk'; Label = 'Android release APK (arm64-v8a and x86_64)' },
    @{ Name = 'SHA256SUMS.txt'; Label = 'SHA-256 checksums' },
    @{ Name = 'THIRD_PARTY_NOTICES.md'; Label = 'Android third-party notices' },
    @{ Name = 'youtubedl-android-GPL-3.0.txt'; Label = 'Android runtime GPL-3.0 license' }
)

function Get-VdReleaseAssetNames { return @($script:Assets | ForEach-Object { $_.Name }) }

function Get-VdReleaseOwnershipMarker {
    param([string]$Repository, [string]$Tag, [string]$CommitSha)
    return "<!-- video-downloader-release:v1 repository=$Repository tag=$Tag sha=$CommitSha -->"
}

function Assert-VdReleaseAssets {
    param([object[]]$Assets, [switch]$Complete, [hashtable]$LocalAssets)
    $expected = @(Get-VdReleaseAssetNames)
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($asset in $Assets) {
        if ($asset.name -cnotin $expected -or -not $names.Add([string]$asset.name)) {
            throw 'The release contains unexpected or duplicate assets; no assets will be changed.'
        }
        if ($Complete) {
            if ($asset.state -cne 'uploaded' -or $asset.size -le 0) {
                throw "Release asset $($asset.name) is not completely uploaded."
            }
            if ($null -ne $LocalAssets) {
                $local = $LocalAssets[$asset.name]
                if ($asset.size -ne $local.Size) { throw "Remote asset size mismatch: $($asset.name)" }
                $digest = $asset.PSObject.Properties['digest']
                if ($null -ne $digest -and $null -ne $digest.Value -and
                    $digest.Value -cne "sha256:$($local.Hash)") {
                    throw "Remote asset digest mismatch: $($asset.name)"
                }
            }
        }
    }
    if ($Complete -and $names.Count -ne $expected.Count) {
        throw 'The release is missing required assets; published releases will not be modified.'
    }
}

function Get-VdReleaseDisposition {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CommitSha,
        [AllowNull()][object]$Release,
        [AllowNull()][string]$TagSha,
        [switch]$CompleteDraft,
        [hashtable]$LocalAssets
    )
    if ($Tag -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$' -or
        [version]$Tag.Substring(1) -lt [version]'2.0.35') {
        throw 'This tag predates the managed release migration; it will not be changed.'
    }
    if ($Repository -notmatch '^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$' -or
        $CommitSha -notmatch '^(?:[0-9a-f]{40}|[0-9a-f]{64})$') {
        throw 'Invalid release repository or commit SHA.'
    }
    if ($TagSha -and $TagSha -cne $CommitSha) {
        throw "Tag $Tag points at another commit; it will not be reassigned."
    }
    if ($null -eq $Release) { return 'Create' }
    if ($Release.tag_name -cne $Tag -or $Release.target_commitish -cne $CommitSha -or $Release.prerelease) {
        throw 'The existing release has a different tag, target SHA or release channel; it will not be changed.'
    }
    if (-not $Release.draft) {
        if (-not $TagSha) { throw 'The published release has no verified Git tag.' }
        Assert-VdReleaseAssets -Assets @($Release.assets) -Complete
        return 'Published'
    }
    $marker = Get-VdReleaseOwnershipMarker -Repository $Repository -Tag $Tag -CommitSha $CommitSha
    $markers = [regex]::Matches([string]$Release.body, '<!-- video-downloader-release:v1 [^\r\n]* -->')
    if ($markers.Count -ne 1 -or $markers[0].Value -cne $marker) {
        throw 'The draft is not owned by this repository, commit and release tag; it will not be changed.'
    }
    Assert-VdReleaseAssets -Assets @($Release.assets) -Complete:$CompleteDraft -LocalAssets $LocalAssets
    return 'Resume'
}

function Test-VdLatestPromotion {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CommitSha,
        [object[]]$Releases = @(),
        [Parameter(Mandatory)][scriptblock]$ResolveTag,
        [Parameter(Mandatory)][scriptblock]$IsAncestor
    )
    $version = [version]$Tag.Substring(1)
    foreach ($release in $Releases) {
        if ($release.draft -or $release.prerelease -or
            $release.tag_name -notmatch '^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') { continue }
        if ([version]$release.tag_name.Substring(1) -ge $version) { return $false }
        $publishedSha = & $ResolveTag $release.tag_name $false
        if (-not $publishedSha) { throw "Cannot verify the published release tag $($release.tag_name)." }
        # Also protect history when an older/manual release has an unexpected
        # version. A published descendant must never lose Latest to its ancestor.
        if (& $IsAncestor $CommitSha $publishedSha) { return $false }
    }
    return $true
}

function Invoke-VdGhApi {
    param([string]$Method = 'GET', [string]$Path, [AllowNull()][object]$Body, [switch]$Paginate, [switch]$AllowMissing)
    # Exit codes are checked below so an expected missing ID can be retried
    # even when the caller enables PowerShell's native-command error preference.
    $PSNativeCommandUseErrorActionPreference = $false
    $arguments = @('api', '--method', $Method, $Path)
    if ($Method -ceq 'GET') { $arguments += @('--header', 'Cache-Control: no-cache', '--header', 'Pragma: no-cache') }
    if ($Paginate) { $arguments += @('--paginate', '--slurp') }
    $temporary = $null
    try {
        if ($null -ne $Body) {
            $temporary = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName())
            [IO.File]::WriteAllText($temporary, ($Body | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
            $arguments += @('--input', $temporary)
        }
        $response = @(& gh @arguments)
        $exitCode = $LASTEXITCODE
        $json = $null
        try { $json = $response -join "`n" | ConvertFrom-Json -Depth 30 }
        catch { if ($exitCode -eq 0) { throw } }
        if ($exitCode -ne 0) {
            $status = if ($null -ne $json) { $json.PSObject.Properties['status'] } else { $null }
            if ($AllowMissing -and $null -ne $status -and [string]$status.Value -ceq '404') { return $null }
            throw "GitHub API request failed: $Method $Path"
        }
        if ($AllowMissing -and $null -eq $json) { throw 'GitHub returned an empty release snapshot instead of a confirmed 404.' }
        return $json
    } finally {
        if ($temporary) { Remove-Item -LiteralPath $temporary -Force -ErrorAction SilentlyContinue }
    }
}

function Invoke-VdGitHubReleaseTransport {
    param([string]$Operation, [hashtable]$Data)
    $repository = $Data.Repository
    switch ($Operation) {
        'List' {
            $pages = Invoke-VdGhApi -Path "repos/$repository/releases?per_page=100" -Paginate
            foreach ($page in $pages) { foreach ($release in $page) { $release } }
        }
        'Create' {
            $notes = Invoke-VdGhApi -Method POST -Path "repos/$repository/releases/generate-notes" -Body @{
                tag_name = $Data.Tag; target_commitish = $Data.CommitSha
            }
            Invoke-VdGhApi -Method POST -Path "repos/$repository/releases" -Body @{
                tag_name = $Data.Tag; target_commitish = $Data.CommitSha
                name = "Video Downloader $($Data.Tag)"; draft = $true; prerelease = $false
                body = "$($notes.body)`n`n$($Data.Marker)"
            }
        }
        'Get' {
            $id = [string]$Data.Id
            if ($id -notmatch '^[1-9][0-9]{0,18}$') { throw 'Invalid release ID.' }
            Invoke-VdGhApi -Path "repos/$repository/releases/$id" -AllowMissing
        }
        'Upload' {
            & gh release upload $Data.Tag "$($Data.Path)#$($Data.Label)" --clobber --repo $repository
            if ($LASTEXITCODE -ne 0) { throw "Release asset upload failed: $($Data.Name)" }
        }
        'Publish' {
            Invoke-VdGhApi -Method PATCH -Path "repos/$repository/releases/$($Data.Id)" -Body @{
                draft = $false; make_latest = $Data.MakeLatest.ToString().ToLowerInvariant()
            }
        }
        default { throw "Unknown release transport operation: $Operation" }
    }
}

function Get-VdMatchingRelease {
    param([object[]]$Releases, [string]$Tag)
    $matches = @($Releases | Where-Object { $_.tag_name -ceq $Tag })
    if ($matches.Count -gt 1) { throw "Multiple releases use tag $Tag; none will be changed." }
    if ($matches.Count -eq 1) { return $matches[0] }
    return $null
}

function Get-VdReleaseSnapshot {
    param([string]$Repository, [object]$Id, [scriptblock]$Transport, [scriptblock]$Delay)
    $number = [string]$Id
    if ($number -notmatch '^[1-9][0-9]{0,18}$') { throw 'GitHub did not return a valid release ID.' }
    # The collection endpoint can briefly omit a freshly created draft. Read
    # its stable ID instead; retry only a missing snapshot, never bad ownership
    # or an API/authentication failure. No write is retried here.
    for ($attempt = 0; $attempt -lt 5; $attempt++) {
        $snapshot = & $Transport 'Get' @{ Repository = $Repository; Id = $number }
        if ($null -ne $snapshot) {
            if ([string]$snapshot.id -cne $number) { throw 'GitHub returned a different release ID; it will not be changed.' }
            return $snapshot
        }
        if ($attempt -lt 4) { $null = & $Delay (250 * [math]::Pow(2, $attempt)) }
    }
    throw 'The owned release is not visible by ID after five reads.'
}

function Invoke-VdReleasePublication {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)][string]$Repository,
        [Parameter(Mandatory)][string]$Tag,
        [Parameter(Mandatory)][string]$CommitSha,
        [Parameter(Mandatory)][string]$AssetDirectory,
        [Parameter(Mandatory)][scriptblock]$ResolveTag,
        [Parameter(Mandatory)][scriptblock]$IsAncestor,
        [scriptblock]$Transport = ${function:Invoke-VdGitHubReleaseTransport},
        [scriptblock]$Delay = { param([int]$Milliseconds) Start-Sleep -Milliseconds $Milliseconds }
    )
    $releases = @(& $Transport 'List' @{ Repository = $Repository })
    $release = Get-VdMatchingRelease -Releases $releases -Tag $Tag
    $tagSha = & $ResolveTag $Tag $false
    $disposition = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $tagSha
    if ($disposition -ceq 'Published') {
        return [pscustomobject]@{ Tag = $Tag; Status = 'AlreadyPublished'; MakeLatest = $false }
    }
    # Validate every local file before creating or changing a draft.
    $localAssets = @{}
    foreach ($asset in $script:Assets) {
        $path = Join-Path $AssetDirectory $asset.Name
        $file = Get-Item -LiteralPath $path -ErrorAction Stop
        if ($file.PSIsContainer -or $file.Length -le 0) { throw "Invalid release file: $path" }
        $localAssets[$asset.Name] = @{
            Path = $file.FullName; Size = $file.Length
            Hash = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        }
    }
    if ($disposition -ceq 'Create') {
        $marker = Get-VdReleaseOwnershipMarker -Repository $Repository -Tag $Tag -CommitSha $CommitSha
        $release = & $Transport 'Create' @{ Repository = $Repository; Tag = $Tag; CommitSha = $CommitSha; Marker = $marker }
        $createdState = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $tagSha
        if ($createdState -cne 'Resume') { throw 'GitHub did not create the expected owned draft.' }
    }
    # Re-read ownership immediately before clobbering any assets. No published
    # release or unrelated draft is ever an upload target, including on retry.
    $releaseId = $release.id
    $release = Get-VdReleaseSnapshot -Repository $Repository -Id $releaseId -Transport $Transport -Delay $Delay
    $disposition = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $tagSha
    if ($disposition -cne 'Resume') { throw 'The owned draft disappeared or changed before upload.' }
    foreach ($asset in $script:Assets) {
        $null = & $Transport 'Upload' @{
            Repository = $Repository; Tag = $Tag; Name = $asset.Name
            Path = $localAssets[$asset.Name].Path; Label = $asset.Label
        }
    }
    $release = Get-VdReleaseSnapshot -Repository $Repository -Id $releaseId -Transport $Transport -Delay $Delay
    $disposition = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $tagSha -CompleteDraft -LocalAssets $localAssets
    if ($disposition -cne 'Resume') { throw 'The owned draft changed before publication.' }
    $releases = @(& $Transport 'List' @{ Repository = $Repository })
    $makeLatest = Test-VdLatestPromotion -Tag $Tag -CommitSha $CommitSha -Releases $releases -ResolveTag $ResolveTag -IsAncestor $IsAncestor
    $null = & $Transport 'Publish' @{ Repository = $Repository; Id = $release.id; MakeLatest = $makeLatest }
    $release = Get-VdReleaseSnapshot -Repository $Repository -Id $releaseId -Transport $Transport -Delay $Delay
    $tagSha = & $ResolveTag $Tag $true
    $disposition = Get-VdReleaseDisposition -Repository $Repository -Tag $Tag -CommitSha $CommitSha -Release $release -TagSha $tagSha
    if ($disposition -cne 'Published') { throw 'GitHub did not publish the complete release.' }
    return [pscustomobject]@{ Tag = $Tag; Status = 'Published'; MakeLatest = $makeLatest }
}

Export-ModuleMember -Function Get-VdReleaseAssetNames, Get-VdReleaseOwnershipMarker, Get-VdReleaseDisposition, Test-VdLatestPromotion, Invoke-VdGitHubReleaseTransport, Get-VdMatchingRelease, Invoke-VdReleasePublication
