using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class SubscriptionScheduleTests
{
    private const string Known="https://scheduled.invalid/Video";
    private const string Fresh="https://scheduled.invalid/video";
    internal static async Task RunAsync()
    {
        Planner();
        string root=Path.Combine(Path.GetTempPath(),"vd-schedule-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            StoreCompatibility(Path.Combine(root,"compatibility"));
            await CompiledScanner(Path.Combine(root,"scanner"));
            Console.WriteLine("PASS: opt-in schedules, clock/backoff, guarded state and real discovery-only scanner cancellation");
        }
        finally{Directory.Delete(root,true);}
    }
    private static void Planner()
    {
        var clock=new Clock();var planner=new SubscriptionSchedulePlanner(clock);
        var a=Source("a","https://one.invalid/a");
        var b=Source("b","https://one.invalid/b");
        var c=Source("c","https://two.invalid/c");
        var off=Source("off","https://off.invalid/a");off.ScheduleEnabled=false;
        var initial=Source("initial","https://initial.invalid/a");initial.Initialized=false;
        var paused=Source("paused","https://paused.invalid/a");paused.Paused=true;
        SubscriptionDefinition[] sources=[a,b,c,off,initial,paused];
        planner.Synchronize(sources);
        Require(planner.TakeDue() is null && planner.NextCheckUtc("a")==clock.GetUtcNow().AddMinutes(5) &&
            planner.NextCheckUtc("off") is null && planner.NextCheckUtc("initial") is null && planner.NextCheckUtc("paused") is null,
            "A launch waits the full interval; opt-out, paused and uninitialized sources never run");
        clock.Advance(TimeSpan.FromDays(3));planner.Synchronize(sources);
        Require(planner.TakeDue()=="a" && planner.TakeDue()=="c" && planner.TakeDue() is null,
            "Sleep coalesces missed intervals once, and same-host sources have bounded spacing");
        planner.Complete("a",true,null);planner.Complete("c",true,null);
        Require(planner.TakeDue() is null,"No missed-interval catch-up storm follows completion");
        clock.Advance(TimeSpan.FromMinutes(1));Require(planner.TakeDue()=="b","A same-host source resumes after its spacing");
        planner.Complete("b",false,null);
        Require(planner.NextCheckUtc("b")==clock.GetUtcNow().AddMinutes(10),"A failed attempt doubles its source interval");
        clock.Advance(TimeSpan.FromMinutes(10));
        while(planner.TakeDue() is {} id)
        {
            planner.Complete(id,id!="b",null);
            if(id=="b")break;
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Require(planner.NextCheckUtc("b")==clock.GetUtcNow().AddMinutes(20),"Repeated failure backs off again");
        b.LastAttemptUtc=clock.GetUtcNow();planner.Synchronize(sources);
        Require(planner.NextCheckUtc("b")==clock.GetUtcNow().AddMinutes(5),"An explicit manual attempt resets source backoff");
        a.ScheduleIntervalMinutes=30;planner.Synchronize(sources);
        Require(planner.NextCheckUtc("a")==clock.GetUtcNow().AddMinutes(30),"Changing one interval starts that source's interval again");
        var priorC=planner.NextCheckUtc("c");a.ScheduleEnabled=false;planner.Synchronize(sources);
        Require(planner.NextCheckUtc("a") is null && planner.NextCheckUtc("c")==priorC,"Editing one subscription preserves unrelated deadlines");
        clock.Advance(-TimeSpan.FromDays(4));planner.Synchronize(sources);
        Require(planner.NextCheckUtc("b")==clock.GetUtcNow().AddMinutes(5),"A backward wall clock correction cannot freeze a schedule");
        planner.Reset();planner.Synchronize(sources);
        Require(planner.TakeDue() is null,"Maintenance or restart resumes after a full interval");
        var retryClock=new Clock();var retryPlanner=new SubscriptionSchedulePlanner(retryClock);
        var retry=Source("retry","https://retry.invalid/a");retry.ScheduleIntervalMinutes=60;
        retryPlanner.Synchronize([retry]);
        for(int n=0;n<20;n++)
        {
            retryClock.Advance(retryPlanner.NextCheckUtc(retry.Id)!.Value-retryClock.GetUtcNow());
            Require(retryPlanner.TakeDue()==retry.Id,"An overdue retry remains eligible");
            retryPlanner.Complete(retry.Id,false,null);
            Require(retryPlanner.NextCheckUtc(retry.Id)<=retryClock.GetUtcNow().AddDays(7),"Repeated failures never extend beyond one week");
        }
        retryClock.Advance(retryPlanner.NextCheckUtc(retry.Id)!.Value-retryClock.GetUtcNow());retryPlanner.TakeDue();
        retryPlanner.Complete(retry.Id,true,null);
        Require(retryPlanner.NextCheckUtc(retry.Id)==retryClock.GetUtcNow().AddHours(1),"A successful scan clears failure backoff");
        retryClock.Advance(TimeSpan.FromHours(1));retryPlanner.TakeDue();retryPlanner.Complete(retry.Id,false,null,cancelled:true);
        Require(retryPlanner.NextCheckUtc(retry.Id)==retryClock.GetUtcNow().AddHours(1),"User cancellation does not count as a failed retry");
    }
    private static void StoreCompatibility(string directory)
    {
        var store=Seed(directory);var source=store.Read().Subscriptions.Single();
        Require(!source.ScheduleEnabled && source.ScheduleIntervalMinutes==30,"Schedules are opt-in for new sources");
        var json=JsonNode.Parse(File.ReadAllText(store.Path))!;
        var definition=json["Subscriptions"]![0]!.AsObject();
        definition.Remove("ScheduleEnabled");definition.Remove("ScheduleIntervalMinutes");
        string legacy=json.ToJsonString();File.WriteAllText(store.Path,legacy);
        source=store.Read().Subscriptions.Single();
        Require(!source.ScheduleEnabled && source.ScheduleIntervalMinutes==30 && File.ReadAllText(store.Path)==legacy,
            "Old version-1 files read without rewrite or implied opt-in");
        foreach(JsonNode invalid in new JsonNode[]{JsonValue.Create("true")!,JsonValue.Create(1)!})
        {
            definition["ScheduleEnabled"]=invalid.DeepClone();string corrupt=json.ToJsonString();File.WriteAllText(store.Path,corrupt);
            Refuses(()=>store.Read());Refuses(()=>store.SetSchedule(source.Id,true,5));
            Require(File.ReadAllText(store.Path)==corrupt,"Corrupt optional schedule fields are read-only");
        }
        definition["ScheduleEnabled"]=false;definition["ScheduleIntervalMinutes"]=4;
        string bounds=json.ToJsonString();File.WriteAllText(store.Path,bounds);Refuses(()=>store.Read());
        Require(File.ReadAllText(store.Path)==bounds,"Invalid intervals cannot be repaired silently");
        File.WriteAllText(store.Path,legacy);store.SetSchedule(source.Id,true,5);
        var captured=store.Read().Subscriptions.Single();store.SetSchedule(source.Id,false,5);
        byte[] before=File.ReadAllBytes(store.Path);
        Refuses(()=>store.ApplyScheduledScan(captured,Scan(Known,Fresh),[],CancellationToken.None));
        store.RecordScheduledFailure(captured,"stale failure",CancellationToken.None);
        Require(before.SequenceEqual(File.ReadAllBytes(store.Path)),"Disabling while a scan runs prevents stale scan/error writes under the mutex");
        void RejectStale(Action change)
        {
            store.SetPaused(source.Id,false);store.SetSchedule(source.Id,true,5);
            var inFlight=store.Read().Subscriptions.Single();change();byte[] saved=File.ReadAllBytes(store.Path);
            Refuses(()=>store.ApplyScheduledScan(inFlight,Scan(Known,Fresh),[],CancellationToken.None));
            store.RecordScheduledFailure(inFlight,"stale failure",CancellationToken.None);
            Require(saved.SequenceEqual(File.ReadAllBytes(store.Path)),"Paused, reconfigured or removed sources reject stale success and failure writes");
        }
        RejectStale(()=>store.SetPaused(source.Id,true));
        RejectStale(()=>store.SetSchedule(source.Id,true,10));
        RejectStale(()=>store.Remove(source.Id));
        var pending=store.Add("https://subscription-scanner.invalid/not-initialized",false);
        Refuses(()=>store.SetSchedule(pending.Id,true,5));
        Require(!store.Read().Subscriptions.Single(x=>x.Id==pending.Id).Initialized,"An initial baseline always requires the manual choice");
    }
    private static async Task CompiledScanner(string directory)
    {
        Directory.CreateDirectory(directory);var store=Seed(directory);var source=store.Read().Subscriptions.Single();
        store.SetSchedule(source.Id,true,5);
        string config=Path.Combine(directory,"scan.json");
        void Fixture(string mode)=>File.WriteAllText(config,JsonSerializer.Serialize(new{Mode=mode,Response=JsonSerializer.Serialize(new
        {_type="playlist",title="Scheduled source",playlist_count=2,entries=new[]{new{webpage_url=Known,title="Known"},new{webpage_url=Fresh,title="Fresh"}}})}));
        var check=new ScheduledSubscriptionCheck(store,Executable(),()=>new(StringComparer.Ordinal));
        Fixture("normal");var first=await check.RunAsync(source.Id,config,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
        Require(first.NewItems==1 && first.Snapshot.Items.Single(x=>x.Url==Known).State=="Baseline" &&
            first.Snapshot.Items.Single(x=>x.Url==Fresh) is {State:"New",InitialQueuePending:false},
            "The actual compiled scanner discovers exact-URL new items without enqueue/start or initial archive acceptance");
        var repeated=await check.RunAsync(source.Id,config,CancellationToken.None);
        Require(repeated.NewItems==0 && repeated.Snapshot.Items.Single(x=>x.Url==Fresh).State=="New",
            "Unaccepted New does not create repeated new-material notifications");
        var lastSuccess=repeated.Source.LastSuccessUtc;
        Fixture("nonzero");await RefusesAsync(()=>check.RunAsync(source.Id,config,CancellationToken.None));
        var failed=store.Read();Require(failed.Subscriptions.Single().LastError.Length>0 &&
            failed.Subscriptions.Single().LastSuccessUtc==lastSuccess && failed.Items.Count==2,
            "An actual scanner failure records its attempt but preserves the last good snapshot");
        Fixture("hang");byte[] before=File.ReadAllBytes(store.Path);
        File.Delete(Path.Combine(directory,"parent.pid"));using var cancel=new CancellationTokenSource();
        Task pending=check.RunAsync(source.Id,config,cancel.Token);
        try
        {
            await WaitUntil(()=>File.Exists(Path.Combine(directory,"child.pid")),pending);
            int parent=ReadPid("parent.pid"),child=ReadPid("child.pid");
            cancel.Cancel();await RefusesAsync(()=>pending);
            await WaitUntil(()=>!Alive(parent) && !Alive(child),null);
            Require(before.SequenceEqual(File.ReadAllBytes(store.Path)),"Cancellation joins the native process tree and preserves all durable bytes");
        }
        finally{cancel.Cancel();await RefusesAsync(()=>pending);}
        Fixture("normal");var recovered=await check.RunAsync(source.Id,config,CancellationToken.None);
        Require(recovered.NewItems==0 && recovered.Source.LastError.Length==0,"A joined cancellation leaves the next check usable");
        int ReadPid(string name)=>int.Parse(File.ReadAllText(Path.Combine(directory,name)));
    }
    private static SubscriptionStore Seed(string directory)
    {
        var store=new SubscriptionStore(directory);var source=store.Add("https://subscription-scanner.invalid/scheduled",false);
        store.ApplyScan(source.Id,Scan(Known),[]);return store;
    }
    private static SubscriptionScan Scan(params string[] urls)=>new("Scheduled source",urls.Select(x=>new DiscoveredMedia(x,x)).ToArray(),"","");
    private static SubscriptionDefinition Source(string id,string url)=>new(){Id=id,Url=url,Initialized=true,ScheduleEnabled=true,ScheduleIntervalMinutes=5};
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,typeof(SubscriptionScheduleTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static async Task WaitUntil(Func<bool> condition,Task? pending)
    {
        var deadline=Stopwatch.StartNew();
        while(!condition())
        {
            if(pending?.IsCompleted==true){await pending;throw new InvalidOperationException("Native fixture ended before handshake");}
            if(deadline.Elapsed>TimeSpan.FromSeconds(15))throw new TimeoutException("Native schedule process handshake timed out");
            await Task.Delay(20);
        }
    }
    private static bool Alive(int pid)
    {
        try
        {
            if(OperatingSystem.IsLinux())
            {string stat=File.ReadAllText($"/proc/{pid}/stat");if(stat[stat.LastIndexOf(") ",StringComparison.Ordinal)+2]=='Z')return false;}
            using var process=Process.GetProcessById(pid);return !process.HasExited;
        }
        catch(ArgumentException){return false;}catch(IOException){return false;}
    }
    private static async Task RefusesAsync(Func<Task> action)
    {
        try{await action().WaitAsync(TimeSpan.FromSeconds(15));}
        catch(Exception error) when(error is not TimeoutException){return;}
        throw new InvalidOperationException("Expected a rejected or cancelled schedule check");
    }
    private static void Refuses(Action action){try{action();}catch(Exception){return;}throw new InvalidOperationException("Expected rejected schedule state");}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private sealed class Clock:TimeProvider
    {
        private DateTimeOffset _now=new(2030,1,1,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        internal void Advance(TimeSpan value)=>_now+=value;
    }
}
