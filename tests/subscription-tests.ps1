#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Subscriptions.psm1') -Force -DisableNameChecking
function Assert([bool]$Value,[string]$Message) { if (-not $Value) { throw $Message } }
function Expect-Error([scriptblock]$Action,[string]$Message) {
    $failed=$false
    try { & $Action | Out-Null } catch { $failed=$true }
    Assert $failed $Message
}
function Scan([string[]]$Urls,[string]$Title='Example') {
    return @{Title=$Title;Items=@($Urls|ForEach-Object { @{Url=$_;Title='Material '+$_} });FeedUrl='';Warning=''}
}
function Item($Snapshot,[string]$Id,[string]$Url) {
    return @($Snapshot.Items | Where-Object { $_.SubscriptionId -ceq $Id -and $_.Url -ceq $Url })[0]
}
$testRoot=Join-Path $root ('work/subscription-tests-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($testRoot)
try {
    $directory=Join-Path $testRoot 'store'
    $empty=Read-VdSubscriptions -Directory $directory
    Assert ($empty.Version -eq 1 -and $empty.Items.Count -eq 0 -and $empty.Subscriptions.Count -eq 0) 'Missing store must be empty without networking'
    $a=Add-VdSubscription -Directory $directory -Url 'https://example.com/Channel'
    Expect-Error { Add-VdSubscription -Directory $directory -Url 'https://example.com/Channel' } 'Exact duplicate source must be refused'
    $b=Add-VdSubscription -Directory $directory -Url 'https://example.com/channel' -DownloadExisting $true
    $u='https://example.com/Video?x=1'; $lower='https://example.com/video?x=1'; $new='https://example.com/new'
    $first=Apply-VdSubscriptionScan -Directory $directory -Id $a.Id -Scan (Scan @($u,$lower))
    Assert ((Item $first $a.Id $u).State -eq 'Baseline' -and -not (Item $first $a.Id $u).InitialQueuePending) 'Default first scan must baseline existing materials'
    $oldSuccess=$first.Subscriptions[0].LastSuccessUtc
    $first.Items[0].Title='Detached mutation'
    Assert ((Read-VdSubscriptions -Directory $directory).Items[0].Title -cne 'Detached mutation') 'Read snapshots must be detached'
    $failed=Set-VdSubscriptionFailure -Directory $directory -Id $a.Id -Error 'Network failure'
    Assert ($failed.Items.Count -eq 2 -and $failed.Subscriptions[0].LastSuccessUtc -eq $oldSuccess -and $failed.Subscriptions[0].LastError) 'Failed check must preserve baseline and last successful check'
    $second=Apply-VdSubscriptionScan -Directory $directory -Id $a.Id -Scan (Scan @($lower,$new,$u))
    Assert ((Item $second $a.Id $new).State -eq 'New' -and $second.Items.Count -eq 3) 'Reorder must not rediscover known URLs; unseen URL must be new'
    $initial=Apply-VdSubscriptionScan -Directory $directory -Id $b.Id -Scan (Scan @($u,$lower))
    Assert ((Item $initial $b.Id $u).State -eq 'New' -and (Item $initial $b.Id $u).InitialQueuePending) 'Explicit existing import must remain pending until queue acknowledgement'
    $initial=Set-VdSubscriptionItemsQueued -Directory $directory -Id $b.Id -Urls @($u)
    Assert ((Item $initial $b.Id $u).State -eq 'Queued' -and -not (Item $initial $b.Id $u).InitialQueuePending) 'Queue acknowledgement is per exact URL'
    Assert ((Item $initial $b.Id $lower).InitialQueuePending) 'Unqueued URLs must retain initial queue intent'
    $initial=Skip-VdSubscriptionItems -Directory $directory -Id $b.Id -Urls @($lower)
    Assert ((Item $initial $b.Id $lower).State -eq 'Skipped' -and -not (Item $initial $b.Id $lower).InitialQueuePending) 'Skip must be durable and clear initial queue intent'
    Expect-Error { Skip-VdSubscriptionItems -Directory $directory -Id $b.Id -Urls @($u) } 'Skip must not silently remove an already queued job'
    $before=[IO.File]::ReadAllText((Join-Path $directory 'subscriptions.json'))
    Expect-Error { Set-VdSubscriptionItemsQueued -Directory $directory -Id $b.Id -Urls @($lower,'https://example.com/unknown') } 'Unknown queue selection must fail atomically'
    Assert ([IO.File]::ReadAllText((Join-Path $directory 'subscriptions.json')) -ceq $before) 'Rejected selection must preserve store bytes'
    $success=Set-VdSubscriptionSuccessful -Directory $directory -Url $u
    Assert ((Item $success $b.Id $u).State -eq 'Processed' -and (Item $success $a.Id $u).State -eq 'AlreadyProcessed') 'A genuine success must reconcile cross-source matches using the queued source'
    Assert ((Item $success $a.Id $u).DuplicateSubscriptionId -ceq $b.Id -and -not (Item $success $a.Id $u).DuplicateNotified) 'Cross-source notice needs durable provenance and acknowledgement'
    Assert ((Item $success $a.Id $lower).State -eq 'Baseline') 'Exact case-distinct URL must not be processed'
    $notice=Item $success $a.Id $u
    $success=Confirm-VdSubscriptionDuplicates -Directory $directory -Items @($notice)
    $success=Sync-VdSubscriptionsSuccessful -Directory $directory -SuccessfulUrls @($u)
    Assert ((Item $success $a.Id $u).DuplicateNotified) 'Repeated success/history recovery must never replay an acknowledged notice'
    $success=Remove-VdSubscription -Directory $directory -Id $b.Id
    Assert ((Item $success $a.Id $u).DuplicateSubscriptionId -ceq $b.Id -and (Item $success $a.Id $u).DuplicateNotified) 'Removing a source must preserve duplicate provenance and acknowledgement'
    Assert ((Read-VdSubscriptions -Directory $directory).Items.Count -eq 3) 'Deleting a source must remove only its own items'

    $directory=Join-Path $testRoot 'unknown-success'
    $a=Add-VdSubscription -Directory $directory -Url 'https://example.com/a'
    $b=Add-VdSubscription -Directory $directory -Url 'https://example.com/b'
    Apply-VdSubscriptionScan -Directory $directory -Id $a.Id -Scan (Scan @($u))|Out-Null
    Apply-VdSubscriptionScan -Directory $directory -Id $b.Id -Scan (Scan @($u))|Out-Null
    $snapshot=Sync-VdSubscriptionsSuccessful -Directory $directory -SuccessfulUrls @($u)
    $snapshot=Sync-VdSubscriptionsSuccessful -Directory $directory -SuccessfulUrls @($u)
    Assert (@($snapshot.Items|Where-Object State -eq 'Processed').Count -eq 2 -and @($snapshot.Items|Where-Object State -eq 'AlreadyProcessed').Count -eq 0) 'Success without a known source must remain idempotently processed without cross-source notices'
    $paused=Set-VdSubscriptionPaused -Directory $directory -Id $a.Id -Paused $true
    Assert ($paused.Subscriptions[0].Paused) 'Pause must persist without clearing materials'

    # Failure before initialization never commits a baseline or advances success.
    $fresh=Add-VdSubscription -Directory $directory -Url 'https://example.com/fresh' -DownloadExisting $true
    $snapshot=Set-VdSubscriptionFailure -Directory $directory -Id $fresh.Id -Error 'Incomplete list'
    $fresh=@($snapshot.Subscriptions|Where-Object Id -eq $fresh.Id)[0]
    Assert (-not $fresh.Initialized -and $null -eq $fresh.LastSuccessUtc) 'Failed initial check must remain retryable and uninitialized'
    Expect-Error { Apply-VdSubscriptionScan -Directory $directory -Id $fresh.Id -Scan (Scan @('file:///not-http')) } 'Invalid discovered URL must fail before any store update'

    # Strict JSON schema is shared with the native store, including case and nullability.
    $path=Join-Path $directory 'subscriptions.json'; $original=[IO.File]::ReadAllText($path)
    foreach ($bad in @(
        '{"Version":2,"Subscriptions":[],"Items":[]}',
        '{"Version":1,"subscriptions":[],"Items":[]}',
        '{"Version":1,"Subscriptions":[],"Items":[],"Extra":true}',
        '{"Version":1,"Subscriptions":[],"Items":[],"Version":1}',
        ($original.Replace('"Paused":true','"Paused":null')),
        ($original.Replace('"Initialized":true','"Initialized":false')),
        ($original.Replace('"State":"Processed"','"State":"processed"')),
        ($original.Substring(0,$original.Length-2))
    )) {
        Assert ($bad -cne $original) 'Corrupt fixture must differ from valid snapshot'
        [IO.File]::WriteAllText($path,$bad)
        Expect-Error { Read-VdSubscriptions -Directory $directory } 'Corrupt/unknown/missing subscription schema must be refused'
        Expect-Error { Add-VdSubscription -Directory $directory -Url 'https://example.com/should-not-write' } 'Mutations must refuse damaged stores'
        Assert ([IO.File]::ReadAllText($path) -ceq $bad) 'Damaged store must remain byte-identical'
    }
    [IO.File]::WriteAllText($path,$original)

    $concurrent=Join-Path $testRoot 'concurrent'
    $modulePath=Join-Path $root 'lib/VideoDownloader.Subscriptions.psm1'
    $writers=@(1..3 | ForEach-Object {
        Start-ThreadJob -ArgumentList $modulePath,$concurrent,$_ -ScriptBlock {
            param($module,$directory,$writer)
            Import-Module $module -Force -DisableNameChecking
            foreach ($index in 1..4) { Add-VdSubscription -Directory $directory -Url "https://example.com/writer/$writer/$index" | Out-Null }
        }
    })
    try {
        $writers|Wait-Job|Out-Null
        $writers|Receive-Job -ErrorAction Stop|Out-Null
        Assert ((Read-VdSubscriptions -Directory $concurrent).Subscriptions.Count -eq 12) 'Concurrent writers must not lose updates or corrupt the snapshot'
    } finally { $writers|Remove-Job -Force -ErrorAction SilentlyContinue }

    # Generic platforms, exact URLs, nested complete lists, and bounded metadata.
    $json='{"title":"Generic playlist","entries":[{"url":"https://other.example/V?x=1","title":"One"},{"webpage_url":"https://other.example/v?x=1","title":"Two"}]}'
    $scan=ConvertFrom-VdSubscriptionMetadata -Json $json -SourceUrl 'https://other.example/list'
    Assert ($scan.Items.Count -eq 2 -and $scan.Items[0].Url -ceq 'https://other.example/V?x=1') 'Discovery must support non-YouTube HTTP(S) sources and preserve exact URLs'
    $nested=ConvertFrom-VdSubscriptionMetadata -Json '{"entries":[{"entries":[{"url":"https://other.example/one"}]}]}' -SourceUrl 'https://other.example/list'
    Assert ($nested.Items.Count -eq 1) 'Inline fully enumerated nested playlists must be supported'
    $yt=ConvertFrom-VdSubscriptionMetadata -Json '{"channel_id":"UC1234567890123456789012","entries":[{"id":"abcdefghijk","ie_key":"Youtube"}]}' -SourceUrl 'https://youtube.com/@test'
    Assert ($yt.Items[0].Url -ceq 'https://www.youtube.com/watch?v=abcdefghijk' -and $yt.FeedUrl -match 'channel_id=UC') 'YouTube media URLs and automatic feed URL must be resolved explicitly'
    foreach ($bad in @(
        '{}','{"entries":null}','{"entries":[null]}',
        '{"entries":[{"url":"javascript:alert(1)"}]}',
        '{"playlist_count":2,"entries":[{"url":"https://other.example/one"}]}',
        '{"n_entries":0,"entries":[{"url":"https://other.example/one"}]}',
        '{"requested_entries":[2],"entries":[{"url":"https://other.example/one"}]}',
        '{"requested_entries":1,"entries":[{"url":"https://other.example/one"}]}',
        '{"playlist_count":"1","entries":[{"url":"https://other.example/one"}]}',
        '{"entries":[],"_error":"failed"}',
        '{"entries":[],"truncated":true}',
        '{"entries":[{"url":"https://other.example/one","_incomplete":true}]}',
        '{"entries":[{"url":"https://other.example/list","_type":"playlist"}]}',
        '{"entries":[{"url":"https://gem.cbc.ca/show/s01","_type":"url","ie_key":"CBCGemPlaylist"}]}',
        '{"entries":[{"url":"https://soundcloud.com/artist/sets/album","_type":"url_transparent"}]}',
        '{"entries":[{"url":"https://other.example/one","availability":"private"}]}'
    )) { Expect-Error { ConvertFrom-VdSubscriptionMetadata -Json $bad -SourceUrl 'https://other.example/list' } 'Incomplete/unavailable source must never claim a complete scan' }
    Expect-Error { ConvertFrom-VdSubscriptionMetadata -Json $json -SourceUrl 'https://other.example/list' -MaxItems 1 } 'Metadata item cap must refuse partial collection'
    $oversized='x'*(32MB+1)
    Expect-Error { ConvertFrom-VdSubscriptionMetadata -Json $oversized -SourceUrl 'https://other.example/list' } 'Oversized metadata must be refused'
    $oversized=$null

    $rss='<rss version="2.0"><channel><item><title>From RSS</title><link>https://other.example/one</link></item></channel></rss>'
    $atom='<feed xmlns="http://www.w3.org/2005/Atom"><entry><title>From Atom</title><link rel="alternate" href="https://other.example/two"/></entry></feed>'
    Assert (@(ConvertFrom-VdSubscriptionFeed -Xml $rss).Count -eq 1 -and (ConvertFrom-VdSubscriptionFeed -Xml $atom)[0].Title -ceq 'From Atom') 'Both RSS and Atom must parse without network'
    Expect-Error { ConvertFrom-VdSubscriptionFeed -Xml '<!DOCTYPE rss [<!ENTITY x SYSTEM "file:///etc/passwd">]><rss><channel>&x;</channel></rss>' } 'Feed parser must reject DTD/external entities'
    Expect-Error { ConvertFrom-VdSubscriptionFeed -Xml ('x'*(2MB+1)) } 'Oversized feed must be refused'
    $source=@{Url='https://other.example/list';FeedUrl='https://other.example/feed'}
    $script:scanCalls=0
    $runner={param($file,$arguments,$token,$timeout,$maximum)
        $script:scanCalls++
        foreach ($required in @('--ignore-config','--no-flat-playlist','--dump-single-json','--skip-download','--playlist-end','10001','--abort-on-error')) { Assert ($arguments -contains $required) "Missing full enumeration option: $required" }
        return @{ExitCode=0;Stdout='{"entries":[{"url":"https://other.example/one"}]}';Stderr=''}
    }
    $scan=Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $runner -FeedLoader {param($url,$token) return '<rss><channel><item><title>Enriched</title><link>https://other.example/one</link></item><item><title>Unconfirmed</title><link>https://other.example/feed-only</link></item></channel></rss>'}
    Assert ($scan.Items.Count -eq 1 -and $scan.Items[0].Title -ceq 'Enriched' -and $scan.Warning) 'Feed may enrich exact matches but must not add unconfirmed membership'
    $scan=Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $runner -FeedLoader { throw 'Feed unavailable' }
    Assert ($scan.Items.Count -eq 1 -and $scan.Warning -match 'Feed unavailable') 'Optional feed failure must fall back to successful full enumeration'
    Assert ($script:scanCalls -eq 2) 'Unchanged/unavailable RSS must never skip a full enumeration'
    Expect-Error { Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner { @{ExitCode=1;Stdout='';Stderr='Failed'} } -FeedLoader { $rss } } 'Feed success must never hide full enumeration failure'
    foreach ($diagnostic in @('WARNING: unable to extract page','WARNING: failed to retrieve playlist page','WARNING: giving up after 3 retries','WARNING: partial collection result')) {
        $partialRunner={ @{ExitCode=0;Stdout='{"entries":[{"url":"https://other.example/one"}]}';Stderr=$diagnostic} }.GetNewClosure()
        Expect-Error { Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $partialRunner -FeedLoader { $rss } } 'Incomplete-page warnings must reject an otherwise successful metadata response'
    }

    # Exercise actual bounded native process I/O, timeout and cancellation, offline.
    $pwsh=(Get-Process -Id $PID).Path
    $result=Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','[Console]::Out.Write("metadata"); [Console]::Error.Write("diagnostic")')
    Assert ($result.ExitCode -eq 0 -and $result.Stdout -ceq 'metadata' -and $result.Stderr -ceq 'diagnostic') 'Worker must drain stdout and stderr independently'
    Expect-Error { Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','[Console]::Out.Write(("x"*4096)); Start-Sleep -Seconds 30') -MaxOutputBytes 128 } 'Native stdout limit must stop its process'
    $timer=[Diagnostics.Stopwatch]::StartNew()
    Expect-Error { Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 30') -TimeoutSeconds .2 } 'Native scan must enforce its deadline'
    Assert ($timer.Elapsed.TotalSeconds -lt 4) 'Timed out child must be terminated promptly'
    $cts=[Threading.CancellationTokenSource]::new(); $cts.CancelAfter(200)
    try {
        $timer.Restart()
        Expect-Error { Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 30') -CancellationToken $cts.Token } 'Cancellation token must stop the owned discovery process'
        Assert ($timer.Elapsed.TotalSeconds -lt 4) 'Cancelled child must be terminated promptly'
    } finally { $cts.Dispose() }

    # Cancellation after enumeration but before the atomic store commit must
    # still preserve the baseline, including while another writer owns the lock.
    $lateDirectory=Join-Path $testRoot 'late-cancel'
    $lateSource=Add-VdSubscription -Directory $lateDirectory -Url 'https://cancel.example/collection'
    $lateBefore=[IO.File]::ReadAllText((Join-Path $lateDirectory 'subscriptions.json'))
    $lateCancellation=[Threading.CancellationTokenSource]::new()
    $entered=[Threading.ManualResetEventSlim]::new()
    $storeMutex=[Threading.Mutex]::new($false,'Local\VideoDownloader.Subscriptions')
    $lateJob=$null
    [void]$storeMutex.WaitOne()
    try {
        $lateJob=Start-ThreadJob -ArgumentList (Join-Path $root 'lib/VideoDownloader.Subscriptions.psm1'),$lateDirectory,$lateSource.Id,$lateCancellation.Token,$entered -ScriptBlock {
            param($modulePath,$directory,$id,$token,$entered)
            $ErrorActionPreference='Stop'
            Import-Module $modulePath -Force -DisableNameChecking
            $entered.Set()
            try {
                Apply-VdSubscriptionScan -Directory $directory -Id $id -Scan @{Title='Complete';FeedUrl='';Warning='';Items=@(@{Url='https://cancel.example/video';Title='Video'})} -CancellationToken $token | Out-Null
                throw 'Cancelled scan was committed'
            } catch {
                if (-not $token.IsCancellationRequested) { throw }
                'Cancelled'
            }
        }
        Assert ($entered.Wait([TimeSpan]::FromSeconds(5))) 'Store worker must start before cancellation'
        $lateCancellation.Cancel()
        Assert (@($lateJob | Wait-Job -Timeout 3).Count -eq 1) 'Store lock wait must observe cancellation promptly'
    } finally {
        $storeMutex.ReleaseMutex();$storeMutex.Dispose()
    }
    try {
        Assert ($lateJob.State -eq 'Completed' -and (Receive-Job $lateJob -ErrorAction Stop) -ceq 'Cancelled') 'Cancelled commit must stop successfully without changing the source'
        Assert ([IO.File]::ReadAllText((Join-Path $lateDirectory 'subscriptions.json')) -ceq $lateBefore) 'Late cancellation must preserve baseline, items and full-success timestamp'
    } finally { Remove-Job $lateJob -Force -ErrorAction SilentlyContinue;$entered.Dispose();$lateCancellation.Dispose() }

    # Cancellation owns a single scan tree and must leave an unrelated process alive.
    $neighbourInfo=[Diagnostics.ProcessStartInfo]::new($pwsh); $neighbourInfo.UseShellExecute=$false
    foreach ($argument in @('-NoProfile','-Command','Start-Sleep -Seconds 30')) { [void]$neighbourInfo.ArgumentList.Add($argument) }
    $neighbour=[Diagnostics.Process]::Start($neighbourInfo)
    $cts=[Threading.CancellationTokenSource]::new(); $cts.CancelAfter(200)
    try {
        Expect-Error { Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','Start-Sleep -Seconds 30') -CancellationToken $cts.Token } 'Owned scan must be cancelled'
        Assert (-not $neighbour.HasExited) 'Cancelling discovery must not terminate unrelated native processes'
    } finally {
        $cts.Dispose()
        if (-not $neighbour.HasExited) { $neighbour.Kill($true); [void]$neighbour.WaitForExit(2000) }
        $neighbour.Dispose()
    }
} finally {
    Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'PowerShell subscription store, discovery and bounded-process tests passed.'
