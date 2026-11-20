using System.IO;
using System.Diagnostics;
using System.Text.Json;
using VideoDownloader.Native;

// Real managed children, independently held after staging. These tests observe
// process admission, publication and joins rather than a simulated slot counter.
internal static class OutOfOrderDownloadTests
{
    internal const string Prefix="https://out-of-order.invalid/";
    internal const string ControlVariable="VD_OUT_OF_ORDER_CONTROL";

    internal static bool TryRunFixture(string[] args)
    {
        string? url=args.LastOrDefault();
        if(url?.StartsWith(Prefix,StringComparison.Ordinal)!=true)return false;
        string name=url[Prefix.Length..];
        Require(name.Length>0 && name.All(c=>char.IsAsciiLetterOrDigit(c) || c=='-'),"Unexpected fixture URL");
        if(args.Contains("--dump-single-json",StringComparer.Ordinal))
        {Console.WriteLine(JsonSerializer.Serialize(new{title="Out of order "+name,duration=2}));return true;}
        string control=Environment.GetEnvironmentVariable(ControlVariable)
            ??throw new InvalidOperationException("Out-of-order fixture needs its control directory.");
        Directory.CreateDirectory(control);
        string output=args[Array.IndexOf(args,"-o")+1];
        string media=Path.Combine(Path.GetDirectoryName(output)!,name+".mp4");
        Directory.CreateDirectory(Path.GetDirectoryName(media)!);
        File.WriteAllText(media,"OWNED OUT-OF-ORDER MEDIA "+name);
        Console.WriteLine("__VD_TITLE__:Out of order "+name);Console.Out.Flush();
        File.WriteAllText(Path.Combine(control,name+"."+Environment.ProcessId+".starts"),"started");
        while(!File.Exists(Path.Combine(control,name+".release")))Thread.Sleep(20);
        Console.WriteLine("__VD_FILE__:"+media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-out-of-order-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous=Environment.GetEnvironmentVariable(ControlVariable);
        try
        {
            await BypassAndRetirement(root);
            await PauseCancellationAndSkip(root);
            await PreparedAdmissionGuards(root);
            await StopJoinsTemporaryOwner(root);
            Console.WriteLine("PASS: out-of-order downloads add independent fifth/sixth real workers, claim queued generations once, retire without draining, preserve pause/skip/cancel and join before disposal");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ControlVariable,previous);
            Directory.Delete(root,true);
        }
    }

