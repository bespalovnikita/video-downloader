namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly HashSet<string> _subscriptionSuccesses=new(StringComparer.Ordinal);

    // Subscriptions never enter a running coordinator. The explicit Start
    // action remains the boundary for download/history confirmation.
    private IReadOnlyList<string> QueueSubscriptionUrls(IEnumerable<string> urls)
    {
        if (_closing || _disposed) return [];
        if (_checkpointReadOnly || _legacyEngineBlocked)
            throw new InvalidOperationException("Очередь недоступна для сохранения. Сначала устраните ошибку восстановления или завершите прежний движок.");
        var requested=urls.Where(AppPersistence.ValidUrl).Distinct(StringComparer.Ordinal).ToArray();
        var candidates=requested.Where(url=>!_queue.Any(row=>row.Url==url))
            .Select(url=>new DownloadItem{Url=url}).ToList();
        var approved=ConfirmDuplicates(candidates,false);
        if (approved==null) return [];
        foreach (var row in approved)
        {
            // A modal duplicate prompt may have let another UI action add it.
            if (_queue.Any(existing=>existing.Url==row.Url)) continue;
            row.DeferredToNextStart=_session?.IsRunning==true;
            _queue.Add(row);
            _previews.Request(row.Url);
        }
        Reindex();
        if (_checkpointReadOnly)
            throw new IOException("Не удалось сохранить очередь. Материалы подписки остаются доступными для повторного добавления.");
        var accepted=requested.Where(url=>_queue.Any(row=>row.Url==url && row.CanResume && row.Status!="Completed")).ToArray();
        T("ClipboardHint").Text=$"Из подписок принято {accepted.Length}. Загрузка начнётся по кнопке Start.";
        return accepted;
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
