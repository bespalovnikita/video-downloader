#requires -Version 7.0
# Shared v1 subscription persistence and the independent, bounded discovery worker.
$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot 'VideoDownloader.PornHub.psm1')

function Test-VdSubscriptionUrl([string]$Url) {
    $uri=$null
    return -not [string]::IsNullOrWhiteSpace($Url) -and $Url -ceq $Url.Trim() -and $Url -notmatch '[\x00-\x1f\x7f]' -and
        [Uri]::TryCreate($Url,[UriKind]::Absolute,[ref]$uri) -and
        $uri.Scheme -in @('http','https') -and -not [string]::IsNullOrWhiteSpace($uri.Host)
}

function Invoke-VdSubscriptionsLocked([scriptblock]$Action,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None) {
    $mutex=[Threading.Mutex]::new($false,'Local\VideoDownloader.Subscriptions')
    $held=$false
    try {
        $CancellationToken.ThrowIfCancellationRequested()
        try {
            if ($CancellationToken.CanBeCanceled) {
                $waiting=[Diagnostics.Stopwatch]::StartNew()
                do {
                    $CancellationToken.ThrowIfCancellationRequested()
                    $held=$mutex.WaitOne(50)
                } while (-not $held -and $waiting.Elapsed.TotalSeconds -lt 30)
            } else { $held=$mutex.WaitOne([TimeSpan]::FromSeconds(30)) }
        }
        catch [Threading.AbandonedMutexException] { $held=$true }
        if (-not $held) { throw 'Не удалось получить доступ к хранилищу подписок за 30 секунд.' }
        $CancellationToken.ThrowIfCancellationRequested()
        & $Action
    } finally {
        if ($held) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

function Assert-VdSubscriptionJsonShape($Element,[hashtable]$Fields,[string]$Description) {
    if ($Element.ValueKind -ne [Text.Json.JsonValueKind]::Object) { throw "Некорректный объект подписок: $Description" }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $allowed=[Collections.Generic.HashSet[string]]::new([string[]]@($Fields.Keys),[StringComparer]::Ordinal)
    foreach ($property in $Element.EnumerateObject()) {
        if (-not $allowed.Contains($property.Name) -or -not $seen.Add($property.Name)) { throw "Неизвестное или повторное поле ${Description}: $($property.Name)" }
        $expected=$Fields[$property.Name]
        $kind=$property.Value.ValueKind.ToString()
        if ($expected -eq 'Date?') {
            if ($kind -ne 'Null') {
                $date=[DateTimeOffset]::MinValue
                if ($kind -ne 'String' -or -not $property.Value.TryGetDateTimeOffset([ref]$date)) { throw "Некорректная дата ${Description}: $($property.Name)" }
            }
        } elseif ($expected -eq 'Date') {
            $date=[DateTimeOffset]::MinValue
            if ($kind -ne 'String' -or -not $property.Value.TryGetDateTimeOffset([ref]$date)) { throw "Некорректная дата ${Description}: $($property.Name)" }
        } elseif ($expected -eq 'Boolean') {
            if ($kind -notin @('True','False')) { throw "Некорректный тип ${Description}: $($property.Name)" }
        } elseif ($kind -ne $expected) { throw "Некорректный тип ${Description}: $($property.Name)" }
    }
    if ($seen.Count -ne $Fields.Count) { throw "В объекте $Description отсутствуют обязательные поля." }
}

function Read-VdSubscriptionsUnlocked([string]$Directory) {
    $path=Join-Path $Directory 'subscriptions.json'
    try { $inputStream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read) }
    catch [IO.FileNotFoundException] { return @{Version=1;Subscriptions=@();Items=@()} }
    catch [IO.DirectoryNotFoundException] { return @{Version=1;Subscriptions=@();Items=@()} }
    try {
        if ($inputStream.Length -gt 128MB) { throw 'Файл подписок превышает 128 МиБ.' }
        $reader=[IO.StreamReader]::new($inputStream,[Text.UTF8Encoding]::new($false,$true),$true)
        try { $json=$reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $inputStream.Dispose() }
    $document=[Text.Json.JsonDocument]::Parse($json)
    try {
        $root=$document.RootElement
        Assert-VdSubscriptionJsonShape $root @{Version='Number';Subscriptions='Array';Items='Array'} 'snapshot'
        if ($root.GetProperty('Version').GetInt32() -ne 1) { throw 'Неподдерживаемая версия хранилища подписок.' }
        $definitions=@{Id='String';Url='String';Title='String';FeedUrl='String';DownloadExisting='Boolean';Initialized='Boolean';Paused='Boolean';LastAttemptUtc='Date?';LastSuccessUtc='Date?';LastError='String';LastWarning='String'}
        foreach ($entry in $root.GetProperty('Subscriptions').EnumerateArray()) { Assert-VdSubscriptionJsonShape $entry $definitions 'subscription' }
        $items=@{SubscriptionId='String';Url='String';Title='String';State='String';DiscoveredAtUtc='Date';InitialQueuePending='Boolean';SuccessAttributed='Boolean';DuplicateSubscriptionId='String';DuplicateNotified='Boolean'}
        foreach ($entry in $root.GetProperty('Items').EnumerateArray()) { Assert-VdSubscriptionJsonShape $entry $items 'item' }
    } finally { $document.Dispose() }
    $snapshot=ConvertFrom-Json -InputObject $json -AsHashtable -ErrorAction Stop
    $ids=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $sources=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $byId=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($source in $snapshot.Subscriptions) {
        if ([string]::IsNullOrWhiteSpace($source.Id) -or -not $ids.Add($source.Id) -or
            -not (Test-VdSubscriptionUrl $source.Url) -or -not $sources.Add($source.Url) -or
            ($source.FeedUrl -and -not (Test-VdSubscriptionUrl $source.FeedUrl)) -or
            (-not $source.Initialized -and $null -ne $source.LastSuccessUtc)) { throw 'Некорректная или повторная подписка в хранилище.' }
        $byId[$source.Id]=$source
    }
    $keys=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($item in $snapshot.Items) {
        if (-not $byId.ContainsKey($item.SubscriptionId) -or -not $byId[$item.SubscriptionId].Initialized -or -not (Test-VdSubscriptionUrl $item.Url) -or
            $item.State -cnotin @('Baseline','New','Queued','Processed','AlreadyProcessed','Skipped') -or
            $item.DuplicateSubscriptionId -ceq $item.SubscriptionId -or
            ($item.InitialQueuePending -and $item.State -ne 'New') -or
            ($item.SuccessAttributed -and $item.State -ne 'Processed') -or
            ($item.State -eq 'AlreadyProcessed' -and [string]::IsNullOrWhiteSpace($item.DuplicateSubscriptionId)) -or
            ($item.State -ne 'AlreadyProcessed' -and ($item.DuplicateSubscriptionId -or $item.DuplicateNotified))) { throw 'Некорректный материал подписки.' }
        if (-not $keys.ContainsKey($item.SubscriptionId)) { $keys[$item.SubscriptionId]=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal) }
        if (-not $keys[$item.SubscriptionId].Add($item.Url)) { throw 'Повторный URL в одной подписке.' }
    }
    return $snapshot
}

function Write-VdSubscriptionsUnlocked([string]$Directory,$Snapshot,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None) {
    [void][IO.Directory]::CreateDirectory($Directory)
    $path=Join-Path $Directory 'subscriptions.json'
    $temporary=$path+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try {
        $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Snapshot -Depth 12 -Compress))
        if ($bytes.Length -gt 128MB) { throw 'Файл подписок превышает 128 МиБ.' }
        $stream=[IO.FileStream]::new($temporary,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try { $stream.Write($bytes,0,$bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
        $CancellationToken.ThrowIfCancellationRequested()
        [IO.File]::Move($temporary,$path,$true)
    } finally { if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) } }
}

