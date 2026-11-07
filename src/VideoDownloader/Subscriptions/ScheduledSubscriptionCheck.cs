namespace VideoDownloader.Native;

internal sealed record ScheduledSubscriptionResult(SubscriptionSnapshot Snapshot,SubscriptionScan Scan,
    SubscriptionDefinition Source,int NewItems,DateTimeOffset? AttemptUtc);

internal sealed class ScheduledSubscriptionCheckException(Exception error,DateTimeOffset? attempt)
    : Exception(error.Message,error)
{
    internal DateTimeOffset? AttemptUtc {get;}=attempt;
}

/// <summary>A discovery-only transaction. It has no queue or download callback.</summary>
internal sealed class ScheduledSubscriptionCheck(SubscriptionStore store,string ytDlp,
    Func<Dictionary<string,DateTimeOffset?>> readHistory)
{
    internal async Task<ScheduledSubscriptionResult> RunAsync(string id,string cookieFile,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        SubscriptionDefinition? captured=null;
        try
        {
            var before=await Task.Run(store.Read,token).ConfigureAwait(false);
            captured=before.Subscriptions.FirstOrDefault(source=>source.Id==id);
            if(captured is null || !captured.Initialized || !captured.ScheduleEnabled || captured.Paused)
                throw new OperationCanceledException("Подписка больше не участвует в расписании.",token);
            var known=before.Items.Where(item=>item.SubscriptionId==id).Select(item=>item.Url).ToHashSet(StringComparer.Ordinal);
            using var scanner=new SubscriptionScanner(ytDlp);
            var scan=await scanner.ScanAsync(captured,cookieFile,token,knownUrls:known).ConfigureAwait(false);
            var after=await Task.Run(()=>store.ApplyScheduledScan(captured,scan,readHistory().Keys,token),token).ConfigureAwait(false);
            var current=after.Subscriptions.Single(source=>source.Id==id);
            int found=after.Items.Count(item=>item.SubscriptionId==id && item.State=="New" && !known.Contains(item.Url));
            return new(after,scan,current,found,current.LastAttemptUtc);
        }
        catch(OperationCanceledException){throw;}
        catch(Exception error)
        {
            token.ThrowIfCancellationRequested();
            DateTimeOffset? attempt=captured?.LastAttemptUtc;
            if(captured is not null)
            {
                try{attempt=await Task.Run(()=>store.RecordScheduledFailure(captured,error.Message,token),token).ConfigureAwait(false);}
                catch(OperationCanceledException){throw;}
                catch(Exception saveError){throw new ScheduledSubscriptionCheckException(new AggregateException(
                    "Ошибка проверки и сохранения её результата.",error,saveError),attempt);}
            }
            throw new ScheduledSubscriptionCheckException(error,attempt);
        }
    }
}
