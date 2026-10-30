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
            await TestElapsedClock(root);
            TestPortablePersistenceBounds(root,options);
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

    private static async Task TestElapsedClock(string root)
    {
        var time=new SessionTestTimeProvider();
        var owner=new QueueSessionController(time);
        var data=new AppPersistence(Path.Combine(root,"clock-profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"clock-output"),Threads="1"};
        await using var first=new DownloadCoordinator("must-not-launch",data,options,_=>{},_=>{});
        await using var second=new DownloadCoordinator("must-not-launch",data,options,_=>{},_=>{});
        var firstRow=new DownloadItem{Url="https://queue-owner.invalid/clock-first"};
        var pending=new DownloadItem{Url="https://queue-owner.invalid/clock-pending"};
        owner.TryAdd(firstRow);owner.TryAdd(pending);
        time.Advance(TimeSpan.FromHours(2));
        Require(owner.Elapsed==TimeSpan.Zero,"Idle metadata and queued URLs must not start a session clock");
        owner.Begin(first,options,[firstRow,pending]);
        time.Advance(TimeSpan.FromSeconds(4));
        firstRow.Status="Completed";pending.Status="Running";
        Require(owner.Elapsed==TimeSpan.FromSeconds(4) && owner.ReadMetrics().Running,
            "One completed row must not stop time while other session work remains");
        owner.Pause();time.Advance(TimeSpan.FromSeconds(3));owner.Pause();
        Require(owner.Elapsed==TimeSpan.FromSeconds(7),"A session keeps its existing elapsed-time semantics across pause/resume");
        first.Stop();await first.RunAsync();owner.End(first);
        var frozen=owner.Elapsed;
        time.Advance(TimeSpan.FromDays(1));
        Require(owner.Elapsed==frozen && frozen==TimeSpan.FromSeconds(7),
            "After workers and cancelled cleanup finish, idle time must never change the completed duration");
        owner.Begin(second,options,[]);
        Require(owner.Elapsed==TimeSpan.Zero,"A new Start/Retry must reset the elapsed clock");
        time.Advance(TimeSpan.FromSeconds(2));owner.End(first);time.Advance(TimeSpan.FromSeconds(1));
        Require(owner.Elapsed==TimeSpan.FromSeconds(3),"A stale previous session must not freeze its replacement's clock");
        second.Stop();await second.RunAsync();owner.End(second);
        time.Advance(TimeSpan.FromHours(1));
        Require(owner.Elapsed==TimeSpan.FromSeconds(3),"The replacement freezes only when its own workers finish");
        Console.WriteLine("PASS: deterministic session clock idle/start, multiple rows, pause, cancellation, frozen finish and replacement reset");
    }

    private sealed class SessionTestTimeProvider:TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency=>TimeSpan.TicksPerSecond;
        public override long GetTimestamp()=>_ticks;
        public override DateTimeOffset GetUtcNow()=>new DateTimeOffset(2026,10,30,0,0,0,TimeSpan.Zero).AddTicks(_ticks);
        internal void Advance(TimeSpan elapsed)=>_ticks+=elapsed.Ticks;
    }

    private static void TestPortablePersistenceBounds(string root,DownloadOptions options)
    {
        var data=new AppPersistence(Path.Combine(root,"portable-bounds"));
        var owner=new QueueSessionController();
        var existing=new DownloadItem{Url="https://queue-owner.invalid/existing",Status="Completed",Progress=100,Order=1};
        owner.TryAdd(existing);
        data.Save(owner.Queue,options);
        byte[] previous=File.ReadAllBytes(data.SessionPath);
        // Cyrillic is valid in a compact portable packet, but session JSON
        // escapes each character and can exceed the persisted 16 MiB limit.
        string title=new('Ж',3_000_000);
        var packet=PortableQueueCodec.Encode([new PortableQueueEntry(existing.Url,"Новое название"),new PortableQueueEntry("https://queue-owner.invalid/too-large",title)]);
        var entries=PortableQueueCodec.Decode(packet);
        Refuses(()=>owner.ImportPortable(entries,options),"A valid transfer must be rejected before exceeding the complete persisted checkpoint byte limit");
        Require(owner.Queue.Count==1 && ReferenceEquals(owner.Queue[0],existing) && existing.Title=="" &&
            existing.Status=="Completed" && existing.Progress==100 && File.ReadAllBytes(data.SessionPath).SequenceEqual(previous),
            "A rejected portable import must not partially enrich titles, append rows or overwrite the prior checkpoint");
        Refuses(()=>data.Save([new DownloadItem{Url="https://queue-owner.invalid/too-large",Title=title}],options),
            "Save must not overwrite a recoverable checkpoint with a snapshot Restore would reject");
        Refuses(()=>data.Save(Enumerable.Repeat(existing,AppPersistence.MaxQueueItems+1).ToArray(),options),
            "The saved queue count limit applies before serialization");
        Require(File.ReadAllBytes(data.SessionPath).SequenceEqual(previous) && data.Restore()!.Queue.Count==1,
            "Rejected byte and count boundaries leave the existing session readable and unchanged");
        Console.WriteLine("PASS: portable queue preflight bounds the full checkpoint before any title, queue or disk mutation");
    }

    private static void Refuses(Action action,string error)
    {
        try{action();}
        catch(InvalidDataException){return;}
        throw new InvalidOperationException(error);
    }

    private static void Require(bool condition,string error)
    {
        if(!condition)throw new InvalidOperationException("Queue/session controller test failed: "+error);
    }
}