function Get-VdSubscriptionDefinition($Snapshot,[string]$Id) {
    foreach ($source in $Snapshot.Subscriptions) { if ([string]::Equals($source.Id,$Id,[StringComparison]::Ordinal)) { return $source } }
    throw 'Подписка больше не существует.'
}

function Read-VdSubscriptions {
    param([Parameter(Mandatory)][string]$Directory)
    Invoke-VdSubscriptionsLocked { Read-VdSubscriptionsUnlocked $Directory }
}

function Add-VdSubscription {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Url,[bool]$DownloadExisting=$false,[string]$FeedUrl='')
    if (-not (Test-VdSubscriptionUrl $Url) -or ($FeedUrl -and -not (Test-VdSubscriptionUrl $FeedUrl))) { throw 'Нужен корректный HTTP(S) URL источника и ленты.' }
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory
        foreach ($existing in $snapshot.Subscriptions) { if ([string]::Equals($existing.Url,$Url,[StringComparison]::Ordinal)) { throw 'Этот источник уже добавлен.' } }
        $source=@{Id=[guid]::NewGuid().ToString('N');Url=$Url;Title=$Url;FeedUrl=$FeedUrl;DownloadExisting=$DownloadExisting;Initialized=$false;Paused=$false;LastAttemptUtc=$null;LastSuccessUtc=$null;LastError='';LastWarning=''}
        $snapshot.Subscriptions=@($snapshot.Subscriptions)+@($source)
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $source
    }
}

