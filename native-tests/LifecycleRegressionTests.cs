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
    private static readonly byte[] PreviewPng=Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAADklEQVR4nGP4DwYMEAoAU7oL9ZisIGcAAAAASUVORK5CYII=");
    private static readonly byte[] PreviewWebP=Convert.FromBase64String("UklGRiQAAABXRUJQVlA4IBgAAAAwAQCdASoCAAIAAgA0JaQAA3AA/vuUAAA=");
    private static readonly byte[] PreviewAvif=Convert.FromBase64String("AAAAIGZ0eXBhdmlmAAAAAGF2aWZtaWYxbWlhZk1BMUIAAAD5bWV0YQAAAAAAAAAvaGRscgAAAAAAAAAAcGljdAAAAAAAAAAAAAAAAFBpY3R1cmVIYW5kbGVyAAAAAA5waXRtAAAAAAABAAAAHmlsb2MAAAAARAAAAQABAAAAAQAAASEAAAAWAAAAKGlpbmYAAAAAAAEAAAAaaW5mZQIAAAAAAQAAYXYwMUNvbG9yAAAAAGppcHJwAAAAS2lwY28AAAAUaXNwZQAAAAAAAAACAAAAAgAAABBwaXhpAAAAAAMICAgAAAAMYXYxQ4EADAAAAAATY29scm5jbHgAAgACAAIAAAAAF2lwbWEAAAAAAAAAAQABBAECgwQAAAAebWRhdAoFGAA2wCAyDRgAAABQAAAAALASmcg=");
    internal static bool TryRunFixture(string[] args)
    {
        if(args.Contains("webp_pipe") && args.LastOrDefault()=="pipe:1")
        {
            byte[] input=File.ReadAllBytes(args[Array.IndexOf(args,"-i")+1]);
            string mode=Encoding.ASCII.GetString(input);
            string? pid=Environment.GetEnvironmentVariable("VD_PREVIEW_CONVERTER_PID");
            if(pid!=null)File.WriteAllText(pid,Environment.ProcessId.ToString());
            if(mode.EndsWith("hang",StringComparison.Ordinal))Thread.Sleep(Timeout.Infinite);
            using var convertedOutput=Console.OpenStandardOutput();convertedOutput.Write(PreviewPng);
            if(mode.EndsWith("large",StringComparison.Ordinal))convertedOutput.Write(new byte[2_000_001]);
            return true;
        }
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
            if(url.EndsWith("preview-slow-metadata",StringComparison.Ordinal))Thread.Sleep(700);
            Console.WriteLine(thumbnail.StartsWith('{')?thumbnail:JsonSerializer.Serialize(new { title="Preview fixture",thumbnail }));
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
            await PreviewThumbnails(Path.Combine(root,"preview-thumbnails"));
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
    private static async Task PreviewThumbnails(string root)
    {
        var data=new AppPersistence(root);
        using var stop=new CancellationTokenSource();
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        string address="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
        var requested=new System.Collections.Concurrent.ConcurrentQueue<string>();
        var responses=new List<Task>();
        var server=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                responses.Add(Task.Run(async()=>
                {
                    using(client)
                    {
                        var stream=client.GetStream();byte[] request=new byte[4096];
                        int length=await stream.ReadAsync(request,stop.Token);
                        string first=Encoding.ASCII.GetString(request,0,length).Split('\r')[0];
                        string target=first.Split(' ')[1];requested.Enqueue(target);
                        if(target.StartsWith("/stall",StringComparison.Ordinal))
                        {
                            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100\r\n\r\n"),stop.Token);
                            await Task.Delay(Timeout.Infinite,stop.Token);return;
                        }
                        bool failed=target.StartsWith("/missing",StringComparison.Ordinal);
                        byte[] bytes=target.StartsWith("/webp-hang",StringComparison.Ordinal)?"RIFF1234WEBPhang"u8.ToArray():
                            target.StartsWith("/webp-large",StringComparison.Ordinal)?"RIFF1234WEBPlarge"u8.ToArray():
                            target.StartsWith("/webp",StringComparison.Ordinal)?PreviewWebP:
                            target.StartsWith("/avif",StringComparison.Ordinal)?PreviewAvif:PreviewPng;
                        string header=$"HTTP/1.1 {(failed?"404 Not Found":"200 OK")}\r\nContent-Type: application/octet-stream\r\nContent-Length: {(failed?0:bytes.Length)}\r\nConnection: close\r\n\r\n";
                        await stream.WriteAsync(Encoding.ASCII.GetBytes(header),stop.Token);
                        if(!failed)await stream.WriteAsync(bytes,stop.Token);
                    }
                }));
            }
        });
        string cookies=Path.Combine(root,"preview-json");
        async Task<(PreviewData Data,string[] Logs)> Fetch(string suffix,object metadata,string? converter=null,TimeSpan? overall=null,TimeSpan? image=null)
        {
            File.WriteAllText(cookies,JsonSerializer.Serialize(metadata));
            var ready=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
            var logs=new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var service=new PreviewService(Executable(),data,()=>cookies,value=>ready.TrySetResult(value),logs.Enqueue,
                overall??TimeSpan.FromSeconds(8),image??TimeSpan.FromSeconds(2),converter);
            service.Request(Prefix+suffix);
            var result=await ready.Task.WaitAsync(TimeSpan.FromSeconds(12));
            Require(result.Title=="Thumbnail fixture" && result.Uploader=="Subscription author" && result.MaxHeight==1080,
                "Thumbnail errors must preserve title, author and video format metadata");
            return(result,logs.ToArray());
        }
        object Metadata(string thumbnail,object[]? thumbnails=null)=>new{title="Thumbnail fixture",uploader="Subscription author",thumbnail,
            thumbnails=thumbnails??[],formats=new[]{new{vcodec="avc1",height=1080}}};
        string? previousPid=Environment.GetEnvironmentVariable("VD_PREVIEW_CONVERTER_PID");
        try
        {
            var extensionless=await Fetch("preview-extensionless",Metadata(address+"/image?format=png&signed=1"));
            Require(Convert.FromBase64String(extensionless.Data.ThumbnailBase64).SequenceEqual(PreviewPng),
                "An extensionless signed HTTP URL must load according to image bytes instead of its suffix");
            var fallback=await Fetch("preview-fallback",Metadata(address+"/image-next",[
                new{url=address+"/missing.png",width=100,height=100},new{url=address+"/image-next",width=1,height=1}]));
            Require(fallback.Data.ThumbnailBase64.Length>0 && requested.Any(value=>value=="/missing.png") && requested.Any(value=>value=="/image-next"),
                "A failed preferred image must fall back to another confirmed metadata URL");
            var missing=await Fetch("preview-missing",Metadata(""));
            Require(missing.Data.ThumbnailBase64.Length==0,"Missing cover must remain a successful text preview");
            string unwritable=Path.Combine(data.PreviewDir,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Prefix+"preview-cache-unwritable"))).ToLowerInvariant()+".json");
            Directory.CreateDirectory(unwritable);
            var uncached=await Fetch("preview-cache-unwritable",Metadata(""));
            Require(uncached.Data.Title=="Thumbnail fixture" && uncached.Logs.Any(value=>value.Contains("cache write failed")),
                "A cache write failure must not prevent available metadata from reaching the UI");
            var converted=await Fetch("preview-webp",Metadata(address+"/webp"),Executable());
            Require(Convert.FromBase64String(converted.Data.ThumbnailBase64).SequenceEqual(PreviewPng),
                "WebP must pass downloaded bytes through a bounded converter and return PNG");
            string real=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
            if(File.Exists(real))
            {
                var actual=await Fetch("preview-webp-real",Metadata(address+"/webp"),real);
                Require(Convert.FromBase64String(actual.Data.ThumbnailBase64).AsSpan().StartsWith(new byte[]{0x89,0x50,0x4e,0x47}),
                    "Installed FFmpeg must convert a real tiny WebP into a real PNG");
                var avif=await Fetch("preview-avif-real",Metadata(address+"/avif"),real);
                Require(Convert.FromBase64String(avif.Data.ThumbnailBase64).AsSpan().StartsWith(new byte[]{0x89,0x50,0x4e,0x47}),
                    "AVIF alternatives must also convert to the same WPF-compatible PNG representation");
            }
            var unavailable=await Fetch("preview-webp-unavailable",Metadata(address+"/webp"),Path.Combine(root,"no-ffmpeg"));
            Require(unavailable.Data.ThumbnailBase64.Length==0 && unavailable.Logs.Any(value=>value.Contains("needs FFmpeg")),
                "Without FFmpeg, unsupported cover must keep useful text metadata");
            using(var refreshed=new PreviewService("unused",data,()=>"",_=>{},_=>{},ffmpegPath:Executable()))
                Require(refreshed.Get(Prefix+"preview-webp-unavailable")==null,
                    "A text-only disk cache must retry its missing converter after FFmpeg becomes available");
            string installedConverter=Path.Combine(root,"installed-ffmpeg");
            File.WriteAllText(cookies,JsonSerializer.Serialize(Metadata(address+"/webp")));
            var installReady=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
            using(var waiting=new PreviewService(Executable(),data,()=>cookies,p=>installReady.TrySetResult(p),_=>{},ffmpegPath:installedConverter))
            {
                string waitingUrl=Prefix+"preview-webp-install";waiting.Request(waitingUrl);
                await installReady.Task.WaitAsync(TimeSpan.FromSeconds(8));
                Require(waiting.Get(waitingUrl)?.Title=="Thumbnail fixture","The same missing converter result must remain cacheable while FFmpeg is absent");
                File.WriteAllText(installedConverter,"Installed after the request");
                Require(waiting.Get(waitingUrl)==null,"An in-memory missing converter result must refresh after installing FFmpeg in the same app run");
            }
            var broken=await Fetch("preview-webp-broken-converter",Metadata(address+"/webp"),installedConverter);
            Require(broken.Data.ThumbnailBase64.Length==0 && broken.Logs.Any(value=>value.Contains("could not start")),
                "An installed but invalid FFmpeg executable must not erase valid text metadata");
            string pid=Path.Combine(root,"converter.pid");Environment.SetEnvironmentVariable("VD_PREVIEW_CONVERTER_PID",pid);
            var timeout=await Fetch("preview-webp-timeout",Metadata(address+"/webp-hang"),Executable(),image:TimeSpan.FromSeconds(1));
            int converterPid=int.Parse(File.ReadAllText(pid));
            Require(timeout.Data.ThumbnailBase64.Length==0 && !ProcessAlive(converterPid),
                "A timed-out converter must terminate without dropping parsed metadata");
            var large=await Fetch("preview-webp-large",Metadata(address+"/webp-large"),Executable());
            Require(large.Data.ThumbnailBase64.Length==0 && large.Logs.Any(value=>value.Contains("2MB")),
                "Converted output must obey the same 2MB image limit");
            var total=await Fetch("preview-slow-metadata",Metadata(address+"/stall"),overall:TimeSpan.FromMilliseconds(2500),image:TimeSpan.FromSeconds(5));
            Require(total.Data.ThumbnailBase64.Length==0 && total.Logs.Any(value=>value.Contains("Thumbnail timed out")),
                "The overall deadline during image retrieval must preserve completed metadata");

            string staleUrl=Prefix+"preview-legacy-thumbnail";
            string file=Path.Combine(data.PreviewDir,Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(staleUrl))).ToLowerInvariant()+".json");
            File.WriteAllText(file,JsonSerializer.Serialize(new{CacheVersion=1,Url=staleUrl,SavedAtUtc=DateTime.UtcNow,Title="Old",Uploader="",PlaylistTitle="",Extractor="",Codecs="",DynamicRange="",Duration=0,ThumbnailBase64="",ThumbnailError="no JPEG/PNG thumbnail was provided by yt-dlp"}));
            using var reader=new PreviewService("unused",data,()=>"",_=>{},_=>{});
            Require(reader.Get(staleUrl)==null && !File.Exists(file),"Only legacy suffix-rejected cache results must be retried after upgrade");
            Console.WriteLine("PASS: thumbnail URL fallback, WebP conversion, deadlines and metadata preservation");
        }
        finally
        {
            Environment.SetEnvironmentVariable("VD_PREVIEW_CONVERTER_PID",previousPid);
            stop.Cancel();listener.Stop();
            try{await server;}catch(OperationCanceledException){}
            try{await Task.WhenAll(responses);}catch(OperationCanceledException){}
        }
        static bool ProcessAlive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
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
