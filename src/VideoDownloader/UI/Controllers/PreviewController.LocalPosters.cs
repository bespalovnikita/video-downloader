using System.Windows.Threading;
using System.Runtime.CompilerServices;

namespace VideoDownloader.Native;

internal sealed partial class PreviewController
{
    // A startup pass retains at most 256 row references, while only sixteen
    // local requests may enter the shared worker. New completions are explicit
    // notifications rather than a retry timer or a scan of the whole queue.
    internal const int LocalPosterBackfillLimit=256;
    private const int LocalPosterAdmissionLimit=16;
    private readonly Func<bool> _allowLocalPosters;
    private readonly Queue<DownloadItem> _localBackfill=new();
    private readonly Dictionary<DownloadItem,LocalPosterJob> _localPosterJobs=new();
    private readonly Dictionary<DownloadItem,LocalPosterAttempt> _localPosterSeen=new();
    private readonly HashSet<DownloadItem> _localPosterSuppressed=new();
    private readonly ConditionalWeakTable<DownloadItem,PendingPoster> _localPosterOverflow=new();
    private sealed class PendingPoster { }
    private sealed record LocalPosterAttempt(LocalPosterInput Input,bool HadImage);
    private bool _hasLocalPosterOverflow;
    private bool _localPosterRefilling;
    private int _localPosterGeneration;

    private sealed class LocalPosterJob(DownloadItem row,LocalPosterInput input,int generation)
    {
        internal readonly DownloadItem Row=row;
        internal readonly LocalPosterInput Input=input;
        internal readonly int Generation=generation;
        internal readonly CancellationTokenSource Stop=new();
        internal Task Completion=Task.CompletedTask;
    }

    internal bool HasLocalPosterWork=>_localPosterJobs.Count>0 || _localBackfill.Count>0;

    // A deliberate dependency installation is an explicit retry boundary.
    // Optional failures otherwise stay quiet instead of looping on a timer.
    internal void ResetLocalPosterAttempts()=>_localPosterSeen.Clear();

    internal void BackfillLocalPosters()
    {
        if(_disposed || !_allowLocalPosters())return;
        foreach(var row in _rows().Distinct().Where(row=>LocalPosterInput.Capture(row) is not null)
            .Take(LocalPosterBackfillLimit))
            if(!_localBackfill.Contains(row))_localBackfill.Enqueue(row);
        RefillLocalPosters();
    }

    // Selecting a URL is independent of the bounded startup backfill. It may
    // refer to a completed row beyond that pass, including from subscriptions.
    // Retry a previously successful attempt if its image was evicted/expired;
    // repeatedly selecting an audio-only or damaged file must not spin a decoder.
    private void RequestSelectedLocalPoster(string url)
    {
        if(_disposed || !_allowLocalPosters() || _service.Peek(url) is {ThumbnailBase64.Length:>0})return;
        var row=_rows().Distinct().FirstOrDefault(row=>string.Equals(row.Url,url,StringComparison.Ordinal) &&
            LocalPosterInput.Capture(row) is not null);
        if(row is null)return;
        if(_localPosterSeen.TryGetValue(row,out var seen) && seen.HadImage && seen.Input.Matches(row))
            _localPosterSeen.Remove(row);
        ScheduleLocalPoster(row);
    }

    internal void ScheduleLocalPoster(DownloadItem row)
    {
        if(_disposed || !_allowLocalPosters() || _localPosterSuppressed.Contains(row))return;
        var input=LocalPosterInput.Capture(row);
        if(input is null)
        {
            if(_localPosterJobs.TryGetValue(row,out var stale))stale.Stop.Cancel();
            _localPosterSeen.Remove(row);
            _localPosterOverflow.Remove(row);
            return;
        }
        if(_localPosterJobs.TryGetValue(row,out var current))
        {
            if(!current.Input.Matches(row))
            {
                current.Stop.Cancel();
                if(_localBackfill.Count<LocalPosterBackfillLimit+32 && !_localBackfill.Contains(row))_localBackfill.Enqueue(row);
                else MarkLocalPosterPending(row);
            }
            return;
        }
        if(_localPosterSeen.TryGetValue(row,out var seen) && seen.Input.Matches(row)){_localPosterOverflow.Remove(row);return;}
        if(_localPosterJobs.Count>=LocalPosterAdmissionLimit)
        {
            // Thirty-two simultaneous download slots fit beside the bounded
            // startup pass without creating an unbounded second work queue.
            if(_localBackfill.Count<LocalPosterBackfillLimit+32 && !_localBackfill.Contains(row))_localBackfill.Enqueue(row);
            else MarkLocalPosterPending(row);
            return;
        }
        _localPosterOverflow.Remove(row);
        var job=new LocalPosterJob(row,input,_localPosterGeneration);
        _localPosterJobs.Add(row,job);
        job.Completion=RunLocalPosterAsync(job);
    }

    private void RefillLocalPosters()
    {
        if(_localPosterRefilling || _disposed || !_allowLocalPosters())return;
        _localPosterRefilling=true;
        try
        {
            while(_localPosterJobs.Count<LocalPosterAdmissionLimit)
            {
                if(_localBackfill.Count==0 && _hasLocalPosterOverflow)
                {
                    // Overflow keeps only a weak marker on an existing row,
                    // not another unbounded queue of file/input snapshots.
                    // Refill a small batch from current membership; failed
                    // attempts lose their marker on admission and never spin.
                    var next=_rows().Where(row=>!_localPosterSuppressed.Contains(row) && !_localPosterJobs.ContainsKey(row) &&
                        _localPosterOverflow.TryGetValue(row,out _)).Distinct().Take(LocalPosterAdmissionLimit).ToArray();
                    if(next.Length==0)_hasLocalPosterOverflow=false;
                    else foreach(var row in next)_localBackfill.Enqueue(row);
                }
                if(_localBackfill.Count==0)break;
                ScheduleLocalPoster(_localBackfill.Dequeue());
            }
        }
        finally{_localPosterRefilling=false;}
    }
    private void MarkLocalPosterPending(DownloadItem row)
    {
        _localPosterOverflow.GetValue(row,_=>new PendingPoster());
        _hasLocalPosterOverflow=true;
    }