function Sync-VdSubscriptionSuccess($Snapshot,[string[]]$Urls) {
    $successful=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($url in $Urls) {
        if (-not (Test-VdSubscriptionUrl $url)) { throw 'Некорректный URL успешной загрузки.' }
        [void]$successful.Add($url)
    }
    $byUrl=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($item in $Snapshot.Items) {
        if (-not $byUrl.ContainsKey($item.Url)) { $byUrl[$item.Url]=[Collections.Generic.List[object]]::new() }
        $byUrl[$item.Url].Add($item)
    }
    foreach ($url in $successful) {
        if (-not $byUrl.ContainsKey($url)) { continue }
        $matches=$byUrl[$url]
        $origin=$null
        foreach ($state in @('Processed','Queued')) {
            foreach ($source in $Snapshot.Subscriptions) {
                foreach ($item in $matches) {
                    if ($item.SubscriptionId -ceq $source.Id -and $item.State -eq $state -and ($state -ne 'Processed' -or $item.SuccessAttributed)) { $origin=$item; break }
                }
                if ($null -ne $origin) { break }
            }
            if ($null -ne $origin) { break }
        }
        $originId=if ($null -ne $origin) { [string]$origin.SubscriptionId } else { '' }
        if (-not $originId) {
            foreach ($item in $matches) {
                if ($item.State -eq 'AlreadyProcessed') { $originId=[string]$item.DuplicateSubscriptionId; break }
            }
        }
        foreach ($item in $matches) {
            $item.InitialQueuePending=$false
            if ($item.State -in @('AlreadyProcessed','Processed')) { continue }
            if ($originId -and $item.SubscriptionId -cne $originId) {
                $item.State='AlreadyProcessed'; $item.SuccessAttributed=$false; $item.DuplicateSubscriptionId=$originId; $item.DuplicateNotified=$false
            } else {
                $item.State='Processed'; $item.SuccessAttributed=[bool]$originId; $item.DuplicateSubscriptionId=''; $item.DuplicateNotified=$false
            }
        }
    }
}

function Apply-VdSubscriptionScan {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)]$Scan,[AllowEmptyCollection()][string[]]$SuccessfulUrls=@(),[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    $CancellationToken.ThrowIfCancellationRequested()
    if ($null -eq $Scan -or $null -eq $Scan.Items -or $null -eq $Scan.Title -or $null -eq $Scan.FeedUrl -or $null -eq $Scan.Warning) { throw 'Некорректный результат обхода.' }
    $entries=@($Scan.Items)
    if ($entries.Count -gt 10000) { throw 'Полный обход превышает предел 10 000 материалов.' }
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($entry in $entries) { if ($null -eq $entry -or $null -eq $entry.Title -or -not (Test-VdSubscriptionUrl ([string]$entry.Url))) { throw 'Некорректный материал в результате обхода.' } }
    if ($Scan.FeedUrl -and -not (Test-VdSubscriptionUrl ([string]$Scan.FeedUrl))) { throw 'Некорректный URL ленты.' }
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory
        $source=Get-VdSubscriptionDefinition $snapshot $Id
        Sync-VdSubscriptionSuccess $snapshot $SuccessfulUrls
        $processed=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        foreach ($item in $snapshot.Items) { if ($item.State -eq 'Processed') { [void]$processed.Add($item.Url) } }
        $known=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
        foreach ($item in $snapshot.Items) { if ($item.SubscriptionId -ceq $Id) { $known[$item.Url]=$item } }
        $now=[DateTimeOffset]::UtcNow.ToString('o')
        $added=[Collections.Generic.List[object]]::new()
        foreach ($entry in $entries) {
            if (-not $seen.Add([string]$entry.Url)) { continue }
            if ($known.ContainsKey([string]$entry.Url)) { $known[[string]$entry.Url].Title=[string]$entry.Title; continue }
            $baseline=-not $source.Initialized -and -not $source.DownloadExisting
            $added.Add(@{SubscriptionId=$Id;Url=[string]$entry.Url;Title=[string]$entry.Title;State=$(if ($baseline) {'Baseline'} else {'New'});DiscoveredAtUtc=$now;InitialQueuePending=(-not $source.Initialized -and $source.DownloadExisting);SuccessAttributed=$false;DuplicateSubscriptionId='';DuplicateNotified=$false})
        }
        $snapshot.Items=@($snapshot.Items)+@($added.ToArray())
        $reconcile=[Collections.Generic.HashSet[string]]::new($SuccessfulUrls,[StringComparer]::Ordinal)
        foreach ($item in $added) {
            if ($processed.Contains($item.Url)) { [void]$reconcile.Add($item.Url) }
        }
        Sync-VdSubscriptionSuccess $snapshot @($reconcile)
        $source.Title=if ([string]::IsNullOrWhiteSpace([string]$Scan.Title)) { $source.Url } else { [string]$Scan.Title }
        if ($Scan.FeedUrl) { $source.FeedUrl=[string]$Scan.FeedUrl }
        $source.Initialized=$true; $source.LastAttemptUtc=$now; $source.LastSuccessUtc=$now; $source.LastError=''; $source.LastWarning=[string]$Scan.Warning
        Write-VdSubscriptionsUnlocked $Directory $snapshot $CancellationToken
        return $snapshot
    } $CancellationToken
}

