#requires -Version 7.0
# Modeless PowerShell subscriptions window. The timer only observes explicitly
# requested work; it never schedules a new check or starts a download session.
$ErrorActionPreference='Stop'
# Dependencies are shared with the owner GUI. Reloading them with -Force here
# removes its imported commands, breaking the callback that queues discoveries.
Import-Module (Join-Path $PSScriptRoot 'VideoDownloader.Subscriptions.psm1')
Import-Module (Join-Path $PSScriptRoot 'VideoDownloader.DownloadHistory.psm1')
Import-Module (Join-Path $PSScriptRoot 'VideoDownloader.PornHub.psm1')
Import-Module (Join-Path $PSScriptRoot 'VideoDownloader.SubscriptionNames.psm1')
$script:SubscriptionWindows=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
$script:ClosingSubscriptionWindows=[Collections.Generic.List[object]]::new()

function Get-VdSubscriptionUiHistory([string]$Directory) {
    # A damaged history must not be interpreted as an empty successful set.
    $history=Read-VdDownloadHistory -Path (Join-Path $Directory 'successful-downloads.jsonl')
    return ,([string[]]@($history))
}

function Get-VdSubscriptionUiRows($Snapshot,[string]$SelectedId='') {
    $subscriptions=@($Snapshot.Subscriptions | ForEach-Object {
        $source=$_
        $last=if ($source.LastSuccessUtc) { ([DateTimeOffset]$source.LastSuccessUtc).LocalDateTime.ToString('g') } else { 'Ещё не проверялась' }
        $state=if ($source.Paused) { 'Приостановлена' } elseif ($source.LastError) { 'Ошибка проверки' } elseif ($source.Initialized) { 'Готова' } else { 'Нужна первая проверка' }
        [pscustomobject]@{Id=[string]$source.Id;Title=[string]$source.Title;ShortName=[string]$source.ShortName;Url=[string]$source.Url;Paused=[bool]$source.Paused;
            StatusLabel=$state;LastSuccessLabel=$last;Message=(@($source.LastError,$source.LastWarning) | Where-Object {$_}) -join ' | '}
    })
    $labels=@{New='Новый';Queued='В очереди';Processed='Обработан';AlreadyProcessed='Обработан в другой подписке';Skipped='Пропущен'}
    $inbox=@($Snapshot.Items | Where-Object { $_.SubscriptionId -ceq $SelectedId -and $_.State -ne 'Baseline' } |
        Sort-Object @{Expression={if ($_.State -eq 'New') {0} else {1}}},DiscoveredAtUtc | ForEach-Object {
            [pscustomobject]@{SubscriptionId=[string]$_.SubscriptionId;Url=[string]$_.Url;Title=[string]$_.Title;State=[string]$_.State;
                StateLabel=$labels[[string]$_.State];DiscoveredLabel=([DateTimeOffset]$_.DiscoveredAtUtc).LocalDateTime.ToString('g')}
        })
    return @{Subscriptions=$subscriptions;Inbox=$inbox}
}

