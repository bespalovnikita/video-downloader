using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class LifecycleRegressionTests
{
    private const string Prefix="https://lifecycle-regression.invalid/";
    internal static bool TryRunFixture(string[] args)
    {
        string? url=args.LastOrDefault();
        if (url?.StartsWith(Prefix,StringComparison.Ordinal)!=true) return false;
        Require(args.Contains("--ignore-config"),"Every managed yt-dlp call must ignore ambient configuration");
        if (url.EndsWith("preview-hang"))
        {
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        if (args.Contains("--dump-single-json"))
        {
            int index=Array.IndexOf(args,"--cookies");
            string thumbnail=index>=0 ? File.ReadAllText(args[index+1]) : "";
            Console.WriteLine(JsonSerializer.Serialize(new { title="Preview fixture",thumbnail }));
            return true;
        }
        string output=args[Array.IndexOf(args,"-o")+1];
        string media=Path.Combine(Path.GetDirectoryName(output)!,"fixture.mp4");
        File.WriteAllText(media,"MEDIA");
        if (url.Contains("folders-",StringComparison.Ordinal))
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!,"fixture.info.json"),"{\"fixture\":true}");
        Console.WriteLine("__VD_TITLE__:Fixture");Console.Out.Flush();
        if (url.EndsWith("cancel")) Thread.Sleep(Timeout.Infinite);
        Console.WriteLine("__VD_FILE__:"+media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-lifecycle-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            History(Path.Combine(root,"history"));
            Profiles();
            Migration(Path.Combine(root,"migration"));
            Cleanup(Path.Combine(root,"cleanup"));
            await Preview(Path.Combine(root,"preview"));
            await Cancellation(Path.Combine(root,"cancellation"));
            await SubscriptionFolders(Path.Combine(root,"subscription-folders"));
            Console.WriteLine("PASS: history separators, profile transitions, migration, cleanup, preview deadlines and cancellation");
        }
        finally { Directory.Delete(root,true); }
    }
    private static void History(string root)
    {
        var data=new AppPersistence(root);
        foreach (string suffix in new[]{"","\n","\r\n"})
        {
            string before="{\"Version\":1,\"Url\":\"https://example.com/старый\"}"+suffix;
            File.WriteAllText(data.HistoryPath,before,new UTF8Encoding(false));
            data.AppendSuccess("https://example.com/new");
            Require(data.ReadHistory().Count==2 && File.ReadAllLines(data.HistoryPath).Length==2,
                "Append must separate valid records, including an unterminated UTF-8 record");
            Require(File.ReadAllText(data.HistoryPath).StartsWith(before,StringComparison.Ordinal),"Append must preserve existing bytes");
        }
        File.WriteAllText(data.HistoryPath,"");data.AppendSuccess("https://example.com/empty");
        Require(File.ReadAllLines(data.HistoryPath).Length==1,"Empty history must not gain a blank line");
        string damaged="{\"Version\":1,\"Url\":";
        File.WriteAllText(data.HistoryPath,damaged);
        Refuses(()=>data.AppendSuccess("https://example.com/new"));
        Require(File.ReadAllText(data.HistoryPath)==damaged,"Damaged tail must remain untouched");
        File.WriteAllText(data.HistoryPath,"{\"Version\":1,\"Url\":\"https://example.com/seed\"}");
        Parallel.For(0,16,n=>data.AppendSuccess("https://example.com/concurrent/"+n));
        Require(data.ReadHistory().Count==17,"Concurrent append must neither lose nor concatenate records");
    }
    private static string Preset(DownloadOptions o)=>JsonSerializer.Serialize(new{
        o.Quality,o.Codec,o.Container,o.AudioOnly,o.Archive,o.WriteSubs,o.AutoSubs,o.EmbedSubs,
        o.EmbedThumbnail,o.EmbedMetadata,o.EmbedChapters});
    private static void Profiles()
    {
        var names=new[]{"Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive"};
        foreach(string previous in names.Concat(ProfileCatalog.DisplayNames))
        foreach(string target in names.Concat(ProfileCatalog.DisplayNames))
        {
            var actual=new DownloadOptions{Output="custom",Cookies="cookies",RateLimit="5M",Threads="7",Sponsor=true};
            ProfileCatalog.Apply(actual,previous);ProfileCatalog.Apply(actual,target);
            var expected=new DownloadOptions();ProfileCatalog.Apply(expected,target);
            Require(Preset(actual)==Preset(expected),$"Preset {target} must not inherit {previous}");
            Require(actual.Output=="custom" && actual.Cookies=="cookies" && actual.RateLimit=="5M" && actual.Threads=="7" && actual.Sponsor,
                "Applying a profile must retain unrelated connection/user settings");
        }
        var options=new DownloadOptions();ProfileCatalog.Apply(options,"Music MP3");ProfileCatalog.Apply(options,"4K archive");
        Require(!options.AudioOnly && options.Archive && options.Quality=="2160","4K after MP3 must download video to a dated folder");
        ProfileCatalog.Apply(options,"Universal");Require(!options.Archive && !options.AutoSubs,"Universal must reset archive/subtitle state");
        string before=Preset(options);ProfileCatalog.Apply(options,"Unknown");Require(Preset(options)==before,"Unknown profiles must not change settings");
    }
    private static void Migration(string root)
    {
        var data=new AppPersistence(root);
        string events=Path.Combine(root,"events.jsonl"),run=Path.Combine(root,"run");
        string stage=Path.Combine(run,"download-1-"+Guid.NewGuid().ToString("N"));
        string file=Path.Combine(root,"missing.mp4");
        File.WriteAllLines(events,new[]{
            JsonSerializer.Serialize(new{Kind="Result",Url="https://example.com/readded",Success=true,Path=file,OwnedPaths=new[]{file}}),
            JsonSerializer.Serialize(new{Kind="Result",Url="https://example.com/cleanup-event",Success=false,Cancelled=true,
                OutputRoot=root,StageDir=stage,CleanupError="Locked file",CleanupPaths=new[]{file},OwnedPaths=new[]{file}})});
        File.WriteAllText(data.SessionPath,JsonSerializer.Serialize(new{Version=1,
            Queue=new object[]{
                new{Url="https://example.com/readded",Status="Queued",DeferredToNextStart=true},
                new{Url="https://example.com/cleanup-event",Status="Running"},
                new{Url="https://example.com/cleanup-saved",Status="Interrupted",PendingRemoval=true,
                    CleanupError="Locked",CleanupPaths=new[]{file},StageDir=stage,RunDir=run,OutputRoot=root},
                new{Url="https://example.com/partial-publish",Status="Running",CleanupPaths=new[]{file},StageDir=stage,RunDir=run,OutputRoot=root}},
            Settings=new{Output=Path.Combine(root,"different-output")},
            Engine=new{Pid=int.MaxValue,StartedUtc="2000-01-01T00:00:00Z",EventsPath=events,RunDir=run}}));
        var restored=data.Restore()!;
        Require(restored.Queue[0].Status=="Queued" && restored.Queue[0].DeferredToNextStart && restored.Queue[0].Path=="",
            "A previous Result must not complete a deferred new attempt");
        foreach(var row in restored.Queue.Skip(1))
            Require(row.PendingRemoval && !row.CanResume && row.CleanupPaths.SequenceEqual(new[]{file}) && row.StageDir==stage &&
                row.OutputRoot==root && row.RunDir==run && row.AttemptFinished,"Saved and event-based cleanup context must survive migration");
        data.Save(restored.Queue,restored.Settings);
        var roundtrip=data.Restore()!;
        Require(roundtrip.Queue.Skip(1).All(row=>!row.CanResume && row.CleanupPaths.Contains(file) && row.StageDir==stage),
            "Native save/restart must not erase PowerShell cleanup metadata");
    }
    private static void Cleanup(string root)
    {
        Directory.CreateDirectory(root);
        string output=Path.Combine(root,"output"),run=Path.Combine(root,"run");
        string stage=Path.Combine(run,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(stage);Directory.CreateDirectory(output);
        string owned=Path.Combine(output,"owned.mp4"),outside=Path.Combine(root,"sentinel.mp4");
        File.WriteAllText(owned,"OWNED");File.WriteAllText(outside,"PRESERVE");File.WriteAllText(Path.Combine(stage,"partial"),"PARTIAL");
        var row=new DownloadItem{Url="https://example.com/cleanup",OutputRoot=output,RunDir=run,StageDir=stage,
            OwnedPaths=[owned],CleanupPaths=[outside],PendingRemoval=true,AttemptFinished=true};
        Refuses(()=>DownloadCleanup.Retry(row));
        Require(row.PendingRemoval && row.CleanupError.Length>0 && row.CleanupPaths.Contains(owned) && row.StageDir==stage,
            "Failed removal must preserve the complete manifest and block restart");
        Require(File.Exists(owned) && File.ReadAllText(outside)=="PRESERVE","Validate the entire deletion batch before touching files");
        var data=new AppPersistence(Path.Combine(root,"profile"));data.Save([row],new DownloadOptions{Output="new-output"});
        row=data.Restore()!.Queue[0];row.CleanupPaths.Remove(outside);DownloadCleanup.Retry(row);
        Require(!File.Exists(owned) && !Directory.Exists(stage) && File.Exists(outside),"Retry must use original roots and preserve unrelated files");
        var legacy=new DownloadItem{Path=outside,Status="Completed",OutputRoot=root};
        Refuses(()=>DownloadCleanup.Retry(legacy));Refuses(()=>DownloadCleanup.Retry(legacy));
        Require(File.Exists(outside) && legacy.OwnedPaths.Count==0 && legacy.CleanupPaths.Count==0,"Legacy Path must never become proof of ownership on retry");
        File.Delete(outside);DownloadCleanup.Retry(legacy);
        Refuses(()=>OwnedFiles.DeleteStage(run,root));
        if (OperatingSystem.IsWindows()) Rollback(root);
    }
    private static void Rollback(string root)
    {
        string stage=Path.Combine(root,"rollback-stage"),output=Path.Combine(root,"rollback-output");
        Directory.CreateDirectory(stage);Directory.CreateDirectory(output);
        File.WriteAllText(Path.Combine(stage,"one.mp4"),"ONE");File.WriteAllText(Path.Combine(stage,"two.mp4"),"TWO");
        FileStream? held=null;List<string> manifest=[];
        try
        {
            Refuses(()=>OwnedFiles.Publish(stage,output,null,paths=>{
                manifest=paths.ToList();
                held=new FileStream(paths[0],FileMode.Open,FileAccess.Read,FileShare.None);
                throw new IOException("Simulate publication failure while its first output is locked");
            }));
            Require(manifest.Count==1 && File.Exists(manifest[0]),"A failed rollback must retain published ownership");
        }
        finally {held?.Dispose();}
        var row=new DownloadItem{OutputRoot=output,CleanupPaths=manifest,PendingRemoval=true};DownloadCleanup.Retry(row);
        Require(!Directory.EnumerateFiles(output).Any(),"Retry must delete a retained partial publication");
    }
    private static async Task Preview(string root)
    {
        var data=new AppPersistence(root);
        var next=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
        using(var previews=new PreviewService(Executable(),data,()=>"",p=>next.TrySetResult(p),logs.Enqueue,TimeSpan.FromSeconds(2)))
        {
            previews.Request(Prefix+"preview-hang");previews.Request(Prefix+"preview-next");
            var result=await next.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Require(result.Url==Prefix+"preview-next" && logs.Any(l=>l.Contains("timed out")),"A hung metadata process must time out and release the only preview worker");
        }
        using var stop=new CancellationTokenSource();
        var server=new TcpListener(IPAddress.Loopback,0);server.Start();
        int port=((IPEndPoint)server.LocalEndpoint).Port;
        var serving=Task.Run(async()=>{
            using var client=await server.AcceptTcpClientAsync(stop.Token);
            var stream=client.GetStream();var request=new byte[4096];await stream.ReadAsync(request,stop.Token);
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"),stop.Token);
            await Task.Delay(Timeout.Infinite,stop.Token);
        });
        string cookies=Path.Combine(root,"thumbnail-url");File.WriteAllText(cookies,$"http://127.0.0.1:{port}/thumb.png");
        var ready=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        int cookieReads=0;
        try
        {
            using var previews=new PreviewService(Executable(),data,()=>Interlocked.Increment(ref cookieReads)==1?cookies:"",
                p=>{if(p.Url==Prefix+"preview-after-body")ready.TrySetResult(p);},logs.Enqueue,
                TimeSpan.FromSeconds(8),TimeSpan.FromMilliseconds(500));
            previews.Request(Prefix+"preview-stalled-body");previews.Request(Prefix+"preview-after-body");
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var first=previews.Get(Prefix+"preview-stalled-body");
            Require(first?.Title=="Preview fixture" && first.ThumbnailBase64=="" && logs.Any(l=>l.Contains("Thumbnail timed out")),
                "A stalled image body must time out while preserving metadata and allowing the next preview");
        }
        finally {stop.Cancel();server.Stop();try{await serving;}catch(OperationCanceledException){} }
    }
    private static async Task Cancellation(string root)
    {
        var data=new AppPersistence(Path.Combine(root,"profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",EmbedMetadata=false,EmbedChapters=false};
        Directory.CreateDirectory(options.Output);
        string sentinel=Path.Combine(options.Output,"sentinel.mp4");File.WriteAllText(sentinel,"PRESERVE");
        var row=new DownloadItem{Url=Prefix+"cancel"};FileStream? held=null;bool cancelled=false;
        DownloadCoordinator? engine=null;
        try
        {
            await using(engine=new DownloadCoordinator(Executable(),data,options,item=>{
                if(item.Title.Length==0 || cancelled)return;
                cancelled=true;
                if(OperatingSystem.IsWindows()) held=new FileStream(Path.Combine(item.StageDir,"fixture.mp4"),FileMode.Open,FileAccess.Read,FileShare.None);
                item.PendingRemoval=true;
                Require(engine!.IsPending(item),"An active row must remain pending until cleanup is acknowledged");
                engine.Cancel(item);
            },_=>{}))
            {
                engine.Add(row);await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                Require(row.AttemptFinished && !engine.IsPending(row),"Terminal callback must acknowledge process exit and cleanup attempt");
            }
            Require(cancelled && !data.ReadHistory().ContainsKey(row.Url),"Cancellation must not record success");
            if(OperatingSystem.IsWindows())
            {
                Require(row.CleanupError.Length>0 && row.PendingRemoval && Directory.Exists(row.StageDir) && !row.CanResume,
                    "Locked staging must remain visible, checkpointable and excluded from retry downloads");
                data.Save([row],options);row=data.Restore()!.Queue[0];
                Require(row.StageDir.Length>0 && row.CleanupError.Length>0,"Cleanup error must survive restart");
            }
        }
        finally {held?.Dispose();}
        DownloadCleanup.Retry(row);
        Require(row.StageDir=="" && row.CleanupError=="" && File.ReadAllText(sentinel)=="PRESERVE","Retry must clean only the cancelled attempt");
    }

    private static async Task SubscriptionFolders(string root)
    {
        var data=new AppPersistence(Path.Combine(root,"profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="4",EmbedMetadata=false,EmbedChapters=false};
        string firstRoot=Path.Combine(options.Output,"123"),secondRoot=Path.Combine(options.Output,"Другой канал");
        Directory.CreateDirectory(firstRoot);Directory.CreateDirectory(secondRoot);
        string previous=Path.Combine(firstRoot,"fixture.mp4"),sibling=Path.Combine(secondRoot,"keep.mp4");
        File.WriteAllText(previous,"PREVIOUS");File.WriteAllText(sibling,"SIBLING");
        var first=new DownloadItem{Url=Prefix+"folders-one",SubscriptionId="source-one",SubscriptionName="123"};
        var second=new DownloadItem{Url=Prefix+"folders-two",SubscriptionId="source-two",SubscriptionName="Другой канал"};
        var ordinary=new DownloadItem{Url=Prefix+"folders-ordinary"};
        var cancelled=new DownloadItem{Url=Prefix+"folders-cancel",SubscriptionId="source-one",SubscriptionName="123"};
        int cancellation=0;
        DownloadCoordinator? engine=null;
        await using(engine=new DownloadCoordinator(Executable(),data,options,item=>
        {
            if (item.Url==cancelled.Url && item.Title.Length>0 && Interlocked.Exchange(ref cancellation,1)==0)
                engine!.Cancel(item);
        },_=>{}))
        {
            foreach(var row in new[]{first,second,ordinary,cancelled}) Require(engine.Add(row),"Mixed subscription queue must be accepted");
            // The destination is captured by Add, independently of later metadata.
            first.SubscriptionName="Changed after enqueue";
            await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        first.SubscriptionName="123";
        foreach(var (row,output) in new[]{(first,firstRoot),(second,secondRoot),(ordinary,options.Output)})
        {
            Require(row.Status=="Completed" && row.OutputRoot==Path.GetFullPath(output) && row.OwnedPaths.Count==2,
                "Every item must retain its own media/sidecar publication boundary");
            Require(row.OwnedPaths.All(path=>OwnedFiles.Inside(output,path) && File.Exists(path)),
                "Subscription publication must stay inside the captured leaf folder");
        }
        Require(cancelled.Status=="Interrupted" && cancelled.AttemptFinished && cancelled.OutputRoot==Path.GetFullPath(firstRoot) &&
                cancelled.StageDir.Length==0 && cancelled.OwnedPaths.Count==0 && !data.ReadHistory().ContainsKey(cancelled.Url),
            "Cancelling one subscription item must clean staging without changing its sibling success/history");
        Require(File.ReadAllText(previous)=="PREVIOUS" && File.ReadAllText(sibling)=="SIBLING" && first.Path!=previous &&
                !Directory.Exists(Path.Combine(options.Output,"Changed after enqueue")),
            "Naming collisions and later metadata changes must not overwrite or redirect subscribed downloads");

        data.Save([first,second,ordinary],options);
        var restored=data.Restore()!;
        Require(restored.Queue[0].SubscriptionId=="source-one" && restored.Queue[0].SubscriptionName=="123" &&
                restored.Queue[0].OutputRoot==firstRoot && restored.Queue[1].SubscriptionName=="Другой канал" &&
                restored.Queue[2].SubscriptionId.Length==0 && restored.Queue[2].SubscriptionName.Length==0,
            "Checkpoint restart must preserve source/folder snapshots while ordinary rows remain ordinary");
        string saved=File.ReadAllText(data.SessionPath);
        File.WriteAllText(data.SessionPath,saved.Replace("\"SubscriptionName\":\"123\"","\"SubscriptionName\":\"../escape\"",StringComparison.Ordinal));
        Refuses(()=>data.Restore());
        File.WriteAllText(data.SessionPath,saved.Replace("\"SubscriptionName\":\"123\"","\"SubscriptionName\":null",StringComparison.Ordinal));
        Refuses(()=>data.Restore());
        File.WriteAllText(data.SessionPath,saved.Replace("\"SubscriptionId\":\"source-one\"","\"SubscriptionId\":null",StringComparison.Ordinal));
        Refuses(()=>data.Restore());
        File.WriteAllText(data.SessionPath,saved);

        var retry=new DownloadItem{Url=first.Url,SubscriptionId=first.SubscriptionId,SubscriptionName=first.SubscriptionName};
        await using(var repeated=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{}))
        {
            Require(repeated.Add(retry),"An explicitly approved retry must be accepted");
            await repeated.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        Require(retry.Status=="Completed" && retry.Path!=first.Path && retry.OwnedPaths.All(path=>!first.OwnedPaths.Contains(path)),
            "A subscription retry must publish a new batch without taking ownership of earlier files");
        DownloadCleanup.Retry(first);
        Require(retry.OwnedPaths.All(File.Exists) && second.OwnedPaths.All(File.Exists) && ordinary.OwnedPaths.All(File.Exists) &&
                File.ReadAllText(previous)=="PREVIOUS" && File.ReadAllText(sibling)=="SIBLING",
            "Removing an older subscription result must preserve retries, sibling subscriptions and ordinary downloads");

        await using(var rejecting=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{}))
        {
            foreach(string folder in new[]{"..","../escape","bad\\nested","C:\\escape","CON"})
                Refuses(()=>rejecting.Add(new DownloadItem{Url=Prefix+"bad-"+Guid.NewGuid(),SubscriptionName=folder}));
            Require(rejecting.Outstanding==0,"Invalid folder metadata must be refused before accepting a worker");
        }
        string outside=Path.Combine(root,"outside"),linked=Path.Combine(options.Output,"linked");
        Directory.CreateDirectory(outside);
        string protectedFile=Path.Combine(outside,"protected.mp4");File.WriteAllText(protectedFile,"OUTSIDE");
        if (OperatingSystem.IsWindows())
        {
            var linkStart=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true};
            foreach(string argument in new[]{"/d","/c","mklink","/J",linked,outside}) linkStart.ArgumentList.Add(argument);
            using var linkProcess=Process.Start(linkStart)!;
            await linkProcess.WaitForExitAsync();
            Require(linkProcess.ExitCode==0,"Subscription junction fixture must be created");
        }
        else Directory.CreateSymbolicLink(linked,outside);
        try
        {
            await using var rejectingLink=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{});
            Refuses(()=>rejectingLink.Add(new DownloadItem{Url=Prefix+"folders-link",SubscriptionName="linked"}));
            Require(rejectingLink.Outstanding==0 && File.ReadAllText(protectedFile)=="OUTSIDE",
                "A subscription leaf pointing outside the base must fail before accepting a worker");
        }
        finally {Directory.Delete(linked);}
        var archived=new DownloadOptions{Output=Path.Combine(root,"archive"),Archive=true};
        await using(var archiveEngine=new DownloadCoordinator(Executable(),data,archived,_=>{},_=>{}))
        {
            var row=new DownloadItem{Url=Prefix+"folders-archive",SubscriptionId="source-one",SubscriptionName="123"};
            Require(archiveEngine.Add(row),"Archived subscription must be accepted");
            await archiveEngine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
            Require(row.Status=="Completed" && row.OutputRoot==Path.Combine(archived.OutputDirectory,"123"),
                "Archive keeps its pinned date prefix before the subscription leaf");
        }
        Console.WriteLine("PASS: mixed subscription folders, pinned names, restart, collision retry and sibling-safe cancellation/removal");
    }
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,
        typeof(LifecycleRegressionTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Refuses(Action action)
    {
        try{action();}catch(Exception e) when(e is IOException or InvalidDataException){return;}
        throw new InvalidOperationException("Unsafe filesystem/history operation must fail");
    }
    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException(message);}
}