function Set-VdSubscriptionFailure {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][string]$Error)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; $source=Get-VdSubscriptionDefinition $snapshot $Id
        $source.LastAttemptUtc=[DateTimeOffset]::UtcNow.ToString('o'); $source.LastError=$Error
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Set-VdSubscriptionPaused {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][bool]$Paused)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; (Get-VdSubscriptionDefinition $snapshot $Id).Paused=$Paused
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Remove-VdSubscription {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; [void](Get-VdSubscriptionDefinition $snapshot $Id)
        $snapshot.Subscriptions=@($snapshot.Subscriptions | Where-Object { $_.Id -cne $Id })
        $snapshot.Items=@($snapshot.Items | Where-Object { $_.SubscriptionId -cne $Id })
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Set-VdSubscriptionItemsQueued {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Urls)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; [void](Get-VdSubscriptionDefinition $snapshot $Id)
        $selected=[Collections.Generic.HashSet[string]]::new($Urls,[StringComparer]::Ordinal)
        $items=@($snapshot.Items | Where-Object { $_.SubscriptionId -ceq $Id -and $selected.Contains($_.Url) })
        if ($items.Count -ne $selected.Count -or @($items | Where-Object { $_.State -notin @('New','Skipped','Queued') }).Count -gt 0) { throw 'Материал не найден или его состояние нельзя изменить.' }
        foreach ($item in $items) { $item.State='Queued'; $item.InitialQueuePending=$false }
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Skip-VdSubscriptionItems {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Id,[Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Urls)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; [void](Get-VdSubscriptionDefinition $snapshot $Id)
        $selected=[Collections.Generic.HashSet[string]]::new($Urls,[StringComparer]::Ordinal)
        $items=@($snapshot.Items | Where-Object { $_.SubscriptionId -ceq $Id -and $selected.Contains($_.Url) })
        if ($items.Count -ne $selected.Count -or @($items | Where-Object { $_.State -notin @('New','Skipped') }).Count -gt 0) { throw 'Материал не найден или его состояние нельзя изменить.' }
        foreach ($item in $items) { $item.State='Skipped'; $item.InitialQueuePending=$false }
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Set-VdSubscriptionSuccessful {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][string]$Url)
    if (-not (Test-VdSubscriptionUrl $Url)) { throw 'Некорректный URL успешной загрузки.' }
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; Sync-VdSubscriptionSuccess $snapshot @($Url)
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Sync-VdSubscriptionsSuccessful {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][AllowEmptyCollection()][string[]]$SuccessfulUrls)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory; Sync-VdSubscriptionSuccess $snapshot $SuccessfulUrls
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Confirm-VdSubscriptionDuplicates {
    param([Parameter(Mandatory)][string]$Directory,[Parameter(Mandatory)][AllowEmptyCollection()][object[]]$Items)
    Invoke-VdSubscriptionsLocked {
        $snapshot=Read-VdSubscriptionsUnlocked $Directory
        foreach ($selected in $Items) {
            foreach ($item in $snapshot.Items) {
                if ($item.SubscriptionId -ceq $selected.SubscriptionId -and [string]::Equals($item.Url,[string]$selected.Url,[StringComparison]::Ordinal) -and $item.DuplicateSubscriptionId -ceq $selected.DuplicateSubscriptionId -and $item.State -eq 'AlreadyProcessed') { $item.DuplicateNotified=$true }
            }
        }
        Write-VdSubscriptionsUnlocked $Directory $snapshot
        return $snapshot
    }
}

