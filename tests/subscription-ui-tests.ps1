#requires -Version 7.0
# Execute production orchestration with real stores and ThreadJobs. Only the
# platform controls/dialog and external discovery boundary are substituted.
$ErrorActionPreference='Stop'
$root=Split-Path -Parent $PSScriptRoot
function Assert([bool]$Condition,[string]$Message){if(-not $Condition){throw $Message}}
# Execute the actual startup import graph in its real order. Importing history
# again after the UI would hide a nested -Force import removing owner commands.
$tokens=$null;$errors=$null
$guiAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'ytdl-manager-gui.ps1'),[ref]$tokens,[ref]$errors)
Assert ($errors.Count -eq 0) 'GUI syntax failed'
foreach($statement in $guiAst.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.AssignmentStatementAst] -and
        $statement.Left -is [Management.Automation.Language.VariableExpressionAst] -and
        $statement.Left.VariablePath.UserPath -match 'Module$') {
        . ([scriptblock]::Create($statement.Extent.Text))
    } elseif ($statement -is [Management.Automation.Language.PipelineAst] -and
        $statement.PipelineElements[0] -is [Management.Automation.Language.CommandAst] -and
        $statement.PipelineElements[0].GetCommandName() -eq 'Import-Module') {
        . ([scriptblock]::Create($statement.Extent.Text))
    }
}
foreach($command in @('Read-VdDownloadHistory','Add-VdSuccessfulDownloadUrl','Resolve-VdDuplicateDownloadPlan',
    'Read-VdSubscriptions','Set-VdSubscriptionSuccessful','Apply-VdSubscriptionScan')) {
    Assert ([bool](Get-Command $command -ErrorAction SilentlyContinue)) "GUI startup lost owner command: $command"
}
$module=Get-Module VideoDownloader.SubscriptionUi
# Keep the real owner's history/duplicate/queue code across the module callback.
foreach($name in @('Confirm-DuplicateDownloads','Add-SubscriptionUrls','Is-ValidUrl','Find-QueueItem')) {
    $function=$guiAst.EndBlock.Statements | Where-Object {$_ -is [Management.Automation.Language.FunctionDefinitionAst] -and $_.Name -eq $name}
    Assert ($null -ne $function) "Missing production GUI function: $name"
    . ([scriptblock]::Create($function.Extent.Text))
}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('vd-subscription-ui-'+[guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temp)
$contexts=[Collections.Generic.List[object]]::new()
try {
    & $module {
        $script:TestMessages=[Collections.Generic.List[object]]::new()
        $script:FailNotice=$false
        Set-Item function:script:Show-VdSubscriptionUiMessage {
            param($Context,[string]$Message,[string]$Title='Подписки',[switch]$Error)
            $script:TestMessages.Add(@{Message=$Message;Title=$Title;Error=[bool]$Error})
            if($script:FailNotice -and -not $Error){throw 'Dialog was interrupted'}
        }
    }
    function New-TestContext([string]$Directory){
        $controls=@{}
        foreach($name in @('CheckSubscriptionButton','PauseSubscriptionButton','RemoveSubscriptionButton','CancelSubscriptionCheckButton','QueueSubscriptionItemsButton','SkipSubscriptionItemsButton','OpenSubscriptionItemButton')) {
            $controls[$name]=[pscustomobject]@{IsEnabled=$true;Content=''}
        }
        $controls.SubscriptionGrid=[pscustomobject]@{SelectedItem=$null;ItemsSource=@()}
        $controls.SubscriptionInboxGrid=[pscustomobject]@{SelectedItem=$null;SelectedItems=@();ItemsSource=@()}
        $controls.SubscriptionInboxTitle=[pscustomobject]@{Text=''}
        $controls.SubscriptionStatusText=[pscustomobject]@{Text=''}
        $timer=[pscustomobject]@{Running=$false}
        $timer | Add-Member ScriptMethod Start {$this.Running=$true}
        $timer | Add-Member ScriptMethod Stop {$this.Running=$false}
        $context=@{Directory=$Directory;Controls=$controls;Closed=$false;Updating=$false;Notifying=$false;
            LastError='';Snapshot=$null;QueueUrls={param($urls) $urls};CookiePathScript={''};Pending=[Collections.Generic.List[string]]::new();
            Job=$null;Cancellation=$null;ActiveId='';Timer=$timer;OwnerClosedHandler=$null;Key=[guid]::NewGuid().ToString('N')}
        $contexts.Add($context)
        return $context
    }
    $directory=Join-Path $temp 'profile'
    $source=Add-VdSubscription -Directory $directory -Url 'https://example.com/source' -DownloadExisting $true
    $scan=@{Title='Источник';FeedUrl='';Warning='';Items=@(
        @{Url='https://example.com/Video';Title='Видео'},@{Url='https://example.com/video';Title='Другое видео'})}
    $snapshot=Apply-VdSubscriptionScan -Directory $directory -Id $source.Id -Scan $scan
    $context=New-TestContext $directory
    $script:callbackCount=0;$script:callbackInputs=@()
    $context.QueueUrls={param($urls)
        $script:callbackCount++;$script:callbackInputs=@($urls)
        @('https://example.com/Video','https://example.com/VIDEO','https://unrelated.example/should-not-queue')
    }
    $queued=& $module {param($c,$items) Invoke-VdSubscriptionUiQueue $c $items} $context $snapshot.Items
    $snapshot=Read-VdSubscriptions -Directory $directory
    Assert ($script:callbackCount -eq 1 -and $script:callbackInputs.Count -eq 2 -and $queued.Accepted -eq 1 -and $queued.Remaining -eq 1) 'Queue must use the owner return value, excluding altered/unrequested URLs'
    Assert ($snapshot.Items[0].State -eq 'Queued' -and -not $snapshot.Items[0].InitialQueuePending -and $snapshot.Items[1].State -eq 'New' -and $snapshot.Items[1].InitialQueuePending) 'Rejected initial items must remain recoverable in Inbox'
    Assert ((Read-VdDownloadHistory -Path (Join-Path $directory 'successful-downloads.jsonl')).Count -eq 0) 'Queueing must never count as successful processing'
    $context.QueueUrls={param($urls) $urls} # Idempotent owner accepts an already queued URL.
    $null=& $module {param($c,$items) Invoke-VdSubscriptionUiQueue $c $items} $context @($snapshot.Items[1])
    $snapshot=Read-VdSubscriptions -Directory $directory
    Assert ($snapshot.Items[1].State -eq 'Queued' -and -not $snapshot.Items[1].InitialQueuePending) 'Already present ordinary queue URL must acknowledge pending initial enqueue'
    $ordinaryQueue=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $context.QueueUrls={param($urls) foreach($url in $urls){[void]$ordinaryQueue.Add($url);$url}}.GetNewClosure()
    $null=& $module {param($c,$items) Invoke-VdSubscriptionUiQueue $c $items} $context @($snapshot.Items[1])
    [void]$ordinaryQueue.Remove('https://example.com/video')
    $requeued=& $module {param($c,$items) Invoke-VdSubscriptionUiQueue $c $items} $context @($snapshot.Items[1])
    Assert ($requeued.Accepted -eq 1 -and $ordinaryQueue.Contains('https://example.com/video')) 'A Queued item removed from the ordinary queue must remain available for explicit requeue'

    $other=Add-VdSubscription -Directory $directory -Url 'https://example.com/other-source' -DownloadExisting $true
    $null=Apply-VdSubscriptionScan -Directory $directory -Id $other.Id -Scan $scan
    Add-VdSuccessfulDownloadUrl -Path (Join-Path $directory 'successful-downloads.jsonl') -Url 'https://example.com/Video'
    $context.Controls.SubscriptionGrid.SelectedItem=[pscustomobject]@{Id=$other.Id}
    & $module {param($c) Update-VdSubscriptionUi $c -Reconcile} $context
    $snapshot=Read-VdSubscriptions -Directory $directory
    $duplicate=@($snapshot.Items | Where-Object {$_.SubscriptionId -ceq $other.Id -and $_.Url -ceq 'https://example.com/Video'})[0]
    Assert ($duplicate.State -eq 'AlreadyProcessed' -and $duplicate.DuplicateNotified) 'A true global success must be reconciled and acknowledged after its notice'
    $noticeCount=& $module {@($script:TestMessages | Where-Object {-not $_.Error}).Count}
    Assert ($noticeCount -eq 1) 'Cross-subscription success must show one notice'
    & $module {param($c) Update-VdSubscriptionUi $c -Reconcile} $context
    Assert ((& $module {@($script:TestMessages | Where-Object {-not $_.Error}).Count}) -eq 1) 'An acknowledged duplicate must never notify again on refresh'
    Assert ($context.Controls.SubscriptionGrid.ItemsSource[0].StatusLabel -and $context.Controls.SubscriptionInboxGrid.ItemsSource[0].StateLabel -and $context.Controls.SubscriptionInboxTitle.Text -like '*Источник*') 'Shared controls must receive readable projections instead of dictionary/internal state names'

    $third=Add-VdSubscription -Directory $directory -Url 'https://example.com/third-source' -DownloadExisting $true
    $null=Apply-VdSubscriptionScan -Directory $directory -Id $third.Id -Scan $scan -SuccessfulUrls @('https://example.com/Video')
    & $module {$script:FailNotice=$true}
    & $module {param($c) Update-VdSubscriptionUi $c -Reconcile} $context
    $snapshot=Read-VdSubscriptions -Directory $directory
    Assert (-not @($snapshot.Items | Where-Object {$_.SubscriptionId -ceq $third.Id -and $_.Url -ceq 'https://example.com/Video'})[0].DuplicateNotified) 'Interrupted notice must not acknowledge unseen duplicates'
    & $module {$script:FailNotice=$false}
    & $module {param($c) Update-VdSubscriptionUi $c -Reconcile} $context

    $before=[IO.File]::ReadAllText((Join-Path $directory 'subscriptions.json'))
    [IO.File]::AppendAllText((Join-Path $directory 'successful-downloads.jsonl'),'{broken')
    $context.QueueUrls={throw 'Queue callback must not run with corrupted history'}
    $refused=$false
    try {& $module {param($c,$items) Invoke-VdSubscriptionUiQueue $c $items} $context $snapshot.Items | Out-Null} catch {$refused=$true}
    Assert ($refused -and [IO.File]::ReadAllText((Join-Path $directory 'subscriptions.json')) -ceq $before) 'Damaged history must fail closed before queue/store changes'

    # Actual ThreadJob boundary: the fake scanner only replaces network and the
    # external process. Its token observes close/cancel without any UI timer.
    $scanner=Join-Path $temp 'OfflineScanner.psm1'
    @'
function Invoke-VdSubscriptionScan {
 param($YtDlpPath,$Subscription,$CookiePath,[Threading.CancellationToken]$CancellationToken)
 if($Subscription.Url.EndsWith('/slow')){
  while(-not $CancellationToken.IsCancellationRequested){Start-Sleep -Milliseconds 10}
  $CancellationToken.ThrowIfCancellationRequested()
 }
 @{Title='Фоновая проверка';FeedUrl='';Warning='';Items=@(@{Url='https://example.com/discovered';Title='Обнаружено'})}
}
Export-ModuleMember -Function Invoke-VdSubscriptionScan
'@ | Set-Content -LiteralPath $scanner
    $workDirectory=Join-Path $temp 'worker'
    $background=Add-VdSubscription -Directory $workDirectory -Url 'https://example.com/background'
    $worker=New-TestContext $workDirectory
    $worker.StoreModule=$scanner;$worker.YtDlpPath='unused'
    $worker.QueueUrls={throw 'Baseline scan and refresh must never queue or start media'}
    $watch=[Diagnostics.Stopwatch]::StartNew()
    & $module {param($c,$id) Add-VdSubscriptionUiChecks $c @($id)} $worker $background.Id
    Assert ($watch.Elapsed.TotalSeconds -lt 3 -and $null -ne $worker.Job -and $worker.Timer.Running) 'Manual check must return while discovery executes in a ThreadJob'
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while($worker.Job -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 30;& $module {param($c) Complete-VdSubscriptionUiCheck $c} $worker}
    $saved=Read-VdSubscriptions -Directory $workDirectory
    Assert ($null -eq $worker.Job -and $saved.Subscriptions[0].Initialized -and $saved.Items[0].State -eq 'Baseline' -and -not $worker.Timer.Running) 'Completion observer must apply a complete scan, preserve baseline mode and release job/timer'

    # Initial archive import invokes the actual main GUI queue callback, which
    # must still be able to read history after importing the subscriptions UI.
    $script:historyPath=Join-Path $workDirectory 'successful-downloads.jsonl'
    $script:queue=[Collections.ObjectModel.ObservableCollection[object]]::new()
    $script:checkpointReadOnly=$false;$script:lastSessionError='';$script:proc=$null;$script:recoveredEngine=$null
    $script:ClipboardHint=[pscustomobject]@{Text=''}
    $script:prefetched=[Collections.Generic.List[string]]::new()
    function Queue-PreviewPrefetch([string]$Url) {$script:prefetched.Add($Url)}
    function Reindex-Queue {}
    $archive=Add-VdSubscription -Directory $workDirectory -Url 'https://example.com/archive' -DownloadExisting $true
    $worker.QueueUrls={throw 'Owner queue callback failed'}
    & $module {param($c,$id) Add-VdSubscriptionUiChecks $c @($id)} $worker $archive.Id
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while($worker.Job -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 30;& $module {param($c) Complete-VdSubscriptionUiCheck $c} $worker}
    $saved=Read-VdSubscriptions -Directory $workDirectory
    $pending=@($saved.Items | Where-Object SubscriptionId -CEQ $archive.Id)
    Assert ($pending.Count -eq 1 -and $pending[0].InitialQueuePending -and $pending[0].State -eq 'New') 'An owner callback failure must keep the initial archive pending for another manual check'
    $worker.QueueUrls={param($urls) Add-SubscriptionUrls -Urls $urls}
    & $module {param($c,$id) Add-VdSubscriptionUiChecks $c @($id)} $worker $archive.Id
    $deadline=[DateTime]::UtcNow.AddSeconds(15)
    while($worker.Job -and [DateTime]::UtcNow -lt $deadline){Start-Sleep -Milliseconds 30;& $module {param($c) Complete-VdSubscriptionUiCheck $c} $worker}
    $saved=Read-VdSubscriptions -Directory $workDirectory
    $archiveSource=@($saved.Subscriptions | Where-Object Id -CEQ $archive.Id)[0]
    $archiveItems=@($saved.Items | Where-Object SubscriptionId -CEQ $archive.Id)
    Assert ($null -eq $worker.Job -and $archiveSource.Initialized -and -not $archiveSource.LastError -and
        $archiveItems.Count -eq 1 -and $archiveItems[0].State -eq 'Queued' -and -not $archiveItems[0].InitialQueuePending) 'Real owner callback must apply discovery and acknowledge the initial queue without losing history commands'
    Assert ($script:queue.Count -eq 1 -and $script:queue[0].Url -ceq 'https://example.com/discovered' -and
        $script:prefetched.Count -eq 1 -and (Read-VdDownloadHistory -Path $script:historyPath).Count -eq 0) 'Initial import must create an ordinary queued item and preview intent, without recording download success'

    $slow=Add-VdSubscription -Directory $workDirectory -Url 'https://example.com/slow'
    & $module {param($c,$id) Add-VdSubscriptionUiChecks $c @($id)} $worker $slow.Id
    & $module {param($c) Close-VdSubscriptionUiContext $c} $worker
    # The main dispatcher has stopped: shutdown must also find already closed
    # windows and join their cancelled workers without timer ticks.
    Close-VdSubscriptionsWindow
    $saved=Read-VdSubscriptions -Directory $workDirectory
    $cancelled=@($saved.Subscriptions | Where-Object {$_.Id -ceq $slow.Id})[0]
    Assert ($worker.Closed -and $null -eq $worker.Job -and -not $worker.Timer.Running -and -not $cancelled.Initialized -and $cancelled.LastError -like '*отменена*') 'Owner close must cancel independently of the UI and retain the previous baseline'

    if($IsWindows) {
        # Load the same window through the production PowerShell controller on
        # Windows as well as exercising orchestration above without controls.
        Add-Type -AssemblyName PresentationFramework
        $wpfDirectory=Join-Path $temp 'wpf'
        $wpfSource=Add-VdSubscription -Directory $wpfDirectory -Url 'https://example.com/wpf'
        $null=Apply-VdSubscriptionScan -Directory $wpfDirectory -Id $wpfSource.Id -Scan @{Title='Проверка WPF';FeedUrl='';Warning='';Items=@()}
        $null=Apply-VdSubscriptionScan -Directory $wpfDirectory -Id $wpfSource.Id -Scan @{Title='Проверка WPF';FeedUrl='';Warning='';Items=@(@{Url='https://example.com/wpf/video';Title='Материал WPF'})}
        $owner=[Windows.Window]::new()
        try {
            $owner.Show()
            Show-VdSubscriptionsWindow -Owner $owner -Root $root -DataDirectory $wpfDirectory -YtDlpPath 'unused' -CookiePathScript {''} -QueueUrls {throw 'Opening subscriptions must not enqueue'}
            $wpf=& $module {param($o) @($script:SubscriptionWindows.Values | Where-Object {[object]::ReferenceEquals($_.Owner,$o)})[0]} $owner
            Assert ($wpf.Window.IsVisible -and $owner.IsEnabled -and -not $wpf.Controls.DownloadExistingCheck.IsChecked -and $null -eq $wpf.Job) 'Actual PowerShell subscriptions WPF window must open modeless with baseline default and no background scan'
            $wpf.Window.UpdateLayout()
            $grid=$wpf.Controls.SubscriptionInboxGrid
            Assert ($grid.Items.Count -eq 1 -and $grid.Items[0].Title -eq 'Материал WPF' -and $grid.Items[0].StateLabel -eq 'Новый') 'Actual PowerShell WPF window must bind the saved inbox projection'
            $grid.ScrollIntoView($grid.Items[0]);$grid.UpdateLayout()
            Assert ($grid.Columns[0].GetCellContent($grid.Items[0]).Text -eq 'Материал WPF') 'Actual PowerShell WPF cells must resolve title bindings'
        } finally {
            Close-VdSubscriptionsWindow -Owner $owner
            $owner.Close()
        }
    }
    Write-Host 'PowerShell subscription UI queue, notices, projections, history and background lifecycle: PASS'
} finally {
    foreach($context in $contexts){
        if($context.Cancellation){$context.Cancellation.Cancel()}
        if($context.Job){$null=Wait-Job $context.Job -Timeout 10;Remove-Job $context.Job -Force -ErrorAction SilentlyContinue}
    }
    Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
}
