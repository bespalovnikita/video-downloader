namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Subscriptions never enter a running coordinator. The explicit Start
    // action remains the boundary for download/history confirmation.
    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls) => QueueSubscriptionUrls(urls,"","");

    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName) =>
        QueueSubscriptionUrls(urls,subscriptionId,subscriptionName,null);

    internal IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls,string subscriptionId,string subscriptionName,
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
        var candidates=requested.Where(url=>!SessionState.Queue.Any(row=>row.Url==url))
            .Select(url=>new DownloadItem{Url=url,Title=discoveredTitles.GetValueOrDefault(url,""),
                SubscriptionId=subscriptionId,SubscriptionName=subscriptionName}).ToList();
        var approved=ConfirmDuplicates(candidates,false);
        if (approved==null) return [];
        foreach (var row in approved)
        {
            // A modal duplicate prompt may have let another UI action add it.
            row.DeferredToNextStart=SessionState.Coordinator?.IsRunning==true;
            if(!SessionState.TryAdd(row))continue;
            RequestRowPreview(row);
        }
        foreach (var row in SessionState.Queue.Where(row=>requestedSet.Contains(row.Url) &&
            row.CanResume && row.Status!="Completed"))
        {
            if ((string.IsNullOrWhiteSpace(row.Title) || string.Equals(row.Title,row.Url,StringComparison.Ordinal)) &&
                discoveredTitles.TryGetValue(row.Url,out string? title)) row.Title=title;
            _preview.RefreshKnownTitle(row);
        }
        Reindex();
        if (_checkpointReadOnly)
            throw new IOException("Не удалось сохранить очередь. Материалы подписки остаются доступными для повторного добавления.");
        var accepted=requested.Where(url=>SessionState.Queue.Any(row=>row.Url==url && row.CanResume && row.Status!="Completed")).ToArray();
        T("ClipboardHint").Text=$"Из подписок принято {accepted.Length}. Загрузка начнётся по кнопке Start.";
        return accepted;
    }

    private void RestoreSubscriptionQueueTitles(IEnumerable<DownloadItem> rows)=>Subscriptions.RestoreQueueTitles(rows);
    private void RecordSubscriptionSuccess(string url)=>Subscriptions.RecordSuccess(url);
}