function Invoke-VdSubscriptionProcess {
    param([Parameter(Mandatory)][string]$FilePath,[Parameter(Mandatory)][AllowEmptyCollection()][string[]]$Arguments,
        [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None,
        [ValidateRange(0.05,900)][double]$TimeoutSeconds=180,[ValidateRange(1,33554432)][int]$MaxOutputBytes=33554432)
    $deadline=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken,[Threading.CancellationToken]::None)
    $deadline.CancelAfter([TimeSpan]::FromSeconds($TimeoutSeconds))
    $process=[Diagnostics.Process]::new(); $started=$false
    $output=[IO.MemoryStream]::new(); $errors=[IO.MemoryStream]::new()
    try {
        $deadline.Token.ThrowIfCancellationRequested()
        $start=[Diagnostics.ProcessStartInfo]::new($FilePath)
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        foreach ($argument in $Arguments) { [void]$start.ArgumentList.Add($argument) }
        $process.StartInfo=$start; [void]$process.Start(); $started=$true
        $outBuffer=[byte[]]::new(16384); $errBuffer=[byte[]]::new(16384)
        $stdout=$process.StandardOutput.BaseStream.ReadAsync($outBuffer,0,$outBuffer.Length,$deadline.Token)
        $stderr=$process.StandardError.BaseStream.ReadAsync($errBuffer,0,$errBuffer.Length,$deadline.Token)
        while ($null -ne $stdout -or $null -ne $stderr -or -not $process.HasExited) {
            $deadline.Token.ThrowIfCancellationRequested()
            if ($stdout -and $stdout.IsCompleted) {
                $count=$stdout.GetAwaiter().GetResult()
                if ($count -eq 0) { $stdout=$null }
                else {
                    if ($output.Length+$count -gt $MaxOutputBytes) { throw 'Ответ yt-dlp превышает предел 32 МиБ.' }
                    $output.Write($outBuffer,0,$count)
                    $stdout=$process.StandardOutput.BaseStream.ReadAsync($outBuffer,0,$outBuffer.Length,$deadline.Token)
                }
            }
            if ($stderr -and $stderr.IsCompleted) {
                $count=$stderr.GetAwaiter().GetResult()
                if ($count -eq 0) { $stderr=$null }
                else {
                    if ($errors.Length+$count -gt 1MB) { throw 'Диагностика yt-dlp превышает допустимый объём.' }
                    $errors.Write($errBuffer,0,$count)
                    $stderr=$process.StandardError.BaseStream.ReadAsync($errBuffer,0,$errBuffer.Length,$deadline.Token)
                }
            }
            if ($stdout -or $stderr -or -not $process.HasExited) { Start-Sleep -Milliseconds 10 }
        }
        return @{ExitCode=$process.ExitCode;Stdout=[Text.UTF8Encoding]::new($false,$true).GetString($output.ToArray());Stderr=[Text.Encoding]::UTF8.GetString($errors.ToArray())}
    } catch {
        if ($deadline.IsCancellationRequested) {
            if ($CancellationToken.IsCancellationRequested) { throw [OperationCanceledException]::new('Проверка подписки отменена.',$CancellationToken) }
            throw [TimeoutException]::new('Полный обход подписки превысил отведённое время.')
        }
        throw
    } finally {
        if ($started) { try { if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(2000) } } catch {} }
        $process.Dispose(); $output.Dispose(); $errors.Dispose(); $deadline.Dispose()
    }
}