    private static async Task BypassAndRetirement(string root)
    {
        string directory=Case(root,"bypass");
        var data=new AppPersistence(Path.Combine(directory,"profile"));
        var options=Options(directory,"4");
        var regular=Enumerable.Range(1,4).Select(n=>Row("regular-"+n)).ToArray();
        var waiting=Row("ordinary-waiting");var first=Row("urgent-first");var second=Row("urgent-second");
        var all=regular.Concat(new[]{waiting,first,second}).ToArray();
        await using var engine=Engine(data,options);
        foreach(var row in all)Require(engine.Add(row),"Every original channel attempt must be admitted.");
        Require(!engine.TryStartOutOfOrder(first),"An unstarted coordinator must not launch an unowned temporary child.");
        Task run=engine.RunAsync();
        try
        {
            await Until(()=>regular.All(row=>Started(directory,row)),"All four ordinary processes must hold their slots.");
            Require(!Started(directory,waiting) && !Started(directory,first) && !Started(directory,second),
                "The ordinary waiter and urgent candidates must initially remain behind all four held workers.");
            Require(engine.TryStartOutOfOrder(first) && engine.TryStartOutOfOrder(second),
                "Each explicit candidate must claim its own additional slot.");
            Require(!engine.TryStartOutOfOrder(first) && !engine.CanStartOutOfOrder(first) &&
                !engine.TryStartOutOfOrder(new DownloadItem{Url=first.Url}),
                "Repeated clicks and another row with the same exact URL must not create a second generation.");
            await Until(()=>Started(directory,first) && Started(directory,second),
                "Both extra processes must start while all four original processes remain held.");
            Require(all.Count(row=>row.Status=="Running")==6 && first.Slot=="T5" && second.Slot=="T6" &&
                options.Threads=="4" && engine.Outstanding==7,
                "Four ordinary plus two temporary real workers must coexist without changing the configured limit.");
            Release(directory,first,second);
            await Until(()=>first.AttemptFinished && second.AttemptFinished,"Both urgent owners must finish publication.");
            await Task.Delay(200);
            Require(first.Status=="Completed" && second.Status=="Completed" && !Started(directory,waiting) &&
                regular.All(row=>row.Status=="Running") && !run.IsCompleted && engine.Outstanding==5,
                "A completed temporary slot must retire instead of consuming the next ordinary queued entry.");
            Release(directory,regular[0]);
            await Until(()=>Started(directory,waiting),"The ordinary waiter must start only after an ordinary slot is freed.");
            Require(waiting.Slot is "T1" or "T2" or "T3" or "T4",
                "An ordinary worker, never a retired urgent worker, must own the waiting entry.");
            Release(directory,all);
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Require(all.All(row=>row.Status=="Completed" && row.AttemptFinished && File.Exists(row.Path)) &&
                engine.Completed==7 && engine.Outstanding==0 && data.ReadHistory().Count==7,
                "Every exact URL must publish through the shared result owner and append history once.");
            Require(all.All(row=>StartsPaths(directory,row).Length==1) &&
                data.ReadHistoryLibrary().Entries.Count==7,
                "The stale original channel tokens for promoted attempts must never launch duplicate processes or history records.");
            var late=Row("too-late");
            Require(!engine.CanStartOutOfOrder(late) && !engine.TryStartOutOfOrder(late) && late.Status=="Queued",
                "A finished coordinator must leave late explicit work queued rather than launch outside its lifetime.");
        }
        finally {engine.Stop();Release(directory,all);await run.WaitAsync(TimeSpan.FromSeconds(15));}
    }

    private static async Task PauseCancellationAndSkip(string root)
    {
        string directory=Case(root,"pause-cancel");
        var data=new AppPersistence(Path.Combine(directory,"profile"));var options=Options(directory,"1");
        var regular=Row("pause-regular");var waiting=Row("pause-waiting");
        var urgent=Row("pause-urgent");var skipped=Row("skip-urgent");
        await using var engine=Engine(data,options);
        Require(engine.Add(regular) && engine.Add(waiting),"Regular pause fixtures must be queued.");
        Task run=engine.RunAsync();
        try
        {
            await Until(()=>Started(directory,regular),"The ordinary pause fixture must start.");
            if(OperatingSystem.IsWindows())engine.Pause(true);
            Require(await StartFresh(engine,urgent),"A fresh deferred task may enter an explicit temporary slot.");
            if(OperatingSystem.IsWindows())
            {
                await Until(()=>urgent.Status=="Running","The paused urgent owner must be claimed.");
                await Task.Delay(150);
                Require(!Started(directory,urgent),"An urgent child must obey the existing global pause before process admission.");
                engine.Pause(false);
            }
            await Until(()=>Started(directory,urgent),"The temporary process must start after resume.");
            engine.Cancel(urgent);
            await Until(()=>urgent.AttemptFinished,"Cancellation must join the temporary process and cleanup owner.");
            Require(urgent.Status=="Interrupted" && urgent.StageDir.Length==0 && urgent.OwnedPaths.Count==0 &&
                !data.ReadHistory().ContainsKey(urgent.Url) && !Started(directory,waiting),
                "Urgent cancellation must clean only its own data and must not free a permanent extra queue reader.");
            Require(await StartFresh(engine,skipped),"A second fresh urgent request must be independently owned.");
            await Until(()=>Started(directory,skipped),"The urgent skip fixture must first stage its own file.");
            Require(await engine.SkipAsync(skipped).WaitAsync(TimeSpan.FromSeconds(10)) && skipped.Status=="Skipped" &&
                skipped.AttemptFinished && skipped.ResumeState is not null && !data.ReadHistory().ContainsKey(skipped.Url) && !Started(directory,waiting),
                "Skipping a temporary task must use the existing generation, retain/cleanup contract and history rules.");
            Require(!engine.TryStartOutOfOrder(skipped),"Skipped retained work requires the explicit Resume path and consent.");
            Require(engine.Resume(skipped) && engine.CanStartOutOfOrder(skipped) && engine.TryStartOutOfOrder(skipped),
                "An already admitted resumed generation may be promoted without discarding its pinned plan or asking consent twice.");
            await Until(()=>StartsPaths(directory,skipped).Length==2 && skipped.Status=="Running",
                "The resumed retained generation must run in its own newly requested temporary slot.");
            Release(directory,skipped);
            await Until(()=>skipped.AttemptFinished,"The promoted resumed owner must finish once.");
            Require(skipped.Status=="Completed" && skipped.ResumeState is null && !Started(directory,waiting),
                "Resumed urgent publication must retire retained staging while the normal waiter still waits.");
            Release(directory,regular,waiting);
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            if(skipped.ResumeState is not null)DownloadResumeStore.DeleteRetained(skipped);
            Require(regular.Status=="Completed" && waiting.Status=="Completed" && data.ReadHistory().Count==3,
                "Cancelled/skipped temporary work must not alter ordinary publication or successful history.");
        }
        finally
        {
            engine.Stop();Release(directory,regular,waiting,urgent,skipped);
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            if(skipped.ResumeState is not null)DownloadResumeStore.DeleteRetained(skipped);
        }
    }