function Invoke-VdSubscriptionUiQueue($Context,[AllowEmptyCollection()][object[]]$Items) {
    $history=Get-VdSubscriptionUiHistory $Context.Directory
    $snapshot=Sync-VdSubscriptionsSuccessful -Directory $Context.Directory -SuccessfulUrls $history
    $requested=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $Items) { [void]$requested.Add(([string]$item.SubscriptionId)+"`n"+[string]$item.Url) }
    $eligible=@($snapshot.Items | Where-Object {
        $_.State -in @('New','Skipped','Queued') -and $requested.Contains(([string]$_.SubscriptionId)+"`n"+[string]$_.Url)
    })
    $urls=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($item in $eligible) { [void]$urls.Add([string]$item.Url) }
    if ($urls.Count -eq 0) { return @{Accepted=0;Remaining=0} }
    # The owner callback only enqueues for a future manual Start. Its return is
    # authoritative, including an URL already present in the ordinary queue.
    $accepted=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($group in @($eligible | Group-Object SubscriptionId)) {
        $source=@($snapshot.Subscriptions | Where-Object {$_.Id -ceq $group.Name} | Select-Object -First 1)
        if ($source.Count -eq 0) { continue }
        # Pass a detached source with a safe fallback for released v1 records
        # that have not yet completed a scan with automatic name detection.
        $ownerSource=[pscustomobject]($source[0].Clone())
        if (-not $ownerSource.ShortName) {
            $suggestion=if (Test-VdSubscriptionUrl ([string]$ownerSource.Title)) {''} else {[string]$ownerSource.Title}
            $fallback='Подписка '+$ownerSource.Id.Substring(0,[Math]::Min(8,$ownerSource.Id.Length))
            $ownerSource.ShortName=ConvertTo-VdSubscriptionName -Name $suggestion -Fallback $fallback
        }
        $groupUrls=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $groupTitles=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($item in $group.Group) {
            [void]$groupUrls.Add([string]$item.Url)
            $groupTitles[[string]$item.Url]=[string]$item.Title
        }
        $groupAccepted=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $returned=@(& $Context.QueueUrls ([string[]]@($groupUrls)) $ownerSource $groupTitles)
        foreach ($url in $returned) {
            if ($url -is [string] -and $groupUrls.Contains($url)) { [void]$groupAccepted.Add($url);[void]$accepted.Add($url) }
        }
        $selected=[string[]]@($group.Group | Where-Object {$groupAccepted.Contains([string]$_.Url)} | ForEach-Object {[string]$_.Url})
        if ($selected.Count -gt 0) { $null=Set-VdSubscriptionItemsQueued -Directory $Context.Directory -Id $group.Name -Urls $selected }
    }
    return @{Accepted=$accepted.Count;Remaining=$urls.Count-$accepted.Count}
}

function Show-VdSubscriptionUiMessage($Context,[string]$Message,[string]$Title='Подписки',[switch]$Error) {
    if ($Context.Closed) { return }
    $icon=if ($Error) { [System.Windows.MessageBoxImage]::Warning } else { [System.Windows.MessageBoxImage]::Information }
    [void][System.Windows.MessageBox]::Show($Context.Window,$Message,$Title,[System.Windows.MessageBoxButton]::OK,$icon)
}

function Show-VdSubscriptionUiError($Context,[string]$Message) {
    if ($Context.Closed) { return }
    $Context.Controls.SubscriptionStatusText.Text=$Message
    if ($Context.LastError -cne $Message) {
        $Context.LastError=$Message
        Show-VdSubscriptionUiMessage $Context $Message -Error
    }
}

function Update-VdSubscriptionUiButtons($Context) {
    if ($Context.Closed) { return }
    $source=$Context.Controls.SubscriptionGrid.SelectedItem
    $selected=@($Context.Controls.SubscriptionInboxGrid.SelectedItems)
    $Context.Controls.CheckSubscriptionButton.IsEnabled=($null -ne $source -and -not $source.Paused)
    $Context.Controls.PauseSubscriptionButton.IsEnabled=($null -ne $source)
    $Context.Controls.PauseSubscriptionButton.Content=if ($source -and $source.Paused) {'Возобновить'} else {'Приостановить'}
    $Context.Controls.RemoveSubscriptionButton.IsEnabled=($null -ne $source)
    $Context.Controls.CancelSubscriptionCheckButton.IsEnabled=($null -ne $Context.Job -or $Context.Pending.Count -gt 0)
    $Context.Controls.QueueSubscriptionItemsButton.IsEnabled=@($selected | Where-Object {$_.State -in @('New','Skipped','Queued')}).Count -gt 0
    $Context.Controls.SkipSubscriptionItemsButton.IsEnabled=@($selected | Where-Object {$_.State -eq 'New'}).Count -gt 0
    $Context.Controls.OpenSubscriptionItemButton.IsEnabled=($selected.Count -gt 0)
    $Context.Controls.SubscriptionNameBox.IsEnabled=($null -ne $source)
    $Context.Controls.SaveSubscriptionNameButton.IsEnabled=($null -ne $source -and -not [string]::IsNullOrWhiteSpace($Context.Controls.SubscriptionNameBox.Text))
}

function Update-VdSubscriptionUiName($Context,$Source) {
    $id=if ($Source) {[string]$Source.Id} else {''}
    $name=if ($Source) {[string]$Source.ShortName} else {''}
    # Rebinding the grids or observing a download must not erase a typed name.
    if ($Context.NameEditorId -cne $id -or $Context.NameEditorSavedValue -cne $name) {
        $Context.Controls.SubscriptionNameBox.Text=$name
        $Context.NameEditorId=$id
        $Context.NameEditorSavedValue=$name
    }
}

