#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Subscriptions.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $root 'lib/VideoDownloader.SubscriptionNames.psm1') -Force
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
    foreach($case in @(
        @(' . Мой: канал / Test . ','Мой_ канал _ Test'),@('CON','_CON'),@('nul.txt','_nul.txt'),
        @('LPT³','_LPT³'),@('con .txt','_con .txt'),@('Conductor','Conductor'),@('..','Подписка'),
        @('Имя   канала','Имя канала'),@("bad$([char]1)name",'bad_name'),@("left$([char]0xd800)right",'left_right')
    )) {
        $name=ConvertTo-VdSubscriptionName $case[0]
        Assert ($name -ceq $case[1] -and (Test-VdSubscriptionName $name)) ('Safe folder normalization: '+$case[1])
    }
    $boundary='x'*79
    Assert ((ConvertTo-VdSubscriptionName ($boundary+'😀')) -ceq $boundary) 'Length cap must not split a UTF16 surrogate pair'
    foreach($invalid in @('','.','..','../escape','..\escape','C:\escape','a/b','a\b','CON','name.',' name','a  b',('x'*81))) {
        Assert (-not (Test-VdSubscriptionName $invalid)) ('Unsafe folder must fail validation: '+$invalid)
        if($invalid){Expect-Error {Resolve-VdSubscriptionOutput -BaseDirectory $testRoot -Name $invalid} 'Output resolution must reject unsafe leaves'}
    }
    Assert ((Resolve-VdSubscriptionOutput -BaseDirectory $testRoot -Name '123') -ceq [IO.Path]::Combine([IO.Path]::GetFullPath($testRoot),'123')) 'A subscription name must resolve one folder below the selected output'
    $nameDirectory=Join-Path $testRoot 'names'
    $named=Add-VdSubscription -Directory $nameDirectory -Url 'https://names.example/playlist'
    $namedScan=@{Title='Полный плейлист';SuggestedName='Канал: 123';Items=@();FeedUrl='';Warning=''}
    $snapshot=Apply-VdSubscriptionScan -Directory $nameDirectory -Id $named.Id -Scan $namedScan
    Assert ($snapshot.Subscriptions[0].ShortName -ceq 'Канал_ 123') 'First successful scan must assign a safe channel-based name'
    $namedScan.SuggestedName='Новое имя канала'
    $snapshot=Apply-VdSubscriptionScan -Directory $nameDirectory -Id $named.Id -Scan $namedScan
    Assert ($snapshot.Subscriptions[0].ShortName -ceq 'Канал_ 123') 'Later metadata must not silently change the folder'
    $null=Set-VdSubscriptionShortName -Directory $nameDirectory -Id $named.Id -Name '123'
    $snapshot=Apply-VdSubscriptionScan -Directory $nameDirectory -Id $named.Id -Scan $namedScan
    Assert ((Read-VdSubscriptions -Directory $nameDirectory).Subscriptions[0].ShortName -ceq '123') 'Manual name must survive scans and restarts'
    $custom=Add-VdSubscription -Directory $nameDirectory -Url 'https://names.example/custom' -ShortName 'Моя: папка'
    Assert ($custom.ShortName -ceq 'Моя_ папка') 'Explicit initial name must use the same normalization rules'
    $namePath=Join-Path $nameDirectory 'subscriptions.json'
    $beforeRename=[IO.File]::ReadAllText($namePath)
    Expect-Error {Set-VdSubscriptionShortName -Directory $nameDirectory -Id $named.Id -Name '   '} 'Blank manual name must fail'
    Expect-Error {Add-VdSubscription -Directory $nameDirectory -Url 'https://names.example/rejected' -ShortName '   '} 'Blank initial manual name must fail'
    Assert ([IO.File]::ReadAllText($namePath) -ceq $beforeRename) 'Rejected name changes must preserve the store'
    $legacy=ConvertFrom-Json $beforeRename -AsHashtable
    foreach($source in $legacy.Subscriptions){$source.Remove('ShortName')}
    $legacyJson=ConvertTo-Json $legacy -Depth 12 -Compress
    [IO.File]::WriteAllText($namePath,$legacyJson)
    $legacyRead=Read-VdSubscriptions -Directory $nameDirectory
    Assert (@($legacyRead.Subscriptions | Where-Object ShortName).Count -eq 0 -and [IO.File]::ReadAllText($namePath) -ceq $legacyJson) 'Released v1 without ShortName must remain readable without rewriting on Read'
    $snapshot=Apply-VdSubscriptionScan -Directory $nameDirectory -Id $named.Id -Scan $namedScan
    Assert ($snapshot.Subscriptions[0].ShortName -ceq 'Новое имя канала') 'First successful scan after upgrade must populate a legacy folder'
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
    $namedMetadata='{"title":"Полный плейлист","channel":"Канал 123","uploader":"Автор","entries":[]}'
    $namedResult=ConvertFrom-VdSubscriptionMetadata -Json $namedMetadata -SourceUrl 'https://other.example/list'
    Assert ($namedResult.Title -ceq 'Полный плейлист' -and $namedResult.SuggestedName -ceq 'Канал 123') 'Channel folder suggestion must preserve the original collection title'
    $uploaderResult=ConvertFrom-VdSubscriptionMetadata -Json '{"title":"Плейлист","uploader":"Автор","entries":[]}' -SourceUrl 'https://other.example/list'
    Assert ($uploaderResult.SuggestedName -ceq 'Автор') 'Uploader must precede collection title as folder suggestion'
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

    # Progress is separate from the completed scan. It reports actual phases
    # and only supplies an item count after full metadata has been parsed.
    $progressJson='{"title":"Progress source","entries":[{"url":"https://other.example/one"},{"url":"https://other.example/two","title":"Two"}]}'
    $progressRunner={@{ExitCode=0;Stdout=$progressJson;Stderr=''}}.GetNewClosure()
    $progressEvents=[Collections.Generic.List[object]]::new()
    $reportProgress={param($event) [void]$progressEvents.Add($event);'ignored observer output'}.GetNewClosure()
    $progressResult=@(Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $progressRunner -FeedLoader {$rss} -ProgressScript $reportProgress)
    Assert ($progressResult.Count -eq 1 -and $progressResult[0].Title -ceq 'Progress source' -and
        $progressResult[0].Items.Count -eq 2 -and $progressResult[0].Items[0].Title -ceq 'From RSS') 'Progress output must not leak into or replace the real scan result'
    Assert ((@($progressEvents.Stage) -join '|') -ceq 'Enumerating|Parsing|Feed|Ready') 'The progress phases must follow complete metadata before optional feed enrichment'
    Assert ($null -eq $progressEvents[0].ItemCount -and $null -eq $progressEvents[0].MetadataBytes -and
        $null -eq $progressEvents[1].ItemCount -and $progressEvents[1].MetadataBytes -eq [Text.Encoding]::UTF8.GetByteCount($progressJson) -and
        $progressEvents[2].ItemCount -eq 2 -and $progressEvents[3].ItemCount -eq 2) 'Unknown totals must remain unknown; confirmed counts and bytes must reflect the actual response'
    $noFeed=@{Url=$source.Url;FeedUrl=''}
    $progressEvents.Clear()
    $scan=Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $noFeed -ProcessRunner $progressRunner -ProgressScript $reportProgress
    Assert ((@($progressEvents.Stage) -join '|') -ceq 'Enumerating|Parsing|Ready' -and $scan.Items.Count -eq 2) 'A scan without a feed must not display a fictitious feed phase'
    $progressEvents.Clear()
    $scan=Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $progressRunner -FeedLoader {throw 'Progress feed unavailable'} -ProgressScript $reportProgress
    Assert ($progressEvents[-1].Stage -ceq 'Ready' -and $scan.Warning -match 'Progress feed unavailable' -and $scan.Items.Count -eq 2) 'Optional RSS failure must retain successful full results and report them ready for saving'
    $scan=Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $noFeed -ProcessRunner $progressRunner -ProgressScript {throw 'Observer unavailable'}
    Assert ($scan.Items.Count -eq 2) 'An optional progress consumer failure must not change metadata or fail the scan'
    $noisyResult=@(Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $noFeed -ProcessRunner $progressRunner -ProgressScript {
        Write-Error 'Observer nonterminating error' -ErrorAction Continue
        Write-Warning 'Observer warning'
        'Observer output'
    } *>&1)
    Assert ($noisyResult.Count -eq 1 -and $noisyResult[0].Items.Count -eq 2) 'Progress consumer error/warning streams must remain isolated from the real scan result'
    foreach($badRunner in @(
        {@{ExitCode=1;Stdout='';Stderr='Failed'}},
        {@{ExitCode=0;Stdout='{}';Stderr=''}}
    )) {
        $progressEvents.Clear()
        Expect-Error {Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $badRunner -ProgressScript $reportProgress} 'Failed or incomplete metadata must reject the scan with progress enabled'
        Assert (@($progressEvents | Where-Object Stage -eq 'Ready').Count -eq 0) 'A failed scan must never report ready or a fabricated final item total'
    }
    $progressCancellation=[Threading.CancellationTokenSource]::new()
    $cancelEvents=[Collections.Generic.List[object]]::new()
    try {
        $cancelProgress={param($event) [void]$cancelEvents.Add($event);if($event.Stage -ceq 'Parsing'){$progressCancellation.Cancel()}}.GetNewClosure()
        Expect-Error {Invoke-VdSubscriptionScan -YtDlpPath 'injected-no-executable' -Subscription $source -ProcessRunner $progressRunner -CancellationToken $progressCancellation.Token -ProgressScript $cancelProgress} 'Cancellation between metadata receipt and parsing must still abort the scan'
        Assert ((@($cancelEvents.Stage) -join '|') -ceq 'Enumerating|Parsing') 'Cancellation must stop before feed/ready progress is emitted'
    } finally {$progressCancellation.Dispose()}

    # Exercise actual bounded native process I/O, timeout and cancellation, offline.
    $pwsh=(Get-Process -Id $PID).Path
    $result=Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command','[Console]::Out.Write("metadata"); [Console]::Error.Write("diagnostic")')
    Assert ($result.ExitCode -eq 0 -and $result.Stdout -ceq 'metadata' -and $result.Stderr -ceq 'diagnostic') 'Worker must drain stdout and stderr independently'
    $byteProgress=[Collections.Generic.List[object]]::new()
    $byteWatch=[Diagnostics.Stopwatch]::StartNew()
    $byteReporter={param($event) [void]$byteProgress.Add(@{Record=$event;At=$byteWatch.ElapsedMilliseconds});'ignored child observer output'}.GetNewClosure()
    $streamCommand='[Console]::Out.Write("first");[Console]::Out.Flush();Start-Sleep -Milliseconds 300;[Console]::Out.Write("second");[Console]::Out.Flush();Start-Sleep -Milliseconds 300;[Console]::Out.Write("third");[Console]::Out.Flush()'
    $streamed=@(Invoke-VdSubscriptionProcess -FilePath $pwsh -Arguments @('-NoProfile','-Command',$streamCommand) -ProgressScript $byteReporter)
    Assert ($streamed.Count -eq 1 -and $streamed[0].Stdout -ceq 'firstsecondthird' -and $streamed[0].ExitCode -eq 0) 'Real process byte progress must not contaminate either process output or its return record'
    Assert ($byteProgress.Count -ge 1) 'Real streamed stdout must publish measured metadata bytes'
    $lastBytes=0L;$lastAt=$null
    foreach($sample in $byteProgress) {
        Assert ($sample.Record.Stage -ceq 'Enumerating' -and $null -eq $sample.Record.ItemCount -and
            $sample.Record.MetadataBytes -gt $lastBytes -and $sample.Record.MetadataBytes -le 16) 'Byte progress must remain cumulative actual stdout, without guessed video totals'
        if($null -ne $lastAt){Assert ($sample.At-$lastAt -ge 200) 'Byte progress must be throttled instead of flooding the UI for every read'}
        $lastBytes=$sample.Record.MetadataBytes;$lastAt=$sample.At
    }
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
