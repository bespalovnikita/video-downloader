#requires -Version 7.4
# Exercise the production ingress -> preview queue -> metadata child -> row title
# path. Only WPF controls and unrelated checkpoint/button state are isolated.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
$previewCacheModule=Join-Path $root 'lib/VideoDownloader.PreviewCache.psm1'
Import-Module $previewCacheModule
Import-Module (Join-Path $root 'lib/VideoDownloader.Session.psm1')
Import-Module (Join-Path $root 'lib/VideoDownloader.DownloadHistory.psm1')
Import-Module (Join-Path $root 'lib/VideoDownloader.SubscriptionNames.psm1')
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$null,[ref]$null)
foreach($name in @('Is-ValidUrl','Extract-Urls','Find-QueueItem','Reindex-Queue','Add-Urls','Add-FileToQueue',
    'Add-SubscriptionUrls','Confirm-DuplicateDownloads','Set-VideoTitle','Get-CachedPreview','Save-CachedPreview',
    'Queue-PreviewPrefetch','Start-NextPreviewPrefetch','Complete-PreviewPrefetch')) {
    $node=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Assert ($null -ne $node) "Production function $name is missing"
    . ([scriptblock]::Create($node.Extent.Text))
}
$urlHandler=$ast.Find({param($n)
    $n -is [Management.Automation.Language.InvokeMemberExpressionAst] -and
    $n.Expression.Extent.Text -eq '$AddUrlButton' -and $n.Member.Extent.Text -eq 'Add_Click'
},$true)
Assert ($null -ne $urlHandler) 'The real Add URL click handler must exist'
$addUrlClick=$urlHandler.Arguments[0].ScriptBlock.GetScriptBlock()
function Save-SessionCheckpoint {$script:checkpointCount++}
function Update-StartState {}
function Show-DuplicateDownloadDialog {throw 'Fresh fixture URLs must not require download-history approval'}
function Show-PreviewResult {throw 'Background title hydration must not select/render a preview'}
function Log-Line([string]$Message){$script:logs.Add($Message)}
function Wait-ForIngressPreview {
    $deadline=[DateTime]::UtcNow.AddSeconds(20)
    while(($script:previewPrefetchJob -or $script:previewPrefetchQueue.Count) -and [DateTime]::UtcNow -lt $deadline) {
        Complete-PreviewPrefetch
        Start-Sleep -Milliseconds 20
    }
    Complete-PreviewPrefetch
    Assert ($null -eq $script:previewPrefetchJob -and $script:previewPrefetchQueue.Count -eq 0) 'The actual metadata worker must finish its queued requests'
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-title-ingress-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$previousDirectory=$env:VD_TITLE_INGRESS_FIXTURE
$previousPwsh=$env:VD_TITLE_INGRESS_PWSH
try {
    $env:VD_TITLE_INGRESS_FIXTURE=$temp
    $ytDlp=Join-Path $temp 'yt-dlp.exe'
    if($IsWindows) {
        $compiler=@('Framework64','Framework') | ForEach-Object {Join-Path $env:WINDIR "Microsoft.NET/$_/v4.0.30319/csc.exe"} |
            Where-Object {Test-Path -LiteralPath $_} | Select-Object -First 1
        Assert ([bool]$compiler) 'Fixture compiler unavailable'
        $fixture=Join-Path $temp 'Fixture.cs'
        @'
using System;
using System.IO;
using System.Linq;
using System.Text;
class Fixture {
 static int Main(string[] args) {
  if(!args.Contains("--ignore-config") || !args.Contains("--dump-single-json") || !args.Contains("--skip-download"))return 2;
  string url=args.Last();
  File.AppendAllText(Path.Combine(Environment.GetEnvironmentVariable("VD_TITLE_INGRESS_FIXTURE"),"metadata-calls.txt"),url+"\n",Encoding.UTF8);
  if(url.EndsWith("/subscription")){Console.Error.WriteLine("Fixture metadata unavailable");return 1;}
  string title=url.EndsWith("/file")?"Название из файла":url.EndsWith("/shortcut")?"Название из ярлыка":"Название из URL";
  Console.OutputEncoding=new UTF8Encoding(false);
  Console.WriteLine("{\"title\":\""+title+"\",\"uploader\":\"Fixture\",\"duration\":42}");
  return 0;
 }
}
'@ | Set-Content -LiteralPath $fixture -Encoding utf8
        $result=& $compiler /nologo /codepage:65001 "/out:$ytDlp" $fixture 2>&1
        Assert ($LASTEXITCODE -eq 0) "Fixture compilation failed: $result"
    } else {
        $env:VD_TITLE_INGRESS_PWSH=(Get-Process -Id $PID).Path
        @'
$ErrorActionPreference='Stop'
foreach($required in '--ignore-config','--dump-single-json','--skip-download'){if($args -notcontains $required){exit 2}}
$url=[string]$args[-1]
[IO.File]::AppendAllText((Join-Path $env:VD_TITLE_INGRESS_FIXTURE 'metadata-calls.txt'),$url+"`n",[Text.Encoding]::UTF8)
if($url.EndsWith('/subscription')){[Console]::Error.WriteLine('Fixture metadata unavailable');exit 1}
$title=if($url.EndsWith('/file')){'Название из файла'}elseif($url.EndsWith('/shortcut')){'Название из ярлыка'}else{'Название из URL'}
@{title=$title;uploader='Fixture';duration=42}|ConvertTo-Json -Compress
'@ | Set-Content -LiteralPath (Join-Path $temp 'metadata.ps1') -Encoding utf8
        [IO.File]::WriteAllText($ytDlp,"#!/bin/sh`nexec `"`$VD_TITLE_INGRESS_PWSH`" -NoProfile -File `"`$VD_TITLE_INGRESS_FIXTURE/metadata.ps1`" `"`$@`"`n")
        & chmod +x $ytDlp
    }
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:downloads=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:failedUrls=[Collections.Generic.List[string]]::new()
    $script:previewMemoryCache=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $script:previewCacheDir=Join-Path $temp 'preview-cache';$script:previewCacheTtlHours=72
    $script:previewPrefetchQueue=[Collections.Generic.List[string]]::new()
    $script:previewPrefetchQueued=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $script:previewPrefetchJob=$null;$script:previewPrefetchUrl='';$script:previewUrl=''
    $script:proc=$null;$script:recoveredEngine=$null;$script:activeEngine=$null
    $script:checkpointReadOnly=$false;$script:lastSessionError='';$script:checkpointCount=0
    $script:historyPath=Join-Path $temp 'successful-downloads.jsonl'
    $script:logs=[Collections.Generic.List[string]]::new()
    foreach($name in 'QueueGrid','DownloadGrid') {
        $items=[pscustomobject]@{RefreshCount=0}
        $items|Add-Member ScriptMethod Refresh {$this.RefreshCount++}
        Set-Variable -Name $name -Scope Script -Value ([pscustomobject]@{Items=$items;SelectedItem=$null})
    }
    $QueueCount=[pscustomobject]@{Text=''};$CookiesBox=[pscustomobject]@{Text=''}
    $ClipboardHint=[pscustomobject]@{Text=''};$UrlInput=[pscustomobject]@{Text=''}
    $UrlInput|Add-Member ScriptMethod Clear {$this.Text=''}

    $url='https://example.invalid/title-ingress/url'
    $UrlInput.Text=$url
    & $addUrlClick
    Assert ($UrlInput.Text -eq '' -and $script:queue.Count -eq 1 -and (Find-QueueItem $url).Title -ceq '') 'The real URL button must add the URL and schedule metadata without supplying a fake row title'
    Wait-ForIngressPreview
    Assert ((Find-QueueItem $url).Title -ceq 'Название из URL') 'URL entry must receive the real metadata-child title before Start or selection'
    Write-Host 'PASS URL: Название из URL (background metadata, before Start, no selection)'

    $fileUrl='https://example.invalid/title-ingress/file'
    $list=Join-Path $temp 'URLs [из файла].txt'
    [IO.File]::WriteAllText($list,$fileUrl+"`n",[Text.Encoding]::UTF8)
    Add-FileToQueue $list
    Assert ((Find-QueueItem $fileUrl).Title -ceq '') 'Plain file import must schedule metadata rather than copying the list filename as video title'
    Wait-ForIngressPreview
    Assert ((Find-QueueItem $fileUrl).Title -ceq 'Название из файла') 'Plain file import must receive the real metadata-child title before Start or selection'
    Write-Host 'PASS FILE .txt: Название из файла (background metadata, before Start, no selection)'

    $shortcutUrl='https://example.invalid/title-ingress/shortcut'
    $shortcut=Join-Path $temp 'Ярлык [video].url'
    [IO.File]::WriteAllText($shortcut,"[InternetShortcut]`r`nURL=$shortcutUrl`r`n",[Text.Encoding]::UTF8)
    Add-FileToQueue $shortcut
    Wait-ForIngressPreview
    Assert ((Find-QueueItem $shortcutUrl).Title -ceq 'Название из ярлыка') 'Internet Shortcut import must use its URL and hydrate its video title'
    Write-Host 'PASS FILE .url: Название из ярлыка (background metadata, before Start, no selection)'

    $subscriptionUrl='https://example.invalid/title-ingress/subscription'
    $titles=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    $titles[$subscriptionUrl]='Название из подписки'
    $accepted=@(Add-SubscriptionUrls -Urls @($subscriptionUrl) -SubscriptionId 'fixture-subscription' -SubscriptionName '123' -Titles $titles)
    Assert ($accepted.Count -eq 1 -and (Find-QueueItem $subscriptionUrl).Title -ceq 'Название из подписки') 'Subscription import must publish the discovery title immediately before preview completes'
    Wait-ForIngressPreview
    Assert ((Find-QueueItem $subscriptionUrl).Title -ceq 'Название из подписки' -and
        @($script:logs|Where-Object {$_ -like '*Fixture metadata unavailable*'}).Count -eq 1) 'A real metadata-child failure must leave the immediate subscription title intact'
    Write-Host 'PASS SUBSCRIPTION: Название из подписки (immediate; retained after metadata failure)'

    $expected=@($url,$fileUrl,$shortcutUrl,$subscriptionUrl)
    $calls=@(Get-Content -LiteralPath (Join-Path $temp 'metadata-calls.txt') -Encoding utf8)
    Assert ($calls.Count -eq 4 -and (($calls -join '|') -ceq ($expected -join '|'))) 'Every exact URL must go through one actual metadata-only child, in ingress order'
    Assert ($script:queue.Count -eq 4 -and @($script:queue|Where-Object {$_.Status -ne 'Queued' -or $_.State -ne 'Queued'}).Count -eq 0 -and
        $null -eq $script:proc -and $null -eq $QueueGrid.SelectedItem -and $script:previewUrl -eq '') 'Title hydration must neither start a download nor select a row'
    Assert ((@($script:queue.Url) -join '|') -ceq ($expected -join '|')) 'All ingress paths must preserve the exact original URLs'
    Assert ($script:checkpointCount -ge 4) 'All ingress paths must retain their normal checkpoint boundary'
    Write-Host 'PASS all title ingress paths: 4 exact URLs, 4 metadata-only children, 0 downloads, 0 row selections'
} finally {
    if($script:previewPrefetchJob) {
        $script:previewPrefetchJob|Stop-Job -ErrorAction SilentlyContinue
        $script:previewPrefetchJob|Remove-Job -Force -ErrorAction SilentlyContinue
    }
    $env:VD_TITLE_INGRESS_FIXTURE=$previousDirectory;$env:VD_TITLE_INGRESS_PWSH=$previousPwsh
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