    private static async Task PreparedAdmissionGuards(string root)
    {
        string directory=Case(root,"prepared-admission");
        var data=new AppPersistence(Path.Combine(directory,"profile"));var options=Options(directory,"1");
        var regular=Row("prepared-regular");var held=Row("prepared-held");var late=Row("prepared-late");
        using var preparing=new ManualResetEventSlim();using var allowPreparation=new ManualResetEventSlim();
        int prepareTimeout=0;
        string heldIdentity=DownloadTraceIdentity.DiagnosticIdentity(held.Url);
        await using var engine=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{},record=>
        {
            if(record.Event!="out_of_order_prepare" || record.Identity!=heldIdentity)return;
            preparing.Set();
            if(!allowPreparation.Wait(TimeSpan.FromSeconds(10)))Interlocked.Exchange(ref prepareTimeout,1);
        });
        Require(engine.Add(regular),"The prepared-admission fixture must keep its ordinary session alive.");
        Task run=engine.RunAsync();Task<DownloadCoordinator.OutOfOrderPreparation?>? preparingTask=null;
        string changedPath=Path.Combine(options.OutputDirectory,"changed-destination");
        try
        {
            await Until(()=>Started(directory,regular),"The ordinary preparation fixture must start.");
            var changed=Row("prepared-changed");changed.SubscriptionName="captured-destination";
            var prepared=await engine.PrepareOutOfOrderAsync(changed);
            Require(prepared is not null && !engine.TryStartOutOfOrder(changed) && !engine.IsPending(changed) && engine.Outstanding==1,
                "Background preparation alone must not admit a row or reserve a slot; a fresh final claim needs its proof.");
            changed.SubscriptionName="different-destination";
            Require(!engine.TryStartOutOfOrder(changed,prepared),"Changing the captured destination must invalidate the prepared proof.");
            changed.SubscriptionName="captured-destination";
            Require(!engine.TryStartOutOfOrder(new DownloadItem{Url=changed.Url,SubscriptionName=changed.SubscriptionName},prepared),
                "A proof must never admit another row with the same exact URL.");
            engine.Cancel(changed);
            Require(!engine.TryStartOutOfOrder(changed,prepared),"A cancellation tombstone must invalidate a prepared fresh row.");

            var cancelled=Row("prepared-cancelled");using var cancellation=new CancellationTokenSource();
            var cancelledPreparation=await engine.PrepareOutOfOrderAsync(cancelled,cancellation.Token);
            cancellation.Cancel();
            Require(cancelledPreparation is not null && !engine.TryStartOutOfOrder(cancelled,cancelledPreparation) &&
                await engine.PrepareOutOfOrderAsync(cancelled,cancellation.Token) is null,
                "Cancellation before or after preparation must leave the fresh row queued and unowned.");

            var generation=Row("prepared-generation");var generationPreparation=await engine.PrepareOutOfOrderAsync(generation);
            Require(generationPreparation is not null && engine.Add(generation) && !engine.TryStartOutOfOrder(generation,generationPreparation),
                "A fresh proof must not silently promote a generation admitted after the proof was captured.");
            engine.Cancel(generation);
            await Until(()=>generation.AttemptFinished,"The intervening queued generation must finish its own cancellation owner.");

            var destinationChanged=Row("prepared-path-change");destinationChanged.SubscriptionName="changed-destination";
            var pathPreparation=await engine.PrepareOutOfOrderAsync(destinationChanged);
            Directory.CreateDirectory(options.OutputDirectory);File.WriteAllText(changedPath,"FOREIGN FILE AFTER PREPARATION");
            Require(pathPreparation is not null && engine.TryStartOutOfOrder(destinationChanged,pathPreparation),
                "The final claim must use the captured proof without repeating a filesystem read on its caller thread.");
            await Until(()=>destinationChanged.AttemptFinished,"The worker must independently validate its destination before launching a process.");
            Require(destinationChanged.Status=="Failed" && !Started(directory,destinationChanged) &&
                File.ReadAllText(changedPath)=="FOREIGN FILE AFTER PREPARATION" && data.ReadHistory().Count==0,
                "A destination changed after preparation must fail closed in the worker without modifying the foreign file or history.");

            var latePreparation=await engine.PrepareOutOfOrderAsync(late);
            preparingTask=engine.PrepareOutOfOrderAsync(held);
            Require(preparing.Wait(TimeSpan.FromSeconds(5)) && !preparingTask.IsCompleted && engine.Outstanding==1 && !engine.IsPending(held),
                "Potentially slow ancestor checks must execute asynchronously before admission, outside the coordinator's ownership gate.");
            await Task.Run(engine.Stop).WaitAsync(TimeSpan.FromSeconds(2));
            Require(latePreparation is not null && !engine.TryStartOutOfOrder(late,latePreparation) && !engine.IsPending(late),
                "Stop must close admission immediately even while preparation is held; a previously ready proof must not launch late work.");
            allowPreparation.Set();
            Require(await preparingTask.WaitAsync(TimeSpan.FromSeconds(5)) is null && Volatile.Read(ref prepareTimeout)==0,
                "A preparation completing after Stop must be discarded without an attempt or process.");
            await run.WaitAsync(TimeSpan.FromSeconds(15));
            Require(regular.Status=="Interrupted" && !Started(directory,held) && !Started(directory,late) &&
                engine.Outstanding==0 && data.ReadHistory().Count==0 && File.Exists(changedPath),
                "Late preparation must preserve ordinary cancellation, zero successful history and foreign destination data.");
        }
        finally
        {
            allowPreparation.Set();engine.Stop();Release(directory,regular,held,late);
            if(preparingTask is not null)await preparingTask.WaitAsync(TimeSpan.FromSeconds(5));
            await run.WaitAsync(TimeSpan.FromSeconds(15));
        }
    }

    private static async Task StopJoinsTemporaryOwner(string root)
    {
        string directory=Case(root,"stop-join");
        var data=new AppPersistence(Path.Combine(directory,"profile"));
        var regular=Row("stop-regular");var urgent=Row("stop-urgent");var late=Row("stop-late");
        using var exiting=new ManualResetEventSlim();using var allowExit=new ManualResetEventSlim();
        int gateTimeout=0;
        string identity=DownloadTraceIdentity.DiagnosticIdentity(urgent.Url);
        var engine=new DownloadCoordinator(Executable(),data,Options(directory,"1"),_=>{},_=>{},record=>
        {
            if(record.Event!="process_exit" || record.Identity!=identity)return;
            exiting.Set();
            if(!allowExit.Wait(TimeSpan.FromSeconds(10)))Interlocked.Exchange(ref gateTimeout,1);
        });
        Require(engine.Add(regular) && engine.Add(urgent),"Stop fixtures must be admitted before Run.");
        Task run=engine.RunAsync();
        Task? disposing=null;
        try
        {
            await Until(()=>Started(directory,regular),"The regular stop fixture must start.");
            Require(engine.TryStartOutOfOrder(urgent),"The stop fixture must promote its pending generation.");
            await Until(()=>Started(directory,urgent),"The urgent stop fixture must start its real child.");
            int regularPid=ReadPid(directory,regular),urgentPid=ReadPid(directory,urgent);
            engine.Stop();
            Require(exiting.Wait(TimeSpan.FromSeconds(5)),"The temporary process exit must reach the controlled owner join.");
            Require(engine.IsRunning && !run.IsCompleted && !engine.CanStartOutOfOrder(late) && !engine.TryStartOutOfOrder(late),
                "Stop must close admissions while the coordinator is still alive joining its temporary owner.");
            disposing=engine.DisposeAsync().AsTask();
            Require(!disposing.IsCompleted && Volatile.Read(ref gateTimeout)==0,
                "Dispose must wait for the real temporary result owner, not just its already-exited child.");
            allowExit.Set();await disposing.WaitAsync(TimeSpan.FromSeconds(15));
            Require(run.IsCompletedSuccessfully && !engine.IsRunning && regular.AttemptFinished && urgent.AttemptFinished &&
                !Alive(regularPid) && !Alive(urgentPid) && data.ReadHistory().Count==0 &&
                regular.StageDir.Length==0 && urgent.StageDir.Length==0 && Volatile.Read(ref gateTimeout)==0,
                "Disposal must join every child, pipe and cleanup owner before completing, without cancelled success/history.");
        }
        finally
        {
            allowExit.Set();Release(directory,regular,urgent);
            if(disposing is null)await engine.DisposeAsync();
            else await disposing;
        }
    }

    private static string Case(string root,string name)
    {
        string path=Path.Combine(root,name);Directory.CreateDirectory(path);
        Environment.SetEnvironmentVariable(ControlVariable,path);return path;
    }
    private static DownloadOptions Options(string directory,string threads)=>new(){Output=Path.Combine(directory,"output"),
        Threads=threads,EmbedMetadata=false,EmbedChapters=false};
    private static DownloadItem Row(string name)=>new(){Url=Prefix+name};
    private static DownloadCoordinator Engine(AppPersistence data,DownloadOptions options)=>new(Executable(),data,options,_=>{},_=>{});
    private static async Task<bool> StartFresh(DownloadCoordinator engine,DownloadItem row)
    {
        var preparation=await engine.PrepareOutOfOrderAsync(row);
        return preparation is not null && engine.TryStartOutOfOrder(row,preparation);
    }
    private static string[] StartsPaths(string directory,DownloadItem row)=>Directory.GetFiles(directory,row.Url[Prefix.Length..]+".*.starts");
    private static bool Started(string directory,DownloadItem row)=>StartsPaths(directory,row).Length>0;
    private static void Release(string directory,params DownloadItem[] rows)
    {foreach(var row in rows)File.WriteAllText(Path.Combine(directory,row.Url[Prefix.Length..]+".release"),"release");}
    private static int ReadPid(string directory,DownloadItem row)
    {
        string name=Path.GetFileNameWithoutExtension(StartsPaths(directory,row).Single());
        return int.Parse(name[(name.LastIndexOf('.')+1)..]);
    }
    private static bool Alive(int pid)
    {try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static async Task Until(Func<bool> condition,string message)
    {
        var elapsed=Stopwatch.StartNew();
        while(!condition() && elapsed.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(20);
        Require(condition(),message);
    }
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,
        typeof(OutOfOrderDownloadTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Out-of-order download test: "+message);}
}