    private bool CanPublishLocalPoster(LocalPosterJob job)=>!_disposed && !job.Stop.IsCancellationRequested &&
        job.Generation==_localPosterGeneration && _allowLocalPosters() && job.Input.Matches(job.Row) &&
        _rows().Any(row=>ReferenceEquals(row,job.Row));

    private async Task RunLocalPosterAsync(LocalPosterJob job)
    {
        Exception? failure=null;
        try
        {
            await _service.RequestLocalPosterAsync(job.Input,async(commit,token)=>
            {
                if(token.IsCancellationRequested || _window.Dispatcher.HasShutdownStarted || _window.Dispatcher.HasShutdownFinished)return false;
                try
                {
                    return await _window.Dispatcher.InvokeAsync(()=>
                    {
                        if(token.IsCancellationRequested || !CanPublishLocalPoster(job))return false;
                        commit();
                        if(_service.Peek(job.Input.Url) is {} image)ReceiveLocalPoster(image);
                        return true;
                    },DispatcherPriority.Background,token).Task.ConfigureAwait(false);
                }
                catch(OperationCanceledException){return false;}
            },job.Stop.Token).ConfigureAwait(false);
        }
        catch(OperationCanceledException) when(job.Stop.IsCancellationRequested || _disposed){}
        catch(Exception error){failure=error;}
        finally
        {
            void Finish()
            {
                try
                {
                    // Even constructor backfill may have started before WPF
                    // installed a SynchronizationContext. Always return to the
                    // owner dispatcher for collection access and subscribers.
                    var data=_service.Peek(job.Input.Url);
                    if(CanPublishLocalPoster(job) && data is {ThumbnailBase64.Length:>0})
                        ReceiveLocalPoster(data);
                    if(CanPublishLocalPoster(job))
                    {
                        if(_localPosterSeen.Count>=512)_localPosterSeen.Remove(_localPosterSeen.Keys.First());
                        _localPosterSeen[job.Row]=new(job.Input,data is {ThumbnailBase64.Length:>0});
                    }
                    if(failure is not null && !_disposed)_log("Не удалось подготовить локальное превью: "+failure.Message);
                }
                finally
                {
                    if(_localPosterJobs.TryGetValue(job.Row,out var current) && ReferenceEquals(current,job))_localPosterJobs.Remove(job.Row);
                    job.Stop.Dispose();
                    RefillLocalPosters();
                }
            }
            if(_window.Dispatcher.CheckAccess())Finish();
            else if(!_window.Dispatcher.HasShutdownStarted && !_window.Dispatcher.HasShutdownFinished)
                await _window.Dispatcher.InvokeAsync(Finish,DispatcherPriority.Background).Task.ConfigureAwait(false);
            else job.Stop.Dispose();
        }
    }

    private void ReceiveLocalPoster(PreviewData data)
    {
        if(_disposed)return;
        Notify(Changed,listener=>listener(data,false));
        if(string.Equals(SelectedUrl,data.Url,StringComparison.Ordinal))SetGallery(data);
    }

    private void CancelLocalPosters()
    {
        _localPosterGeneration++;
        if(!_disposed)
        {
            foreach(var row in _localBackfill)MarkLocalPosterPending(row);
            foreach(var job in _localPosterJobs.Values)MarkLocalPosterPending(job.Row);
        }
        else{_localPosterOverflow.Clear();_hasLocalPosterOverflow=false;}
        _localBackfill.Clear();
        foreach(var job in _localPosterJobs.Values.ToArray())job.Stop.Cancel();
    }

    internal async Task CancelLocalPostersAsync(IEnumerable<DownloadItem>? rows=null)
    {
        var selected=rows?.ToHashSet();
        LocalPosterJob[] jobs;
        if(selected is null)
        {
            jobs=_localPosterJobs.Values.ToArray();
            CancelLocalPosters();
        }
        else
        {
            foreach(var row in selected){_localPosterSuppressed.Add(row);MarkLocalPosterPending(row);}
            jobs=_localPosterJobs.Values.Where(job=>selected.Contains(job.Row)).ToArray();
            foreach(var job in jobs)job.Stop.Cancel();
            var retained=_localBackfill.Where(row=>!selected.Contains(row)).ToArray();
            _localBackfill.Clear();foreach(var row in retained)_localBackfill.Enqueue(row);
            foreach(var row in selected)_localPosterSeen.Remove(row);
        }
        try{await Task.WhenAll(jobs.Select(job=>job.Completion));}
        finally
        {
            if(selected is not null)
            {
                foreach(var row in selected)_localPosterSuppressed.Remove(row);
                // The caller still has to finish its mutation on this same
                // dispatcher turn before any later refill may reopen a file.
                _hasLocalPosterOverflow=true;
            }
        }
    }

    internal async Task StopAsync()
    {
        Dispose();
        await JoinPreviewOperationsAsync();
        await Task.WhenAll(_localPosterJobs.Values.Select(job=>job.Completion).ToArray());
        await _service.Completion;
    }
}
