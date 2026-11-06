using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDownloader.Native;

internal static class MediaClipServiceTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-clip-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            await CacheOwnership(Path.Combine(root,"cache-ownership"));
            string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
            string ffprobe=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
            Require(File.Exists(ffmpeg) && File.Exists(ffprobe),"Actual playback preparation requires FFmpeg/FFprobe");
            await Tool(ffmpeg,root,new[]{"-f","lavfi","-i","color=c=red:s=160x90:r=10:d=16","-f","lavfi","-i","color=c=blue:s=160x90:r=10:d=16",
                "-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=16","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=16",
                "-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0[v];[2:a][3:a]concat=n=2:v=0:a=1[a]","-map","[v]","-map","[a]",
                "-c:v","libx264","-preset","ultrafast","-g","20","-keyint_min","20","-sc_threshold","0","-pix_fmt","yuv420p","-c:a","aac","-movflags","+faststart","source.mp4"});
            await Tool(ffmpeg,root,new[]{"-i","source.mp4","-map","0:v","-an","-c","copy","-movflags","+faststart","video.mp4"});
            await Tool(ffmpeg,root,new[]{"-i","source.mp4","-vn","-c","copy","-movflags","+faststart","audio.m4a"});
            await Tool(ffmpeg,root,new[]{"-i","source.mp4","-c","copy","-f","hls","-hls_time","2","-hls_playlist_type","vod","-hls_segment_filename","ts-%02d.ts","vod.m3u8"});
            await Tool(ffmpeg,root,new[]{"-i","source.mp4","-c","copy","-f","hls","-hls_time","2","-hls_playlist_type","vod","-hls_segment_type","fmp4","-hls_fmp4_init_filename","init.mp4","-hls_segment_filename","cmaf-%02d.m4s","cmaf.m3u8"});
            await Tool(ffmpeg,root,new[]{"-i","source.mp4","-map","0:v","-map","0:a","-c","copy","-f","dash","-seg_duration","2","dash.mpd"});
            await using var origin=new Origin(root);
            MediaFrameSource Source(string path,string id="m",bool video=true,bool audio=true,string protocol="")=>new(new Uri(origin.Address,path),id,
                new Dictionary<string,string>{{"User-Agent","VD-ClipFixture"},{"Authorization","Bearer CLIP_PRIVATE"}},"","","fixture",10,32,video?160:0,video?90:0,
                protocol,protocol.Length>0?new Uri(origin.Address,path):null,Extension:video?"mp4":"m4a",HasVideo:video,HasAudio:audio);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            string sentinel=Path.Combine(root,"user-result.mp4");File.WriteAllText(sentinel,"FOREIGN");
            var muxed=Source("source.mp4");
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(muxed,null)),storage.PreviewDir,ffmpeg,ffprobe))
            {
                var red=await service.GetClipAsync(new("https://clip.invalid/video",3,6,DurationSeconds:32),CancellationToken.None);
                Require(red.Success,"Actual progressive clip failed: "+red.Error);
                string owned=red.Clip!.Path;Require(File.Exists(owned) && red.Clip.HasVideo && red.Clip.HasAudio,"Muxed clip must contain video and audio");
                await CheckClip(ffmpeg,root,red.Clip,true);red.Clip.Dispose();red.Clip.Dispose();Require(!File.Exists(owned),"Idempotent disposal must remove only the owned cache");
                var blue=await service.GetClipAsync(new("https://clip.invalid/video",0,32,DurationSeconds:32,FromEnd:true),CancellationToken.None);
                Require(blue.Success && blue.Clip!.StartSeconds==17 && blue.Clip.EndSeconds==32,"A long-range end preview must report exactly its last15seconds: "+blue.Error);
                await CheckClip(ffmpeg,root,blue.Clip!,false);blue.Clip!.Dispose();
                var first=await service.GetClipAsync(new("https://clip.invalid/video",0,32,DurationSeconds:32),CancellationToken.None);
                Require(first.Success && first.Clip!.StartSeconds==0 && first.Clip.EndSeconds==15,"A long-range start preview must cap at exactly15seconds");
                string remains=first.Clip!.Path;service.Dispose();Require(!File.Exists(remains),"Owner shutdown removes prepared local clips");
            }
            foreach(string manifest in new[]{"vod.m3u8","cmaf.m3u8","dash.mpd"})
            {
                bool dash=manifest.EndsWith("mpd",StringComparison.Ordinal);
                var video=Source(manifest,dash?"0":"m",audio:!dash,protocol:dash?"dash":"m3u8_native");
                var audio=dash?Source(manifest,"1",video:false,protocol:"dash"):null;
                using var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(video,audio)),storage.PreviewDir,ffmpeg,ffprobe);
                var clip=await service.GetClipAsync(new("https://clip.invalid/manifest",17,22,DurationSeconds:32),CancellationToken.None);
                Require(clip.Success,"Actual "+manifest+" AV clip failed: "+clip.Error);
                await CheckClip(ffmpeg,root,clip.Clip!,false);clip.Clip!.Dispose();
            }
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(Source("video.mp4",audio:false),Source("audio.m4a",video:false))),storage.PreviewDir,ffmpeg,ffprobe))
            {
                var clip=await service.GetClipAsync(new("https://clip.invalid/separate",17,20),CancellationToken.None);
                Require(clip.Success,"Separate video/audio must be combined in short playback: "+clip.Error);await CheckClip(ffmpeg,root,clip.Clip!,false);clip.Clip!.Dispose();
            }
            foreach(bool silent in new[]{false,true})
            {
                var unknown=Source(silent?"video.mp4":"source.mp4",audio:silent) with{AudioKnown=false};
                using var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(unknown,null)),storage.PreviewDir,ffmpeg,ffprobe);
                var clip=await service.GetClipAsync(new("https://clip.invalid/unknown",17,20),CancellationToken.None);
                Require(clip.Success && clip.Clip!.HasAudio!=silent,"Missing extractor codec fields must inspect actual audio instead of stripping sound or requiring a nonexistent track: "+clip.Error);
                await CheckClip(ffmpeg,root,clip.Clip!,false);clip.Clip!.Dispose();
            }
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(null,Source("audio.m4a",video:false))),storage.PreviewDir,ffmpeg,ffprobe))
            {
                var clip=await service.GetClipAsync(new("https://clip.invalid/audio",3,6,AudioOnly:true),CancellationToken.None);
                Require(clip.Success && !clip.Clip!.HasVideo && clip.Clip.HasAudio,"Audio-only selection must create audible preview: "+clip.Error);
                await CheckAudio(ffmpeg,root,clip.Clip!);clip.Clip!.Dispose();
            }
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(null,Source("audio.m4a",video:false))),storage.PreviewDir,ffmpeg,ffprobe))
            {
                var cached=new List<MediaClipPreview>();
                for(int index=0;index<MediaClipService.MaxCachedClips;index++)
                {var result=await service.GetClipAsync(new("https://clip.invalid/cache",0,1,AudioOnly:true),CancellationToken.None);Require(result.Success,"Clip cache should accept its documented bound");cached.Add(result.Clip!);}
                Require(!(await service.GetClipAsync(new("https://clip.invalid/cache",0,1,AudioOnly:true),CancellationToken.None)).Success && cached.All(clip=>File.Exists(clip.Path)),"The cache bound must reject another clip without deleting playing clips");
                cached[0].Dispose();var replacement=await service.GetClipAsync(new("https://clip.invalid/cache",0,1,AudioOnly:true),CancellationToken.None);
                Require(replacement.Success,"Releasing a clip makes one cache slot available");replacement.Clip!.Dispose();foreach(var clip in cached)clip.Dispose();
            }
            File.WriteAllText(Path.Combine(root,"nested.m3u8"),"#EXTM3U\n#EXTINF:2,\nnested.ts\n#EXT-X-ENDLIST\n");
            File.WriteAllText(Path.Combine(root,"nested.ts"),"#EXTM3U\n#EXTINF:2,\n"+new Uri(origin.Address,"secondary.ts")+"\n#EXT-X-ENDLIST\n");
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(Source("nested.m3u8",protocol:"m3u8_native"),null)),storage.PreviewDir,ffmpeg,ffprobe))
                Require(!(await service.GetClipAsync(new("https://clip.invalid/nested",0,1),CancellationToken.None)).Success && origin.Secondary==0,"Disguised nested manifests must never reach an unbounded decoder fetch");
            using(var huge=new FileStream(Path.Combine(root,"big.ts"),FileMode.CreateNew)){huge.SetLength(33L*1024*1024);}
            File.WriteAllText(Path.Combine(root,"big.m3u8"),"#EXTM3U\n#EXTINF:2,\nbig.ts\n#EXT-X-ENDLIST\n");
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(Source("big.m3u8",protocol:"m3u8_native"),Source("audio.m4a",video:false))),storage.PreviewDir,ffmpeg,ffprobe))
                Require(!(await service.GetClipAsync(new("https://clip.invalid/budget",0,1),CancellationToken.None)).Success,"Two-input previews split the64MiB shared transfer bound and reject an oversized segment");
            using(var service=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(Source("stall.mp4"),null)),storage.PreviewDir,ffmpeg,ffprobe))
            {
                using var cancel=new CancellationTokenSource();var work=service.GetClipAsync(new("https://clip.invalid/cancel",1,3),cancel.Token);
                await origin.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(10));cancel.Cancel();
                try{await work;throw new InvalidOperationException("Cancelled clip succeeded");}catch(OperationCanceledException){ }
                await origin.Closed.Task.WaitAsync(TimeSpan.FromSeconds(3));
            }
            using(var service=new MediaClipService(async(_,token)=>{await Task.Delay(Timeout.Infinite,token);return new(null,null);},storage.PreviewDir,ffmpeg,ffprobe,TimeSpan.FromMilliseconds(150)))
                Require(!(await service.GetClipAsync(new("https://clip.invalid/deadline",0,2),CancellationToken.None)).Success,"Clip preparation must obey its deadline");
            Require(File.ReadAllText(sentinel)=="FOREIGN" && !File.Exists(storage.SessionPath) && !File.Exists(storage.HistoryPath),"Playback must not mutate queue/history/user results");
            Require(!Directory.EnumerateFiles(storage.PreviewDir,"*.mp4",SearchOption.AllDirectories).Any(),"Completed/cancelled playback leaves no owned clip files");
            Require(!Directory.EnumerateFiles(storage.PreviewDir,"*",SearchOption.AllDirectories).Any(path=>File.ReadAllText(path).Contains("CLIP_PRIVATE",StringComparison.Ordinal)),"Private transport headers must never be persisted");
            await ActualMetadata(ffmpeg,ffprobe,root,origin);
            Console.WriteLine("PASS: real bounded progressive/HLS/DASH selected-range AV clips, separate audio, decodedPCM RMS, cap/range, cancellation/deadline and owned cache");
        }
        finally{Directory.Delete(root,true);}
    }
    private static async Task CacheOwnership(string root)
    {
        Directory.CreateDirectory(root);string foreign=Path.Combine(root,"foreign.mp4");File.WriteAllText(foreign,"FOREIGN");
        string abandoned=Path.Combine(root,Guid.NewGuid().ToString("N")+".mp4");File.WriteAllText(abandoned,"owned interrupted clip");
        string marker=Path.ChangeExtension(abandoned,".owner");File.WriteAllText(marker,"VDCLIP1\n2147483647\n1\n");
        var cache=new MediaClipCache(root);using(var lease=cache.Reserve())
            Require(!File.Exists(abandoned) && !File.Exists(marker) && File.ReadAllText(foreign)=="FOREIGN","Dead owner cleanup must reclaim exact recognized files and preserve foreign content");
        if(OperatingSystem.IsWindows())
        {
            var locked=cache.Reserve();using(var held=new FileStream(locked.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                locked.Dispose();Require(File.Exists(locked.Path),"Windows fixture must hold the actual failed-delete file");
                var retained=new List<MediaClipCache.Lease>();
                try
                {
                    for(int index=1;index<MediaClipService.MaxCachedClips;index++)retained.Add(cache.Reserve());
                    bool refused=false;try{using var excess=cache.Reserve();}catch(MediaFrameUnavailableException){refused=true;}
                    Require(refused && File.Exists(locked.Path),"A failed delete must remain charged to the cache slots even after its descriptor is disposed");
                }
                finally{foreach(var lease in retained)lease.Dispose();}
            }
            using(var next=cache.Reserve())Require(!File.Exists(locked.Path),"A later cache reservation must retry an unlocked retained deletion");
            await Task.Delay(300);
        }
        Require(File.ReadAllText(foreign)=="FOREIGN","Cache cleanup must preserve unrecognized foreign files");
    }
    private static async Task ActualMetadata(string ffmpeg,string ffprobe,string root,Origin origin)
    {
        string extractor=DependencyCenter.Executable("yt-dlp.exe");Require(File.Exists(extractor),"Actual clip metadata requires packaged extractor");
        var storage=new AppPersistence(Path.Combine(root,"metadata-profile"));var logs=new ConcurrentQueue<string>();
        using var previews=new PreviewService(extractor,storage,()=>"",_=>{},logs.Enqueue);
        using var clips=new MediaClipService(previews,storage.PreviewDir,ffmpeg,ffprobe);
        foreach(string manifest in new[]{"vod.m3u8","dash.mpd","source.mp4","video.mp4"})
        {
            var result=await clips.GetClipAsync(new(new Uri(origin.Address,manifest).AbsoluteUri,17,20,DurationSeconds:32),CancellationToken.None);
            Require(result.Success,"The existing actual extractor worker must supply AV clip sources for "+manifest+": "+result.Error+"\n"+string.Join('\n',logs));
            Require(result.Clip!.HasVideo && result.Clip.HasAudio==(manifest!="video.mp4"),"Actual metadata with omitted codecs must retain exactly the actual tracks");if(result.Clip.HasAudio)await CheckAudio(ffmpeg,root,result.Clip);result.Clip.Dispose();
        }
        previews.Dispose();await previews.Completion.WaitAsync(TimeSpan.FromSeconds(10));
    }
    private static async Task CheckClip(string ffmpeg,string root,MediaClipPreview clip,bool red)
    {
        var pixel=await Tool(ffmpeg,root,new[]{"-i",clip.Path,"-frames:v","1","-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"});
        Require(pixel.Length==3 && (red?pixel[0]>190 && pixel[2]<80:pixel[2]>190 && pixel[0]<80),"Preview pixels must come from the selected source interval");
        if(clip.HasAudio)await CheckAudio(ffmpeg,root,clip);
    }
    private static async Task CheckAudio(string ffmpeg,string root,MediaClipPreview clip)
    {
        var pcm=await Tool(ffmpeg,root,new[]{"-i",clip.Path,"-vn","-ac","1","-ar","8000","-f","s16le","pipe:1"});
        Require(pcm.Length>=Math.Max(.02,clip.DurationSeconds-.35)*8000*2,"Audio must cover the short selected interval");
        double sum=0,previous=0;int crossings=0;
        for(int index=0;index+1<pcm.Length;index+=2)
        {double sample=System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(index,2))/32768d;sum+=sample*sample;if(previous<=0 && sample>0)crossings++;previous=sample;}
        Require(Math.Sqrt(sum/(pcm.Length/2))>.02,"Decoded PCM RMS must prove actual non-silent audio");
        double tone=crossings/(pcm.Length/2/8000d),expected=clip.StartSeconds>=16?880:440;
        Require(Math.Abs(tone-expected)<35,"Decoded audio tone must come from the selected source interval, expected "+expected+"Hz but heard "+tone.ToString("0.0",CultureInfo.InvariantCulture)+"Hz");
    }
    private static async Task<byte[]> Tool(string executable,string working,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=working,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin"}.Concat(arguments))start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output=await BoundedProcessOutput.ReadAsync(child,4*1024*1024,128*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"Actual clip fixture failed: "+Encoding.UTF8.GetString(output.StandardError));return output.StandardOutput;
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Clip test: "+message);}
    private sealed class Origin:IAsyncDisposable
    {
        private readonly string _root;private readonly TcpListener _listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _clients=[];private readonly Task _accept;
        internal readonly TaskCompletionSource Stalled=new(TaskCreationOptions.RunContinuationsAsynchronously),Closed=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int Secondary;
        internal Uri Address{get;}
        internal Origin(string root){_root=root;_listener.Start();Address=new("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/");_accept=Accept();}
        private async Task Accept(){try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_clients.Add(Serve(client));}}catch(Exception) when(_stop.IsCancellationRequested){ }}
        private async Task Serve(TcpClient client)
        {
            using(client)try
            {
                using var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                var lines=new List<string>();string? line;while((line=await reader.ReadLineAsync(_stop.Token)) is not null && line.Length>0){lines.Add(line);if(lines.Sum(value=>value.Length)>16384)return;}
                if(lines.Count==0)return;var parts=lines[0].Split(' ');string name=Uri.UnescapeDataString(parts[1].Split('?')[0]).TrimStart('/');
                if(name.Contains("..",StringComparison.Ordinal) || name.Contains('\\'))return;
                if(name=="secondary.ts"){Interlocked.Increment(ref Secondary);return;}
                if(name=="stall.mp4")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: 10000000\r\nConnection: close\r\n\r\n"),_stop.Token);Stalled.TrySetResult();
                    try{byte[] one=new byte[1];while(await stream.ReadAsync(one,_stop.Token)>0){ }}finally{Closed.TrySetResult();}return;
                }
                string path=Path.Combine(_root,name);if(!File.Exists(path)){await stream.WriteAsync("HTTP/1.1 404 Missing\r\nContent-Length:0\r\n\r\n"u8.ToArray(),_stop.Token);return;}
                byte[] bytes=await File.ReadAllBytesAsync(path,_stop.Token);long first=0,last=bytes.Length-1;bool range=false;
                string? value=lines.FirstOrDefault(value=>value.StartsWith("Range: bytes=",StringComparison.OrdinalIgnoreCase));
                if(value is not null){var limits=value[13..].Split('-');first=long.Parse(limits[0],CultureInfo.InvariantCulture);if(limits[1].Length>0)last=Math.Min(last,long.Parse(limits[1],CultureInfo.InvariantCulture));range=true;}
                if(first>last || first>=bytes.Length){await stream.WriteAsync("HTTP/1.1 416 Range\r\nContent-Length:0\r\n\r\n"u8.ToArray(),_stop.Token);return;}
                string type=Path.GetExtension(path) switch{".m3u8"=>"application/vnd.apple.mpegurl",".mpd"=>"application/dash+xml",".m4a"=>"audio/mp4",".ts"=>"video/mp2t",_=>"video/mp4"};
                string headers="HTTP/1.1 "+(range?"206 Partial Content":"200 OK")+"\r\nConnection: close\r\nAccept-Ranges: bytes\r\nContent-Type: "+type+"\r\nContent-Length: "+(last-first+1)+"\r\n"+(range?"Content-Range: bytes "+first+"-"+last+"/"+bytes.Length+"\r\n":"")+"\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),_stop.Token);if(parts[0]!="HEAD")await stream.WriteAsync(bytes.AsMemory((int)first,(int)(last-first+1)),_stop.Token);
            }
            catch(Exception error) when(error is IOException or SocketException or OperationCanceledException or ObjectDisposedException){ }
        }
        public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_clients.ToArray());_stop.Dispose();}
    }
}
