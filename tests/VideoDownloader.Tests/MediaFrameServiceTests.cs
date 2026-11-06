using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class MediaFrameServiceTests
{
    private const string Prefix="https://media-frame.invalid/";
    private const string ControlPrefix="# VD_FRAME_CONFIG:";
    private sealed record Control(string Root,string MediaUrl,string Context,bool Hold=false,bool Fail=false);

    internal static bool TryRunFixture(string[] arguments)
    {
        string url=arguments.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        Require(arguments.Contains("--dump-single-json") && arguments.Contains("--skip-download"),"Frames must resolve through a metadata-only child");
        int index=Array.IndexOf(arguments,"--cookies");
        Require(index>=0,"The fixture requires its isolated cookie jar");
        string first=File.ReadLines(arguments[index+1]).First();
        Require(first.StartsWith(ControlPrefix,StringComparison.Ordinal),"Fixture control comment is missing");
        var control=JsonSerializer.Deserialize<Control>(first[ControlPrefix.Length..])!;
        string name=url[Prefix.Length..];
        Require(name.All(character=>char.IsAsciiLetterOrDigit(character)||character=='-'),"Unexpected metadata fixture name");
        File.AppendAllText(Path.Combine(control.Root,name+".calls"),control.Context+Environment.NewLine);
        File.WriteAllText(Path.Combine(control.Root,"started-"+control.Context),Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        if(control.Hold)
            while(!File.Exists(Path.Combine(control.Root,"release-"+control.Context)))Thread.Sleep(10);
        if(control.Fail)
        {
            Console.Error.WriteLine("ERROR: HTTP Error 403: Forbidden");Environment.ExitCode=1;return true;
        }
        Console.WriteLine(JsonSerializer.Serialize(new {
            id="frame-fixture",title="Frame fixture",duration=4,extractor="fixture",
            http_headers=new Dictionary<string,string> { ["Authorization"]="Bearer HEADER_"+control.Context,["User-Agent"]="VD-FrameFixture" },
            formats=new object[] {
                new {format_id="v1",url=control.MediaUrl,protocol="http",ext="mp4",vcodec="avc1",acodec="none",width=160,height=90,fps=10,
                    cookies="session=SESSION_"+control.Context+"; Domain=127.0.0.1; Path=/media/"},
                new {format_id="a1",url=control.MediaUrl,protocol="http",ext="m4a",vcodec="none",acodec="aac"},
                new {format_id="hls",url=control.MediaUrl+"/playlist.m3u8",protocol="m3u8_native",ext="mp4",vcodec="avc1",acodec="aac"},
                new {format_id="drm",url=control.MediaUrl,protocol="http",ext="mp4",vcodec="avc1",acodec="none",has_drm=true},
            }
        }));
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-media-frames-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await CoalescedSourcesAndPrivacy(Path.Combine(root,"sources"));
            await CookieContextHandoff(Path.Combine(root,"context-success"),false);
            await CookieContextHandoff(Path.Combine(root,"context-failure"),true);
            await CookieContextHandoff(Path.Combine(root,"context-same-path"),false,samePath:true);
            await RealFramesAndCancellation(Path.Combine(root,"frames"));
            Console.WriteLine("PASS: actual boundary frames, metadata coalescing/privacy, cookie-context handoff, manifest isolation and joined cancellation");
        }
        finally{Directory.Delete(root,true);}
    }
    private static string CookieFile(Control control,string filename)
    {
        Directory.CreateDirectory(control.Root);
        string path=Path.Combine(control.Root,filename);
        File.WriteAllText(path,ControlPrefix+JsonSerializer.Serialize(control)+Environment.NewLine+
            "# Netscape HTTP Cookie File\n127.0.0.1\tFALSE\t/media/\tFALSE\t0\tauth\tCOOKIE_"+control.Context+"\n"+
            "127.0.0.1\tFALSE\t/private/\tFALSE\t0\twrongpath\tDO_NOT_SEND\n"+
            "unrelated.invalid\tFALSE\t/\tFALSE\t0\twronghost\tDO_NOT_SEND\n");
        return path;
    }
    private static async Task CoalescedSourcesAndPrivacy(string root)
    {
        string cookie=CookieFile(new(root,"http://127.0.0.1:1/media/clip.mp4","A",Hold:true),"cookies.txt");
        var data=new AppPersistence(Path.Combine(root,"profile"));
        var logs=new ConcurrentQueue<string>();
        await using var owned=Own(new PreviewService(TestExecutable(),data,()=>cookie,_=>{},logs.Enqueue),root);
        var previews=owned.Service;
        using var cancelled=new CancellationTokenSource();
        var discarded=previews.GetFrameSourceAsync(Prefix+"coalesced","v1",cancelled.Token);
        var tasks=Enumerable.Range(0,12).Select(_=>previews.GetFrameSourceAsync(Prefix+"coalesced","v1",CancellationToken.None)).ToArray();
        await Until(()=>File.Exists(Path.Combine(root,"started-A")),"The shared metadata child did not start");
        cancelled.Cancel();
        await RefusesCancellation(discarded);
        File.WriteAllText(Path.Combine(root,"release-A"),"");
        var sources=await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
        Require(sources.All(source=>source.Headers["Authorization"]=="Bearer HEADER_A") && File.ReadAllLines(Path.Combine(root,"coalesced.calls")).Length==1,
            "Concurrent frame requests must share the sole metadata job while another cancelled waiter stays isolated");
        _=await previews.GetFrameSourceAsync(Prefix+"coalesced","v1",CancellationToken.None);
        Require(File.ReadAllLines(Path.Combine(root,"coalesced.calls")).Length==1,"Fresh memory sources must not trigger another extraction");
        foreach(string unsupported in new[]{"a1","hls","drm"})
        {
            var error=await Refuses(()=>previews.GetFrameSourceAsync(Prefix+"coalesced",unsupported,CancellationToken.None));
            Require(error is MediaFrameUnavailableException,"Unsupported audio/manifest/DRM formats must give an honest no-frame result");
        }
        int calls=File.ReadAllLines(Path.Combine(root,"coalesced.calls")).Length;
        _=await Refuses(()=>previews.GetFrameSourceAsync(Prefix+"coalesced","a1",CancellationToken.None));
        Require(File.ReadAllLines(Path.Combine(root,"coalesced.calls")).Length==calls,"Repeated unsupported requests must reuse their short-lived negative source result");
        _=CookieFile(new(root,"http://127.0.0.1:1/media/new.mp4","B"),"cookies.txt");
        var changed=await previews.GetFrameSourceAsync(Prefix+"coalesced","v1",CancellationToken.None);
        Require(changed.Headers["Authorization"]=="Bearer HEADER_B" && changed.Address.AbsolutePath=="/media/new.mp4" &&
            File.ReadAllLines(Path.Combine(root,"coalesced.calls")).Length==calls+1,
            "Replacing credentials at the same cookie-file path must invalidate the private source cache");
        await Until(()=>Directory.EnumerateFiles(data.PreviewDir,"*.json").Any(),"The ordinary preview cache was not saved");
        string stored=string.Join("\n",Directory.EnumerateFiles(data.PreviewDir,"*.json").Select(File.ReadAllText));
        Require(!stored.Contains("HEADER_A",StringComparison.Ordinal) && !stored.Contains("SESSION_A",StringComparison.Ordinal) &&
            !stored.Contains("HEADER_B",StringComparison.Ordinal) && !stored.Contains("SESSION_B",StringComparison.Ordinal) &&
            !stored.Contains("127.0.0.1:1",StringComparison.Ordinal) && !string.Join("\n",logs).Contains("HEADER_A",StringComparison.Ordinal),
            "Signed media URLs, authorization and cookies must remain outside the persistent preview cache and diagnostics");
    }
    private static async Task CookieContextHandoff(string root,bool failFirst,bool samePath=false)
    {
        string a=CookieFile(new(root,"http://127.0.0.1:1/media/a.mp4","A",Hold:true,Fail:failFirst),"a.txt");
        string b=samePath?a:CookieFile(new(root,"http://127.0.0.1:1/media/b.mp4","B",Hold:true),"b.txt");
        string current=a;
        await using var owned=Own(new PreviewService(TestExecutable(),new AppPersistence(Path.Combine(root,"profile")),()=>Volatile.Read(ref current),_=>{},_=>{}),root);
        var previews=owned.Service;
        var first=previews.GetFrameSourceAsync(Prefix+"handoff","v1",CancellationToken.None);
        await Until(()=>File.Exists(Path.Combine(root,"started-A")),"Cookie-A extraction did not start");
        if(samePath)_=CookieFile(new(root,"http://127.0.0.1:1/media/b.mp4","B",Hold:true),"a.txt");
        Volatile.Write(ref current,b);
        var second=previews.GetFrameSourceAsync(Prefix+"handoff","v1",CancellationToken.None);
        File.WriteAllText(Path.Combine(root,"release-A"),"");
        if(failFirst)_=await Refuses(()=>first);
        else Require((await first.WaitAsync(TimeSpan.FromSeconds(10))).Headers["Authorization"]=="Bearer HEADER_A","Cookie-A waiter must retain its source context");
        await Until(()=>File.Exists(Path.Combine(root,"started-B")),"A changed cookie context must requeue its own extraction even after the old context fails");
        Require(!second.IsCompleted,"Completing or failing extraction A must never complete a waiter for cookie context B");
        File.WriteAllText(Path.Combine(root,"release-B"),"");
        var resolved=await second.WaitAsync(TimeSpan.FromSeconds(10));
        Require(resolved.Headers["Authorization"]=="Bearer HEADER_B" && resolved.Address.AbsolutePath=="/media/b.mp4" &&
            File.ReadAllLines(Path.Combine(root,"handoff.calls")).SequenceEqual(new[]{"A","B"}),
            "The replacement context must resolve exactly its own authenticated stream through the same worker");
    }
    private static async Task RealFramesAndCancellation(string root)
    {
        Directory.CreateDirectory(root);
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        Require(File.Exists(ffmpeg),"Actual frame tests require FFmpeg");
        string video=Path.Combine(root,"colours.mp4");
        _=await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i","color=c=red:s=160x90:r=10:d=2",
            "-f","lavfi","-i","color=c=blue:s=160x90:r=10:d=2","-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0",
            "-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-movflags","+faststart",video});
        await using var origin=new MediaOrigin(await File.ReadAllBytesAsync(video));
        string cookie=CookieFile(new(root,new Uri(origin.Address,"/media/clip.mp4").AbsoluteUri,"A"),"cookies.txt");
        var data=new AppPersistence(Path.Combine(root,"profile"));
        await using var owned=Own(new PreviewService(TestExecutable(),data,()=>cookie,_=>{},_=>{}),root);
        var previews=owned.Service;
        using var frames=new MediaFrameService(previews,ffmpeg);
        string url=Prefix+"colours";
        var red=await frames.GetFrameAsync(new(url,.5,"v1",4),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        var blue=await frames.GetFrameAsync(new(url,3,"v1",4),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
        Require(red.Success && blue.Success && red.SampledSeconds==.5 && blue.SampledSeconds==3,"Actual FFmpeg must return both requested timestamp images: "+red.Error+" / "+blue.Error);
        byte[] r=await Pixel(ffmpeg,root,"red",red.PngBytes),b=await Pixel(ffmpeg,root,"blue",blue.PngBytes);
        Require(r[0]>200 && r[2]<80 && b[2]>200 && b[0]<80,"Changing time across the colour boundary must return different decoded video frames");
        var end=await frames.GetFrameAsync(new(url,4,"v1",4,IsEnd:true),CancellationToken.None);
        Require(end.Success && end.RequestedSeconds==4 && Math.Abs(end.SampledSeconds!.Value-3.9)<.0001,
            "The exclusive end at duration must sample the preceding video frame instead of seeking past EOF: "+end.Error);
        var automatic=await frames.GetFrameAsync(new(url,.5,DurationSeconds:4),CancellationToken.None);
        Require(automatic.Success,"Automatic mode must select a modest supported source from the existing metadata");
        int beforeAudio=origin.Requests.Count;
        var audio=await frames.GetFrameAsync(new(url,1,"a1",4),CancellationToken.None);
        Require(!audio.Success && origin.Requests.Count==beforeAudio,"An audio-only format must report no video frame without fetching media");
        Require(File.ReadAllLines(Path.Combine(root,"colours.calls")).Length==1 && origin.Requests.Any(request=>request.Contains("Range: bytes=",StringComparison.OrdinalIgnoreCase)),
            "Timestamp changes must reuse the private source while FFmpeg reads through range-limited HTTP");
        Require(origin.Requests.All(request=>request.Contains("Authorization: Bearer HEADER_A",StringComparison.OrdinalIgnoreCase) &&
            request.Contains("auth=COOKIE_A",StringComparison.Ordinal) && request.Contains("session=SESSION_A",StringComparison.Ordinal) &&
            !request.Contains("DO_NOT_SEND",StringComparison.Ordinal)),"The real decoder must receive scoped imported and metadata cookies plus required headers");
        Require(data.ReadHistory().Count==0,"Frame rendering must not record a downloaded URL");

        string manifestCookie=CookieFile(new(root,new Uri(origin.Address,"/media/manifest.mp4").AbsoluteUri,"M"),"manifest.txt");
        await using var ownedManifest=Own(new PreviewService(TestExecutable(),new AppPersistence(Path.Combine(root,"manifest-profile")),()=>manifestCookie,_=>{},_=>{}),root);
        var manifestPreviews=ownedManifest.Service;
        using var manifestFrames=new MediaFrameService(manifestPreviews,ffmpeg);
        var manifest=await manifestFrames.GetFrameAsync(new(Prefix+"manifest",0,"v1",4),CancellationToken.None);
        Require(!manifest.Success && origin.SecondaryRequests==0,
            "A malicious playlist disguised as MP4 must fail without fetching a secondary URL outside the bounded proxy");

        string stalledCookie=CookieFile(new(root,new Uri(origin.Address,"/media/stall.mp4").AbsoluteUri,"S"),"stall.txt");
        await using var ownedStalled=Own(new PreviewService(TestExecutable(),new AppPersistence(Path.Combine(root,"stall-profile")),()=>Volatile.Read(ref stalledCookie),_=>{},_=>{}),root);
        var stalledPreviews=ownedStalled.Service;
        using var stalledFrames=new MediaFrameService(stalledPreviews,ffmpeg,TimeSpan.FromSeconds(2));
        using var cancel=new CancellationTokenSource();
        var stalled=stalledFrames.GetFrameAsync(new(Prefix+"stall",1,"v1",4),cancel.Token);
        await origin.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var clock=Stopwatch.StartNew();cancel.Cancel();await RefusesCancellation(stalled);
        await origin.StalledClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Require(clock.Elapsed<TimeSpan.FromSeconds(3),"Cancelling a frame must close media HTTP and join FFmpeg before returning");
        var timeoutWatch=Stopwatch.StartNew();
        var timeout=await stalledFrames.GetFrameAsync(new(Prefix+"stall",1,"v1",4),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Require(!timeout.Success && timeout.Error.Contains("истекло",StringComparison.Ordinal) && timeoutWatch.Elapsed<TimeSpan.FromSeconds(5),
            "The overall frame deadline must close a stalled source and report a retryable no-frame result");
        string recoveredCookie=CookieFile(new(root,new Uri(origin.Address,"/media/clip.mp4").AbsoluteUri,"S2"),"recovered.txt");
        Volatile.Write(ref stalledCookie,recoveredCookie);
        var healthy=await stalledFrames.GetFrameAsync(new(Prefix+"recovered",1,"v1",4),CancellationToken.None);
        Require(healthy.Success,"Cancellation and timeout must release the same serialized decoder so its next frame can succeed: "+healthy.Error);

        // A second path uses the actual packaged extractor too, so a fixture
        // cannot hide differences in generic direct-media JSON or null codecs.
        string actualCookie=CookieFile(new(root,new Uri(origin.Address,"/media/clip.mp4").AbsoluteUri,"REAL"),"actual-cookies.txt");
        string actualJar=File.ReadAllText(actualCookie);
        File.WriteAllText(actualCookie,"# Netscape HTTP Cookie File\n"+actualJar);
        var actualLogs=new ConcurrentQueue<string>();
        await using var ownedActual=Own(new PreviewService(DependencyCenter.Executable("yt-dlp.exe"),new AppPersistence(Path.Combine(root,"actual-profile")),()=>actualCookie,_=>{},actualLogs.Enqueue),root);
        using var actualFrames=new MediaFrameService(ownedActual.Service,ffmpeg);
        var extracted=await actualFrames.GetFrameAsync(new(new Uri(origin.Address,"/media/clip.mp4").AbsoluteUri,3,DurationSeconds:4),CancellationToken.None);
        Require(extracted.Success,"Actual yt-dlp metadata must resolve a direct progressive video's boundary frame: "+extracted.Error+"\n"+string.Join("\n",actualLogs));
        var actualPixel=await Pixel(ffmpeg,root,"actual",extracted.PngBytes);
        Require(actualPixel[2]>200 && actualPixel[0]<80,"The actual extractor/decoder path must render the requested blue source frame");
    }
    private static async Task<byte[]> Pixel(string ffmpeg,string root,string name,byte[] png)
    {
        string path=Path.Combine(root,name+".png");await File.WriteAllBytesAsync(path,png);
        var pixel=await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-i",path,"-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"});
        Require(pixel.Length==3,"The decoded PNG must contain one inspectable RGB pixel");return pixel;
    }
    private static async Task<byte[]> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output=await BoundedProcessOutput.ReadAsync(child,1024*1024,128*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"The actual media fixture tool failed: "+Encoding.UTF8.GetString(output.StandardError));return output.StandardOutput;
    }
    private static string TestExecutable()=>Path.Combine(AppContext.BaseDirectory,typeof(MediaFrameServiceTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static async Task Until(Func<bool> condition,string message)
    {
        var clock=Stopwatch.StartNew();while(!condition()){Require(clock.Elapsed<TimeSpan.FromSeconds(10),message);await Task.Delay(10);}
    }
    private static async Task<Exception> Refuses(Func<Task> action)
    {
        try{await action().WaitAsync(TimeSpan.FromSeconds(10));}catch(Exception error){return error;}
        throw new InvalidOperationException("The frame fixture unexpectedly succeeded");
    }
    private static async Task RefusesCancellation(Task task)
    {
        var error=await Refuses(()=>task);Require(error is OperationCanceledException,"Caller cancellation must retain cancellation semantics: "+error.GetType().Name);
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Media frame service: "+message);}

    private static PreviewLifetime Own(PreviewService service,string root)=>new(service,root);
    private sealed class PreviewLifetime(PreviewService service,string root):IAsyncDisposable
    {
        internal PreviewService Service=>service;
        public async ValueTask DisposeAsync()
        {
            // Release only this fixture's held metadata children even if an
            // assertion fails, then join the actual worker before deleting data.
            if(Directory.Exists(root))
                foreach(string started in Directory.EnumerateFiles(root,"started-*"))
                    File.WriteAllText(Path.Combine(root,"release-"+Path.GetFileName(started)[8..]),"");
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class MediaOrigin:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _handlers=[];
        private readonly ConcurrentDictionary<TcpClient,byte> _clients=[];
        private readonly byte[] _media;
        private readonly Task _accept;
        private int _secondary;
        internal Uri Address {get;}
        internal int SecondaryRequests=>Volatile.Read(ref _secondary);
        internal ConcurrentQueue<string> Requests {get;}=[];
        internal TaskCompletionSource Stalled {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StalledClosed {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal MediaOrigin(byte[] media)
        {
            _media=media;_listener.Start();Address=new("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/");_accept=Accept();
        }
        private async Task Accept()
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients[client]=0;_handlers.Add(Serve(client));
                }
            }
            catch(Exception) when(_stop.IsCancellationRequested){ }
        }
        private async Task Serve(TcpClient client)
        {
            bool stalled=false;
            using(client)
            try
            {
                using var stream=client.GetStream();var bytes=new List<byte>();byte[] one=new byte[1];
                while(bytes.Count<8192)
                {
                    if(await stream.ReadAsync(one,_stop.Token)==0)return;bytes.Add(one[0]);
                    if(bytes.Count>=4 && bytes[^4]==13 && bytes[^3]==10 && bytes[^2]==13 && bytes[^1]==10)break;
                }
                string request=Encoding.ASCII.GetString(bytes.ToArray());Requests.Enqueue(request);
                string path=request.Split(' ')[1];
                if(path=="/secondary.mp4")Interlocked.Increment(ref _secondary);
                if(path=="/media/stall.mp4")
                {
                    stalled=true;await Write(stream,"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n");
                    Stalled.TrySetResult();while(await stream.ReadAsync(one,_stop.Token)>0){}return;
                }
                byte[] body=path=="/media/manifest.mp4"?Encoding.ASCII.GetBytes("#EXTM3U\n#EXT-X-TARGETDURATION:4\n#EXTINF:4,\n"+new Uri(Address,"secondary.mp4")+"\n#EXT-X-ENDLIST\n"):_media;
                long first=0,last=body.Length-1;bool ranged=false;
                string? header=request.Split("\r\n").FirstOrDefault(line=>line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase));
                if(header is not null)
                {
                    string value=header[(header.IndexOf('=')+1)..];var bounds=value.Trim().Split('-');
                    first=long.Parse(bounds[0],CultureInfo.InvariantCulture);if(bounds[1].Length>0)last=Math.Min(last,long.Parse(bounds[1],CultureInfo.InvariantCulture));ranged=true;
                }
                if(first>=body.Length){await Write(stream,"HTTP/1.1 416 Range Not Satisfiable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return;}
                await Write(stream,(ranged?"HTTP/1.1 206 Partial Content":"HTTP/1.1 200 OK")+"\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: "+(last-first+1)+
                    (ranged?"\r\nContent-Range: bytes "+first+"-"+last+"/"+body.Length:"")+"\r\nConnection: close\r\n\r\n");
                if(!request.StartsWith("HEAD ",StringComparison.Ordinal))await stream.WriteAsync(body.AsMemory((int)first,(int)(last-first+1)),_stop.Token);
            }
            catch(Exception error) when(error is IOException or OperationCanceledException or SocketException or ObjectDisposedException){ }
            finally{_clients.TryRemove(client,out _);if(stalled)StalledClosed.TrySetResult();}
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