function ConvertFrom-VdSubscriptionMetadata {
    param([Parameter(Mandatory)][string]$Json,[Parameter(Mandatory)][string]$SourceUrl,[ValidateRange(1,10000)][int]$MaxItems=10000)
    if (-not (Test-VdSubscriptionUrl $SourceUrl)) { throw 'Некорректный URL источника.' }
    if ([Text.Encoding]::UTF8.GetByteCount($Json) -gt 32MB) { throw 'Ответ yt-dlp превышает предел 32 МиБ.' }
    $root=ConvertFrom-Json -InputObject $Json -AsHashtable -ErrorAction Stop
    $results=[Collections.Generic.List[object]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $state=@{Count=0}
    function Assert-Complete($Value) {
        foreach ($name in @('_error','incomplete','_incomplete','truncated')) {
            if ($Value.Contains($name) -and $null -ne $Value[$name] -and -not ($Value[$name] -is [bool] -and -not $Value[$name])) {
                throw 'Источник сообщает об ошибке или неполном обходе.'
            }
        }
    }
    function Read-Collection($Collection) {
        if ($Collection -isnot [Collections.IDictionary] -or -not $Collection.Contains('entries') -or $Collection.entries -isnot [array]) { throw 'Источник не предоставил полный список материалов.' }
        Assert-Complete $Collection
        $children=@($Collection.entries)
        if ($children.Count -gt $MaxItems) { throw 'Полный обход превышает предел 10 000 материалов.' }
        foreach ($name in @('playlist_count','n_entries')) {
            if ($Collection.Contains($name) -and $null -ne $Collection[$name]) {
                $expected=0L
                if ($Collection[$name] -isnot [int] -and $Collection[$name] -isnot [long]) { throw 'Источник предоставил некорректное число материалов.' }
                if (-not [long]::TryParse([string]$Collection[$name],[ref]$expected) -or $expected -ne $children.Count) { throw 'Источник предоставил неполный список материалов.' }
            }
        }
        if ($Collection.Contains('requested_entries') -and $null -ne $Collection.requested_entries) {
            if ($Collection.requested_entries -isnot [array]) { throw 'Источник предоставил некорректный список индексов.' }
            $requested=@($Collection.requested_entries)
            if ($requested.Count -ne $children.Count) { throw 'Источник предоставил только часть списка.' }
            for ($i=0;$i -lt $requested.Count;$i++) { if (($requested[$i] -isnot [int] -and $requested[$i] -isnot [long]) -or [long]$requested[$i] -ne ($i+1)) { throw 'Источник предоставил только часть списка.' } }
        }
        foreach ($entry in $children) {
            if ($null -eq $entry -or $entry -isnot [Collections.IDictionary]) { throw 'Список содержит недоступный материал; полный обход не подтверждён.' }
            Assert-Complete $entry
            if ($entry.Contains('entries') -or $entry._type -cin @('playlist','multi_video','compat_list')) { Read-Collection $entry; continue }
            if ($entry._type -cin @('url','url_transparent')) { throw 'Вложенный источник не раскрыт до материалов; полный обход не подтверждён.' }
            $state.Count++
            if ($state.Count -gt $MaxItems) { throw 'Полный обход превышает предел 10 000 материалов.' }
            if ($entry.availability -and $entry.availability -cnotin @('public','unlisted')) { throw 'Список содержит недоступный материал.' }
            $url=[string]$entry.webpage_url
            if (-not (Test-VdSubscriptionUrl $url)) { $url=[string]$entry.url }
            if (-not (Test-VdSubscriptionUrl $url)) {
                if (($entry.ie_key -ceq 'Youtube' -or $entry.extractor_key -ceq 'Youtube') -and [string]$entry.id -match '^[A-Za-z0-9_-]{11}$') { $url='https://www.youtube.com/watch?v='+[string]$entry.id }
                else { throw 'Материал не содержит корректный HTTP(S) URL.' }
            }
            if ($seen.Add($url)) { $results.Add(@{Url=$url;Title=$(if ([string]::IsNullOrWhiteSpace([string]$entry.title)) { $url } else { [string]$entry.title })}) }
        }
    }
    Read-Collection $root
    $feedUrl=''
    $sourceHost=([Uri]$SourceUrl).Host
    if ($sourceHost -ieq 'youtube.com' -or $sourceHost.EndsWith('.youtube.com',[StringComparison]::OrdinalIgnoreCase)) {
        $channel=[string]$root.channel_id
        if ($channel -notmatch '^UC[A-Za-z0-9_-]{22}$') { $channel=[string]$root.uploader_id }
        if ($channel -match '^UC[A-Za-z0-9_-]{22}$') { $feedUrl='https://www.youtube.com/feeds/videos.xml?channel_id='+$channel }
    }
    return @{Title=$(if ([string]::IsNullOrWhiteSpace([string]$root.title)) { $SourceUrl } else { [string]$root.title });Items=@($results.ToArray());FeedUrl=$feedUrl;Warning=''}
}

function Read-VdSubscriptionFeed {
    param([Parameter(Mandatory)][string]$Url,[Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None)
    if (-not (Test-VdSubscriptionUrl $Url)) { throw 'Некорректный URL RSS/Atom.' }
    $deadline=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken,[Threading.CancellationToken]::None)
    $deadline.CancelAfter([TimeSpan]::FromSeconds(12))
    $handler=[Net.Http.HttpClientHandler]::new(); $client=[Net.Http.HttpClient]::new($handler)
    $client.Timeout=[Threading.Timeout]::InfiniteTimeSpan
    $response=$null; $stream=$null; $buffer=[IO.MemoryStream]::new()
    try {
        $response=$client.GetAsync($Url,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
        [void]$response.EnsureSuccessStatusCode()
        if ($response.Content.Headers.ContentLength -gt 2MB) { throw 'RSS/Atom превышает предел 2 МиБ.' }
        $stream=$response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
        $chunk=[byte[]]::new(16384)
        while (($count=$stream.ReadAsync($chunk,0,$chunk.Length,$deadline.Token).GetAwaiter().GetResult()) -gt 0) {
            if ($buffer.Length+$count -gt 2MB) { throw 'RSS/Atom превышает предел 2 МиБ.' }
            $buffer.Write($chunk,0,$count)
        }
        return [Text.UTF8Encoding]::new($false,$true).GetString($buffer.ToArray())
    } finally {
        if ($stream) { $stream.Dispose() }; if ($response) { $response.Dispose() }
        $buffer.Dispose(); $client.Dispose(); $deadline.Dispose()
    }
}

function ConvertFrom-VdSubscriptionFeed {
    param([Parameter(Mandatory)][string]$Xml,[ValidateRange(1,10000)][int]$MaxItems=10000)
    if ([Text.Encoding]::UTF8.GetByteCount($Xml) -gt 2MB) { throw 'RSS/Atom превышает предел 2 МиБ.' }
    $settings=[Xml.XmlReaderSettings]::new(); $settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit; $settings.XmlResolver=$null; $settings.MaxCharactersInDocument=2MB
    $text=[IO.StringReader]::new($Xml); $reader=[Xml.XmlReader]::Create($text,$settings)
    $document=[Xml.XmlDocument]::new(); $document.XmlResolver=$null
    try { $document.Load($reader) } finally { $reader.Dispose(); $text.Dispose() }
    $items=[Collections.Generic.List[object]]::new()
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $root=$document.DocumentElement
    if ($root.LocalName -eq 'rss') { $nodes=$root.SelectNodes('./*[local-name()="channel"]/*[local-name()="item"]') }
    elseif ($root.LocalName -eq 'feed') { $nodes=$root.SelectNodes('./*[local-name()="entry"]') }
    elseif ($root.LocalName -eq 'RDF') { $nodes=$root.SelectNodes('./*[local-name()="item"]') }
    else { throw 'Ответ не является RSS/Atom-лентой.' }
    if ($nodes.Count -gt $MaxItems) { throw 'В ленте больше допустимого числа материалов.' }
    foreach ($node in $nodes) {
        $titleNode=$node.SelectSingleNode('./*[local-name()="title"]'); $title=if ($titleNode) { $titleNode.InnerText.Trim() } else { '' }
        $url=''
        foreach ($link in $node.SelectNodes('./*[local-name()="link" and (not(@rel) or @rel="alternate")]')) {
            $candidate=if ($link.HasAttribute('href')) { $link.GetAttribute('href').Trim() } else { $link.InnerText.Trim() }
            if (Test-VdSubscriptionUrl $candidate) { $url=$candidate; break }
        }
        if (-not $url) { $enclosure=$node.SelectSingleNode('./*[local-name()="enclosure"]'); if ($enclosure) { $url=$enclosure.GetAttribute('url') } }
        if (-not (Test-VdSubscriptionUrl $url)) { throw 'Лента содержит некорректный HTTP(S) URL.' }
        if ($seen.Add($url)) { $items.Add(@{Url=$url;Title=$title}) }
    }
    return ,$items.ToArray()
}

function Invoke-VdSubscriptionScan {
    param([Parameter(Mandatory)][string]$YtDlpPath,[Parameter(Mandatory)]$Subscription,[string]$CookiePath='',
        [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None,
        [scriptblock]$ProcessRunner,[scriptblock]$FeedLoader,[ValidateRange(0.05,180)][double]$TimeoutSeconds=180,
        [ValidateRange(0.05,900)][double]$ChannelTimeoutSeconds=900)
    if (-not (Test-VdSubscriptionUrl ([string]$Subscription.Url))) { throw 'Некорректный URL подписки.' }
    $deadline=[Threading.CancellationTokenSource]::CreateLinkedTokenSource($CancellationToken,[Threading.CancellationToken]::None)
    $channel=[bool](Get-VdPornHubListingUrl ([string]$Subscription.Url))
    $effectiveTimeout=if($channel){$ChannelTimeoutSeconds}else{$TimeoutSeconds}
    $deadline.CancelAfter([TimeSpan]::FromSeconds($effectiveTimeout))
    try {
        $arguments=@('--ignore-config','--dump-single-json','--skip-download','--abort-on-error','--encoding','utf-8','--impersonate','chrome')
        if ($channel) {
            $arguments+=@('--no-plugin-dirs','--plugin-dirs',(Get-VdPornHubCollectorDirectory),'--use-extractors','videodownloader:pornhub:channel',
                '--flat-playlist','--socket-timeout','20','--extractor-args','VideoDownloaderPornHubChannel:max_items=10000;max_pages=500')
        } else { $arguments+=@('--no-flat-playlist','--playlist-end','10001') }
        if ($CookiePath) { $arguments+=@('--cookies',$CookiePath) }
        $arguments+=@('--',[string]$Subscription.Url)
        if ($ProcessRunner) { $result=& $ProcessRunner $YtDlpPath $arguments $deadline.Token $effectiveTimeout 33554432 }
        else { $result=Invoke-VdSubscriptionProcess -FilePath $YtDlpPath -Arguments $arguments -CancellationToken $deadline.Token -TimeoutSeconds $effectiveTimeout }
        $deadline.Token.ThrowIfCancellationRequested()
        # Match the native scanner: extractors may report a partial page in a
        # warning and still exit successfully. Such output cannot set baseline.
        $incompleteDiagnostic='(?i)\bERROR:|\bincomplete\b|\btruncat\w*|\bpartial (?:playlist|collection)|\b(?:unable|failed) to (?:download|extract|retrieve).*(?:page|playlist|entries)|\bgiving up after'
        if ($result.ExitCode -ne 0 -or [string]$result.Stderr -match $incompleteDiagnostic) { throw ('Не удалось полностью проверить подписку: '+[string]$result.Stderr) }
        $scan=if($channel){ConvertFrom-VdPornHubScan -Json ([string]$result.Stdout) -SourceUrl ([string]$Subscription.Url)}
            else{ConvertFrom-VdSubscriptionMetadata -Json ([string]$result.Stdout) -SourceUrl ([string]$Subscription.Url)}
        if ($Subscription.FeedUrl) { $scan.FeedUrl=[string]$Subscription.FeedUrl }
        if ($scan.FeedUrl) {
            try {
                if ($FeedLoader) { $xml=& $FeedLoader $scan.FeedUrl $deadline.Token }
                else { $xml=Read-VdSubscriptionFeed -Url $scan.FeedUrl -CancellationToken $deadline.Token }
                $feed=ConvertFrom-VdSubscriptionFeed -Xml ([string]$xml)
                $byUrl=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
                foreach ($item in $scan.Items) { $byUrl[$item.Url]=$item }
                $unconfirmed=0
                foreach ($item in $feed) {
                    if ($byUrl.ContainsKey($item.Url)) {
                        $current=$byUrl[$item.Url]
                        if (($current.Title -ceq $current.Url -or -not $current.Title) -and $item.Title) { $current.Title=$item.Title }
                    } else { $unconfirmed++ }
                }
                if ($unconfirmed -gt 0) { $scan.Warning="Материалы ленты, не подтверждённые полным обходом: $unconfirmed. Они не добавлены." }
            } catch {
                if ($deadline.IsCancellationRequested) { throw }
                $scan.Warning='RSS/Atom недоступна; использован полный обход yt-dlp. '+$_.Exception.Message
            }
        }
        $deadline.Token.ThrowIfCancellationRequested()
        return $scan
    } catch {
        if ($deadline.IsCancellationRequested) {
            if ($CancellationToken.IsCancellationRequested) { throw [OperationCanceledException]::new('Проверка подписки отменена.',$CancellationToken) }
            throw [TimeoutException]::new('Полный обход подписки превысил отведённое время.')
        }
        throw
    } finally { $deadline.Dispose() }
}

Export-ModuleMember -Function Test-VdSubscriptionUrl,Read-VdSubscriptions,Add-VdSubscription,Apply-VdSubscriptionScan,Set-VdSubscriptionFailure,Set-VdSubscriptionPaused,Remove-VdSubscription,Set-VdSubscriptionItemsQueued,Skip-VdSubscriptionItems,Set-VdSubscriptionSuccessful,Sync-VdSubscriptionsSuccessful,Confirm-VdSubscriptionDuplicates,Invoke-VdSubscriptionProcess,ConvertFrom-VdSubscriptionMetadata,Read-VdSubscriptionFeed,ConvertFrom-VdSubscriptionFeed,Invoke-VdSubscriptionScan
