using System.Collections.ObjectModel;

namespace VideoDownloader.Native;

// The UI dispatcher owns this state. The download coordinator still owns
// workers, publication and cancellation; this controller owns their queue view.
internal sealed class QueueSessionController
{
    private readonly HashSet<string> _urls=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _finalStates=new(StringComparer.Ordinal);
    private int _lastActive;

    internal ObservableCollection<DownloadItem> Queue { get; }=new();
    internal ObservableCollection<DownloadItem> Downloads { get; }=new();
    internal DownloadCoordinator? Coordinator { get; private set; }
    internal Task? RunningTask { get; private set; }
    internal DownloadOptions Options { get; private set; }=new();
    internal DateTime? StartedAt { get; private set; }
    internal bool Stopping { get; private set; }
    internal IReadOnlyCollection<string> Urls=>_urls;

    internal void SetOptions(DownloadOptions options)=>Options=options;
    internal void AttachCoordinator(DownloadCoordinator? coordinator)=>Coordinator=coordinator;
    internal void TrackTask(Task task)=>RunningTask=task;
    internal void SetPresentation(DateTime? startedAt,IEnumerable<string> urls)
    {
        StartedAt=startedAt;
        ReplaceUrls(urls);
    }
    internal void ReplaceUrls(IEnumerable<string> urls)
    {
        var snapshot=urls.ToArray();
        _urls.Clear();
        foreach(string url in snapshot)_urls.Add(url);
    }

    internal bool TryAdd(DownloadItem row)
    {
        if(Queue.Any(existing=>string.Equals(existing.Url,row.Url,StringComparison.Ordinal)))return false;
        Queue.Add(row);
        return true;
    }
    internal void Forget(DownloadItem row)
    {
        Queue.Remove(row);Downloads.Remove(row);_urls.Remove(row.Url);
    }
    internal void RejectQueueEntry(DownloadItem row)=>Queue.Remove(row);
    internal int Reindex()
    {
        int order=0;
        foreach(var row in Queue)row.Order=++order;
        return order;
    }
    internal bool Move(DownloadItem row,int index)
    {
        if(Coordinator?.IsRunning==true)return false;
        int previous=Queue.IndexOf(row);
        if(previous<0 || index<0 || index>=Queue.Count || previous==index)return false;
        Queue.Move(previous,index);
        return true;
    }
    internal bool TryJoinActive(DownloadCoordinator? expected,DownloadItem row)
    {
        if(expected!=null && ReferenceEquals(Coordinator,expected) && expected.IsRunning && expected.Add(row))
        {
            _urls.Add(row.Url);
            return true;
        }
        row.DeferredToNextStart=true;
        return false;
    }
    internal void Begin(DownloadCoordinator coordinator,DownloadOptions options,IReadOnlyList<DownloadItem> rows)
    {
        Options=options;Coordinator=coordinator;StartedAt=DateTime.Now;Stopping=false;
        _finalStates.Clear();Downloads.Clear();ReplaceUrls(rows.Select(row=>row.Url));
        foreach(var row in rows)coordinator.Add(row);
    }
    internal void End(DownloadCoordinator expected)
    {
        if(ReferenceEquals(Coordinator,expected))Coordinator=null;
    }
    internal void MarkStopping()=>Stopping=true;
    internal void Stop()
    {
        MarkStopping();Coordinator?.Stop();
    }
    internal void Pause()=>Coordinator?.Pause(!Coordinator.IsPaused);

    // Reports a final transition once; repeated worker notifications still
    // refresh the row without repeating a checkpoint or cleanup warning.
    internal bool RecordRowChanged(DownloadItem row)
    {
        if(!Queue.Contains(row))return false;
        if(!Downloads.Contains(row))Downloads.Add(row);
        if(row.Status is not ("Completed" or "Failed" or "Interrupted"))return false;
        string final=row.Status+":"+row.CleanupError;
        if(_finalStates.TryGetValue(row.Url,out string? before) && before==final)return false;
        _finalStates[row.Url]=final;
        return true;
    }
    internal DownloadItem[] CurrentRows()=>StartedAt.HasValue
        ? Queue.Where(row=>_urls.Contains(row.Url)).ToArray() : Queue.ToArray();
    internal QueueSessionMetrics ReadMetrics()
    {
        var rows=CurrentRows();
        int active=rows.Count(row=>row.Status=="Running");
        bool activeChanged=active!=_lastActive;_lastActive=active;
        bool running=Coordinator?.IsRunning==true;
        return new QueueSessionMetrics(active,rows.Count(row=>row.Status is not ("Completed" or "Failed")),
            rows.Count(row=>row.Status=="Completed"),rows.Count(row=>row.Status=="Failed"),rows.Length,
            rows.Where(row=>row.Status=="Running").Sum(row=>DownloadCoordinator.ParseSize(row.Speed)),
            rows.Sum(row=>row.TransferredBytes),rows.Length==0?0:rows.Sum(row=>
                row.Status is "Completed" or "Failed"?100:row.Progress)/(double)rows.Length,
            running,running && Coordinator!.IsPaused,activeChanged);
    }
}

internal sealed record QueueSessionMetrics(int Active,int Left,int Successful,int Failed,int Total,
    double Speed,long Transferred,double Progress,bool Running,bool Paused,bool ActiveChanged);
