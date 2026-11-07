using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class LocalPosterTests
{
    internal static bool TryRunFixture(string[] arguments)
    {
        if(arguments.LastOrDefault()=="https://local-poster.invalid/remote" &&
            Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_ROOT") is {} remoteRoot)
        {
            File.WriteAllText(Path.Combine(remoteRoot,"metadata-started"),"");
            while(!File.Exists(Path.Combine(remoteRoot,"metadata-release")))Thread.Sleep(10);
            Console.WriteLine(JsonSerializer.Serialize(new{title="Remote title",duration=4,thumbnail=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_IMAGE")}));return true;
        }
        string? mode=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_FIXTURE");
        if(mode is not ("stall" or "flood"))return false;
        if(arguments.Contains("-show_entries",StringComparer.Ordinal))
        {
            Console.WriteLine("{\"streams\":[{\"index\":0,\"width\":160,\"height\":90,\"disposition\":{\"attached_pic\":0}}],\"format\":{\"duration\":\"4\"}}");return true;
        }
        if(!arguments.Contains("image2pipe",StringComparer.Ordinal))return false;
        AppPersistence.AtomicWrite(Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_READY")!,Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if(mode=="flood")
        {
            using var output=Console.OpenStandardOutput();
            byte[] bytes=new byte[32768];for(int i=0;i<128;i++)output.Write(bytes);output.Flush();
        }
        Thread.Sleep(Timeout.Infinite);return true;
    }
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-local-poster-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
            Require(File.Exists(ffmpeg),"Actual local posters require FFmpeg and FFprobe.");
            string video=Path.Combine(root,"colours.mp4");
            await Tool(ffmpeg,["-f","lavfi","-i","color=c=red:s=160x90:r=10:d=0.2","-f","lavfi","-i","color=c=blue:s=160x90:r=10:d=3.8",
                "-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-movflags","+faststart",video]);
            var row=Row(root,video,"colours");var input=LocalPosterInput.Capture(row)??throw new InvalidOperationException("A completed published ownership manifest must be admitted.");
            Admission(row);
            string mediaHash=Hash(video);
            using var frame=await new LocalPosterDecoder(ffmpeg).DecodeAsync(input,CancellationToken.None);
            Require(frame is not null && MediaFrameService.IsBoundedPng(frame.Bytes) && PreviewImage.IsValid(frame.Bytes),"The actual decoder must produce a bounded decodable frame.");
            string png=Path.Combine(root,"inspection.png");await File.WriteAllBytesAsync(png,frame!.Bytes);
            var pixel=await Tool(ffmpeg,["-i",png,"-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"]);
            Require(pixel.Length==3 && pixel[2]>200 && pixel[0]<80,"The representative 10% frame must be blue rather than the red first frame.");
            await UnsupportedFiles(root,ffmpeg,input);
            await CacheMerges(root,ffmpeg,input,frame.Bytes);
            await QueuedCancellation(root,ffmpeg,input);
            await JoinedCancellation(root,input);
            Require(Hash(video)==mediaHash && row.Status=="Completed" && row.OwnedPaths.SequenceEqual(new[]{video}) && row.AttemptFinished,
                "Every optional poster outcome must leave the successful result and published media unchanged.");
            Console.WriteLine("PASS: actual local-video posters, published ownership, image-only cache merge/remote race, bounded queue/output and joined cancellation");
        }
        finally{Directory.Delete(root,true);}
    }
    private static DownloadItem Row(string root,string path,string name)=>new(){Url="https://local-poster.invalid/"+name,Path=path,OutputRoot=root,
        OwnedPaths=[path],Title="Known title",Status="Completed",State="Done",AttemptFinished=true};
    private static void Admission(DownloadItem row)
    {
        row.Status="Running";Require(LocalPosterInput.Capture(row) is null,"An active download must not be probed.");row.Status="Completed";
        row.AttemptFinished=false;Require(LocalPosterInput.Capture(row) is null,"Publication must finish before local access.");row.AttemptFinished=true;
        var owned=row.OwnedPaths;row.OwnedPaths=[];Require(LocalPosterInput.Capture(row) is null,"An imported/history path alone must not grant local access.");row.OwnedPaths=owned;
        row.StageDir="active-stage";Require(LocalPosterInput.Capture(row) is null,"A staging directory must not be admitted.");row.StageDir="";
        row.PendingRemoval=true;Require(LocalPosterInput.Capture(row) is null,"Removal-pending results must not open native file readers.");row.PendingRemoval=false;
        row.CleanupPaths=[row.Path];Require(LocalPosterInput.Capture(row) is null,"Cleanup ownership must not be mistaken for publication.");row.CleanupPaths=[];
        var captured=LocalPosterInput.Capture(row)!;row.Title="Later metadata title";
        Require(captured.Matches(row),"A title-only metadata update must not invalidate an otherwise identical successful attempt.");
        row.Path+=".other";Require(!captured.Matches(row),"A changed successful result must reject the old callback.");row.Path=owned[0];
    }
    private static async Task UnsupportedFiles(string root,string ffmpeg,LocalPosterInput original)
    {
        string audio=Path.Combine(root,"audio.mp4");await Tool(ffmpeg,["-f","lavfi","-i","sine=frequency=440:duration=0.4","-c:a","aac",audio]);
        string cover=Path.Combine(root,"cover.mp4");
        await Tool(ffmpeg,["-f","lavfi","-i","sine=frequency=440:duration=0.4","-f","lavfi","-i","color=c=yellow:s=160x90:r=1:d=1",
            "-map","0:a","-map","1:v","-c:a","aac","-c:v","mjpeg","-disposition:v","attached_pic","-frames:v","1","-t","0.4",cover]);
        string corrupt=Path.Combine(root,"corrupt.mp4");File.WriteAllText(corrupt,"Not a video");
        string manifest=Path.Combine(root,"manifest.mp4");File.WriteAllText(manifest,"#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n"+original.Path+"\n#EXT-X-ENDLIST\n");
        foreach(string path in new[]{audio,cover,corrupt,manifest})
        {
            var input=LocalPosterInput.Capture(Row(root,path,Path.GetFileNameWithoutExtension(path)))!;
            Require(await new LocalPosterDecoder(ffmpeg).DecodeAsync(input,CancellationToken.None) is null,
                "Audio-only/attached-cover/corrupt/disguised local playlists must not manufacture a video frame: "+path);
        }
        var outside=original with {OutputRoot=Path.Combine(root,"other-root")};
        Require(await Refuses(()=>new LocalPosterDecoder(ffmpeg).DecodeAsync(outside,CancellationToken.None)) is IOException,"A published path outside its result root must be rejected.");
        Require(await new LocalPosterDecoder(Path.Combine(root,"missing-ffmpeg")).DecodeAsync(original,CancellationToken.None) is null,"A missing optional tool must return no poster.");
    }
    private static async Task CacheMerges(string root,string ffmpeg,LocalPosterInput input,byte[] remoteBytes)
    {
        foreach(var (invalid,expired) in new[]{(false,false),(true,false),(false,true)})
        {
            var storage=new AppPersistence(Path.Combine(root,invalid?"invalid-profile":expired?"expired-profile":"empty-profile"));
            DateTime saved=DateTime.UtcNow.AddHours(expired?-100:-4);
            Seed(storage,input.Url,saved,invalid?"iVBORw0KGgo=":"");
            var before=JsonNode.Parse(File.ReadAllText(CacheFile(storage,input.Url)))!.AsObject();
            int metadataCallbacks=0,published=0;
            using var service=new PreviewService(Path.Combine(root,"must-not-start-yt-dlp"),storage,()=>"",_=>metadataCallbacks++,_=>{},ffmpegPath:ffmpeg);
            try
            {
                if(invalid)Require(service.Get(input.Url)?.Title=="Metadata title" && File.Exists(CacheFile(storage,input.Url)),
                    "An ordinary cold-cache read must retain valid metadata when only its optional image is damaged.");
                bool result=await service.RequestLocalPosterAsync(input,(commit,_)=>{commit();published++;return Task.FromResult(true);},CancellationToken.None);
                Require(result && published==1 && metadataCallbacks==0 && service.Peek(input.Url)?.ThumbnailBase64.Length>0,
                    "A local-only request must publish through the existing memory cache without starting metadata or requiring selection.");
                var after=JsonNode.Parse(File.ReadAllText(CacheFile(storage,input.Url)))!.AsObject();
                foreach(string name in new[]{"SavedAtUtc","Success","PreviewFetchVersion","ThumbnailError","Title","Formats","FormatsLoaded","FutureField"})
                    Require(JsonNode.DeepEquals(before[name],after[name]),"Local image-only merge changed metadata envelope field "+name);
                Require(service.Peek(input.Url)?.SavedAtUtc==saved && !Directory.EnumerateFiles(root,"*.jpg").Any() && storage.ReadHistory().Count==0,
                    "A frame must not refresh metadata TTL, create media sidecars or append successful history.");
                Require(!await service.RequestLocalPosterAsync(input,(_,_)=>throw new InvalidOperationException("Unchanged local poster must not republish"),CancellationToken.None),
                    "An unchanged existing local frame must be reused.");
                if(expired)
                {
                    service.RequestFormats(input.Url);
                    await Until(()=>Volatile.Read(ref metadataCallbacks)>0,"A fresh local image must not mark expired metadata/formats fresh.");
                    Require(service.Peek(input.Url)?.ThumbnailBase64.Length>0,"A failed metadata refresh must keep the local image visible.");
                }
            }
            finally{service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
        }
        await RemoteImageWins(root,ffmpeg,input,remoteBytes);
        await RemoteImageWins(root,ffmpeg,input,remoteBytes,recoverFallback:true);
        await RemoteImageWins(root,ffmpeg,input,remoteBytes,recoverFallback:true,converterRetry:true);
        var partialStorage=new AppPersistence(Path.Combine(root,"partial-remote-profile"));Seed(partialStorage,input.Url,DateTime.UtcNow,"iVBORw0KGgo=");
        var partial=JsonNode.Parse(File.ReadAllText(CacheFile(partialStorage,input.Url)))!.AsObject();
        string goodRemote=Convert.ToBase64String(remoteBytes);partial["ThumbnailsBase64"]=JsonSerializer.SerializeToNode(new[]{"iVBORw0KGgo=",goodRemote});
        File.WriteAllText(CacheFile(partialStorage,input.Url),partial.ToJsonString());
        using(var service=new PreviewService("must-not-run",partialStorage,()=>"",_=>{},_=>{},ffmpegPath:ffmpeg))
        {
            try
            {
                Require(!await service.RequestLocalPosterAsync(input,(_,_)=>throw new InvalidOperationException("A surviving remote gallery image must win."),CancellationToken.None) &&
                    service.Peek(input.Url)?.ThumbnailBase64==goodRemote && service.Peek(input.Url)?.LocalThumbnailFingerprint=="",
                    "A damaged first cached image must not discard another usable remote image in the same gallery.");
            }
            finally{service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
        }
        var staleStorage=new AppPersistence(Path.Combine(root,"stale-profile"));
        using(var service=new PreviewService("must-not-run",staleStorage,()=>"",_=>{},_=>{},ffmpegPath:ffmpeg))
        {
            try
            {
                Require(!await service.RequestLocalPosterAsync(input,(_,_)=>Task.FromResult(false),CancellationToken.None) && service.Peek(input.Url) is null,
                    "A stale/removed attempt rejected by the UI owner must never populate the image cache.");
            }
            finally{service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
        }
    }
    private static async Task RemoteImageWins(string root,string ffmpeg,LocalPosterInput input,byte[] image,bool recoverFallback=false,bool converterRetry=false)
    {
        string name=converterRetry?"converter-recovery":recoverFallback?"remote-recovery":"remote";
        string remoteRoot=Path.Combine(root,name);Directory.CreateDirectory(remoteRoot);
        string? oldRoot=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_ROOT"),oldImage=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_IMAGE");
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();using var stop=new CancellationTokenSource();
        Task response=Serve();
        string address="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port+"/cover.png";
        Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_ROOT",remoteRoot);Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_IMAGE",address);
        var storage=new AppPersistence(Path.Combine(root,name+"-profile"));var logs=new List<string>();
        using var service=new PreviewService(TestExecutable(),storage,()=>"",_=>{},logs.Add,ffmpegPath:ffmpeg);
        try
        {
            string url="https://local-poster.invalid/remote";
            if(recoverFallback)
            {
                Seed(storage,url,converterRetry?DateTime.UtcNow:DateTime.UtcNow.AddMinutes(-2),"",
                    converterRetry?"WebP/AVIF thumbnail needs FFmpeg; text metadata is available.":"HTTP 404");
                Require(await service.RequestLocalPosterAsync(input with{Url=url},(commit,_)=>{commit();return Task.FromResult(true);},CancellationToken.None),
                    "A failed remote image must first acquire its real local fallback.");
            }
            service.Request(url);
            await Until(()=>File.Exists(Path.Combine(remoteRoot,"metadata-started")),"The real in-flight metadata child did not start.");
            if(recoverFallback)Require(service.Peek(url) is {ThumbnailBase64.Length:>0,LocalThumbnailFingerprint.Length:64},
                "The local image must remain visible while an explicit post-cooldown remote retry is in flight.");
            var local=service.RequestLocalPosterAsync(input with{Url=url},(_,_)=>throw new InvalidOperationException("A usable remote image must suppress fallback."),CancellationToken.None);
            File.WriteAllText(Path.Combine(remoteRoot,"metadata-release"),"");
            Require(!await local.WaitAsync(TimeSpan.FromSeconds(10)) && service.Peek(url)?.ThumbnailBase64==Convert.ToBase64String(image) &&
                service.Peek(url)?.LocalThumbnailFingerprint=="","An already in-flight actual remote image must win over queued local extraction: "+string.Join("\n",logs));
            await response.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            File.WriteAllText(Path.Combine(remoteRoot,"metadata-release"),"");service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            stop.Cancel();listener.Stop();try{await response;}catch(Exception) when(stop.IsCancellationRequested){}
            Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_ROOT",oldRoot);Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_REMOTE_IMAGE",oldImage);
        }
        async Task Serve()
        {
            using var client=await listener.AcceptTcpClientAsync(stop.Token);using var stream=client.GetStream();
            byte[] one=new byte[1];var header=new List<byte>();
            while(header.Count<8192 && await stream.ReadAsync(one,stop.Token)>0)
            {header.Add(one[0]);if(header.Count>=4 && header[^4]==13 && header[^3]==10 && header[^2]==13 && header[^1]==10)break;}
            await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: "+image.Length+"\r\nConnection: close\r\n\r\n"),stop.Token);
            await stream.WriteAsync(image,stop.Token);
        }
    }
    private static async Task QueuedCancellation(string root,string ffmpeg,LocalPosterInput input)
    {
        using var service=new PreviewService("must-not-run",new AppPersistence(Path.Combine(root,"queue-profile")),()=>"",_=>{},_=>{},ffmpegPath:ffmpeg);
        using var cancel=new CancellationTokenSource();var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var active=service.RequestLocalPosterAsync(input,async(_,token)=>{entered.TrySetResult();await Task.Delay(Timeout.Infinite,token);return true;},cancel.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var queued=Enumerable.Range(0,PreviewService.MaxPendingLocalPosters).Select(index=>service.RequestLocalPosterAsync(input with{Url=input.Url+index},
                (_,_)=>throw new InvalidOperationException("Cancelled queued work must never publish"),cancel.Token)).ToArray();
            Require(!await service.RequestLocalPosterAsync(input with{Url=input.Url+"overflow"},(_,_)=>Task.FromResult(true),CancellationToken.None),
                "The existing worker's local-only queue must enforce its 64-request limit.");
            cancel.Cancel();
            foreach(var task in queued.Append(active))Require(await Refuses(()=>task) is OperationCanceledException,"Both queued and active local work must retain cancellation semantics.");
        }
        finally{cancel.Cancel();service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
    }
    private static async Task JoinedCancellation(string root,LocalPosterInput input)
    {
        string executable=TestExecutable();
        string? oldMode=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_FIXTURE"),oldReady=Environment.GetEnvironmentVariable("VD_LOCAL_POSTER_READY");
        try
        {
            foreach(string mode in new[]{"stall","flood"})
            {
                string ready=Path.Combine(root,"pid-"+mode);Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_FIXTURE",mode);Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_READY",ready);
                using var cancel=new CancellationTokenSource();
                var decoder=new LocalPosterDecoder(executable,executable,TimeSpan.FromSeconds(5));
                var task=decoder.DecodeAsync(input,cancel.Token);
                await Until(()=>File.Exists(ready),"The owned poster fixture child did not start.");int pid=int.Parse(File.ReadAllText(ready),CultureInfo.InvariantCulture);
                var clock=Stopwatch.StartNew();if(mode=="stall")cancel.Cancel();
                var error=await Refuses(()=>task);
                Require(mode=="stall"?error is OperationCanceledException:error is IOException,"Cancellation/byte overflow must terminate the poster child.");
                Require(clock.Elapsed<TimeSpan.FromSeconds(3) && !Alive(pid),"The decoder must join the owned child and pipe readers before returning.");
            }
            string timeoutReady=Path.Combine(root,"pid-timeout");Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_FIXTURE","stall");Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_READY",timeoutReady);
            var timed=new LocalPosterDecoder(executable,executable,TimeSpan.FromSeconds(3)).DecodeAsync(input,CancellationToken.None);
            await Until(()=>File.Exists(timeoutReady),"Timeout child did not start.");int timeoutPid=int.Parse(File.ReadAllText(timeoutReady),CultureInfo.InvariantCulture);
            Require(await Refuses(()=>timed) is OperationCanceledException && !Alive(timeoutPid),"The shared probe/decode deadline must join the stalled child.");
        }
        finally{Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_FIXTURE",oldMode);Environment.SetEnvironmentVariable("VD_LOCAL_POSTER_READY",oldReady);}
    }
    private static bool Alive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static string TestExecutable()=>Path.Combine(AppContext.BaseDirectory,typeof(LocalPosterTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Seed(AppPersistence storage,string url,DateTime saved,string thumbnail,string thumbnailError="HTTP 404")
    {
        var data=new PreviewData(url,"Metadata title","Uploader","Playlist",4,"fixture",160,10,"h264","SDR",thumbnail,saved)
        {ThumbnailsBase64=thumbnail.Length>0?[thumbnail]:[],FormatsLoaded=true};
        var json=JsonSerializer.SerializeToNode(data,AppPersistence.Json)!.AsObject();
        json["CacheVersion"]=1;json["PreviewFetchVersion"]=3;json["Success"]=false;json["ThumbnailError"]=thumbnailError;json["FutureField"]="preserve me";
        AppPersistence.AtomicWrite(CacheFile(storage,url),json.ToJsonString(AppPersistence.Json));
    }
    private static string CacheFile(AppPersistence storage,string url)=>Path.Combine(storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
    private static string Hash(string file)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)));
    private static async Task<byte[]> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin","-threads","1","-filter_threads","1"}.Concat(arguments))start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var output=await BoundedProcessOutput.ReadAsync(child,2*1024*1024,128*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"Actual media fixture failed: "+Encoding.UTF8.GetString(output.StandardError));return output.StandardOutput;
    }
    private static async Task Until(Func<bool> ready,string message){var clock=Stopwatch.StartNew();while(!ready()){Require(clock.Elapsed<TimeSpan.FromSeconds(5),message);await Task.Delay(10);}}
    private static async Task<Exception> Refuses(Func<Task> action){try{await action().WaitAsync(TimeSpan.FromSeconds(8));}catch(Exception error){return error;}throw new InvalidOperationException("The refused local poster unexpectedly succeeded.");}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Local poster: "+message);}
}
