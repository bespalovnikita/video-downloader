namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Subscriptions never enter a running coordinator. The explicit Start
    // action remains the boundary for download/history confirmation.
    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls) => QueueSubscriptionUrls(urls,"","");

    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName) =>
        QueueSubscriptionUrls(urls,subscriptionId,subscriptionName,null);

    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName,
        IReadOnlyDictionary<string,string>? titles)=>QueueSubscriptionUrls(urls,subscriptionId,subscriptionName,titles,false);

    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName,
        IReadOnlyDictionary<string,string>? titles,bool requeueExisting)
    {
        if (_closing || _disposed || _restoringSession || _queueItemActionBusy || _appUpdateHandoff || RejectProfileBackupMutation()) return [];
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
        var candidates=requested.Where(url=>!SessionState.Queue.Any(row=>row.Url==url))
            .Select(url=>new DownloadItem{Url=url,Title=discoveredTitles.GetValueOrDefault(url,""),
                SubscriptionId=subscriptionId,SubscriptionName=subscriptionName}).ToList();
        var approved=ConfirmDuplicates(candidates,false);
        if (approved==null || _closing || _disposed || _restoringSession || _queueItemActionBusy ||
            _appUpdateHandoff || RejectProfileBackupMutation()) return [];
        if (_checkpointReadOnly || _legacyEngineBlocked)
            throw new InvalidOperationException("Очередь недоступна для сохранения. Сначала устраните ошибку восстановления или завершите прежний движок.");
        // The modal loop may have added an exact URL already. Deduplication,
        // title enrichment and the complete checkpoint budget commit together.
        foreach (var row in approved)row.DeferredToNextStart=SessionState.Coordinator?.IsRunning==true;
        // Explicit selection can schedule a repeat of an existing terminal row.
        // Keep its old status/ownership until Start, including Remove's warning
        // and retained Skip data. Automatic offers retain their previous policy.
        var requeues=requeueExisting?SessionState.Queue.Where(row=>requestedSet.Contains(row.Url) && row.CanResume &&
            row.Status is "Completed" or "Skipped").ToArray():[];
        var titleUpdates=new Dictionary<DownloadItem,string>();
        foreach(var row in SessionState.Queue.Where(row=>requestedSet.Contains(row.Url) && row.CanResume && row.Status!="Completed"))
            if((string.IsNullOrWhiteSpace(row.Title) || string.Equals(row.Title,row.Url,StringComparison.Ordinal)) &&
                discoveredTitles.TryGetValue(row.Url,out string? title))titleUpdates.Add(row,title);
        SessionState.AddBatch(approved,ReadOptions(),titleUpdates,requeues);
        foreach (var row in SessionState.Queue.Where(row=>requestedSet.Contains(row.Url) &&
            row.CanResume && (row.Status!="Completed" || requeueExisting && row.RequeueRequested)))
        {
            _preview.RefreshKnownTitle(row);
            // An explicitly accepted URL may already be queued after an
            // earlier metadata failure. Re-request its preview as well; the
            // shared worker deduplicates pending work and valid cached data.
            RequestRowPreview(row);
        }
        Reindex();
        if (_checkpointReadOnly)
            throw new IOException("Не удалось сохранить очередь. Материалы подписки остаются доступными для повторного добавления.");
        var accepted=requested.Where(url=>SessionState.Queue.Any(row=>row.Url==url && row.CanResume &&
            (row.Status!="Completed" || requeueExisting && row.RequeueRequested))).ToArray();
        T("ClipboardHint").Text=$"Из подписок принято {accepted.Length}. Загрузка начнётся по кнопке «Скачать всю очередь».";
        if(SessionState.Queue.Any(row=>requestedSet.Contains(row.Url) && row.RequeueRequested &&
            row.Status=="Skipped" && row.ResumeState?.Selection?.IsPartial==true))
            T("ClipboardHint").Text+=" Фрагменты через FFmpeg могут скачиваться заново: дозагрузка зависит от источника и загрузчика.";
        return accepted;
    }

    private void RestoreSubscriptionQueueTitles(IEnumerable<DownloadItem> rows)=>Subscriptions.RestoreQueueTitles(rows);
    private void RecordSubscriptionSuccess(string url)=>Subscriptions.RecordSuccess(url);
}