function Save-VdSubscriptionUiName($Context) {
    $selected=$Context.Controls.SubscriptionGrid.SelectedItem
    if (-not $selected) { return }
    $null=Set-VdSubscriptionShortName -Directory $Context.Directory -Id $selected.Id -Name ([string]$Context.Controls.SubscriptionNameBox.Text)
    Update-VdSubscriptionUi $Context -SelectId $selected.Id
    $Context.Controls.SubscriptionStatusText.Text='Краткое имя сохранено. Новые задания будут сохраняться в эту подпапку; уже добавленные задания сохранят прежнюю папку.'
}

function Update-VdSubscriptionUi($Context,[switch]$Reconcile,[string]$SelectId='') {
    if ($Context.Closed -or $Context.Updating) { return }
    $Context.Updating=$true
    try {
        $snapshot=Read-VdSubscriptions -Directory $Context.Directory
        if ($Reconcile) {
            $history=Get-VdSubscriptionUiHistory $Context.Directory
            $snapshot=Sync-VdSubscriptionsSuccessful -Directory $Context.Directory -SuccessfulUrls $history
        }
        $Context.Snapshot=$snapshot
        $selected=if ($SelectId) {$SelectId} elseif ($Context.Controls.SubscriptionGrid.SelectedItem) {[string]$Context.Controls.SubscriptionGrid.SelectedItem.Id} else {''}
        $rows=Get-VdSubscriptionUiRows $snapshot $selected
        $Context.Controls.SubscriptionGrid.ItemsSource=@($rows.Subscriptions)
        $choice=@($rows.Subscriptions | Where-Object {$_.Id -ceq $selected} | Select-Object -First 1)
        if ($choice.Count -eq 0 -and $rows.Subscriptions.Count -gt 0) { $choice=@($rows.Subscriptions[0]) }
        $Context.Controls.SubscriptionGrid.SelectedItem=if ($choice.Count -gt 0) {$choice[0]} else {$null}
        Update-VdSubscriptionUiName $Context $Context.Controls.SubscriptionGrid.SelectedItem
        $selected=if ($choice.Count -gt 0) {[string]$choice[0].Id} else {''}
        $rows=Get-VdSubscriptionUiRows $snapshot $selected
        $Context.Controls.SubscriptionInboxGrid.ItemsSource=@($rows.Inbox)
        $Context.Controls.SubscriptionInboxTitle.Text=if ($choice.Count -gt 0) {'Входящие и результаты: '+$choice[0].Title} else {'Входящие и результаты'}
        $Context.LastError=''
        Update-VdSubscriptionUiButtons $Context
        $notices=@($snapshot.Items | Where-Object {$_.State -eq 'AlreadyProcessed' -and -not $_.DuplicateNotified})
        if ($notices.Count -gt 0 -and -not $Context.Notifying) {
            $Context.Notifying=$true
            try {
                $lines=@($notices | Select-Object -First 8 | ForEach-Object {
                    # Titles/URLs are useful product copy; storage IDs are hidden.
                    '• '+$(if ($_.Title) {$_.Title} else {$_.Url})
                })
                $message="Эти материалы уже успешно обработаны в другой подписке и не будут добавлены повторно:`n"+($lines -join "`n")
                if ($notices.Count -gt 8) { $message+="`nИ ещё $($notices.Count-8)." }
                Show-VdSubscriptionUiMessage $Context $message 'Уже обработано в другой подписке'
                if (-not $Context.Closed) { $Context.Snapshot=Confirm-VdSubscriptionDuplicates -Directory $Context.Directory -Items $notices }
            } finally { $Context.Notifying=$false }
        }
    } catch { Show-VdSubscriptionUiError $Context ('Не удалось обновить подписки: '+$_.Exception.Message) }
    finally { $Context.Updating=$false }
}

function Add-VdSubscriptionUiChecks($Context,[AllowEmptyCollection()][string[]]$Ids) {
    $snapshot=Read-VdSubscriptions -Directory $Context.Directory
    foreach ($id in $Ids) {
        $source=@($snapshot.Subscriptions | Where-Object {$_.Id -ceq $id -and -not $_.Paused} | Select-Object -First 1)
        if ($source.Count -eq 0 -or $Context.ActiveId -ceq $id -or $Context.Pending.Contains($id)) { continue }
        $Context.Pending.Add($id)
    }
    Start-VdSubscriptionUiNextCheck $Context
}

