namespace VideoDownloader.Native;

public sealed partial class DownloadCoordinator
{
    // An admission proof is bound to this exact fresh row and its captured
    // destination. Preparing it neither registers an attempt nor owns a slot.
    internal sealed class OutOfOrderPreparation
    {
        private readonly DownloadCoordinator _owner;
        private readonly DownloadItem _row;
        private readonly string _url,_subscriptionName,_runDir;
        private readonly DownloadSelection? _selection;
        private readonly CancellationToken _cancellation;
        internal string OutputRoot {get;}

        internal OutOfOrderPreparation(DownloadCoordinator owner,DownloadItem row,string outputRoot,CancellationToken cancellation)
        {
            _owner=owner;_row=row;_url=row.Url;_selection=row.Selection;
            _subscriptionName=row.SubscriptionName;_runDir=row.RunDir;
            OutputRoot=outputRoot;_cancellation=cancellation;
        }
        internal bool Matches(DownloadCoordinator owner,DownloadItem row)=>!_cancellation.IsCancellationRequested &&
            ReferenceEquals(_owner,owner) && ReferenceEquals(_row,row) &&
            string.Equals(_url,row.Url,StringComparison.Ordinal) && _selection==row.Selection &&
            string.Equals(_subscriptionName,row.SubscriptionName,StringComparison.Ordinal) &&
            string.Equals(_runDir,row.RunDir,StringComparison.Ordinal);
    }

    private bool CanStartOutOfOrderCore(DownloadItem item)
    {
        if(!_runStarted || !_acceptingWork || !IsRunning || _stop.IsCancellationRequested ||
            item.Status!="Queued" || !item.CanResume || item.CleanupPaths.Count>0 || _cancelled.ContainsKey(item.Url))return false;
        if(RequiresServer(item) || _remoteReady && _remote!.OwnsAssignment(item))return false;
        if(_attempts.TryGetValue(item.Url,out var current))
            return ReferenceEquals(current.Row,item) && !current.Started && !current.Finished.Task.IsCompleted;
        // A retained generation already admitted through Resume has its own
        // validated pinned plan and consent. Fresh urgent admission must not
        // bypass those checks for a not-yet-resumed staging directory.
        return item.ResumeState is null && item.StageDir.Length==0 && item.Path.Length==0 &&
            item.OwnedPaths.Count==0 && !_queued.ContainsKey(item.Url);
    }

    public bool CanStartOutOfOrder(DownloadItem item)
    {lock(_completionGate)return CanStartOutOfOrderCore(item);}

    internal Task<OutOfOrderPreparation?> PrepareOutOfOrderAsync(DownloadItem item,CancellationToken cancellation=default)
    {
        OutOfOrderPreparation preparation;
        CancellationToken stopped;
        lock(_completionGate)
        {
            if(cancellation.IsCancellationRequested || !CanStartOutOfOrderCore(item) || _attempts.ContainsKey(item.Url))
                return Task.FromResult<OutOfOrderPreparation?>(null);
            preparation=new(this,item,AdmissionOutputRoot(item),cancellation);
            stopped=_stop.Token;
        }
        return Task.Run<OutOfOrderPreparation?>(()=>
        {
            if(cancellation.IsCancellationRequested || stopped.IsCancellationRequested)return null;
            Trace(item,JournalLevel.Verbose,"out_of_order_prepare",worker:"");
            OwnedFiles.NoReparseAncestors(Path.Combine(preparation.OutputRoot,"__check__"));
            return cancellation.IsCancellationRequested || stopped.IsCancellationRequested?null:preparation;
        });
    }

    // A user request claims one attempt, not another permanent queue reader.
    // If that attempt was already queued, its channel entry becomes inert.
    // Fresh rows require a prepared path proof; this final claim does no IO.
    public bool TryStartOutOfOrder(DownloadItem item)=>TryStartOutOfOrder(item,null);
    internal bool TryStartOutOfOrder(DownloadItem item,OutOfOrderPreparation? preparation)
    {
        lock(_completionGate)
        {
            if(!CanStartOutOfOrderCore(item))return false;
            if(!_attempts.TryGetValue(item.Url,out var attempt))
            {
                if(preparation is null || !preparation.Matches(this,item) ||
                    !Admit(item,false,enqueue:false,preparedOutputRoot:preparation.OutputRoot))return false;
                attempt=_attempts[item.Url];
            }
            else if(preparation is not null)return false;
            if(!TryClaimAttempt(attempt))return false;
            string worker="T"+(++_nextOutOfOrderSlot);
            // Register while admission is locked. Run/Stop/Dispose close the
            // same gate before taking the set of workers they must join.
            _outOfOrderTasks.Add(Task.Run(()=>ExecuteAttemptAsync(attempt,worker),CancellationToken.None));
            Trace(item,JournalLevel.Information,"job_out_of_order","temporary_worker="+worker,worker:worker);
            return true;
        }
    }
}
