using System.IO;
using VideoDownloader.Native;

internal static class QueueSessionControllerTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-queue-session-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var data=new AppPersistence(Path.Combine(root,"profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",Archive=true};
        var owner=new QueueSessionController();
        await using var first=new DownloadCoordinator("must-not-launch",data,options,_=>{},_=>{});
        await using var replacement=new DownloadCoordinator("must-not-launch",data,
            new DownloadOptions{Output=Path.Combine(root,"replacement")},_=>{},_=>{});
        try
        {
            var old=new DownloadItem{Url="https://queue-owner.invalid/old",Status="Completed"};
            var current=new DownloadItem{Url="https://queue-owner.invalid/Video"};
            var differentCase=new DownloadItem{Url="https://queue-owner.invalid/video"};
            Require(owner.TryAdd(old) && owner.TryAdd(current) && owner.TryAdd(differentCase) &&
                !owner.TryAdd(new DownloadItem{Url=current.Url}),"Queue admission must preserve exact URL identity and reject its duplicate");
            Require(owner.Move(differentCase,1) && owner.Reindex()==3 && differentCase.Order==2,
                "Idle queue moves must preserve a contiguous persisted order");
            owner.Begin(first,options,[current]);
            Require(ReferenceEquals(owner.Options,options) && options.OutputDirectory!=options.Output &&
                owner.StartedAt.HasValue && first.Outstanding==1 && owner.CurrentRows().SequenceEqual(new[]{current}),
                "Beginning a session must retain pinned options and exclude previous successes and deferred queue rows");
            Require(!owner.Move(differentCase,0),"An active coordinator must block queue reordering");
            current.Status="Running";current.Speed="1 MiB/s";current.Progress=50;current.TransferredBytes=123;
            var metrics=owner.ReadMetrics();
            Require(metrics.Active==1 && metrics.Left==1 && metrics.Successful==0 && metrics.Total==1 &&
                metrics.Speed==1024*1024 && metrics.Transferred==123 && metrics.Progress==50 && metrics.ActiveChanged,
                "Current-session metrics must come from its actual rows and count transferred bytes once");
            Require(!owner.ReadMetrics().ActiveChanged,"Unchanged worker counts must not request another grid refresh");
            Require(!owner.RecordRowChanged(current) && owner.Downloads.SequenceEqual(new[]{current}),
                "A running notification must expose one download row without reporting a final transition");
            current.Status="Failed";
            Require(owner.RecordRowChanged(current) && !owner.RecordRowChanged(current),
                "A repeated final callback must checkpoint and warn only once");
            current.CleanupError="Locked file";
            Require(owner.RecordRowChanged(current),"A changed cleanup failure must remain visible as a new final transition");

            var deferred=new DownloadItem{Url="https://queue-owner.invalid/deferred"};
            owner.AttachCoordinator(replacement);
            Require(!owner.TryJoinActive(first,deferred) && deferred.DeferredToNextStart && replacement.Outstanding==0,
                "An accepted URL after a modal session replacement must remain for the next Start");
            owner.End(first);
            Require(ReferenceEquals(owner.Coordinator,replacement),"Completion of the previous coordinator must not clear its replacement");
            owner.AttachCoordinator(first);
            var live=new DownloadItem{Url="https://queue-owner.invalid/live"};
            Require(owner.TryAdd(live) && owner.TryJoinActive(first,live) && first.Outstanding==2 &&
                owner.CurrentRows().Contains(live),"Live acceptance must enter the expected coordinator and its statistics subset together");
            owner.Pause();Require(owner.ReadMetrics().Paused,"Pause state must be read from the owned coordinator");
            owner.Pause();Require(!owner.ReadMetrics().Paused,"Resume must target the same owned coordinator");
            owner.Forget(current);
            Require(!owner.Queue.Contains(current) && !owner.Downloads.Contains(current) && !owner.Urls.Contains(current.Url),
                "Forgetting an acknowledged row must remove it from both views and session counts");
            Require(!owner.RecordRowChanged(current) && !owner.Downloads.Contains(current),
                "A late callback must never recreate a forgotten row");
            owner.RecordRowChanged(live);
            owner.RejectQueueEntry(live);
            Require(!owner.Queue.Contains(live) && owner.Downloads.Contains(live),
                "Duplicate-dialog rejection must preserve the separate download view");
            owner.Stop();
            var task=first.RunAsync();owner.TrackTask(task);await task;
            Require(owner.Stopping && ReferenceEquals(owner.RunningTask,task),"Stop must retain its final worker task for orderly window shutdown");
            owner.End(first);
            Require(owner.Coordinator==null && owner.CurrentRows().Length==0 && !File.Exists(data.HistoryPath),
                "Ending this session must retain its queue subset without counting old successes or recording cancelled downloads");
            Console.WriteLine("PASS: queue/session owner exact identity, live admission, metrics, final dedup, pause and cancelled shutdown");
        }
        finally
        {
            first.Stop();replacement.Stop();
            await first.RunAsync();await replacement.RunAsync();
            Directory.Delete(root,true);
        }
    }

    private static void Require(bool condition,string error)
    {
        if(!condition)throw new InvalidOperationException("Queue/session controller test failed: "+error);
    }
}
