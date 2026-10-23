#requires -Version 7.0
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Subscriptions.psm1') -Force -DisableNameChecking
Import-Module (Join-Path $root 'lib/VideoDownloader.PornHub.psm1') -Force
function Assert([bool]$Value,[string]$Message) {if(-not $Value){throw $Message}}
function Refuses([scriptblock]$Action) {$failed=$false;try{& $Action|Out-Null}catch{$failed=$true};Assert $failed 'Incomplete/foreign discovery must be rejected'}
$source='https://rt.pornhub.com/model/fixture-model'
$json=[IO.File]::ReadAllText((Join-Path $PSScriptRoot 'fixtures/pornhub-discovery.json'))
$dir=Join-Path ([IO.Path]::GetTempPath()) ('vd-pornhub-tests-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($dir)
$previous=$env:VD_PH_FIXTURE
try {
    Assert ((Get-VdPornHubListingUrl $source) -ceq ($source+'/videos/upload')) 'Regional model links must select uploads'
    Assert ((Get-VdPornHubListingUrl ($source+'/videos?page=7')) -ceq ($source+'/videos/upload')) 'A partial-page link must still scan the whole channel'
    Assert ((Get-VdPornHubListingUrl 'https://www.pornhub.com/channels/fixture') -ceq 'https://www.pornhub.com/channels/fixture/videos') 'Channel lists must preserve the channel collection'
    foreach($url in @('https://pornhub.com.evil.example/model/a','https://example.com/model/a','https://user@pornhub.com/model/a','https://pornhub.com/view_video.php?viewkey=phfixture1','https://pornhub.com/model/a/videos/favorites')) {
        Assert (-not (Get-VdPornHubListingUrl $url)) 'Only supported Pornhub publication lists may use the collector'
    }
    $scan=ConvertFrom-VdPornHubScan -Json $json -SourceUrl $source
    Assert ($scan.Items.Count -eq 3 -and $scan.Items[0].Url -ceq 'https://www.pornhub.com/view_video.php?viewkey=phfixture1') 'Collector must produce independent canonical video URLs'
    foreach($change in @(
        {param($r) $r.vd_ph_scan.complete=$false},
        {param($r) $r.vd_ph_scan.verified_first_page=$false},
        {param($r) $r.vd_ph_scan.source_url='https://pornhub.com/model/other'},
        {param($r) $r.vd_ph_scan.pages[1].number=3},
        {param($r) $r.vd_ph_scan.pages[1].has_next=$true},
        {param($r) $r.vd_ph_scan.item_count=2},
        {param($r) $r.entries[1]=$r.entries[0]},
        {param($r) $r.entries[0].url='https://other.example/video'},
        {param($r) $r.entries[0].ie_key='Generic'},
        {param($r) $r.entries[0].vd_page=2}
    )) {
        $record=ConvertFrom-Json $json -AsHashtable
        & $change $record
        $bad=ConvertTo-Json $record -Depth 10 -Compress
        Refuses {ConvertFrom-VdPornHubScan -Json $bad -SourceUrl $source}
    }
    Refuses {ConvertFrom-VdPornHubScan -Json '{"entries":[]}' -SourceUrl $source}
    Refuses {ConvertFrom-VdPornHubScan -Json $json -SourceUrl $source -MaxItems 2}
    Refuses {ConvertFrom-VdPornHubScan -Json $json -SourceUrl $source -MaxPages 1}
    $script:fixtureJson=$json
    $runner={param($exe,$arguments,$token,$timeout,$maximum)
        foreach($flag in @('--ignore-config','--no-plugin-dirs','--plugin-dirs','--flat-playlist','--use-extractors','--cookies')) {
            Assert ($arguments -contains $flag) "Missing channel discovery argument: $flag"
        }
        Assert ($arguments -notcontains '--no-flat-playlist' -and $arguments -notcontains '--playlist-end') 'Collector must enumerate the complete list itself'
        Assert ($timeout -eq 900 -and $maximum -eq 33554432) 'Channel discovery must have a bounded independent budget'
        return @{ExitCode=0;Stdout=$script:fixtureJson;Stderr=''}
    }
    $checked=Invoke-VdSubscriptionScan -YtDlpPath 'must-not-launch' -Subscription @{Url=$source;FeedUrl=''} -CookiePath 'cookies.txt' -ProcessRunner $runner
    Assert ($checked.Items.Count -eq 3) 'Subscriptions must automatically select the site collector'
    $definition=Add-VdSubscription -Directory $dir -Url $source -DownloadExisting $true
    $snapshot=Apply-VdSubscriptionScan -Directory $dir -Id $definition.Id -Scan $checked
    Assert (@($snapshot.Items | Where-Object InitialQueuePending).Count -eq 3) 'Initial import must remain per video until queue acknowledgement'
    $path=Join-Path $dir 'subscriptions.json';$before=[IO.File]::ReadAllText($path)
    $script:fixtureJson=$json.Replace('"complete":true','"complete":false')
    Refuses { $bad=Invoke-VdSubscriptionScan -YtDlpPath 'must-not-launch' -Subscription @{Url=$source;FeedUrl=''} -CookiePath 'cookies.txt' -ProcessRunner $runner; Apply-VdSubscriptionScan -Directory $dir -Id $definition.Id -Scan $bad }
    Assert ([IO.File]::ReadAllText($path) -ceq $before) 'Rejected enumeration must preserve the previous snapshot byte for byte'

    if($IsWindows) {
        # Load the packaged collector in the actual bundled EXE, replacing only
        # its HTTP transport with deterministic HTML. No public site is contacted.
        $fixtureRoot=Join-Path $dir 'plugins';$extractors=Join-Path $fixtureRoot 'fixture/yt_dlp_plugins/extractor'
        [void][IO.Directory]::CreateDirectory($extractors)
        @'
import json
import os
from types import SimpleNamespace
from yt_dlp_plugins.extractor.vd_pornhub import VideoDownloaderPornHubChannelIE as _BaseIE
class VideoDownloaderPornHubFixtureIE(_BaseIE):
    IE_NAME = 'videodownloader:ph:fixture'
    def _login(self, host):
        pass
    def _sleep(self, *args, **kwargs):
        pass
    def _download_webpage_handle(self, base, channel, *args, query=None, **kwargs):
        with open(os.environ['VD_PH_FIXTURE'], encoding='utf-8') as handle:
            pages = json.load(handle)
        number = query['page']
        if number > 1 and base.endswith('/upload'):
            raise RuntimeError('The scanner must reuse the canonical publication endpoint')
        canonical = base.removesuffix('/upload').replace('https://rt.', 'https://www.')
        return pages[str(number)], SimpleNamespace(url=f'{canonical}?page={number}')
'@ | Set-Content -LiteralPath (Join-Path $extractors 'vd_transport.py') -Encoding utf8
        function Page([string[]]$Keys,[bool]$More,[string]$Layout) {
            $cards=(@($Keys|ForEach-Object {'<li class="videoblock"><a title="Fixture" href="/view_video.php?viewkey='+$_+'">Video</a></li>'}) -join '')
            $next=if($More){'<li class="page_next"><a href="?page=2">Next</a></li>'}else{''}
            $main='<ul id="mostRecentVideosSection" class="videos" data-total-videos="3">'+$cards+'</ul>'
            $list=switch($Layout) {
                'videoUList' {'<section class="videoUList"><ul class="videos" data-total-videos="3">'+$cards+'</ul></section>'}
                'videosTab' {'<div class="videoUList"><ul class="videos" data-total-videos="3">'+$cards+'</ul></div>'}
                'duplicateLists' {$main+$main.Replace('mostRecentVideosSection','videoSearchResult')}
                'emptyPlaceholder' {$main+'<ul id="videoSearchResult"></ul>'}
                'nestedLists' {'<div class="videoUList"><div id="mostRecentVideosSection"><ul class="videos" data-total-videos="3">'+$cards+'</ul></div></div>'}
                default {'<ul id="'+$Layout+'" class="videos" data-total-videos="3">'+$cards+'</ul>'}
            }
            $content=$list+'<ul class="pagination">'+$next+'</ul>'
            if($Layout -in @('videosTab','nestedLists')) {$content='<section id="videosTab">'+$content+'</section>'}
            $outside=if($Layout -eq 'videosTab') {'<div class="videoUList"><ul id="videoSearchResult" data-total-videos="9"><li class="videoblock" data-video-vkey="phwrong"></li><a rel="next" href="https://other.example/?page=8">Next</a></ul></div><button id="moreDataBtn">More</button><ul class="pagination"><a href="?page=8">Last</a></ul><a rel="next" href="https://other.example/?page=8">Next</a>'}else{''}
            return '<html><body><h1>Fixture channel</h1>'+$content+$outside+'<div id="watchHistory"><li class="videoBox" data-video-vkey="phwrong"></li></div></body></html>'
        }
        $env:VD_PH_FIXTURE=Join-Path $dir 'pages.json'
        $arguments=@('--ignore-config','--no-plugin-dirs','--plugin-dirs',(Join-Path $root 'collection-plugins'),'--plugin-dirs',$fixtureRoot,
            '--use-extractors','videodownloader:ph:fixture','--flat-playlist','--skip-download','--dump-single-json',$source)
        foreach($layout in @('mostRecentVideosSection','claimedUploadedVideoSection','moreData','channelVideosList','showAllChanelVideos','videoUList','videosTab','duplicateLists','emptyPlaceholder','nestedLists')) {
            @{ '1'=(Page @('phfixture1','phfixture2') $true $layout); '2'=(Page @('phfixture3') $false $layout) } | ConvertTo-Json | Set-Content $env:VD_PH_FIXTURE -Encoding utf8
            $result=Invoke-VdSubscriptionProcess -FilePath (Join-Path $root 'yt-dlp.exe') -Arguments $arguments -TimeoutSeconds 30
            Assert ($result.ExitCode -eq 0) ('Bundled collector failed for '+$layout+': '+$result.Stderr)
            $real=ConvertFrom-VdPornHubScan -Json $result.Stdout -SourceUrl $source
            Assert ($real.Items.Count -eq 3 -and @($real.Items | Where-Object Url -match 'phwrong').Count -eq 0) ('Real yt-dlp must preserve the manifest and exclude watch history: '+$layout)
        }
    }
    Write-Host 'Pornhub channel manifest, routing, per-video import and bundled collector: PASS'
} finally {
    $env:VD_PH_FIXTURE=$previous
    Remove-Item -LiteralPath $dir -Recurse -Force -ErrorAction SilentlyContinue
}