function Start-VdSubscriptionUiNextCheck($Context) {
    if ($Context.Closed -or $Context.Job) { return }
    while ($Context.Pending.Count -gt 0) {
        $id=$Context.Pending[0];$Context.Pending.RemoveAt(0)
        try {
            $snapshot=Read-VdSubscriptions -Directory $Context.Directory
            $sources=@($snapshot.Subscriptions | Where-Object {$_.Id -ceq $id -and -not $_.Paused} | Select-Object -First 1)
            if ($sources.Count -eq 0) { continue }
            $null=Get-VdSubscriptionUiHistory $Context.Directory
            $cookie=[string](& $Context.CookiePathScript)
            $Context.Cancellation=[Threading.CancellationTokenSource]::new()
            $Context.ActiveId=$id
            $Context.Job=Start-ThreadJob -ArgumentList $Context.StoreModule,$Context.YtDlpPath,$sources[0],$cookie,$Context.Cancellation.Token -ScriptBlock {
                param($module,$exe,$source,$cookiePath,$token)
                $ErrorActionPreference='Stop'
                try {
                    Import-Module $module -Force
                    $scan=Invoke-VdSubscriptionScan -YtDlpPath $exe -Subscription $source -CookiePath $cookiePath -CancellationToken $token
                    [pscustomobject]@{Success=$true;Scan=$scan;Error=''}
                } catch { [pscustomobject]@{Success=$false;Scan=$null;Error=$_.Exception.Message} }
            }
            $label=if (Get-VdPornHubListingUrl $sources[0].Url) {'Проверка публикаций Pornhub (до 15 минут): '}else{'Проверяется: '}
            $Context.Controls.SubscriptionStatusText.Text=$label+$sources[0].Title
            $Context.Timer.Start()
            Update-VdSubscriptionUiButtons $Context
            return
        } catch {
            $failure=$_.Exception.Message
            if ($Context.Cancellation) {$Context.Cancellation.Dispose();$Context.Cancellation=$null}
            $Context.ActiveId=''
            try {$null=Set-VdSubscriptionFailure -Directory $Context.Directory -Id $id -Error $failure} catch { }
            Show-VdSubscriptionUiError $Context ('Проверка не началась: '+$failure)
        }
    }
    $Context.Timer.Stop()
    Update-VdSubscriptionUiButtons $Context
}

function Complete-VdSubscriptionUiCheck($Context) {
    if (-not $Context.Job) {
        if ($Context.Closed) {$Context.Timer.Stop()} else {Start-VdSubscriptionUiNextCheck $Context}
        return
    }
    if ($Context.Job.State -notin @('Completed','Failed','Stopped')) { return }
    $id=$Context.ActiveId
    $completionCancellation=$Context.Cancellation
    $cancelled=$Context.Cancellation.IsCancellationRequested
    $result=$null
    try {
        $output=@(Receive-Job -Job $Context.Job -ErrorAction Stop)
        $result=@($output | Where-Object {$null -ne $_ -and $_.PSObject.Properties['Success']} | Select-Object -Last 1)
        if ($result.Count -eq 0) {throw 'Проверка завершилась без результата.'}
        $result=$result[0]
    } catch { $result=[pscustomobject]@{Success=$false;Scan=$null;Error=$_.Exception.Message} }
    finally {
        Remove-Job -Job $Context.Job -Force -ErrorAction SilentlyContinue
        $Context.Job=$null;$Context.ActiveId=''
    }
    try {
        $snapshot=Read-VdSubscriptions -Directory $Context.Directory
        $source=@($snapshot.Subscriptions | Where-Object {$_.Id -ceq $id} | Select-Object -First 1)
        if ($source.Count -gt 0) {
            if ($cancelled) {
                $null=Set-VdSubscriptionFailure -Directory $Context.Directory -Id $id -Error 'Проверка отменена пользователем; предыдущие результаты сохранены.'
            } elseif ($result.Success -and -not $source[0].Paused -and -not $Context.Closed) {
                $history=Get-VdSubscriptionUiHistory $Context.Directory
                $snapshot=Apply-VdSubscriptionScan -Directory $Context.Directory -Id $id -Scan $result.Scan -SuccessfulUrls $history -CancellationToken $completionCancellation.Token
                $initial=@($snapshot.Items | Where-Object {$_.SubscriptionId -ceq $id -and $_.State -eq 'New' -and $_.InitialQueuePending})
                $status='Полная проверка завершена. Новые материалы доступны во входящих.'
                if ($initial.Count -gt 0) {
                    $queued=Invoke-VdSubscriptionUiQueue $Context $initial
                    $status="В очередь добавлено: $($queued.Accepted). Запуск — кнопкой Start."
                    if ($queued.Remaining -gt 0) {$status+=" Не добавлено: $($queued.Remaining); материалы сохранены во входящих."}
                }
                if ($result.Scan.Warning) {$status+=' Предупреждение: '+$result.Scan.Warning}
                $Context.Controls.SubscriptionStatusText.Text=$status
            } elseif (-not $result.Success) {
                $null=Set-VdSubscriptionFailure -Directory $Context.Directory -Id $id -Error ([string]$result.Error)
                if (-not $Context.Closed) {Show-VdSubscriptionUiError $Context ('Полная проверка не завершена: '+$result.Error)}
            }
        }
    } catch {
        try {$null=Set-VdSubscriptionFailure -Directory $Context.Directory -Id $id -Error $_.Exception.Message} catch { }
        if (-not $Context.Closed) {Show-VdSubscriptionUiError $Context ('Результат проверки не применён: '+$_.Exception.Message)}
    } finally {
        if ([object]::ReferenceEquals($Context.Cancellation,$completionCancellation)) { $Context.Cancellation=$null }
        $completionCancellation.Dispose()
    }
    if ($Context.Closed) {
        $Context.Timer.Stop()
        [void]$script:ClosingSubscriptionWindows.Remove($Context)
        return
    }
    Update-VdSubscriptionUi $Context -Reconcile
    Start-VdSubscriptionUiNextCheck $Context
}

