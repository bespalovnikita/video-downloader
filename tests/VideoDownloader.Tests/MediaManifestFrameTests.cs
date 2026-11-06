using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using VideoDownloader.Native;

internal static class MediaManifestFrameTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-manifest-frames-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
            Require(File.Exists(ffmpeg),"Manifest frame integration requires FFmpeg");
            string video=Path.Combine(root,"source.mp4");
            await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i","color=c=red:s=160x90:r=10:d=4",
                "-f","lavfi","-i","color=c=blue:s=160x90:r=10:d=4","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=8",
                "-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0[v]","-map","[v]","-map","2:a","-c:v","libx264","-preset","ultrafast",
                "-g","20","-keyint_min","20","-sc_threshold","0","-pix_fmt","yuv420p","-c:a","aac","-movflags","+faststart",video});
            await Hls(ffmpeg,video,root,"vod",false);
            await Hls(ffmpeg,video,root,"cmaf",true);
            await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-i",video,"-map","0:v","-map","0:a","-c","copy",
                "-f","dash","-seg_duration","2","-use_timeline","1","-use_template","1","dash.mpd"},root);
            ValidateDashResources(root,"dash.mpd");
            await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-i",video,"-map","0:v","-an","-c:v","libvpx-vp9","-deadline","realtime","-cpu-used","8",
                "-g","20","-f","dash","-seg_duration","2","-use_timeline","1","-use_template","1","webm.mpd"},root);
            ValidateDashResources(root,"webm.mpd");
            await using var origin=new Origin(root);
            File.WriteAllText(Path.Combine(root,"master.m3u8"),"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000,RESOLUTION=160x90,CODECS=\"avc1.42c00a,mp4a.40.2\"\nvod.m3u8\n");
            string key=Path.Combine(root,"key.bin");File.WriteAllBytes(key,Enumerable.Range(1,16).Select(value=>(byte)value).ToArray());
            string info=Path.Combine(root,"key-info.txt");
            File.WriteAllText(info,new Uri(origin.Address,"key.bin").AbsoluteUri+Environment.NewLine+key+Environment.NewLine+"00000000000000000000000000000001"+Environment.NewLine);
            await Hls(ffmpeg,video,root,"encrypted",false,info);

            var headers=new Dictionary<string,string>{{"Authorization","Bearer MANIFEST_PRIVATE"},{"X-Secret","SEGMENT_PRIVATE"},
                {"User-Agent","VD-ManifestFixture"},{"Referer","https://source.invalid/private?token=REFERER_PRIVATE"}};
            var cookie=Path.Combine(root,"cookies.txt");
            File.WriteAllText(cookie,"# Netscape HTTP Cookie File\n127.0.0.1\tFALSE\t/\tFALSE\t0\tmedia\tCOOKIE_PRIVATE\nlocalhost\tFALSE\t/\tFALSE\t0\tcdn\tCDN_PRIVATE\n");
            MediaFrameSource Source(string path,string protocol,string id="0",string extension="mp4")=>new(new Uri(origin.Address,path),id,headers,"",cookie,"fixture",10,8,160,90,protocol,new Uri(origin.Address,path),Extension:extension);
            foreach(var (path,protocol,extension) in new[]{("vod.m3u8","m3u8_native","mp4"),("master.m3u8","m3u8_native","mp4"),("cmaf.m3u8","m3u8_native","mp4"),("encrypted.m3u8","m3u8_native","mp4"),("dash.mpd","http_dash_segments","mp4"),("webm.mpd","http_dash_segments","webm")})
            {
                var source=Source(path,protocol,extension:extension);
                using var frames=new MediaFrameService((_,_)=>Task.FromResult(source),ffmpeg);
                var red=await frames.GetFrameAsync(new("https://manifest-frame.invalid/video",1,DurationSeconds:8),CancellationToken.None);
                var blue=await frames.GetFrameAsync(new("https://manifest-frame.invalid/video",7,DurationSeconds:8),CancellationToken.None);
                Require(red.Success && blue.Success,"Real "+path+" frames failed: "+red.Error+" / "+blue.Error);
                var first=await Pixel(ffmpeg,root,path+"-red",red.PngBytes);
                var last=await Pixel(ffmpeg,root,path+"-blue",blue.PngBytes);
                Require(first[0]>200 && first[2]<80 && last[2]>200 && last[0]<80,"Manifest seeking must return distinct red/blue source frames: "+path);
                var end=await frames.GetFrameAsync(new("https://manifest-frame.invalid/video",8,DurationSeconds:8,IsEnd:true),CancellationToken.None);
                Require(end.Success,"The manifest end boundary must return the preceding frame: "+path+" "+end.Error);
            }
            Require(origin.Requests.Any(request=>request.Contains("Authorization: Bearer MANIFEST_PRIVATE",StringComparison.OrdinalIgnoreCase)) &&
                origin.Requests.Any(request=>request.Contains("media=COOKIE_PRIVATE",StringComparison.Ordinal)),"Manifest and segments must retain scoped authorization");

            await SegmentList(ffmpeg,root,origin,Source("list.mpd","dash"),headers,cookie);
            await MoreDashAndAuth(ffmpeg,root,origin,Source("constant.mpd","dash"),headers,cookie);
            await SecurityAndCancellation(ffmpeg,root,origin,headers,cookie);
            await ActualExtractor(ffmpeg,root,origin,cookie);
            Console.WriteLine("PASS: actual HLS master/TS/fMP4/AES and DASH timeline/list frames, source-end seek, scoped authorization, manifest isolation and cancellation");
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task MoreDashAndAuth(string ffmpeg,string root,Origin origin,MediaFrameSource constant,
        IReadOnlyDictionary<string,string> headers,string cookie)
    {
        var document=System.Xml.Linq.XDocument.Load(Path.Combine(root,"dash.mpd"));
        foreach(var template in document.Descendants().Where(element=>element.Name.LocalName=="SegmentTemplate"))
        {
            var first=template.Elements().Where(element=>element.Name.LocalName=="SegmentTimeline").SelectMany(element=>element.Elements()).First();
            template.SetAttributeValue("duration",first.Attribute("d")!.Value);
            template.Elements().Where(element=>element.Name.LocalName=="SegmentTimeline").Remove();
        }
        document.Save(Path.Combine(root,"constant.mpd"));
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(constant),ffmpeg))
            Require((await frames.GetFrameAsync(new("https://manifest-frame.invalid/constant",7,DurationSeconds:8),CancellationToken.None)).Success,"DASH duration/Number templates must support seeking");
        File.WriteAllText(Path.Combine(root,"base.mpd"),"<MPD xmlns='urn:mpeg:dash:schema:mpd:2011' type='static' mediaPresentationDuration='PT8S'><Period><AdaptationSet mimeType='video/mp4'><Representation id='0' width='160' height='90'><BaseURL>source.mp4</BaseURL><SegmentBase indexRange='0-100'/></Representation></AdaptationSet></Period></MPD>");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(constant with{Address=new Uri(origin.Address,"base.mpd"),ManifestAddress=new Uri(origin.Address,"base.mpd")}),ffmpeg))
            Require((await frames.GetFrameAsync(new("https://manifest-frame.invalid/base",7,DurationSeconds:8),CancellationToken.None)).Success,"DASH SegmentBase must use the bounded progressive reader");
        int before=origin.Requests.Count;
        var alternate=new UriBuilder(origin.Address){Host="localhost"}.Uri;
        File.WriteAllText(Path.Combine(root,"cdn-master.m3u8"),"#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=100000,RESOLUTION=160x90\n"+new Uri(alternate,"vod.m3u8")+"\n");
        var cross=new MediaFrameSource(new Uri(origin.Address,"cdn-master.m3u8"),"0",headers,"",cookie,"fixture",10,8,160,90,"m3u8_native",Extension:"mp4");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(cross),ffmpeg))
            Require((await frames.GetFrameAsync(new("https://manifest-frame.invalid/cdn",7,DurationSeconds:8),CancellationToken.None)).Success,"HLS variants on a different origin must remain usable with their scoped cookies");
        var cdnRequests=origin.Requests.Skip(before).Where(request=>request.Contains("Host: localhost",StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(cdnRequests.Length>0 && cdnRequests.All(request=>request.Contains("cdn=CDN_PRIVATE",StringComparison.Ordinal) &&
            request.Contains("User-Agent: VD-ManifestFixture",StringComparison.OrdinalIgnoreCase) &&
            !request.Contains("MANIFEST_PRIVATE",StringComparison.Ordinal) && !request.Contains("SEGMENT_PRIVATE",StringComparison.Ordinal) &&
            !request.Contains("REFERER_PRIVATE",StringComparison.Ordinal) && !request.Contains("COOKIE_PRIVATE",StringComparison.Ordinal)),
            "Manifest subresources on another origin must strip private headers and select only that origin's cookies");
        int beforeDash=origin.Requests.Count;
        File.WriteAllText(Path.Combine(root,"base-cdn.mpd"),"<MPD xmlns='urn:mpeg:dash:schema:mpd:2011' type='static' mediaPresentationDuration='PT8S'><Period><AdaptationSet mimeType='video/mp4'><Representation id='0' width='160' height='90'><BaseURL>"+
            new Uri(alternate,"source.mp4").AbsoluteUri+"</BaseURL><SegmentBase indexRange='0-100'/></Representation></AdaptationSet></Period></MPD>");
        var dashCdn=constant with{Address=new Uri(origin.Address,"base-cdn.mpd"),ManifestAddress=new Uri(origin.Address,"base-cdn.mpd")};
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(dashCdn),ffmpeg))
            Require((await frames.GetFrameAsync(new("https://manifest-frame.invalid/dash-cdn",7,DurationSeconds:8),CancellationToken.None)).Success,"DASH SegmentBase on another origin must remain usable through the bounded relay");
        var dashCdnRequests=origin.Requests.Skip(beforeDash).Where(request=>request.Contains("Host: localhost",StringComparison.OrdinalIgnoreCase)).ToArray();
        Require(dashCdnRequests.Length>0 && dashCdnRequests.All(request=>request.Contains("cdn=CDN_PRIVATE",StringComparison.Ordinal) &&
            request.Contains("User-Agent: VD-ManifestFixture",StringComparison.OrdinalIgnoreCase) &&
            !request.Contains("MANIFEST_PRIVATE",StringComparison.Ordinal) && !request.Contains("SEGMENT_PRIVATE",StringComparison.Ordinal) &&
            !request.Contains("REFERER_PRIVATE",StringComparison.Ordinal) && !request.Contains("COOKIE_PRIVATE",StringComparison.Ordinal)),
            "DASH direct-media handoff must preserve the original MPD header origin instead of authorizing the CDN with source secrets");
    }
    private static async Task Hls(string ffmpeg,string video,string root,string name,bool fragmented,string? keyInfo=null)
    {
        var arguments=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-i",video,"-c","copy","-f","hls","-hls_time","2","-hls_playlist_type","vod"};
        if(fragmented){arguments.AddRange(new[]{"-hls_segment_type","fmp4","-hls_fmp4_init_filename",name+"-init.mp4"});}
        if(keyInfo is not null)arguments.AddRange(new[]{"-hls_key_info_file",keyInfo});
        // Keep muxer URLs independent of native path separators (notably Windows drive paths).
        arguments.AddRange(new[]{"-hls_segment_filename",name+"-%02d."+(fragmented?"m4s":"ts"),name+".m3u8"});
        await Tool(ffmpeg,arguments,root);
        foreach(string line in File.ReadLines(Path.Combine(root,name+".m3u8")))
        {
            string? resource=line.StartsWith("#EXT-X-MAP:URI=\"",StringComparison.Ordinal)
                ?line.Split('"')[1]:line.Length>0 && line[0]!='#'?line:null;
            if(resource is null)continue;
            RequireGeneratedResource(root,resource);
        }
    }
    private static void ValidateDashResources(string root,string manifest)
    {
        var document=XDocument.Load(Path.Combine(root,manifest));
        foreach(var representation in document.Descendants().Where(element=>element.Name.LocalName=="Representation"))
        {
            string id=representation.Attribute("id")!.Value;
            var template=representation.Elements().Single(element=>element.Name.LocalName=="SegmentTemplate");
            RequireGeneratedResource(root,template.Attribute("initialization")!.Value.Replace("$RepresentationID$",id,StringComparison.Ordinal));
            string media=template.Attribute("media")!.Value.Replace("$RepresentationID$",id,StringComparison.Ordinal);
            long number=(long?)template.Attribute("startNumber")??1;
            foreach(var segment in template.Descendants().Where(element=>element.Name.LocalName=="S"))
            {
                long repeat=(long?)segment.Attribute("r")??0;
                Require(repeat is >=0 and <100,"Generated DASH timeline must have bounded explicit repeats");
                for(long index=0;index<=repeat;index++,number++)
                {
                    string resource=Regex.Replace(media,@"\$Number(?:%0([0-9]{1,2})d)?\$",match=>number.ToString(
                        match.Groups[1].Success?"D"+match.Groups[1].Value:"D",CultureInfo.InvariantCulture));
                    RequireGeneratedResource(root,resource);
                }
            }
        }
    }
    private static void RequireGeneratedResource(string root,string resource)=>Require(
        !resource.Contains('\\') && !resource.Contains('$') && !Uri.TryCreate(resource,UriKind.Absolute,out _) &&
        Path.GetFileName(resource)==resource && File.Exists(Path.Combine(root,resource)),
        "Generated manifest resource must be a relative URL beside its manifest: "+resource);
    private static async Task SegmentList(string ffmpeg,string root,Origin origin,MediaFrameSource source,
        IReadOnlyDictionary<string,string> headers,string cookie)
    {
        var init=File.ReadAllBytes(Path.Combine(root,"init-stream0.m4s"));
        var parts=Directory.GetFiles(root,"chunk-stream0-*.m4s").OrderBy(value=>value,StringComparer.Ordinal).Select(File.ReadAllBytes).ToArray();
        Require(parts.Length==4,"The DASH fixture should contain four video fragments");
        using var all=new MemoryStream();all.Write(init);var ranges=new List<string>();
        foreach(var part in parts){long first=all.Length;all.Write(part);ranges.Add(first+"-"+(all.Length-1));}
        File.WriteAllBytes(Path.Combine(root,"ranges.mp4"),all.ToArray());
        string xml="<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT8S\"><Period><AdaptationSet mimeType=\"video/mp4\"><Representation id=\"0\" width=\"160\" height=\"90\"><BaseURL>ranges.mp4</BaseURL><SegmentList duration=\"2\" timescale=\"1\"><Initialization range=\"0-"+(init.Length-1)+"\"/>"+
            string.Join("",ranges.Select(range=>"<SegmentURL mediaRange=\""+range+"\"/>"))+"</SegmentList></Representation></AdaptationSet></Period></MPD>";
        File.WriteAllText(Path.Combine(root,"list.mpd"),xml);
        using var frames=new MediaFrameService((_,_)=>Task.FromResult(source),ffmpeg);
        var frame=await frames.GetFrameAsync(new("https://manifest-frame.invalid/list",7,DurationSeconds:8),CancellationToken.None);
        Require(frame.Success,"DASH SegmentList/init byte ranges failed: "+frame.Error);
        var pixel=await Pixel(ffmpeg,root,"list-blue",frame.PngBytes);Require(pixel[2]>200 && pixel[0]<80,"SegmentList must seek to the blue fragment");
        Require(origin.Requests.Any(request=>request.Contains("Range: bytes=",StringComparison.OrdinalIgnoreCase)),"DASH byte ranges must be applied by the owned transport");
    }
    private static async Task SecurityAndCancellation(string ffmpeg,string root,Origin origin,IReadOnlyDictionary<string,string> headers,string cookie)
    {
        MediaFrameSource Source(string path)=>new(new Uri(origin.Address,path),"0",headers,"",cookie,"fixture",10,8,160,90,"m3u8_native",new Uri(origin.Address,path),Extension:"mp4");
        File.WriteAllText(Path.Combine(root,"escape.m3u8"),"#EXTM3U\n#EXTINF:2,\nfile:///private/frame-fixture.ts\n#EXT-X-ENDLIST\n");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(Source("escape.m3u8")),ffmpeg))
        {
            var result=await frames.GetFrameAsync(new("https://manifest-frame.invalid/escape",1,DurationSeconds:8),CancellationToken.None);
            Require(!result.Success && !result.Error.Contains("PRIVATE",StringComparison.Ordinal),"Non-HTTP resource references must fail without revealing authorization");
        }
        File.WriteAllText(Path.Combine(root,"nested.ts"),"#EXTM3U\n#EXTINF:2,\n"+new Uri(origin.Address,"secondary.ts")+"\n#EXT-X-ENDLIST\n");
        File.WriteAllText(Path.Combine(root,"nested.m3u8"),"#EXTM3U\n#EXTINF:2,\nnested.ts\n#EXT-X-ENDLIST\n");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(Source("nested.m3u8")),ffmpeg))
        {
            var result=await frames.GetFrameAsync(new("https://manifest-frame.invalid/nested",1,DurationSeconds:8),CancellationToken.None);
            Require(!result.Success && origin.Secondary==0,"A nested disguised playlist must never reach FFmpeg or fetch outside the manifest resolver");
        }
        File.WriteAllText(Path.Combine(root,"live.m3u8"),"#EXTM3U\n#EXTINF:2,\nvod-00.ts\n");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(Source("live.m3u8")),ffmpeg))
            Require(!(await frames.GetFrameAsync(new("https://manifest-frame.invalid/live",1,DurationSeconds:8),CancellationToken.None)).Success,"An unfinished live playlist must not be treated as a bounded VOD");
        File.WriteAllText(Path.Combine(root,"xxe.mpd"),"<!DOCTYPE MPD [<!ENTITY secret SYSTEM '"+new Uri(origin.Address,"secondary.ts")+"'>]><MPD type='static'>&secret;</MPD>");
        var dash=Source("xxe.mpd") with{Protocol="dash"};
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(dash),ffmpeg))
            Require(!(await frames.GetFrameAsync(new("https://manifest-frame.invalid/xxe",1),CancellationToken.None)).Success && origin.Secondary==0,"XML external entities must be rejected before any secondary request");
        File.WriteAllText(Path.Combine(root,"big.m3u8"),"#EXTM3U\n#EXTINF:2,\nbig.ts\n#EXT-X-ENDLIST\n");
        File.WriteAllBytes(Path.Combine(root,"big.ts"),new byte[17*1024*1024]);
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(Source("big.m3u8")),ffmpeg))
            Require(!(await frames.GetFrameAsync(new("https://manifest-frame.invalid/big",1),CancellationToken.None)).Success,"The whole manifest transfer must retain the shared media byte cap");
        File.WriteAllText(Path.Combine(root,"stall.m3u8"),"#EXTM3U\n#EXTINF:2,\nstall.ts\n#EXT-X-ENDLIST\n");
        using(var frames=new MediaFrameService((_,_)=>Task.FromResult(Source("stall.m3u8")),ffmpeg,TimeSpan.FromSeconds(2)))
        {
            using var cancel=new CancellationTokenSource();
            var task=frames.GetFrameAsync(new("https://manifest-frame.invalid/stall",1),cancel.Token);
            await origin.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();
            try{await task;throw new InvalidOperationException("Cancelled manifest frame unexpectedly succeeded");}catch(OperationCanceledException){ }
            await origin.StalledClosed.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
    }
    private static async Task ActualExtractor(string ffmpeg,string root,Origin origin,string cookie)
    {
        string extractor=DependencyCenter.Executable("yt-dlp.exe");
        if(!File.Exists(extractor))throw new InvalidOperationException("Actual manifest metadata tests require the packaged extractor");
        var profile=new AppPersistence(Path.Combine(root,"extractor-profile"));
        var logs=new ConcurrentQueue<string>();
        using var previews=new PreviewService(extractor,profile,()=>cookie,_=>{},logs.Enqueue);
        using var frames=new MediaFrameService(previews,ffmpeg);
        foreach(string name in new[]{"vod.m3u8","dash.mpd"})
        {
            string url=new Uri(origin.Address,name).AbsoluteUri;
            var frame=await frames.GetFrameAsync(new(url,7,DurationSeconds:8),CancellationToken.None);
            Require(frame.Success,"Actual yt-dlp manifest metadata must resolve a video frame for "+name+": "+frame.Error+"\n"+string.Join("\n",logs));
            var pixel=await Pixel(ffmpeg,root,"actual-"+name,frame.PngBytes);Require(pixel[2]>200 && pixel[0]<80,"Actual manifest metadata must select the blue video frame");
        }
        previews.Dispose();await previews.Completion.WaitAsync(TimeSpan.FromSeconds(10));
        string stored=string.Join("\n",Directory.GetFiles(profile.PreviewDir,"*.json").Select(File.ReadAllText));
        Require(!stored.Contains("MANIFEST_PRIVATE",StringComparison.Ordinal) && !stored.Contains("COOKIE_PRIVATE",StringComparison.Ordinal),"Manifest credentials must not enter the persistent preview cache");
    }
    private static async Task<byte[]> Pixel(string ffmpeg,string root,string name,byte[] png)
    {
        string path=Path.Combine(root,name.Replace('/','-')+".png");await File.WriteAllBytesAsync(path,png);
        return await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-i",path,"-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"});
    }
    private static async Task<byte[]> Tool(string executable,IEnumerable<string> arguments,string? workingDirectory=null)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(workingDirectory is not null)start.WorkingDirectory=workingDirectory;
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var result=await BoundedProcessOutput.ReadAsync(child,1024*1024,128*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"Actual manifest fixture tool failed: "+Encoding.UTF8.GetString(result.StandardError));return result.StandardOutput;
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Manifest frame test: "+message);}
    private sealed class Origin:IAsyncDisposable
    {
        private readonly string _root;
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _clients=[];
        private readonly Task _accept;
        internal readonly ConcurrentQueue<string> Requests=[];
        internal readonly TaskCompletionSource Stalled=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource StalledClosed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Secondary;
        internal Uri Address {get;}
        internal Origin(string root)
        {
            _root=root;_listener.Start();Address=new Uri("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/");_accept=Accept();
        }
        private async Task Accept()
        {
            try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(Serve(client));}}
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
            catch(SocketException) when(_stop.IsCancellationRequested){ }
        }
        private async Task Serve(TcpClient client)
        {
            using(client)
            {
                try
                {
                    using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    var lines=new List<string>();string? line;
                    while((line=await reader.ReadLineAsync(_stop.Token)) is not null && line.Length>0){lines.Add(line);if(lines.Sum(value=>value.Length)>16384)return;}
                    if(lines.Count==0)return;string request=string.Join("\r\n",lines);Requests.Enqueue(request);
                    var first=lines[0].Split(' ');string name=Uri.UnescapeDataString(first[1].Split('?')[0]).TrimStart('/');
                    if(name.Contains("..",StringComparison.Ordinal) || name.Contains('\\'))return;
                    if(name=="secondary.ts"){Interlocked.Increment(ref Secondary);return;}
                    if(name=="stall.ts")
                    {
                        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 100000\r\n\r\n"),_stop.Token);Stalled.TrySetResult();
                        try{var buffer=new byte[1];await stream.ReadAsync(buffer,_stop.Token);}finally{StalledClosed.TrySetResult();}return;
                    }
                    string path=Path.Combine(_root,name);
                    if(!File.Exists(path)){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Missing\r\nContent-Length: 0\r\n\r\n"),_stop.Token);return;}
                    byte[] bytes=await File.ReadAllBytesAsync(path,_stop.Token);long begin=0,end=bytes.Length-1;bool range=false;
                    string? rangeLine=lines.FirstOrDefault(value=>value.StartsWith("Range:",StringComparison.OrdinalIgnoreCase));
                    if(rangeLine is not null)
                    {
                        string[] values=rangeLine[(rangeLine.IndexOf('=')+1)..].Trim().Split('-');begin=long.Parse(values[0],CultureInfo.InvariantCulture);
                        if(values.Length>1 && values[1].Length>0)end=Math.Min(end,long.Parse(values[1],CultureInfo.InvariantCulture));range=true;
                    }
                    if(begin>bytes.Length-1){await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 416 Missing\r\nContent-Length: 0\r\n\r\n"),_stop.Token);return;}
                    string type=name.EndsWith(".m3u8",StringComparison.Ordinal)?"application/vnd.apple.mpegurl":name.EndsWith(".mpd",StringComparison.Ordinal)?"application/dash+xml":"application/octet-stream";
                    string header="HTTP/1.1 "+(range?"206 Partial":"200 OK")+"\r\nConnection: close\r\nAccept-Ranges: bytes\r\nContent-Type: "+type+"\r\nContent-Length: "+(end-begin+1)+"\r\n"+
                        (range?"Content-Range: bytes "+begin+"-"+end+"/"+bytes.Length+"\r\n":"")+"\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(header),_stop.Token);
                    if(first[0]!="HEAD")await stream.WriteAsync(bytes.AsMemory((int)begin,(int)(end-begin+1)),_stop.Token);
                }
                catch(Exception error) when(error is IOException or SocketException or OperationCanceledException or ObjectDisposedException){ }
            }
        }
        public async ValueTask DisposeAsync()
        {_stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_clients.ToArray());_stop.Dispose();}
    }
}
