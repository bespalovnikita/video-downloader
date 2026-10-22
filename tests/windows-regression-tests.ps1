#requires -Version 7.0
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Core.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.DownloadHistory.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.Ipc.psm1') -Force
Import-Module (Join-Path $root 'lib/VideoDownloader.PreviewCache.psm1') -Force
function Assert([bool]$value,[string]$message) { if (-not $value) { throw $message } }
$dir = Join-Path ([IO.Path]::GetTempPath()) ('vd-windows-regression-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
try {
    $cache=Join-Path $dir 'preview-cache'
    $cacheUrl='https://example.com/cache'
    [void](Write-VdPreviewCache -CacheDir $cache -Url $cacheUrl -Preview ([pscustomobject]@{Title='Fresh'}))
    Assert ($null -ne (Read-VdPreviewCache -CacheDir $cache -Url $cacheUrl -TtlHours 1)) 'Fresh JSON cache must preserve UTC on read'
    $cachePath=Get-VdPreviewCachePath -CacheDir $cache -Url $cacheUrl
    foreach ($offset in @(-7,6)) {
        foreach ($age in @(71,73)) {
            $record=Get-Content $cachePath -Raw | ConvertFrom-Json
            $record.SavedAtUtc=[DateTimeOffset]::UtcNow.AddHours(-$age).ToOffset([TimeSpan]::FromHours($offset)).ToString('o')
            $record | ConvertTo-Json -Depth 12 | Set-Content $cachePath
            $cached=Read-VdPreviewCache -CacheDir $cache -Url $cacheUrl -TtlHours 72
            Assert (($null -ne $cached) -eq ($age -lt 72)) "TTL must use absolute time: offset=$offset age=$age"
            [void](Write-VdPreviewCache -CacheDir $cache -Url $cacheUrl -Preview ([pscustomobject]@{Title='Fresh'}))
        }
    }
    # Split a UTF-8 character as well as a JSON record across reads.
    $path = Join-Path $dir 'events.jsonl'
    $bytes = [Text.Encoding]::UTF8.GetBytes('{"title":"Видео","value":1}'+"`n")
    $cursor = @{Offset=0L}
    [IO.File]::WriteAllBytes($path,$bytes[0..13])
    Assert (@(Read-VdEventLines -Path $path -Cursor $cursor).Count -eq 0) 'Incomplete event must remain unread'
    $stream = [IO.File]::Open($path,[IO.FileMode]::Append)
    try { $stream.Write($bytes,14,$bytes.Length-14) } finally { $stream.Dispose() }
    $records = @(Read-VdEventLines -Path $path -Cursor $cursor)
    Assert ($records.Count -eq 1 -and ($records[0]|ConvertFrom-Json).title -ceq 'Видео') 'Completed UTF-8 event was lost/corrupted'
    Assert (@(Read-VdEventLines -Path $path -Cursor $cursor).Count -eq 0) 'Event must be consumed exactly once'
    [IO.File]::WriteAllText($path,"{}`n")
    Assert (@(Read-VdEventLines -Path $path -Cursor $cursor).Count -eq 1) 'Truncated/replaced event file must reset cursor'

    $state=@{}
    Assert ((Add-VdTransferSample $state video 100) -eq 100) 'Video bytes missing'
    Assert ((Add-VdTransferSample $state video 100) -eq 100) 'Repeated sample double-counted'
    Assert ((Add-VdTransferSample $state audio 50) -eq 150) 'Audio component overwrote video bytes'
    Assert ((Add-VdTransferSample $state video 20) -eq 170) 'Retry counter reset must remain cumulative'
    foreach ($code in @(400,401,403,404,410,451)) {
        Assert (-not (Test-TransientDownloadError "ERROR: Unable to download webpage: HTTP Error ${code}: denied")) "Permanent HTTP $code should not retry"
    }
    Assert (Test-TransientDownloadError 'Unable to download webpage: HTTP Error 503: unavailable') 'HTTP 503 should retry'

    $history=Join-Path $dir 'history.jsonl'
    [IO.File]::WriteAllLines($history,@(
        '{"Version":1,"Url":"https://example.com/A","CompletedAtUtc":"2026-10-18T10:00:00Z"}',
        '{"Version":1,"Url":"https://example.com/A","CompletedAtUtc":"2026-10-17T10:00:00Z"}',
        '{"Version":1,"Url":"https://example.com/legacy"}'
    ))
    $latest=Read-VdDownloadHistory -Path $history -IncludeTimestamps
    Assert ($latest['https://example.com/A'].CompletedAtUtc -eq [DateTimeOffset]'2026-10-18T10:00:00Z') 'Latest timestamp must win independently of append order'
    Assert ($null -eq $latest['https://example.com/legacy'].CompletedAtUtc) 'Legacy record must not invent a time'
    $known=Read-VdDownloadHistory $history
    $script:receivedTime=$null
    [void](Resolve-VdDuplicateDownloadPlan -Urls @('https://example.com/A') -Known $known -LastDownloads $latest -Prompt {
        param($url,$index,$total,$completedAt)
        $script:receivedTime=$completedAt; 'Yes'
    })
    Assert ($script:receivedTime -eq $latest['https://example.com/A'].CompletedAtUtc) 'Duplicate dialog callback must receive latest success time'

    # Exercise actual GUI functions without starting the GUI or touching user state.
    $tokens=$null; $errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$errors)
    foreach ($name in @('Format-Bytes','Find-Download','Set-Download','Find-QueueItem','Set-QueueState','Set-QueueFields','Set-RowProgress','Update-TransferBytes','Tail-Events','Reindex-Queue','Register-SubscriptionSuccess')) {
        $node=$ast.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    function Save-SessionCheckpoint {}
    function Update-LiveStats {}
    function Update-StartState {}
    $items=[pscustomobject]@{}; $items|Add-Member ScriptMethod Refresh {}
    $QueueGrid=[pscustomobject]@{Items=$items}; $DownloadGrid=[pscustomobject]@{Items=$items}; $QueueCount=[pscustomobject]@{Text=''}
    $script:downloads=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:failedUrls=[Collections.Generic.List[string]]::new()
    $script:speeds=@{}; $script:lastFiles=@{}
    $script:sessionSuccess=0; $script:sessionFailed=0; $script:sessionBytes=0; $script:sessionTotal=2
    $script:subscriptionsDirectory=$dir
    $script:transferredByUrl=[Collections.Generic.Dictionary[string,long]]::new([StringComparer]::Ordinal)
    $OverallProgress=[pscustomobject]@{Value=0}
    $u='https://example.com/second'
    $script:queue.Add([pscustomobject]@{Order=1;Url=$u;Status='Queued';State='Queued';Path='';Progress=0.0;Speed='';Downloaded='';ETA=''})
    Set-Download -Slot 1 -State Done -Url 'https://example.com/first' -Path 'C:\old.mp4'
    Set-Download -Slot 1 -State Starting -Url $u
    Assert ($script:downloads.Count -eq 2 -and $script:downloads[1].Path -eq '') 'Slot reuse must not attach previous video path'
    $script:runEvents=Join-Path $dir 'gui-events.jsonl'; $script:eventCursor=@{Offset=0L}
    $progress=@{Kind='Event';EventType='Progress';Slot=1;Url=$u;Percent=25;Speed='1.00MiB/s';ETA='00:03';DownloadedBytes=1048576;TotalBytes=4194304;TransferredBytes=1048576}|ConvertTo-Json -Compress
    [IO.File]::WriteAllText($script:runEvents,$progress+"`n")
    Tail-Events
    Assert ($script:queue[0].Progress -eq 25 -and $script:downloads[1].Progress -eq 25) 'Both grids must receive live percentage'
    Assert ($script:queue[0].Speed -eq '1.00MiB/s' -and $script:sessionBytes -eq 1048576) 'Speed/session bytes must update before completion'
    $result=@{Kind='Result';Slot=1;Url=$u;Success=$true;Path='';FileSize=9999999;TransferredBytes=1048576}|ConvertTo-Json -Compress
    [IO.File]::AppendAllText($script:runEvents,$result.Substring(0,10))
    Tail-Events
    [IO.File]::AppendAllText($script:runEvents,$result.Substring(10)+"`n")
    Tail-Events
    Assert ($script:sessionSuccess -eq 1 -and $script:queue[0].Status -eq 'Completed') 'Partial Result must eventually complete the queue item'
    Assert (-not (Test-Path (Join-Path $dir 'subscriptions.json'))) 'A successful ordinary download must not create unconfigured subscriptions'
    Assert ($script:sessionBytes -eq 1048576) 'Completion must not count output filesize a second time'
    [void]$script:failedUrls.Add($u); $script:queue.Clear(); Reindex-Queue
    Assert ($script:failedUrls.Count -eq 0) 'Removed failures must leave retry candidates'

    # Exercise the same pipe client used by a second launcher process.
    $pipe='vd-test-'+[guid]::NewGuid().ToString('N')
    $server=[VideoDownloader.Ipc]::new($pipe)
    try {
        $payload=ConvertTo-Json -InputObject @('videodownloader://https%3A%2F%2Fexample.com%2Fvideo') -Compress
        [void][VideoDownloader.Ipc]::Send($pipe,$payload).GetAwaiter().GetResult()
        Assert ($server.Take() -ceq $payload) 'Second instance must forward the complete protocol argument'
        Assert ($null -eq $server.Take()) 'Forwarded argument must be delivered once'
    } finally { $server.Dispose() }

    $engineAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-v8.ps1'),[ref]$tokens,[ref]$errors)
    foreach ($name in @('Build-Args','Get-FormatSelector')) {
        $node=$engineAst.Find({param($n) $n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
        . ([scriptblock]::Create($node.Extent.Text))
    }
    $fragments=1; $autoFragments=$false; $outputTemplate='C:\test.%(ext)s'; $cookies=''; $rateLimit=''; $sponsorBlock=$false
    $audioOnly=$false; $container='Auto'; $videoCodec='Auto'; $writeSubtitles=$false; $embedSubtitles=$false
    $writeAutoSubtitles=$true; $subtitleLangs='ru.*'; $embedThumbnail=$false; $embedMetadata=$false; $embedChapters=$false; $url='https://example.com/test'
    $arguments=Build-Args best
    $languageIndex=[Array]::IndexOf($arguments,'--sub-langs')
    Assert ($languageIndex -ge 0 -and $arguments[$languageIndex+1] -eq 'ru.*') 'Auto-only subtitles must retain the requested language filter'
    Assert ($arguments -contains '--progress') 'Real yt-dlp progress must be explicitly enabled'

    Add-Type -AssemblyName PresentationFramework
    foreach ($file in @('MainWindow.xaml','DuplicateDownloadDialog.xaml')) {
        [xml]$xml=Get-Content (Join-Path $root "ui/$file") -Raw
        $reader=[Xml.XmlNodeReader]::new($xml)
        try { $w=[Windows.Markup.XamlReader]::Load($reader); $w.Close() } finally { $reader.Dispose() }
    }
} finally {
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
Write-Host 'Windows progress/history/events/IPC regression tests passed.'