function Stop-VdSubscriptionUiChecks($Context) {
    $Context.Pending.Clear()
    if ($Context.Cancellation) {$Context.Cancellation.Cancel()}
    if (-not $Context.Closed) {
        $Context.Controls.SubscriptionStatusText.Text='Проверка отменяется. Предыдущие результаты сохраняются.'
        Update-VdSubscriptionUiButtons $Context
    }
}

function Close-VdSubscriptionUiContext($Context) {
    if ($Context.Closed) {return}
    $Context.Closed=$true
    Stop-VdSubscriptionUiChecks $Context
    [void]$script:SubscriptionWindows.Remove($Context.Key)
    if ($Context.OwnerClosedHandler) {$Context.Owner.Remove_Closed($Context.OwnerClosedHandler)}
    # The worker polls the shared token independently of the WPF dispatcher and
    # kills only its own child tree. Keep observing until its finally completes.
    if ($Context.Job) {$script:ClosingSubscriptionWindows.Add($Context)} else {$Context.Timer.Stop()}
}

function New-VdSubscriptionUiHandler($Context,[scriptblock]$Action,[switch]$AllowClosed) {
    $contextCopy=$Context;$actionCopy=$Action;$allowClosedCopy=[bool]$AllowClosed
    $report=${function:Show-VdSubscriptionUiError}
    return {param($sender,$eventArgs)
        if ($contextCopy.Closed -and -not $allowClosedCopy) {return}
        try {& $actionCopy $contextCopy $sender $eventArgs}
        catch {& $report $contextCopy $_.Exception.Message}
    }.GetNewClosure()
}

