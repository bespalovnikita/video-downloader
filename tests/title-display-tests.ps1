#requires -Version 7.0
# Production title/cache/persistence orchestration without a downloader or network.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
Import-Module (Join-Path $root 'lib/VideoDownloader.Session.psm1')
Import-Module (Join-Path $root 'lib/VideoDownloader.Profiles.psm1')
function Assert([bool]$Value,[string]$Message){if(-not $Value){throw $Message}}
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$null,[ref]$null)
foreach($name in @('Set-VideoTitle','Get-PreviewDisplayTitle','Update-VideoTitleDisplay','Set-Download','Find-Download','Find-QueueItem',
    'Queue-PreviewPrefetch','Start-NextPreviewPrefetch','Complete-PreviewPrefetch','Get-SessionSettings','Restore-SessionSettings','Get-SessionSnapshot')) {
    $node=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
    Assert ($null -ne $node) "Missing production function $name"
    . ([scriptblock]::Create($node.Extent.Text))
}
function Is-ValidUrl([string]$Url){return $Url.StartsWith('https://',[StringComparison]::Ordinal)}
function Get-CachedPreview([string]$Url){if($script:testCache.ContainsKey($Url)){return $script:testCache[$Url]};return $null}
function Save-CachedPreview([string]$Url,$Preview){$script:testCache[$Url]=$Preview;return $Preview}
function Log-Line([string]$Text){}
function Show-PreviewResult{throw 'An unselected background preview must not steal the visible preview'}
function Save-SessionCheckpoint{$script:savedSnapshots++}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-title-display-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
try {
    $script:savedSnapshots=0
    $script:testCache=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    $script:previewPrefetchQueue=[Collections.Generic.List[string]]::new()
    $script:previewPrefetchQueued=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $script:previewPrefetchJob=$null;$script:previewPrefetchUrl='';$script:previewUrl='https://example.org/selected'
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:downloads=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $upper='https://example.org/Video';$lower='https://example.org/video';$unavailable='https://example.org/unavailable'
    foreach($url in @($upper,$lower,$unavailable)){$script:queue.Add([pscustomobject]@{Url=$url;Title='';Status='Queued';State='Queued'})}
    foreach($name in @('QueueGrid','DownloadGrid')) {
        $items=[pscustomobject]@{RefreshCount=0};$items|Add-Member ScriptMethod Refresh {$this.RefreshCount++}
        Set-Variable -Name $name -Value ([pscustomobject]@{Items=$items}) -Scope Script
    }
    foreach($name in @('ProfileCombo','QualityCombo','CodecCombo','ContainerCombo','FragmentsCombo')){
        Set-Variable -Name $name -Value ([pscustomobject]@{SelectedItem='Universal';Items=@('Universal');Text=''}) -Scope Script
    }
    foreach($name in @('RateLimitCombo','ThreadsBox','FilenameTemplateBox','OutputBox','CookiesBox','SubtitleLangsBox')){
        Set-Variable -Name $name -Value ([pscustomobject]@{Text=$temp}) -Scope Script
    }
    foreach($name in @('WriteSubsCheck','AutoSubsCheck','EmbedSubsCheck','EmbedThumbnailCheck','EmbedMetadataCheck','EmbedChaptersCheck','ArchiveCheck','SponsorCheck','AudioOnlyCheck','WatchClipboardCheck','ShowVideoTitlesCheck')){
        Set-Variable -Name $name -Value ([pscustomobject]@{IsChecked=$false}) -Scope Script
    }
    $script:testCache[$upper]=[pscustomobject]@{Title='Кэшированное видео'}
    Queue-PreviewPrefetch -Url $upper
    Assert ($script:queue[0].Title -ceq 'Кэшированное видео' -and $script:queue[1].Title -ceq '' -and $null -eq $script:previewPrefetchJob -and $script:previewPrefetchQueue.Count -eq 0) 'An exact cache hit must hydrate only its row without scheduling metadata'
    Set-Download -Slot 1 -State Starting -Url $upper
    Assert ($script:downloads[0].Title -ceq 'Кэшированное видео') 'Downloads must inherit the known queue title before engine metadata arrives'
    Set-VideoTitle -Url $upper -Title 'Название движка'
    Assert ($script:queue[0].Title -ceq 'Название движка' -and $script:downloads[0].Title -ceq 'Название движка' -and $script:queue[1].Title -ceq '') 'A title event must update both lists using ordinal URL identity'
    Set-VideoTitle -Url $upper -Title $upper
    Assert ($script:queue[0].Title -ceq 'Название движка' -and (Get-PreviewDisplayTitle -Url $upper -Title '') -ceq 'Название движка' -and
        (Get-PreviewDisplayTitle -Url $upper -Title $upper) -ceq 'Название движка') 'An empty or URL-only preview result must preserve a known discovery/engine title in the panel and queue'

    # Receive an actual completed ThreadJob through the production completion
    # handler. The row is not selected, and no second fetch is queued.
    $script:previewPrefetchUrl=$lower
    $script:previewPrefetchJob=Start-ThreadJob { [pscustomobject]@{Success=$true;Title='Фоновое видео'} }
    $null=Wait-Job $script:previewPrefetchJob -Timeout 10
    Complete-PreviewPrefetch
    Assert ($script:queue[1].Title -ceq 'Фоновое видео' -and $null -eq $script:previewPrefetchJob -and $script:previewUrl -ceq 'https://example.org/selected') 'Background metadata must populate an unselected row without stealing selection'
    $script:previewPrefetchUrl=$unavailable
    $script:previewPrefetchJob=Start-ThreadJob {[pscustomobject]@{Success=$false;Error='Unavailable'}}
    $null=Wait-Job $script:previewPrefetchJob -Timeout 10
    Complete-PreviewPrefetch
    Set-VideoTitle -Url $unavailable -Title '   '
    Assert ($script:queue[2].Title -ceq '') 'Failed or blank metadata must preserve the URL fallback'

    $urls=[string[]]@($script:queue.Url)
    $ShowVideoTitlesCheck.IsChecked=$true;Update-VideoTitleDisplay
    $ShowVideoTitlesCheck.IsChecked=$false;Update-VideoTitleDisplay
    Assert ($script:savedSnapshots -eq 2 -and $null -eq $script:previewPrefetchJob -and $script:previewPrefetchQueue.Count -eq 0 -and ((@($script:queue.Url) -join '|') -ceq ($urls -join '|'))) 'Switching labels must only refresh/persist presentation, preserving queue identities and metadata work'
    $ShowVideoTitlesCheck.IsChecked=$true
    $path=Join-Path $temp 'session.json'
    Write-VdSessionCheckpoint -Path $path -Session (Get-SessionSnapshot)
    $saved=Read-VdSessionCheckpoint -Path $path
    $restored=@(Get-VdRestoredQueue -Session $saved)
    Assert ($saved.Settings.ShowVideoTitles -eq $true -and $restored[0].Title -ceq 'Название движка' -and $restored[1].Title -ceq 'Фоновое видео') 'Checkpoint restart must preserve the label preference and discovered titles'
    $ShowVideoTitlesCheck.IsChecked=$false;Restore-SessionSettings $saved.Settings
    Assert ($ShowVideoTitlesCheck.IsChecked -eq $true) 'Restoring settings must reapply title mode'
    $legacy=@{Queue=@(@{Url=$upper;Status='Queued'});Settings=@{}}
    Restore-SessionSettings $legacy.Settings
    Assert (-not $ShowVideoTitlesCheck.IsChecked -and @(Get-VdRestoredQueue -Session $legacy)[0].Title -ceq '') 'Released checkpoints without a preference or title must keep URL mode and fallback'

    Import-Module (Join-Path $root 'lib/VideoDownloader.PreviewCache.psm1')
    $script:previewCacheDir=Join-Path $temp 'preview-cache';$script:previewCacheTtlHours=72
    $script:previewMemoryCache=@{};$ytDlp=Join-Path $temp 'yt-dlp.exe'
    $oldImageFailure=[pscustomobject]@{Title='Известное название';ThumbnailBase64='';ThumbnailError='no JPEG/PNG thumbnail was provided by yt-dlp'}
    $oldRecord=Write-VdPreviewCache -CacheDir $script:previewCacheDir -Url $upper -Preview $oldImageFailure
    $script:previewMemoryCache[$upper]=$oldRecord
    $realCache=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Get-CachedPreview'},$true)
    & {
        param($definition,$url)
        . ([scriptblock]::Create($definition))
        Assert ($null -eq (Get-CachedPreview $url)) 'A legacy suffix-only image failure must not survive in GUI memory after the disk cache becomes retryable'
    } $realCache.Extent.Text $upper
    Assert (-not $script:previewMemoryCache.ContainsKey($upper) -and $null -eq $script:previewPrefetchJob) 'Expiring only the failed thumbnail cache must not launch a second worker'

    # Exercise the real memory/disk orchestration, including its production
    # initializer. A test-only ordinal map would hide the released @{} defect.
    & {
        $initializer=$ast.EndBlock.Statements | Where-Object {
            $_ -is [Management.Automation.Language.AssignmentStatementAst] -and
            $_.Left -is [Management.Automation.Language.VariableExpressionAst] -and
            $_.Left.VariablePath.UserPath -eq 'script:previewMemoryCache'
        }
        Assert ($null -ne $initializer) 'The production preview cache initializer must exist'
        . ([scriptblock]::Create($initializer.Extent.Text))
        foreach($name in @('Get-CachedPreview','Save-CachedPreview')) {
            $definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq $name},$true)
            . ([scriptblock]::Create($definition.Extent.Text))
        }
        $script:previewCacheDir=Join-Path $temp 'exact-cache'
        $upperRecord=Save-CachedPreview -Url $upper -Preview ([pscustomobject]@{Title='Название Video'})
        Assert ($null -eq (Get-CachedPreview $lower)) 'A case-distinct URL must not reuse another video from memory'
        $script:previewMemoryCache[$lower]=$upperRecord
        Assert ($null -eq (Get-CachedPreview $lower)) 'A memory entry with a mismatched exact Url must be rejected even under the requested key'
        $null=Save-CachedPreview -Url $lower -Preview ([pscustomobject]@{Title='Название video'})
        Assert ((Get-CachedPreview $upper).Title -ceq 'Название Video' -and (Get-CachedPreview $lower).Title -ceq 'Название video' -and
            $script:previewMemoryCache.Count -eq 2) 'Case-distinct videos must keep separate cached identities and names'

        # Keep the production save/completion handlers and an actual ThreadJob;
        # substitute only the disk failure and platform rendering boundary.
        function Write-VdPreviewCache {param($CacheDir,$Url,$Preview) throw [IO.IOException]::new('Fixture: cache disk is full')}
        $script:renderedPreview=$null;$script:previewRenderCalls=0
        function Show-PreviewResult {param($Result,$FromCache) $script:renderedPreview=$Result;$script:previewRenderCalls++}
        $failedDiskUrl='https://example.org/cache-write-failure'
        $script:previewUrl=$failedDiskUrl;$script:previewPrefetchUrl=$failedDiskUrl
        $script:previewPrefetchJob=Start-ThreadJob {
            [pscustomobject]@{Success=$true;Title='Полученное видео';Uploader='Автор';Duration=42;MaxHeight=1080;
                PreviewFetchVersion=2;ThumbnailValidationVersion=1;
                ThumbnailBase64='iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAAC0lEQVR4nGP4DwQACfsD/fteaysAAAAASUVORK5CYII='}
        }
        $null=Wait-Job $script:previewPrefetchJob -Timeout 10
        Complete-PreviewPrefetch
        Assert ($script:previewRenderCalls -eq 1 -and $script:renderedPreview.Title -ceq 'Полученное видео' -and
            $script:renderedPreview.Uploader -ceq 'Автор' -and $script:renderedPreview.Duration -eq 42 -and
            $script:renderedPreview.MaxHeight -eq 1080 -and $script:renderedPreview.ThumbnailBase64) 'A disk cache failure must still display the successful text, formats and image'
        $memoryResult=Get-CachedPreview $failedDiskUrl
        Assert ($memoryResult.Url -ceq $failedDiskUrl -and $memoryResult.Title -ceq 'Полученное видео' -and
            -not (Test-Path -LiteralPath (Get-VdPreviewCachePath -CacheDir $script:previewCacheDir -Url $failedDiskUrl))) 'A failed disk write must retain a complete usable memory entry without requiring a disk reread'
        Queue-PreviewPrefetch -Url $failedDiskUrl
        Assert ($null -eq $script:previewPrefetchJob -and $script:previewPrefetchQueue.Count -eq 0) 'Revisiting a preview after disk failure must use memory without refetching metadata'
    }

    if($IsWindows) {
        Add-Type -AssemblyName PresentationFramework
        & {
            $definition=$ast.Find({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -eq 'Show-PreviewResult'},$true)
            . ([scriptblock]::Create($definition.Extent.Text))
            $PreviewTitle=[pscustomobject]@{Text=''};$PreviewMeta=[pscustomobject]@{Text=''}
            $PreviewFormats=[pscustomobject]@{Text=''};$PreviewImage=[pscustomobject]@{Source=$null}
            foreach($size in @(@(2,3,2,3),@(2,2000,1,720),@(2000,2,720,1))) {
                $width=$size[0];$height=$size[1];$stride=$width*4
                $source=[Windows.Media.Imaging.BitmapSource]::Create($width,$height,96,96,[Windows.Media.PixelFormats]::Bgra32,$null,[byte[]]::new($stride*$height),$stride)
                $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new()
                $encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($source))
                $stream=[IO.MemoryStream]::new()
                try {$encoder.Save($stream);$image=[Convert]::ToBase64String($stream.ToArray())} finally {$stream.Dispose()}
                $result=New-VdPreviewCacheRecord -Url $upper -Preview ([pscustomobject]@{Title='Проверка размеров';ThumbnailBase64=$image})
                Show-PreviewResult -Result $result
                Assert ($PreviewImage.Source.PixelWidth -eq $size[2] -and $PreviewImage.Source.PixelHeight -eq $size[3]) 'Actual WPF previews must bound the longest side to 720 without enlarging small images'
            }
        }
        [xml]$xml=Get-Content (Join-Path $root 'ui/MainWindow.xaml') -Raw
        $reader=[Xml.XmlNodeReader]::new($xml)
        try{$window=[Windows.Markup.XamlReader]::Load($reader)}finally{$reader.Dispose()}
        try {
            $window.Show()
            $check=$window.FindName('ShowVideoTitlesCheck')
            $grids=@($window.FindName('QueueGrid'),$window.FindName('DownloadGrid'))
            $known=[pscustomobject]@{Order=1;Slot='T1';Url=$upper;Title='Заголовок WPF';Status='Queued';State='Queued'}
            $missing=[pscustomobject]@{Order=2;Slot='T2';Url=$lower;Status='Queued';State='Queued'}
            function Flush-Wpf {$window.Dispatcher.Invoke([Action]{},[Windows.Threading.DispatcherPriority]::ApplicationIdle);$window.UpdateLayout()}
            function Label($grid,$row) {
                $column=@($grid.Columns|Where-Object {$_ -is [Windows.Controls.DataGridTemplateColumn]})[0]
                $grid.ScrollIntoView($row,$column);Flush-Wpf
                $presenter=$column.GetCellContent($row)
                return [Windows.Media.VisualTreeHelper]::GetChild($presenter,0).Children[0]
            }
            foreach($grid in $grids){$grid.ItemsSource=@($known,$missing)}
            Flush-Wpf
            foreach($grid in $grids){Assert ((Label $grid $known).Text -ceq $upper) 'Both real WPF lists must default to URL labels'}
            $check.IsChecked=$true;Flush-Wpf
            foreach($grid in $grids){
                Assert ((Label $grid $known).Text -ceq 'Заголовок WPF') 'Title mode must render the video name in both real WPF lists'
                Assert ((Label $grid $missing).Text -ceq $lower) 'A real WPF row without a title property must show its URL fallback'
                Assert ((Label $grid $known).ToolTip -ceq $upper) 'The full original URL must remain in the tooltip'
            }
            $known.Title='Поздний заголовок';foreach($grid in $grids){$grid.Items.Refresh()};Flush-Wpf
            foreach($grid in $grids){Assert ((Label $grid $known).Text -ceq 'Поздний заголовок') 'Late metadata must refresh the rendered name'}
            $check.IsChecked=$false;Flush-Wpf
            foreach($grid in $grids){Assert ((Label $grid $known).Text -ceq $upper) 'Switching back must recover the unchanged exact URL'}
        } finally {$window.Close()}
    }
    Write-Host 'Video label modes, cache/background titles, exact URLs and checkpoint persistence: PASS'
} finally {
    if($script:previewPrefetchJob){Remove-Job $script:previewPrefetchJob -Force -ErrorAction SilentlyContinue}
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
