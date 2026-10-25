namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly HashSet<string> _subscriptionSuccesses=new(StringComparer.Ordinal);

    // Subscriptions never enter a running coordinator. The explicit Start
    // action remains the boundary for download/history confirmation.
    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls) => QueueSubscriptionUrls(urls,"","");

    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName) =>
        QueueSubscriptionUrls(urls,subscriptionId,subscriptionName,null);

    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName,
        IReadOnlyDictionary<string,string>? titles)
    {
        if (_closing || _disposed) return [];
        if (_checkpointReadOnly || _legacyEngineBlocked)
            throw new InvalidOperationException("Очередь недоступна для сохранения. Сначала устраните ошибку восстановления или завершите прежний движок.");
        if ((subscriptionId.Length==0)!=(subscriptionName.Length==0) ||
            (subscriptionName.Length>0 && !SubscriptionNames.IsValid(subscriptionName)))
            throw new ArgumentException("У подписки должно быть безопасное краткое имя папки.");
        // Copy into an ordinal dictionary even when the caller uses a custom
        // comparer. Discovery titles must follow the exact URL identity.
        var discoveredTitles=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach (var pair in titles ?? new Dictionary<string,string>())
        {
            string title=pair.Value?.Trim()??"";
            if (title.Length>0 && !string.Equals(title,pair.Key,StringComparison.Ordinal))
                discoveredTitles.TryAdd(pair.Key,title);
        }
        var requested=urls.Where(AppPersistence.ValidUrl).Distinct(StringComparer.Ordinal).ToArray();
        var requestedSet=requested.ToHashSet(StringComparer.Ordinal);
        var candidates=requested.Where(url=>!_queue.Any(row=>row.Url==url))
            .Select(url=>new DownloadItem{Url=url,Title=discoveredTitles.GetValueOrDefault(url,""),
                SubscriptionId=subscriptionId,SubscriptionName=subscriptionName}).ToList();
        var approved=ConfirmDuplicates(candidates,false);
        if (approved==null) return [];
        foreach (var row in approved)
        {
            // A modal duplicate prompt may have let another UI action add it.
            if (_queue.Any(existing=>existing.Url==row.Url)) continue;
            row.DeferredToNextStart=_session?.IsRunning==true;
            _queue.Add(row);
            RequestRowPreview(row);
        }
        foreach (var row in _queue.Where(row=>requestedSet.Contains(row.Url) &&
            row.CanResume && row.Status!="Completed"))
        {
            if ((string.IsNullOrWhiteSpace(row.Title) || string.Equals(row.Title,row.Url,StringComparison.Ordinal)) &&
                discoveredTitles.TryGetValue(row.Url,out string? title)) row.Title=title;
            if (string.Equals(_previewUrl,row.Url,StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(row.Title) &&
                !string.Equals(row.Title,row.Url,StringComparison.Ordinal)) T("PreviewTitle").Text=row.Title;
        }
        Reindex();
        if (_checkpointReadOnly)
            throw new IOException("Не удалось сохранить очередь. Материалы подписки остаются доступными для повторного добавления.");
        var accepted=requested.Where(url=>_queue.Any(row=>row.Url==url && row.CanResume && row.Status!="Completed")).ToArray();
        T("ClipboardHint").Text=$"Из подписок принято {accepted.Length}. Загрузка начнётся по кнопке Start.";
        return accepted;
    }

    private void RestoreSubscriptionQueueTitles(IEnumerable<DownloadItem> rows)
    {
        var pending=rows.Where(row=>row.SubscriptionId.Length>0 &&
            (string.IsNullOrWhiteSpace(row.Title) || string.Equals(row.Title,row.Url,StringComparison.Ordinal))).ToArray();
        if(pending.Length==0)return;
        try
        {
            if(!File.Exists(SubscriptionStorage.Path))
            {
                Log("Названия подписок недоступны: файл подписок отсутствует. Очередь восстановлена без изменения ссылок.");
                return;
            }
            var wanted=pending.Select(row=>(row.SubscriptionId,row.Url)).ToHashSet();
            // Tuple string equality is ordinal. Read the optional store once,
            // before prefetch, without changing its contents or any routing.
            var titles=SubscriptionStorage.Read().Items.Where(item=>wanted.Contains((item.SubscriptionId,item.Url)) &&
                !string.IsNullOrWhiteSpace(item.Title) && !string.Equals(item.Title.Trim(),item.Url,StringComparison.Ordinal))
                .ToDictionary(item=>(item.SubscriptionId,item.Url),item=>item.Title.Trim());
            foreach(var row in pending)
                if(titles.TryGetValue((row.SubscriptionId,row.Url),out string? title))row.Title=title;
        }
        catch(Exception error)
        {
            Log("Не удалось восстановить названия из подписок; сохранённая очередь продолжает работать: "+error.Message);
        }
    }

    private void RecordSubscriptionSuccess(string url)
    {
        string directory=Path.GetDirectoryName(_storage.SessionPath)!;
        if (!File.Exists(Path.Combine(directory,"subscriptions.json")) || !_subscriptionSuccesses.Add(url)) return;
        try
        {
            new SubscriptionStore(directory).MarkSuccessful(url);
            RefreshSubscriptions();
        }
        catch(Exception error)
        {
            // Global successful history remains the source for recovery if a
            // subscription write or a UI continuation fails after completion.
            Log("Не удалось обновить подписки после успешной загрузки: "+error.Message);
        }
    }
}