function Show-VdSubscriptionsWindow {
    param([Parameter(Mandatory)]$Owner,[Parameter(Mandatory)][string]$Root,[Parameter(Mandatory)][string]$DataDirectory,
        [Parameter(Mandatory)][string]$YtDlpPath,[Parameter(Mandatory)][scriptblock]$CookiePathScript,
        [Parameter(Mandatory)][scriptblock]$QueueUrls)
    $key=[string][Runtime.CompilerServices.RuntimeHelpers]::GetHashCode($Owner)
    if ($script:SubscriptionWindows.ContainsKey($key)) {
        $existing=$script:SubscriptionWindows[$key]
        $existing.Window.Show();[void]$existing.Window.Activate();return
    }
    Add-Type -AssemblyName PresentationFramework
    $reader=$null
    try {
        $reader=[Xml.XmlReader]::Create((Join-Path $Root 'ui/SubscriptionsWindow.xaml'))
        $window=[Windows.Markup.XamlReader]::Load($reader)
    } finally {if($reader){$reader.Dispose()}}
    $window.Owner=$Owner
    $controls=@{}
    foreach($name in @('SubscriptionUrlBox','SubscriptionFeedBox','NewSubscriptionNameBox','SubscriptionNameBox','SaveSubscriptionNameButton','DownloadExistingCheck','AddSubscriptionButton','SubscriptionGrid',
        'CheckSubscriptionButton','CheckAllSubscriptionsButton','CancelSubscriptionCheckButton','PauseSubscriptionButton',
        'RemoveSubscriptionButton','SubscriptionInboxTitle','SubscriptionInboxGrid','QueueSubscriptionItemsButton',
        'SkipSubscriptionItemsButton','OpenSubscriptionItemButton','SubscriptionStatusText')) {
        $controls[$name]=$window.FindName($name)
        if($null -eq $controls[$name]) {throw "В разметке подписок отсутствует элемент $name."}
    }
    $context=@{Key=$key;Owner=$Owner;Window=$window;Controls=$controls;Directory=[IO.Path]::GetFullPath($DataDirectory);
        StoreModule=(Join-Path $PSScriptRoot 'VideoDownloader.Subscriptions.psm1');YtDlpPath=$YtDlpPath;
        CookiePathScript=$CookiePathScript;QueueUrls=$QueueUrls;Snapshot=$null;Closed=$false;Updating=$false;Notifying=$false;
        LastError='';NameEditorId='';NameEditorSavedValue='';Pending=[Collections.Generic.List[string]]::new();Job=$null;Cancellation=$null;ActiveId='';OwnerClosedHandler=$null;
        Timer=[Windows.Threading.DispatcherTimer]::new()}
    $context.Timer.Interval=[TimeSpan]::FromMilliseconds(200)
    $context.Timer.Add_Tick((New-VdSubscriptionUiHandler $context {param($c) Complete-VdSubscriptionUiCheck $c} -AllowClosed))
    $window.Add_Closed((New-VdSubscriptionUiHandler $context {param($c) Close-VdSubscriptionUiContext $c} -AllowClosed))
    $context.OwnerClosedHandler=New-VdSubscriptionUiHandler $context {param($c) Stop-VdSubscriptionUiChecks $c;$c.Window.Close()} -AllowClosed
    $Owner.Add_Closed($context.OwnerClosedHandler)
    $controls.AddSubscriptionButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $null=Get-VdSubscriptionUiHistory $c.Directory
        $source=Add-VdSubscription -Directory $c.Directory -Url $c.Controls.SubscriptionUrlBox.Text.Trim() -FeedUrl $c.Controls.SubscriptionFeedBox.Text.Trim() -ShortName $c.Controls.NewSubscriptionNameBox.Text.Trim() -DownloadExisting ([bool]$c.Controls.DownloadExistingCheck.IsChecked)
        $c.Controls.SubscriptionUrlBox.Text='';$c.Controls.SubscriptionFeedBox.Text='';$c.Controls.NewSubscriptionNameBox.Text=''
        Update-VdSubscriptionUi $c -SelectId $source.Id
        Add-VdSubscriptionUiChecks $c @([string]$source.Id)
    }))
    $controls.SaveSubscriptionNameButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c) Save-VdSubscriptionUiName $c}))
    $controls.SubscriptionNameBox.Add_TextChanged((New-VdSubscriptionUiHandler $context {param($c) Update-VdSubscriptionUiButtons $c}))
    $controls.CheckSubscriptionButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        if($c.Controls.SubscriptionGrid.SelectedItem){Add-VdSubscriptionUiChecks $c @([string]$c.Controls.SubscriptionGrid.SelectedItem.Id)}
    }))
    $controls.CheckAllSubscriptionsButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $snapshot=Read-VdSubscriptions -Directory $c.Directory
        Add-VdSubscriptionUiChecks $c ([string[]]@($snapshot.Subscriptions | Where-Object {-not $_.Paused} | ForEach-Object {$_.Id}))
    }))
    $controls.CancelSubscriptionCheckButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c) Stop-VdSubscriptionUiChecks $c}))
    $controls.PauseSubscriptionButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $selected=$c.Controls.SubscriptionGrid.SelectedItem;if(-not $selected){return}
        $null=Set-VdSubscriptionPaused -Directory $c.Directory -Id $selected.Id -Paused (-not $selected.Paused)
        if(-not $selected.Paused -and $c.ActiveId -ceq $selected.Id -and $c.Cancellation){$c.Cancellation.Cancel()}
        Update-VdSubscriptionUi $c -SelectId $selected.Id
    }))
    $controls.RemoveSubscriptionButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $selected=$c.Controls.SubscriptionGrid.SelectedItem;if(-not $selected){return}
        $answer=[Windows.MessageBox]::Show($c.Window,'Удалить подписку? Файлы, очередь и история останутся без изменений.','Удаление подписки',[Windows.MessageBoxButton]::YesNo,[Windows.MessageBoxImage]::Question)
        if($answer -ne [Windows.MessageBoxResult]::Yes){return}
        if($c.ActiveId -ceq $selected.Id -and $c.Cancellation){$c.Cancellation.Cancel()}
        $null=Remove-VdSubscription -Directory $c.Directory -Id $selected.Id
        Update-VdSubscriptionUi $c
    }))
    $controls.QueueSubscriptionItemsButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $result=Invoke-VdSubscriptionUiQueue $c @($c.Controls.SubscriptionInboxGrid.SelectedItems)
        $c.Controls.SubscriptionStatusText.Text="В очереди: $($result.Accepted). Запуск — кнопкой Start. Не добавлено: $($result.Remaining)."
        Update-VdSubscriptionUi $c -Reconcile
    }))
    $controls.SkipSubscriptionItemsButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        foreach($group in @($c.Controls.SubscriptionInboxGrid.SelectedItems | Where-Object {$_.State -eq 'New'} | Group-Object SubscriptionId)) {
            $null=Skip-VdSubscriptionItems -Directory $c.Directory -Id $group.Name -Urls ([string[]]@($group.Group | ForEach-Object {$_.Url}))
        }
        Update-VdSubscriptionUi $c
    }))
    $controls.OpenSubscriptionItemButton.Add_Click((New-VdSubscriptionUiHandler $context {param($c)
        $item=$c.Controls.SubscriptionInboxGrid.SelectedItem
        if($item -and (Test-VdSubscriptionUrl $item.Url)){
            $start=[Diagnostics.ProcessStartInfo]::new($item.Url);$start.UseShellExecute=$true
            [void][Diagnostics.Process]::Start($start)
        }
    }))
    $controls.SubscriptionGrid.Add_SelectionChanged((New-VdSubscriptionUiHandler $context {param($c) Update-VdSubscriptionUi $c}))
    $controls.SubscriptionInboxGrid.Add_SelectionChanged((New-VdSubscriptionUiHandler $context {param($c) Update-VdSubscriptionUiButtons $c}))
    $script:SubscriptionWindows[$key]=$context
    Update-VdSubscriptionUi $context -Reconcile
    $window.Show()
}

