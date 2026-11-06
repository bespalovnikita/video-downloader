#requires -Version 7.0
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleasePublication.psm1') -Force
Import-Module (Join-Path $PSScriptRoot '../.github/scripts/ReleaseVersion.psm1') -Force

$repository = 'bespalovnikita/video-downloader'
$shaA = 'a' * 40
$shaB = 'b' * 40
$shaOther = 'c' * 40
$tagA = 'v2.0.36'
$tagB = 'v2.0.37'
$assetNames = @(Get-VdReleaseAssetNames)
$temporaryRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { Join-Path $PSScriptRoot '../work' }
$scratch = Join-Path $temporaryRoot "release-publication-tests-$([guid]::NewGuid().ToString('N'))"
$assetDirectory = Join-Path $scratch 'assets'
$script:caseCount = 0

function Assert-ReleasePublication {
    param([bool]$Condition, [string]$Message)
    if (-not $Condition) { throw $Message }
}

function Assert-ReleasePublicationFailure {
    param([scriptblock]$Action, [string]$Pattern)
    $caught = $null
    try { $null = & $Action } catch { $caught = $_ }
    Assert-ReleasePublication ($null -ne $caught) 'The operation unexpectedly succeeded.'
    if ($Pattern -and $caught.Exception.Message -notmatch $Pattern) {
        throw "Unexpected error: $($caught.Exception.Message); expected: $Pattern"
    }
}

function New-ReleasePublicationFixture {
    $state = @{
        Releases = [Collections.Generic.List[object]]::new()
        Tags = @{}
        Ancestors = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        Mutations = [Collections.Generic.List[object]]::new()
        ListCalls = 0
        FailListCall = 0
        FailUploadName = $null
        StarterUploadName = $null
        WrongDigestName = $null
        FailAfterCreate = $false
        HideCreatedFromList = $false
        CreatedId = $null
        GetCalls = 0
        MissingGetReads = 0
        FailGetCall = 0
        OverrideRead = $null
        Delays = [Collections.Generic.List[int]]::new()
        NextId = 1
        Latest = $null
    }
    $transport = {
        param([string]$Operation, [hashtable]$Data)
        switch ($Operation) {
            'List' {
                $state.ListCalls++
                if ($state.ListCalls -eq $state.FailListCall) { throw 'Simulated GitHub list API failure.' }
                return @($state.Releases | Where-Object { -not $state.HideCreatedFromList -or $_.id -ne $state.CreatedId })
            }
            'Create' {
                $state.Mutations.Add([pscustomobject]@{ Operation = 'Create'; Tag = $Data.Tag })
                $release = [pscustomobject]@{
                    id = $state.NextId++
                    tag_name = $Data.Tag
                    target_commitish = $Data.CommitSha
                    draft = $true
                    prerelease = $false
                    body = "Generated release notes. $($Data.Marker)"
                    assets = @()
                }
                $state.Releases.Add($release)
                $state.CreatedId = $release.id
                if ($state.FailAfterCreate) {
                    $state.FailAfterCreate = $false
                    throw 'Simulated disconnect after draft creation.'
                }
                return $release
            }
            'Get' {
                $state.GetCalls++
                if ($state.GetCalls -eq $state.FailGetCall) { throw 'Simulated GitHub detail API failure.' }
                if ($state.MissingGetReads -gt 0) { $state.MissingGetReads--; return $null }
                $release = if ($null -ne $state.OverrideRead) { $state.OverrideRead } else {
                    @($state.Releases | Where-Object { [string]$_.id -ceq [string]$Data.Id })[0]
                }
                # Real API responses are snapshots, not references that mutate
                # when a later upload or publish updates the fixture's server.
                return ($release | ConvertTo-Json -Depth 20 | ConvertFrom-Json -Depth 20)
            }
            'Upload' {
                $release = @($state.Releases | Where-Object { $_.tag_name -ceq $Data.Tag })[0]
                if (-not $release.draft) { throw 'The transport was asked to change published assets.' }
                $state.Mutations.Add([pscustomobject]@{ Operation = 'Upload'; Tag = $Data.Tag; Name = $Data.Name })
                $file = Get-Item -LiteralPath $Data.Path
                $digest = 'sha256:' + (Get-FileHash -LiteralPath $Data.Path -Algorithm SHA256).Hash.ToLowerInvariant()
                $uploadState = if ($state.StarterUploadName -ceq $Data.Name) { 'starter' } else { 'uploaded' }
                if ($state.WrongDigestName -ceq $Data.Name) { $digest = 'sha256:' + ('0' * 64) }
                # --clobber removes the old asset before the new upload can fail.
                $release.assets = @($release.assets | Where-Object { $_.name -cne $Data.Name })
                if ($state.FailUploadName -ceq $Data.Name) {
                    $state.FailUploadName = $null
                    $release.assets += [pscustomobject]@{ name = $Data.Name; state = 'starter'; size = 0; digest = $null }
                    throw 'Simulated interrupted asset upload.'
                }
                $release.assets += [pscustomobject]@{ name = $Data.Name; state = $uploadState; size = $file.Length; digest = $digest }
                return
            }
            'Publish' {
                $release = @($state.Releases | Where-Object { $_.id -eq $Data.Id })[0]
                if (-not $release.draft) { throw 'The transport was asked to change a published release.' }
                $state.Mutations.Add([pscustomobject]@{ Operation = 'Publish'; Tag = $release.tag_name; MakeLatest = $Data.MakeLatest })
                $release.draft = $false
                $state.Tags[$release.tag_name] = $release.target_commitish
                if ($Data.MakeLatest) { $state.Latest = $release.tag_name }
                return $release
            }
            default { throw "Unexpected test operation: $Operation" }
        }
    }.GetNewClosure()
    $delay = { param([int]$Milliseconds) $state.Delays.Add($Milliseconds) }.GetNewClosure()
    $resolveTag = {
        param([string]$Name, [bool]$Refresh)
        if ($state.Tags.ContainsKey($Name)) { return $state.Tags[$Name] }
        return $null
    }.GetNewClosure()
    $isAncestor = {
        param([string]$Older, [string]$Newer)
        return $Older -ceq $Newer -or $state.Ancestors.Contains("$Older>$Newer")
    }.GetNewClosure()
    return @{ State = $state; Transport = $transport; ResolveTag = $resolveTag; IsAncestor = $isAncestor; Delay = $delay }
}

