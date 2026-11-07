namespace VideoDownloader.Native;

public sealed partial class DownloadCoordinator
{
    private enum AttemptStopReason{None,Skip,Cancel}
    private sealed class DownloadAttempt(DownloadItem row,CancellationToken stop)
    {
        internal readonly DownloadItem Row=row;
        internal readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        internal readonly TaskCompletionSource<bool> Finished=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AttemptStopReason Reason;
        internal bool Started;
        internal bool Publishing;
        internal bool Finalizing;
    }

    private void FinishQueuedAttempt(DownloadAttempt attempt,bool skipped)
    {
        // Caller owns completionGate. The old channel entry remains only as an
        // inert generation token; resuming this URL creates a different object.
        var row=attempt.Row;
        if(row.CleanupError.Length>0)DownloadCleanup.Failed(row,row.CleanupError);
        else{row.Status=skipped?"Skipped":"Interrupted";row.State=skipped?"Skipped":"Cancelled";}
        row.Speed="";row.ETA="";row.Slot="";row.AttemptFinished=true;
        Interlocked.Decrement(ref _outstanding);
        attempt.Finished.TrySetResult(skipped);attempt.Stop.Dispose();_changed(row);
    }

    public async Task<bool> SkipAsync(DownloadItem item)
    {
        Task<bool>? joined=null;
        lock(_completionGate)
        {
            if(!IsRunning || item.PendingRemoval || item.CleanupError.Length>0 || item.Status=="Completed")return false;
            if(!_attempts.TryGetValue(item.Url,out var attempt) || !ReferenceEquals(attempt.Row,item) || attempt.Finished.Task.IsCompleted)return false;
            // Publication owns a short critical section and wins once it has
            // started. A late click cannot turn a completed result into Skip.
            if(attempt.Publishing || attempt.Reason==AttemptStopReason.Cancel || attempt.Finalizing && attempt.Reason!=AttemptStopReason.Skip)return false;
            attempt.Reason=AttemptStopReason.Skip;
            joined=attempt.Finished.Task;
            attempt.Stop.Cancel();
            if(!attempt.Started)FinishQueuedAttempt(attempt,true);
            else{item.State="Skipping";_changed(item);}
        }
        return await joined.ConfigureAwait(false);
    }

    public bool Resume(DownloadItem item)=>Admit(item,true);

    // Explicit reset is the sole operation allowed to supersede a cancelled
    // URL in a live session. Ordinary Remove/re-add retains its old tombstone.
    internal bool Restart(DownloadItem item)
    {
        lock(_completionGate)
        {
            if(!_attempts.TryGetValue(item.Url,out var previous) || !ReferenceEquals(previous.Row,item) ||
                !previous.Finished.Task.IsCompleted || !item.AttemptFinished || item.Status!="Queued" ||
                item.PendingRemoval || item.CleanupError.Length>0 || item.ResumeState is not null ||
                item.StageDir.Length>0 || item.Path.Length>0 || item.OwnedPaths.Count>0 || item.CleanupPaths.Count>0)return false;
            bool cancelled=_cancelled.TryRemove(item.Url,out _);
            bool queued=_queued.TryRemove(item.Url,out _);
            try
            {
                if(Admit(item,false))return true;
            }
            catch
            {
                if(cancelled)_cancelled[item.Url]=0;
                if(queued)_queued[item.Url]=0;
                throw;
            }
            if(cancelled)_cancelled[item.Url]=0;
            if(queued)_queued[item.Url]=0;
            return false;
        }
    }

    public async Task<bool> CancelAndWaitAsync(DownloadItem item)
    {
        Task<bool>? joined;
        lock(_completionGate)
        {
            joined=_attempts.TryGetValue(item.Url,out var attempt) && ReferenceEquals(attempt.Row,item) && !attempt.Finished.Task.IsCompleted
                ?attempt.Finished.Task:null;
            if(joined is null)return false;
            Cancel(item);
        }
        await joined.ConfigureAwait(false);
        return true;
    }
}