function Refresh-VdSubscriptionsWindow {
    param([Parameter(Mandatory)][string]$DataDirectory)
    $directory=[IO.Path]::GetFullPath($DataDirectory)
    $comparison=if($IsWindows){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    foreach($context in @($script:SubscriptionWindows.Values)) {
        if(-not $context.Closed -and [string]::Equals($context.Directory,$directory,$comparison)) {Update-VdSubscriptionUi $context -Reconcile}
    }
}

function Close-VdSubscriptionsWindow {
    param($Owner)
    foreach($context in @($script:SubscriptionWindows.Values)) {
        if($null -ne $Owner -and -not [object]::ReferenceEquals($Owner,$context.Owner)){continue}
        Stop-VdSubscriptionUiChecks $context
        $context.Window.Close()
        Close-VdSubscriptionUiContext $context
    }
    # The owner dispatcher may already have stopped. Reap retired workers here
    # as well, while the PowerShell host is still alive to run their finally.
    foreach($context in @($script:ClosingSubscriptionWindows.ToArray())) {
        if($null -ne $Owner -and -not [object]::ReferenceEquals($Owner,$context.Owner)){continue}
        Stop-VdSubscriptionUiChecks $context
        if($context.Job) {
            $null=Wait-Job -Job $context.Job -Timeout 5
            Complete-VdSubscriptionUiCheck $context
        }
    }
}

Export-ModuleMember -Function Show-VdSubscriptionsWindow,Refresh-VdSubscriptionsWindow,Close-VdSubscriptionsWindow