function New-TestRelease {
    param(
        [string]$Tag = $tagA,
        [string]$CommitSha = $shaA,
        [bool]$Draft = $true,
        [bool]$Prerelease = $false,
        [AllowNull()][string]$Body,
        [switch]$Complete
    )
    if (-not $PSBoundParameters.ContainsKey('Body')) {
        $Body = Get-VdReleaseOwnershipMarker -Repository $repository -Tag $Tag -CommitSha $CommitSha
    }
    $assets = if ($Complete) {
        foreach ($name in $assetNames) {
            $path = Join-Path $assetDirectory $name
            [pscustomobject]@{
                name = $name; state = 'uploaded'; size = (Get-Item -LiteralPath $path).Length
                digest = 'sha256:' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
            }
        }
    } else { @() }
    return [pscustomobject]@{
        id = 100; tag_name = $Tag; target_commitish = $CommitSha
        draft = $Draft; prerelease = $Prerelease; body = $Body; assets = @($assets)
    }
}

function Invoke-TestReleasePublication {
    param(
        [hashtable]$Fixture,
        [string]$Tag = $tagA,
        [string]$CommitSha = $shaA,
        [string]$Assets = $assetDirectory
    )
    return Invoke-VdReleasePublication -Repository $repository -Tag $Tag -CommitSha $CommitSha -AssetDirectory $Assets `
        -Transport $Fixture.Transport -ResolveTag $Fixture.ResolveTag -IsAncestor $Fixture.IsAncestor -Delay $Fixture.Delay
}

function Assert-NoReleaseMutation {
    param([hashtable]$Fixture)
    Assert-ReleasePublication ($Fixture.State.Mutations.Count -eq 0) 'A protected or invalid release was changed.'
}

function Test-ReleaseGhTransport {
    # Exercise production argument construction and JSON parsing, not only the
    # injectable publication state machine. No network or token is required.
    $prior = Get-Item Function:global:gh -ErrorAction SilentlyContinue
    $global:VdReleaseGhProbe = @{
        Replies = [Collections.Generic.Queue[object]]::new()
        Calls = [Collections.Generic.List[object]]::new()
    }
    function global:gh {
        $arguments = @($args)
        $global:VdReleaseGhProbe.Calls.Add($arguments)
        $reply = $global:VdReleaseGhProbe.Replies.Dequeue()
        $global:LASTEXITCODE = $reply.ExitCode
        return $reply.Json
    }
    try {
        $draft = New-TestRelease
        $old = New-TestRelease -Tag 'v2.0.35' -CommitSha $shaOther -Draft $false -Complete
        $page = ConvertTo-Json -InputObject @($old, $draft) -Depth 20 -Compress
        $global:VdReleaseGhProbe.Replies.Enqueue(@{ ExitCode = 0; Json = "[$page]" })
        $listed = @(Invoke-VdGitHubReleaseTransport 'List' @{ Repository = $repository })
        Assert-ReleasePublication ($listed.Count -eq 2 -and (Get-VdMatchingRelease -Releases $listed -Tag $tagA).id -eq 100) 'Paginated gh JSON did not preserve the draft release.'

        $global:VdReleaseGhProbe.Replies.Enqueue(@{ ExitCode = 0; Json = ($draft | ConvertTo-Json -Depth 20 -Compress) })
        $read = Invoke-VdGitHubReleaseTransport 'Get' @{ Repository = $repository; Id = 100 }
        Assert-ReleasePublication ($read.id -eq 100 -and $read.draft) 'The production transport did not parse a direct release snapshot.'
        $arguments = $global:VdReleaseGhProbe.Calls[1]
        Assert-ReleasePublication ($arguments -ccontains "repos/$repository/releases/100" -and
            $arguments -ccontains 'Cache-Control: no-cache' -and $arguments -ccontains 'Pragma: no-cache') 'A direct ownership read used a cached collection or omitted cache controls.'

        $global:VdReleaseGhProbe.Replies.Enqueue(@{ ExitCode = 1; Json = '{"message":"Not Found","status":"404"}' })
        $missing = Invoke-VdGitHubReleaseTransport 'Get' @{ Repository = $repository; Id = 100 }
        Assert-ReleasePublication ($null -eq $missing) 'A missing ID snapshot must be distinguishable from an API failure.'
        $global:VdReleaseGhProbe.Replies.Enqueue(@{ ExitCode = 0; Json = 'null' })
        Assert-ReleasePublicationFailure { Invoke-VdGitHubReleaseTransport 'Get' @{ Repository = $repository; Id = 100 } } 'empty release snapshot'
        $global:VdReleaseGhProbe.Replies.Enqueue(@{ ExitCode = 1; Json = '{"message":"private-fixture","status":"403"}' })
        Assert-ReleasePublicationFailure { Invoke-VdGitHubReleaseTransport 'Get' @{ Repository = $repository; Id = 100 } } 'GitHub API request failed: GET'
        Assert-ReleasePublication ($global:VdReleaseGhProbe.Replies.Count -eq 0) 'The transport performed an unexpected request.'
    } finally {
        Remove-Item Function:global:gh -Force
        if ($null -ne $prior) { Set-Item Function:global:gh $prior.ScriptBlock }
        Remove-Variable VdReleaseGhProbe -Scope Global
        $global:LASTEXITCODE = 0
    }
}

try {
    $null = New-Item -ItemType Directory -Path $assetDirectory -Force
    foreach ($name in $assetNames) {
        Set-Content -LiteralPath (Join-Path $assetDirectory $name) -Value "Local release test fixture: $name" -Encoding utf8
    }
    Test-ReleaseGhTransport
    $script:caseCount++

    # A newly created draft can be absent from the collection's read replica.
    # Its returned ID must be used for ownership/assets/publication checks.
    $fixture = New-ReleasePublicationFixture
    $fixture.State.HideCreatedFromList = $true
    $result = Invoke-TestReleasePublication $fixture
    Assert-ReleasePublication ($result.Status -ceq 'Published' -and $fixture.State.GetCalls -eq 3) 'A stale release collection still requires a manual workflow retry.'
    Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Create').Count -eq 1) 'Collection lag caused duplicate draft creation.'
    Assert-ReleasePublication ($fixture.State.Releases[0].assets.Count -eq $assetNames.Count -and $fixture.State.Delays.Count -eq 0) 'A visible ID snapshot was not published with all assets immediately.'
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.MissingGetReads = 2
    $result = Invoke-TestReleasePublication $fixture
    Assert-ReleasePublication ($result.Status -ceq 'Published' -and $fixture.State.GetCalls -eq 5 -and
        ($fixture.State.Delays -join ',') -ceq '250,500') 'A briefly missing ID snapshot must use bounded backoff and recover without retrying writes.'
    Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Create').Count -eq 1) 'ID lookup retries created a second draft.'
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.MissingGetReads = 50
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'not visible by ID after five reads'
    Assert-ReleasePublication ($fixture.State.GetCalls -eq 5 -and ($fixture.State.Delays -join ',') -ceq '250,500,1000,2000') 'A missing ID snapshot exceeded its retry budget.'
    Assert-ReleasePublication ($fixture.State.Mutations.Count -eq 1 -and $fixture.State.Mutations[0].Operation -ceq 'Create' -and
        $fixture.State.Releases[0].assets.Count -eq 0) 'Missing ownership evidence allowed asset upload or publication.'
    $script:caseCount++

    foreach ($badRead in @('WrongId', 'WrongOwner', 'WrongTarget', 'ApiFailure')) {
        $fixture = New-ReleasePublicationFixture
        $fixture.State.Releases.Add((New-TestRelease))
        $changed = New-TestRelease
        switch ($badRead) {
            'WrongId' { $changed.id = 101; $fixture.State.OverrideRead = $changed }
            'WrongOwner' { $changed.body = 'Foreign draft'; $fixture.State.OverrideRead = $changed }
            'WrongTarget' { $changed.target_commitish = $shaOther; $fixture.State.OverrideRead = $changed }
            'ApiFailure' { $fixture.State.FailGetCall = 1 }
        }
        Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'different release ID|not owned|different tag|detail API failure'
        Assert-NoReleaseMutation $fixture
        Assert-ReleasePublication ($fixture.State.GetCalls -eq 1 -and $fixture.State.Delays.Count -eq 0) 'Ownership or API failures were retried as missing releases.'
        $script:caseCount++
    }

    # A runner can disappear after draft creation or during --clobber. Retry
    # reuses the same owned draft and replaces the complete asset set together.
    $fixture = New-ReleasePublicationFixture
    $fixture.State.FailUploadName = 'SHA256SUMS.txt'
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'interrupted asset upload'
    Assert-ReleasePublication ($fixture.State.Releases.Count -eq 1 -and $fixture.State.Releases[0].draft) 'The interrupted operation must leave one draft.'
    Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Publish').Count -eq 0) 'An incomplete release was published.'
    $result = Invoke-TestReleasePublication $fixture
    Assert-ReleasePublication ($result.Status -ceq 'Published' -and -not $fixture.State.Releases[0].draft) 'Retry did not complete the owned draft.'
    Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Create').Count -eq 1) 'Retry created a second release.'
    Assert-ReleasePublication ($fixture.State.Releases[0].assets.Count -eq $assetNames.Count) 'Retry left an incomplete asset set.'
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.FailAfterCreate = $true
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'disconnect after draft creation'
    $result = Invoke-TestReleasePublication $fixture
    Assert-ReleasePublication ($result.Status -ceq 'Published') 'A lost draft-create response cannot be recovered.'
    Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Create').Count -eq 1) 'A lost response caused duplicate creation.'
    $script:caseCount++

    # Published idempotency requires no build files and must never change notes,
    # assets or Latest. Old public releases may have no ownership marker.
    $fixture = New-ReleasePublicationFixture
    $published = New-TestRelease -Draft $false -Complete -Body 'Original published notes, unchanged.'
    $fixture.State.Releases.Add($published)
    $fixture.State.Tags[$tagA] = $shaA
    $fixture.State.Latest = $tagB
    $result = Invoke-TestReleasePublication $fixture -Assets (Join-Path $scratch 'no-build-files')
    Assert-ReleasePublication ($result.Status -ceq 'AlreadyPublished') 'A complete published release was not recognized.'
    Assert-ReleasePublication ($published.body -ceq 'Original published notes, unchanged.' -and $fixture.State.Latest -ceq $tagB) 'Idempotent retry modified the published release.'
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    foreach ($foreignCase in @('WrongMarker', 'WrongTarget', 'WrongTagSha', 'Prerelease', 'DuplicateMarker', 'UnexpectedAsset')) {
        $fixture = New-ReleasePublicationFixture
        $draft = New-TestRelease
        switch ($foreignCase) {
            'WrongMarker' { $draft.body = Get-VdReleaseOwnershipMarker -Repository 'other/repository' -Tag $tagA -CommitSha $shaA }
            'WrongTarget' { $draft.target_commitish = $shaOther }
            'WrongTagSha' { $fixture.State.Tags[$tagA] = $shaOther }
            'Prerelease' { $draft.prerelease = $true }
            'DuplicateMarker' { $draft.body += ' ' + $draft.body }
            'UnexpectedAsset' { $draft.assets = @([pscustomobject]@{ name = 'foreign.txt'; state = 'uploaded'; size = 1; digest = $null }) }
        }
        $fixture.State.Releases.Add($draft)
        Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'owned|another commit|different tag|unexpected or duplicate'
        Assert-NoReleaseMutation $fixture
        $script:caseCount++
    }

    # A pre-existing Git tag is protected even when no release exists.
    $fixture = New-ReleasePublicationFixture
    $fixture.State.Tags[$tagA] = $shaOther
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'another commit'
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.Releases.Add((New-TestRelease -Tag 'v1.0.0'))
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture -Tag 'v1.0.0' } 'predates'
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    foreach ($publishedCase in @('MissingAsset', 'IncompleteAsset', 'MissingTag', 'Prerelease')) {
        $fixture = New-ReleasePublicationFixture
        $published = New-TestRelease -Draft $false -Complete
        $fixture.State.Releases.Add($published)
        $fixture.State.Tags[$tagA] = $shaA
        switch ($publishedCase) {
            'MissingAsset' { $published.assets = @($published.assets | Where-Object name -ne 'VideoDownloader-Android.apk') }
            'IncompleteAsset' { $published.assets[0].state = 'starter' }
            'MissingTag' { $fixture.State.Tags.Remove($tagA) }
            'Prerelease' { $published.prerelease = $true }
        }
        Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'missing required assets|not completely uploaded|no verified Git tag|different tag'
        Assert-NoReleaseMutation $fixture
        $script:caseCount++
    }

    # API failures are not treated as a missing release.
    $fixture = New-ReleasePublicationFixture
    $fixture.State.FailListCall = 1
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'list API failure'
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    foreach ($badUpload in @('Starter', 'WrongDigest')) {
        $fixture = New-ReleasePublicationFixture
        if ($badUpload -ceq 'Starter') { $fixture.State.StarterUploadName = 'VideoDownloader-Android.apk' }
        else { $fixture.State.WrongDigestName = 'VideoDownloader-Android.apk' }
        Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture } 'not completely uploaded|digest mismatch'
        Assert-ReleasePublication (@($fixture.State.Mutations | Where-Object Operation -eq 'Publish').Count -eq 0) 'Unverified uploaded assets reached publication.'
        Assert-ReleasePublication ($fixture.State.Releases[0].draft) 'An invalid upload became public.'
        $fixture.State.StarterUploadName = $null
        $fixture.State.WrongDigestName = $null
        $result = Invoke-TestReleasePublication $fixture
        Assert-ReleasePublication ($result.Status -ceq 'Published') 'The invalid upload could not be retried.'
        $script:caseCount++
    }

    # A missing local file is discovered before creating or changing a draft.
    $fixture = New-ReleasePublicationFixture
    Assert-ReleasePublicationFailure { Invoke-TestReleasePublication $fixture -Assets (Join-Path $scratch 'missing') } ''
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    # Published descendants are protected by both independent guards: semantic
    # version order, and ancestry even if a manual tag used a smaller version.
    $fixture = New-ReleasePublicationFixture
    $fixture.State.Releases.Add((New-TestRelease -Tag $tagB -CommitSha $shaB -Draft $false -Complete))
    $fixture.State.Tags[$tagB] = $shaB
    $allowed = Test-VdLatestPromotion -Tag $tagA -CommitSha $shaA -Releases $fixture.State.Releases.ToArray() -ResolveTag $fixture.ResolveTag -IsAncestor $fixture.IsAncestor
    Assert-ReleasePublication (-not $allowed) 'A higher public version did not block Latest promotion.'
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.Releases.Add((New-TestRelease -Tag 'v2.0.35' -CommitSha $shaB -Draft $false -Complete))
    $fixture.State.Tags['v2.0.35'] = $shaB
    $null = $fixture.State.Ancestors.Add("$shaA>$shaB")
    $allowed = Test-VdLatestPromotion -Tag $tagA -CommitSha $shaA -Releases $fixture.State.Releases.ToArray() -ResolveTag $fixture.ResolveTag -IsAncestor $fixture.IsAncestor
    Assert-ReleasePublication (-not $allowed) 'A published descendant with a lower manual version lost Latest protection.'
    $script:caseCount++

    $fixture = New-ReleasePublicationFixture
    $fixture.State.Releases.Add((New-TestRelease -Tag 'v2.0.35' -CommitSha $shaOther -Draft $false -Complete))
    Assert-ReleasePublicationFailure {
        Test-VdLatestPromotion -Tag $tagA -CommitSha $shaA -Releases $fixture.State.Releases.ToArray() -ResolveTag $fixture.ResolveTag -IsAncestor $fixture.IsAncestor
    } 'Cannot verify the published release tag'
    Assert-NoReleaseMutation $fixture
    $script:caseCount++

    # Reordered CI: B is built and published first; delayed A and retries retain
    # A's reserved lower Windows/Android version and never displace B's Latest.
    $anchor = Get-VdMainReleaseAnchor
    $history = @(
        [pscustomobject]@{ Sha = $anchor.Commit; BaseVersion = '2.0.0' }
        [pscustomobject]@{ Sha = $shaA; BaseVersion = '2.0.0' }
        [pscustomobject]@{ Sha = $shaB; BaseVersion = '2.0.0' }
    )
    $planB = Get-VdMainReleasePlan -History $history -TargetSha $shaB
    $planA = Get-VdMainReleasePlan -History $history -TargetSha $shaA
    Assert-ReleasePublication ($planA.Tag -ceq $tagA -and $planB.Tag -ceq $tagB -and $planA.AndroidVersionCode -lt $planB.AndroidVersionCode) 'First-parent versions depend on CI completion order.'
    $fixture = New-ReleasePublicationFixture
    $null = $fixture.State.Ancestors.Add("$shaA>$shaB")
    $resultB = Invoke-TestReleasePublication $fixture -Tag $planB.Tag -CommitSha $shaB
    $resultA = Invoke-TestReleasePublication $fixture -Tag $planA.Tag -CommitSha $shaA
    Assert-ReleasePublication ($resultB.MakeLatest -and -not $resultA.MakeLatest -and $fixture.State.Latest -ceq $tagB) 'Delayed A replaced B as Latest.'
    $beforeRetry = $fixture.State.Mutations.Count
    $retryA = Invoke-TestReleasePublication $fixture -Tag $planA.Tag -CommitSha $shaA -Assets (Join-Path $scratch 'no-old-build-files')
    Assert-ReleasePublication ($retryA.Status -ceq 'AlreadyPublished' -and $fixture.State.Mutations.Count -eq $beforeRetry -and $fixture.State.Latest -ceq $tagB) 'Retry of old A changed a public release or Latest.'
    $script:caseCount++

    Write-Host "Release publication recovery and ordering tests passed ($script:caseCount cases)."
} finally {
    if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
