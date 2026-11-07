using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

// These scenarios run the packaged extractor against a byte-range origin. A
// retained percentage alone is insufficient: the second transfer must actually
// request the remaining bytes and publish the exact original payload.
internal static class QueueSkipResumeTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-queue-skip-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await RunningSkipFreesSlotAndResumes(root);
            await RetainedAttemptSurvivesCheckpointAndRejectsForeignPaths(root);
            await RetainedStageLossRecovery(root);
            await QueuedSkipAndDestructiveCancellation(root);
            await PublicationSkipBoundary(root);
            await QueuedCleanupIsJoinedByDispose(root);
            Console.WriteLine("PASS: real per-item skip joins its worker, frees the next slot, resumes HTTP bytes in-place and after checkpoint, retains its pinned plan, rejects forged ownership and cleans destructive cancellation");
        }
        finally
        {
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }

    private static async Task RunningSkipFreesSlotAndResumes(string root)
    {
        await using var origin=new RangeOrigin();
        origin.Hold("primary.mp4");
        var data=new AppPersistence(Path.Combine(root,"live-profile"));
        var options=Options(Path.Combine(root,"live-output"));
        var first=new DownloadItem{Url=origin.Url("primary.mp4")};
        var next=new DownloadItem{Url=origin.Url("next.mp4")};
        var trace=new ConcurrentQueue<JournalRecord>();
        await using var engine=Engine(data,options,trace);
        Require(engine.Add(first) && engine.Add(next),"The two sequential workers must be admitted");
        Task run=engine.RunAsync();
        await WaitForPartial(first,"The real primary transfer must create a nonempty partial before skip");
        string stage=first.StageDir,partial=Partial(first)!;
        long retainedLength=new FileInfo(partial).Length;
        if(OperatingSystem.IsWindows())engine.Pause(true);
        Require(await engine.SkipAsync(first).WaitAsync(TimeSpan.FromSeconds(15)),
            "Skip must win before publication and join its real extractor");
        Require(first.Status=="Skipped" && first.AttemptFinished && first.StageDir==stage &&
            first.ResumeState is not null && File.Exists(partial) && new FileInfo(partial).Length>=retainedLength &&
            first.Path.Length==0 && first.OwnedPaths.Count==0 && !data.ReadHistory().ContainsKey(first.Url),
            "Skipped must retain owned partial bytes and its resume plan without publication/history: "+Describe(first));
        if(OperatingSystem.IsWindows())engine.Pause(false);
        await Until(()=>next.Status=="Completed",TimeSpan.FromSeconds(20),
            "The freed single worker must finish the next queued URL before the skipped URL is resumed: "+Describe(next));
        Require(data.ReadHistory().ContainsKey(next.Url) && first.Status=="Skipped",
            "Only the next URL may enter successful history while the primary remains skipped");
        int requestsBeforeResume=origin.Requests.Count;
        origin.Release("primary.mp4");
        Require(engine.Resume(first),"The active coordinator must accept the same exact skipped URL again");
        await run.WaitAsync(TimeSpan.FromSeconds(30));
        Require(first.Status=="Completed" && first.AttemptFinished && File.Exists(first.Path) &&
            File.ReadAllBytes(first.Path).SequenceEqual(origin.PrimaryBytes) && origin.Requests.Skip(requestsBeforeResume).Any(request=>request.Path=="primary.mp4" && request.Method=="GET" && request.First>0),
            "Resume must use a positive HTTP Range and publish the exact source bytes: "+Describe(first));
        Require(first.TransferredBytes==origin.PrimaryBytes.Length,
            $"Resume must not count its retained byte baseline twice: {first.TransferredBytes} instead of {origin.PrimaryBytes.Length}");
        string identity=DownloadTraceIdentity.DiagnosticIdentity(first.Url);
        var starts=trace.Where(record=>record.Identity==identity && record.Event=="attempt_start").ToArray();
        Require(starts.Length==2 && starts.All(record=>record.Invocation is >0 && record.Attempt==1) &&
            starts.Select(record=>record.Invocation).Distinct().Count()==2 && starts[1].Invocation>starts[0].Invocation,
            "Live resume must use a new monotonic diagnostic invocation even though its local retry attempt restarts at one");
        Require(trace.Where(record=>record.Identity==identity && record.Event=="job_resumed").Single() is {Invocation:null,Attempt:null},
            "Queued resume diagnostics must not inherit the previous invocation or retry attempt");
        Require(data.ReadHistory().ContainsKey(first.Url) && first.ResumeState is null && first.StageDir.Length==0 &&
            !File.Exists(stage+".resume.json"),"Successful completion must retire its retained stage proof and write history once");
        Require(!await engine.SkipAsync(first) && first.Status=="Completed" && File.Exists(first.Path) &&
            data.ReadHistory().ContainsKey(first.Url),"A late skip must not turn an already published success into a skipped task");
        Require(next.Status=="Completed" && File.ReadAllBytes(next.Path).SequenceEqual(origin.NextBytes),
            "Resuming the earlier URL must not change the next URL's publication");
    }

    private static async Task RetainedAttemptSurvivesCheckpointAndRejectsForeignPaths(string root)
    {
        await using var origin=new RangeOrigin();origin.Hold("restore.mp4");
        var data=new AppPersistence(Path.Combine(root,"restore-profile"));
        var options=Options(Path.Combine(root,"pinned-output"));
        options.FilenameTemplate="pinned-%(id)s.%(ext)s";
        var row=new DownloadItem{Url=origin.Url("restore.mp4")};
        string stage;
        await using(var first=Engine(data,options))
        {
            Require(first.Add(row),"The checkpoint fixture must be admitted");
            _=first.RunAsync();
            await WaitForPartial(row,"Checkpoint fixture must first transfer real bytes");
            Require(await first.SkipAsync(row).WaitAsync(TimeSpan.FromSeconds(15)),"Checkpoint skip must finish");
            stage=row.StageDir;data.Save([row],options);
        }
        Require(Directory.Exists(stage) && File.Exists(stage+".resume.json") && row.Status=="Skipped",
            "Disposing the old session must retain the skipped stage and owner proof");
        var restored=data.Restore()!.Queue.Single();
        Require(restored.Status=="Skipped" && restored.AttemptFinished && restored.ResumeState is not null && restored.StageDir==stage,
            "An explicit checkpoint restore must keep the skipped marker and resumable bytes");

        string outside=Path.Combine(root,"foreign-stage");Directory.CreateDirectory(outside);
        string foreignStage=Path.Combine(outside,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(foreignStage);
        string sentinel=Path.Combine(foreignStage,"never-delete.mp4");File.WriteAllText(sentinel,"FOREIGN FILE");
        var forged=Clone(restored);forged.RunDir=outside;forged.StageDir=foreignStage;
        await RefusesAdmission(data,options,forged,"A checkpoint stage path is not an ownership grant");
        Require(File.ReadAllText(sentinel)=="FOREIGN FILE" && Partial(restored) is not null,
            "A forged resume request must preserve foreign data and the real retained partial");

        string proof=stage+".resume.json",savedProof=File.ReadAllText(proof);
        var invalidProof=JsonNode.Parse(savedProof)!;invalidProof["Token"]=Guid.NewGuid().ToString("N");
        File.WriteAllText(proof,invalidProof.ToJsonString());
        try{await RefusesAdmission(data,options,Clone(restored),"A wrong retained-stage owner token must fail before a native process starts");}
        finally{File.WriteAllText(proof,savedProof);}

        if(OperatingSystem.IsWindows())
        {
            string junction=Path.Combine(stage,"foreign-junction");
            CreateJunction(junction,foreignStage);
            try{await RefusesAdmission(data,options,Clone(restored),"A junction anywhere in retained staging must reject resume");}
            finally{Directory.Delete(junction);}
            Require(File.ReadAllText(sentinel)=="FOREIGN FILE","Rejected staging junction must not change its target");
        }

        var changed=Options(Path.Combine(root,"changed-output"));
        changed.FilenameTemplate="changed-%(id)s.%(ext)s";changed.Quality="720";
        changed.Codec="H264";changed.RateLimit="1M";changed.WriteSubs=true;changed.SubtitleLangs="de.*";
        var resumedTrace=new ConcurrentQueue<JournalRecord>();
        int requestsBeforeResume=origin.Requests.Count;
        origin.Release("restore.mp4");
        await using(var resumed=Engine(data,changed,resumedTrace))
        {
            Require(resumed.Add(restored),"A new coordinator must accept the retained immutable resume plan");
            await resumed.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        Require(restored.Status=="Completed" && origin.Requests.Skip(requestsBeforeResume).Any(request=>request.Path=="restore.mp4" && request.Method=="GET" && request.First>0) &&
            File.ReadAllBytes(restored.Path).SequenceEqual(origin.PrimaryBytes),
            "Checkpoint continuation must resume actual bytes rather than silently restarting: "+Describe(restored));
        Require(restored.TransferredBytes==origin.PrimaryBytes.Length,
            $"Restored resume must retain its per-component baseline without double counting: {restored.TransferredBytes}");
        var resumedSettings=resumedTrace.Single(record=>record.Event=="attempt_start").Settings;
        Require(resumedSettings is not null && resumedSettings.Codec==options.Codec && resumedSettings.RateLimit==options.RateLimit &&
            resumedSettings.Subtitles==options.WriteSubs && resumedSettings.SubtitleLanguages==options.SubtitleLangs &&
            resumedSettings.Parallel==changed.Parallel && resumedSettings.Profile=="неизвестно",
            "Resumed diagnostics must capture the actual retained plan rather than changed global UI options or an invented profile name");
        Require(OwnedFiles.Inside(options.Output,restored.Path) && Path.GetFileName(restored.Path).StartsWith("pinned-",StringComparison.Ordinal) &&
            (!Directory.Exists(changed.Output) || !Directory.EnumerateFiles(changed.Output,"*.mp4",SearchOption.AllDirectories).Any()),
            "New global output/template/quality settings must not redirect a retained attempt");
        Require(resumedTrace.Any(record=>record.Event=="attempt_start" && record.Message.Contains("quality=\"best\"",StringComparison.Ordinal)) &&
            !resumedTrace.Any(record=>record.Event=="attempt_start" && record.Message.Contains("quality=\"720\"",StringComparison.Ordinal)),
            "The new native invocation must use the retained quality rather than the changed global bound");
        Require(!Directory.Exists(stage) && !File.Exists(proof) && restored.ResumeState is null && File.ReadAllText(sentinel)=="FOREIGN FILE",
            "A resumed publication retires only its own staging and leaves the external control untouched");
    }

    private static async Task QueuedSkipAndDestructiveCancellation(string root)
    {
        await using var origin=new RangeOrigin();
        var data=new AppPersistence(Path.Combine(root,"queued-profile"));
        var queued=new DownloadItem{Url=origin.Url("queued.mp4")};
        var next=new DownloadItem{Url=origin.Url("next.mp4")};
        await using(var engine=Engine(data,Options(Path.Combine(root,"queued-output"))))
        {
            Require(engine.Add(queued) && engine.Add(next),"Queued skip fixtures must be admitted");
            Require(await engine.SkipAsync(queued).WaitAsync(TimeSpan.FromSeconds(5)),"An unstarted item must be skipped without waiting for another worker");
            await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Require(queued.Status=="Skipped" && queued.AttemptFinished && next.Status=="Completed" && !origin.Requests.Any(r=>r.Path=="queued.mp4") &&
            queued.StageDir.Length==0 && !data.ReadHistory().ContainsKey(queued.Url),
            "A queued skip must never launch the extractor or mutate history; the worker must handle the next URL");

        var rapid=new DownloadItem{Url=origin.Url("rapid.mp4")};
        string rapidOutput=Path.Combine(root,"rapid-output");
        await using(var engine=Engine(data,Options(rapidOutput)))
        {
            Require(engine.Add(rapid) && await engine.SkipAsync(rapid) && engine.Resume(rapid),
                "A queued skip and explicit resume before a worker starts must create one current attempt");
            await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
        }
        Require(rapid.Status=="Completed" && rapid.OwnedPaths.Count==1 &&
            Directory.EnumerateFiles(rapidOutput,"*.mp4",SearchOption.AllDirectories).Count()==1 &&
            File.ReadLines(data.HistoryPath).Count(line=>line.Contains(rapid.Url,StringComparison.Ordinal))==1,
            "The old queued channel entry must not revive, publish twice, or append duplicate history after rapid skip/resume");

        origin.Hold("cancel.mp4");
        var cancelled=new DownloadItem{Url=origin.Url("cancel.mp4")};
        var resetTrace=new ConcurrentQueue<JournalRecord>();
        await using(var engine=Engine(data,Options(Path.Combine(root,"cancel-output")),resetTrace))
        {
            Require(engine.Add(cancelled),"Destructive cancellation fixture must be admitted");
            Task running=engine.RunAsync();await WaitForPartial(cancelled,"Cancellation fixture must own a real partial");
            string stage=cancelled.StageDir;
            Require(await engine.CancelAndWaitAsync(cancelled).WaitAsync(TimeSpan.FromSeconds(15)),"Destructive cancellation must join the current attempt");
            Require(cancelled.Status=="Interrupted" && cancelled.AttemptFinished && cancelled.ResumeState is null &&
                !Directory.Exists(stage) && !File.Exists(stage+".resume.json") && cancelled.CleanupPaths.Count==0 &&
                !data.ReadHistory().ContainsKey(cancelled.Url),"Cancellation must remove its partial and proof rather than retain resumable bytes");
            Require(!engine.Resume(cancelled),"A cancelled URL must not acquire the skipped resume grant");
            var replacement=new DownloadItem{Url=cancelled.Url,Status="Queued",AttemptFinished=true};
            Require(!engine.Add(replacement) && !engine.Restart(replacement),
                "An ordinary remove/re-add or a different same-URL object must not bypass the cancelled generation tombstone");
            DownloadCleanup.Retry(cancelled);
            cancelled.Status="Queued";cancelled.State="Queued";cancelled.Progress=0;cancelled.TransferredBytes=0;
            cancelled.Downloaded="";cancelled.Speed="";cancelled.ETA="";cancelled.RawError="";cancelled.RunDir="";
            int beforeReset=origin.Requests.Count;origin.Release("cancel.mp4");
            Require(engine.Restart(cancelled),"An explicit reset of the joined original row must admit a fresh live attempt");
            await running.WaitAsync(TimeSpan.FromSeconds(30));
            var freshRequests=origin.Requests.Skip(beforeReset).Where(request=>request.Path=="cancel.mp4" && request.Method=="GET").ToArray();
            Require(cancelled.Status=="Completed" && freshRequests.Length>0 && freshRequests.All(request=>request.First==0) &&
                File.ReadAllBytes(cancelled.Path).SequenceEqual(origin.PrimaryBytes) &&
                File.ReadLines(data.HistoryPath).Count(line=>line.Contains(cancelled.Url,StringComparison.Ordinal))==1,
                "Reset must make a byte-zero transfer and exactly one new publication/history record, with no stale cancelled generation");
            string identity=DownloadTraceIdentity.DiagnosticIdentity(cancelled.Url);
            var attempts=resetTrace.Where(record=>record.Identity==identity && record.Event=="attempt_start").ToArray();
            Require(attempts.Length==2 && attempts[0].Invocation is >0 && attempts[1].Invocation>attempts[0].Invocation &&
                resetTrace.Where(record=>record.Identity==identity && record.Event=="job_queued").All(record=>record.Invocation is null && record.Attempt is null),
                "Explicit reset must separate its diagnostic invocation and clear stale queued-event attempt keys");
        }
    }

    private static async Task QueuedCleanupIsJoinedByDispose(string root)
    {
        string run=Path.Combine(Path.GetTempPath(),"vd-native-"+Guid.NewGuid().ToString("N"));
        string stage=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
        var options=Options(Path.Combine(root,"dispose-output"));
        var row=new DownloadItem{Url="https://queued-cleanup.invalid/retained",Status="Skipped",State="Skipped",AttemptFinished=true,
            OutputRoot=options.Output,RunDir=run,StageDir=stage};
        File.WriteAllText(Path.Combine(stage,"owned.mp4.part"),"OWNED PARTIAL");
        DownloadResumeStore.Create(row,options,null,"best");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release=new ManualResetEventSlim();
        Task? disposal=null;
        var engine=new DownloadCoordinator(DependencyCenter.Executable("yt-dlp.exe"),new AppPersistence(Path.Combine(root,"dispose-profile")),options,
            changed=>
            {
                if(changed.AttemptFinished && changed.Status=="Interrupted")
                {entered.TrySetResult();release.Wait(TimeSpan.FromSeconds(10));}
            },_=>{});
        try
        {
            Require(engine.Add(row),"A retained unstarted attempt must be admitted before cancellation");
            engine.Cancel(row);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            disposal=engine.DisposeAsync().AsTask();
            Require(!disposal.IsCompleted && !Directory.Exists(stage) && !File.Exists(stage+".resume.json"),
                "Dispose must await the actual queued-cleanup task and final callback even after its attempt completion was signalled");
            release.Set();await disposal.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            release.Set();await (disposal??engine.DisposeAsync().AsTask());
            if(Directory.Exists(run))Directory.Delete(run,true);
        }
    }

    private static async Task PublicationSkipBoundary(string root)
    {
        await using var origin=new RangeOrigin();
        foreach(bool before in new[]{true,false})
        {
            string name=before?"before-publish":"after-publish";
            var row=new DownloadItem{Url=origin.Url(name+".mp4")};
            var data=new AppPersistence(Path.Combine(root,name+"-profile"));
            Task<bool>? decision=null;
            DownloadCoordinator? engine=null;
            await using(engine=new DownloadCoordinator(DependencyCenter.Executable("yt-dlp.exe"),data,
                Options(Path.Combine(root,name+"-output")),_=>{},_=>{},trace:record=>
                {
                    if(record.Event==(before?"publishing":"published"))decision??=engine!.SkipAsync(row);
                }))
            {
                Require(engine.Add(row),"Publication boundary fixture must be admitted");
                await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(25));
                Require(decision is not null,"The real coordinator must reach its publication boundary");
                bool skipped=await decision!.WaitAsync(TimeSpan.FromSeconds(5));
                if(before)
                {
                    Require(skipped && row.Status=="Skipped" && row.Path.Length==0 && row.OwnedPaths.Count==0 &&
                        !data.ReadHistory().ContainsKey(row.Url) && row.ResumeState is not null,
                        "Skip winning immediately before publication must retain its stage and never publish or record success");
                    DownloadCleanup.Retry(row);
                }
                else
                    Require(!skipped && row.Status=="Completed" && File.ReadAllBytes(row.Path).SequenceEqual(origin.PrimaryBytes) &&
                        File.ReadLines(data.HistoryPath).Count()==1,
                        "Publication winning before Skip must complete exactly once without losing its published bytes or history");
            }
        }
    }

    private static async Task RetainedStageLossRecovery(string root)
    {
        string run=Path.Combine(Path.GetTempPath(),"vd-native-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        var options=Options(Path.Combine(root,"missing-output"));
        DownloadItem Owned(string name)
        {
            string stage=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);
            var row=new DownloadItem{Url="https://retained-loss.invalid/"+name,Status="Skipped",State="Skipped",AttemptFinished=true,
                OutputRoot=options.Output,RunDir=run,StageDir=stage};
            File.WriteAllText(Path.Combine(stage,"source.mp4.part"),"OWNED PARTIAL");
            DownloadResumeStore.Create(row,options,null,"best");return row;
        }
        try
        {
            string foreignStage=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(foreignStage);
            var collision=new DownloadItem{Url="https://retained-loss.invalid/collision",OutputRoot=options.Output,RunDir=run,StageDir=foreignStage};
            string foreignProof=foreignStage+".resume.json";File.WriteAllText(foreignProof,"FOREIGN PROOF");
            bool collisionRefused=false;
            try{DownloadResumeStore.Create(collision,options,null,"best");}
            catch(IOException){collisionRefused=true;}
            Require(collisionRefused && collision.ResumeState is null && File.ReadAllText(foreignProof)=="FOREIGN PROOF",
                "CreateNew ownership-proof collisions must not delete or replace a preexisting foreign file");
            File.Delete(foreignProof);OwnedFiles.DeleteStage(foreignStage,run);

            var missingProof=Owned("missing-proof");
            string proof=missingProof.StageDir+".resume.json",saved=File.ReadAllText(proof),partial=Path.Combine(missingProof.StageDir,"source.mp4.part");
            File.Delete(proof);bool refused=false;
            try{DownloadCleanup.Retry(missingProof);}
            catch(Exception error) when(error is IOException or InvalidDataException){refused=true;}
            Require(refused && File.ReadAllText(partial)=="OWNED PARTIAL" && missingProof.ResumeState is not null,
                "A missing proof with existing bytes must refuse deletion and preserve the recoverable stage");
            File.WriteAllText(proof,saved);DownloadCleanup.Retry(missingProof);

            var missingBoth=Owned("missing-both");string stage=missingBoth.StageDir;
            OwnedFiles.DeleteStage(stage,run);File.Delete(stage+".resume.json");
            DownloadCleanup.Retry(missingBoth);
            Require(missingBoth.ResumeState is null && missingBoth.StageDir.Length==0 && missingBoth.CanResume,
                "When both owned stage and proof are genuinely absent, explicit cleanup must clear only the stale metadata");
            missingBoth.Status="Queued";missingBoth.State="Queued";
            await using var engine=Engine(new AppPersistence(Path.Combine(root,"missing-profile")),options);
            Require(engine.Add(missingBoth),"A reset item with no retained bytes/proof must permit a fresh admission");
            Require(await engine.CancelAndWaitAsync(missingBoth),"The unstarted fresh admission must retire without a child process");
        }
        finally
        {
            if(Directory.Exists(run))Directory.Delete(run,true);
        }
    }

    private static DownloadCoordinator Engine(AppPersistence data,DownloadOptions options,ConcurrentQueue<JournalRecord>? trace=null)=>
        new(DependencyCenter.Executable("yt-dlp.exe"),data,options,_=>{},_=>{},trace:record=>trace?.Enqueue(record));
    private static DownloadOptions Options(string output)=>new(){Output=output,Threads="1",FilenameTemplate="%(id)s.%(ext)s",
        EmbedMetadata=false,EmbedChapters=false,Container="Auto",Quality="Best"};
    private static DownloadItem Clone(DownloadItem row)=>JsonSerializer.Deserialize<DownloadItem>(JsonSerializer.Serialize(row,AppPersistence.Json),AppPersistence.Json)!;
    private static string Describe(DownloadItem row)=>$"status={row.Status}, state={row.State}, finished={row.AttemptFinished}, error={row.RawError}, cleanup={row.CleanupError}";
    private static string? Partial(DownloadItem row)
    {
        if(row.StageDir.Length==0 || !Directory.Exists(row.StageDir))return null;
        try{return Directory.EnumerateFiles(row.StageDir,"*.part",SearchOption.AllDirectories).FirstOrDefault(path=>new FileInfo(path).Length>=64*1024);}
        catch(IOException){return null;}
    }
    private static Task WaitForPartial(DownloadItem row,string message)=>Until(()=>Partial(row) is not null,TimeSpan.FromSeconds(25),message+" / "+Describe(row));
    private static async Task Until(Func<bool> ready,TimeSpan timeout,string message)
    {
        var elapsed=Stopwatch.StartNew();
        while(!ready() && elapsed.Elapsed<timeout)await Task.Delay(20);
        Require(ready(),message);
    }
    private static async Task RefusesAdmission(AppPersistence data,DownloadOptions options,DownloadItem row,string message)
    {
        bool refused=false;
        try
        {
            await using var engine=Engine(data,options);
            if(!engine.Add(row))refused=true;
            else
            {
                // Disk ownership is deliberately checked by the worker rather
                // than by Add on the WPF dispatcher. Exercise that real gate.
                await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
                refused=row.AttemptFinished && row.Status is "Failed" or "Interrupted";
            }
        }
        catch(Exception error) when(error is IOException or InvalidDataException or ArgumentException or InvalidOperationException){refused=true;}
        Require(refused,message);
    }
    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException("Queue skip/resume: "+message);}
    private static void CreateJunction(string path,string target)
    {
        var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string arg in new[]{"/d","/c","mklink","/J",path,target})start.ArgumentList.Add(arg);
        using var child=Process.Start(start)!;string output=child.StandardOutput.ReadToEnd()+child.StandardError.ReadToEnd();
        Require(child.WaitForExit(10000) && child.ExitCode==0 && (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0,
            "A real NTFS junction is required: "+output);
    }

    private sealed class RangeOrigin:IAsyncDisposable
    {
        internal sealed record Request(string Path,string Method,long First);
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentDictionary<TcpClient,byte> _clients=[];
        private readonly ConcurrentBag<Task> _handlers=[];
        private readonly ConcurrentDictionary<string,TaskCompletionSource> _holds=new(StringComparer.Ordinal);
        private readonly Task _accept;
        internal ConcurrentQueue<Request> Requests{get;}=[];
        internal byte[] PrimaryBytes{get;}=Enumerable.Range(0,1024*1024).Select(i=>(byte)(i*31+7)).ToArray();
        internal byte[] NextBytes{get;}=Enumerable.Range(0,32*1024).Select(i=>(byte)(i*17+11)).ToArray();
        internal Uri Address{get;}
        internal RangeOrigin()
        {_listener.Start();Address=new("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/");_accept=Accept();}
        internal string Url(string path)=>new Uri(Address,path).AbsoluteUri;
        internal void Hold(string path)=>_holds[path]=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Release(string path){if(_holds.TryGetValue(path,out var hold))hold.TrySetResult();}
        private async Task Accept()
        {
            try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients[client]=0;_handlers.Add(Serve(client));}}
            catch(Exception) when(_stop.IsCancellationRequested){}
        }
        private async Task Serve(TcpClient client)
        {
            using(client)
            try
            {
                using var stream=client.GetStream();var bytes=new List<byte>();byte[] one=new byte[1];
                while(bytes.Count<8192)
                {
                    if(await stream.ReadAsync(one,_stop.Token)==0)return;bytes.Add(one[0]);
                    if(bytes.Count>=4 && bytes[^4]==13 && bytes[^3]==10 && bytes[^2]==13 && bytes[^1]==10)break;
                }
                string[] lines=Encoding.ASCII.GetString(bytes.ToArray()).Split("\r\n");
                string[] first=lines[0].Split(' ');string path=new Uri(Address,first[1]).AbsolutePath.TrimStart('/');
                byte[] body=path=="next.mp4"?NextBytes:PrimaryBytes;
                long begin=0,end=body.Length-1;bool ranged=false;
                string? header=lines.FirstOrDefault(line=>line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase));
                if(header is not null)
                {
                    string[] range=header[(header.IndexOf('=')+1)..].Trim().Split('-');
                    begin=long.Parse(range[0],CultureInfo.InvariantCulture);
                    if(range[1].Length>0)end=Math.Min(end,long.Parse(range[1],CultureInfo.InvariantCulture));ranged=true;
                }
                Requests.Enqueue(new(path,first[0],begin));
                if(begin>=body.Length){await Write(stream,"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return;}
                await Write(stream,"HTTP/1.1 "+(ranged?"206 Partial Content":"200 OK")+"\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: "+(end-begin+1)+
                    (ranged?"\r\nContent-Range: bytes "+begin+"-"+end+"/"+body.Length:"")+"\r\nConnection: close\r\n\r\n");
                if(first[0]=="HEAD")return;
                int count=(int)(end-begin+1);
                if(_holds.TryGetValue(path,out var hold) && !hold.Task.IsCompleted)
                {
                    int initial=Math.Min(count,256*1024);await stream.WriteAsync(body.AsMemory((int)begin,initial),_stop.Token);await stream.FlushAsync(_stop.Token);
                    await hold.Task.WaitAsync(_stop.Token);begin+=initial;count-=initial;
                }
                if(count>0)await stream.WriteAsync(body.AsMemory((int)begin,count),_stop.Token);
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or SocketException or ObjectDisposedException){}
            finally{_clients.TryRemove(client,out _);}
        }
        private Task Write(NetworkStream stream,string text)=>stream.WriteAsync(Encoding.ASCII.GetBytes(text),_stop.Token).AsTask();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();foreach(var client in _clients.Keys)client.Dispose();
            try{await _accept;await Task.WhenAll(_handlers);}catch(Exception) when(_stop.IsCancellationRequested){}
            _stop.Dispose();
        }
    }
}
