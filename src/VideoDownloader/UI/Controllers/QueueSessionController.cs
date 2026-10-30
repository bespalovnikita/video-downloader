using System.Collections.ObjectModel;
using System.Text.Json;

namespace VideoDownloader.Native;

// The UI dispatcher owns this state. The download coordinator still owns
// workers, publication and cancellation; this controller owns their queue view.
internal sealed class QueueSessionController
{
    private readonly HashSet<string> _urls=new(StringComparer.Ordinal);
    private readonly Dictionary<string,string> _finalStates=new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private int _lastActive;
    private long _startedTimestamp;
    private TimeSpan _elapsedAtStop;
    private bool _clockRunning;

    internal QueueSessionController(TimeProvider? timeProvider=null)=>_timeProvider=timeProvider??TimeProvider.System;

    internal ObservableCollection<DownloadItem> Queue { get; }=new();
    internal ObservableCollection<DownloadItem> Downloads { get; }=new();
    internal DownloadCoordinator? Coordinator { get; private set; }
    internal Task? RunningTask { get; private set; }
    internal DownloadOptions Options { get; private set; }=new();
    internal DateTime? StartedAt { get; private set; }
    internal TimeSpan Elapsed=>_elapsedAtStop+(_clockRunning
        ? _timeProvider.GetElapsedTime(_startedTimestamp) : TimeSpan.Zero);
    internal bool Stopping { get; private set; }
    internal IReadOnlyCollection<string> Urls=>_urls;

    internal void SetOptions(DownloadOptions options)=>Options=options;
    internal void AttachCoordinator(DownloadCoordinator? coordinator)=>Coordinator=coordinator;
    internal void TrackTask(Task task)=>RunningTask=task;
    internal void SetPresentation(DateTime? startedAt,IEnumerable<string> urls)
    {
        StartedAt=startedAt;
        _elapsedAtStop=startedAt.HasValue
            ? TimeSpan.FromTicks(Math.Max(0,(_timeProvider.GetLocalNow().DateTime-startedAt.Value).Ticks)) : TimeSpan.Zero;
        _startedTimestamp=_timeProvider.GetTimestamp();
        _clockRunning=startedAt.HasValue && Coordinator?.IsRunning==true;
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
    // Portable queues carry display data only. They never enter a live coordinator.
    internal IReadOnlyList<DownloadItem> ImportPortable(IReadOnlyList<PortableQueueEntry> entries,DownloadOptions settings)
    {
        var added=new List<DownloadItem>();
        var existing=Queue.ToDictionary(row=>row.Url,StringComparer.Ordinal);
        var titles=new Dictionary<DownloadItem,string>();
        var seen=new HashSet<string>(StringComparer.Ordinal);
        foreach(var entry in entries)
        {
            if(!seen.Add(entry.Url))continue;
            if(existing.TryGetValue(entry.Url,out var known))
            {
                if((string.IsNullOrWhiteSpace(known.Title) || string.Equals(known.Title,known.Url,StringComparison.Ordinal)) &&
                    !string.IsNullOrWhiteSpace(entry.Title))titles.Add(known,entry.Title);
                continue;
            }
            if(Queue.Count+added.Count>=AppPersistence.MaxQueueItems)
                throw new InvalidDataException("Очередь не помещается в сохранение: допустимо не более 100 000 ссылок. Перенеси её частями.");
            var row=new DownloadItem{Url=entry.Url,Title=entry.Title,DeferredToNextStart=true,Order=Queue.Count+added.Count+1};
            existing.Add(row.Url,row);added.Add(row);
        }
        // Imported JSON is much smaller than the full persisted DownloadItem.
        // Check the complete resulting checkpoint, including enriched titles,
        // before changing any live row or its observable queue.
        var proposed=new List<DownloadItem>(Queue.Count+added.Count);
        foreach(var row in Queue)
        {
            int order=proposed.Count+1;
            if(titles.TryGetValue(row,out string? title) || row.Order!=order)
            {
                var copy=JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.SerializeToUtf8Bytes(row,AppPersistence.Json),AppPersistence.Json)!;
                if(title!=null)copy.Title=title;
                copy.Order=order;proposed.Add(copy);
            }
            else proposed.Add(row);
        }
        proposed.AddRange(added);
        AppPersistence.ValidateCanSave(proposed,settings);
        foreach(var (row,title) in titles)row.Title=title;
        foreach(var row in added)Queue.Add(row);
        return added;
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
        Options=options;Coordinator=coordinator;StartedAt=_timeProvider.GetLocalNow().DateTime;Stopping=false;
        _elapsedAtStop=TimeSpan.Zero;_startedTimestamp=_timeProvider.GetTimestamp();_clockRunning=true;
        _finalStates.Clear();Downloads.Clear();ReplaceUrls(rows.Select(row=>row.Url));
        foreach(var row in rows)coordinator.Add(row);
    }
    internal void End(DownloadCoordinator expected)
    {
        if(!ReferenceEquals(Coordinator,expected))return;
        _elapsedAtStop=Elapsed;_clockRunning=false;
        Coordinator=null;
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
