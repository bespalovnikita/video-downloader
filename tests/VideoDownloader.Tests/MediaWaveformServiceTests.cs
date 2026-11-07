using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDownloader.Native;

internal static class MediaWaveformServiceTests
{
    internal static bool TryRunFixture(string[] args)
    {
        string? mode=Environment.GetEnvironmentVariable("VD_WAVEFORM_FIXTURE");
        if(mode is not ("stall" or "flood") || !args.Contains("pcm_s16le",StringComparer.Ordinal))return false;
        AppPersistence.AtomicWrite(Environment.GetEnvironmentVariable("VD_WAVEFORM_READY")!,Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
        using var output=Console.OpenStandardOutput();
        if(mode=="flood"){byte[] bytes=new byte[32768];for(int i=0;i<256;i++)output.Write(bytes);output.Flush();}
        Thread.Sleep(Timeout.Infinite);
        return true;
    }

    internal static async Task RunAsync()
    {
        ModelsAndTimeMapping();
        string root=Path.Combine(Path.GetTempPath(),"vd-waveform-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
            string ffprobe=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
            Require(File.Exists(ffmpeg) && File.Exists(ffprobe),"Actual waveform integration requires FFmpeg and FFprobe.");
            await Tool(ffmpeg,root,["-f","lavfi","-i","color=c=blue:s=160x90:r=10:d=24",
                "-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=24",
                "-filter_complex","[1:a]volume='if(between(t,4,8),0.25,if(between(t,8,12),1,0))':eval=frame[a]",
                "-map","0:v","-map","[a]","-c:v","libx264","-preset","ultrafast","-g","20","-pix_fmt","yuv420p",
                "-c:a","aac","-movflags","+faststart","source.mp4"]);
            await Tool(ffmpeg,root,["-i","source.mp4","-vn","-c:a","copy","-movflags","+faststart","audio.m4a"]);
            await Tool(ffmpeg,root,["-i","source.mp4","-an","-c:v","copy","-movflags","+faststart","silent.mp4"]);
            await Tool(ffmpeg,root,["-f","lavfi","-i","sine=frequency=12000:sample_rate=48000:duration=4",
                "-c:a","aac","-b:a","128k","-movflags","+faststart","high-frequency.m4a"]);
            await Tool(ffmpeg,root,["-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=4",
                "-af","pan=stereo|c0=c0|c1=-1*c0","-c:a","aac","-b:a","128k","-movflags","+faststart","opposite-phase.m4a"]);
            await using var origin=new Origin(root);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            string foreign=Path.Combine(root,"foreign-user-file.mp4");File.WriteAllText(foreign,"FOREIGN USER FILE");
            MediaFrameSource Source(string name,bool video=true,bool audio=true)=>new(new Uri(origin.Address,name),"chosen",
                new Dictionary<string,string>{{"User-Agent","WaveformFixture"},{"Authorization","Bearer WAVE_PRIVATE"}},
                "","","fixture",10,24,video?160:0,video?90:0,Extension:video?"mp4":"m4a",HasVideo:video,HasAudio:audio);
            var requests=new List<MediaClipRequest>();
            using(var clips=new MediaClipService((request,_)=>{requests.Add(request);return Task.FromResult(new MediaClipSources(Source("source.mp4"),null));},storage.PreviewDir,ffmpeg,ffprobe))
            using(var waveforms=new MediaWaveformService(clips,ffmpeg))
            {
                var request=new MediaClipRequest("https://wave.invalid/Video?token=PRIVATE",7.5,20,"chosen",DurationSeconds:24);
                var playing=await clips.GetClipAsync(request,CancellationToken.None);
                Require(playing.Success,"The shared clip service must prepare a separate playback lease.");
                string playingPath=playing.Clip!.Path;
                var start=await waveforms.GetWaveformAsync(new(request,false),CancellationToken.None);
                Require(start.Success && start.Url==request.Url && start.RequestedSeconds==7.5,"The start waveform must retain its exact URL and requested boundary: "+start.Error);
                Require(start.Waveform!.StartSeconds==0 && start.Waveform.EndSeconds==15 && start.Waveform.Bins.Count==320,
                    "A known-duration start context must contain exactly its bounded real source window.");
                double silence=Mean(start.Waveform,1,3),quiet=Mean(start.Waveform,5,7),loud=Mean(start.Waveform,9,11);
                Require(silence<.004 && quiet>.006 && loud>quiet*2.5,
                    "Decoded PCM must preserve actual silence, quiet speech-like signal and a louder interval rather than fabricate a waveform.");
                Require(File.Exists(playingPath),"Waveform cleanup must not delete another still-owned playback lease.");
                playing.Clip.Dispose();
                var end=await waveforms.GetWaveformAsync(new(request,true),CancellationToken.None);
                Require(end.Success && end.RequestedSeconds==20 && end.Waveform!.StartSeconds==9 && end.Waveform.EndSeconds==24 &&
                    Mean(end.Waveform,9.5,11)>.02 && Mean(end.Waveform,15,22)<.004,
                    "An end context must retain absolute source-time mapping after seeking: "+end.Error);
                var eof=await waveforms.GetWaveformAsync(new(request with{EndSeconds=24},true),CancellationToken.None);
                Require(eof.Success && eof.RequestedSeconds==24 && eof.Waveform!.EndSeconds==24,"The exclusive source end must still have a real preceding audio window.");
                var unknown=await waveforms.GetWaveformAsync(new(request with{DurationSeconds=null},true),CancellationToken.None);
                Require(unknown.Success && unknown.Waveform!.StartSeconds==5 && unknown.Waveform.EndSeconds==20,
                    "Unknown duration must keep the context before the confirmed end rather than seek beyond it.");
                Require(requests[0].VideoFormatId=="chosen" && requests.All(value=>value.Url==request.Url && value.EndSeconds-value.StartSeconds<=15.001),
                    "Waveforms must preserve the selected source identity/format and never request full-video audio.");
                Require(!Directory.EnumerateFiles(Path.Combine(storage.PreviewDir,"playback"),"*.mp4").Any(),
                    "The same clip cache lease must be released after joined PCM decoding.");
            }
            using(var clips=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(null,Source("audio.m4a",video:false))),storage.PreviewDir,ffmpeg,ffprobe))
            using(var waveforms=new MediaWaveformService(clips,ffmpeg))
            {
                var result=await waveforms.GetWaveformAsync(new(new("https://wave.invalid/audio",7.5,20,DurationSeconds:24,AudioOnly:true),false),CancellationToken.None);
                Require(result.Success && Mean(result.Waveform!,9,11)>.02,"Audio-only sources must produce actual audible envelopes: "+result.Error);
            }
            foreach(string file in new[]{"high-frequency.m4a","opposite-phase.m4a"})
            {
                using var clips=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(null,Source(file,video:false) with{Duration=4})),storage.PreviewDir,ffmpeg,ffprobe);
                using var waveforms=new MediaWaveformService(clips,ffmpeg);
                var result=await waveforms.GetWaveformAsync(new(new("https://wave.invalid/"+file,1,3,DurationSeconds:4,AudioOnly:true),false),CancellationToken.None);
                Require(result.Success && Mean(result.Waveform!,.5,3.5)>.02,
                    "Audible high frequencies and opposite-phase stereo must not disappear through downsampling/downmix: "+file+" "+result.Error);
            }
            using(var clips=new MediaClipService((_,_)=>Task.FromResult(new MediaClipSources(Source("silent.mp4",audio:false),null)),storage.PreviewDir,ffmpeg,ffprobe))
            using(var waveforms=new MediaWaveformService(clips,ffmpeg))
            {
                var result=await waveforms.GetWaveformAsync(new(new("https://wave.invalid/silent",7.5,20,DurationSeconds:24),false),CancellationToken.None);
                Require(!result.Success && result.Error.Contains("нет аудиодорожки",StringComparison.Ordinal),"A missing audio track must be an honest absence, not invented silence.");
            }
            // Resolve an actual direct source through the packaged extractor,
            // sharing its normal metadata worker and clip owner.
            using(var previews=new PreviewService(DependencyCenter.Executable("yt-dlp.exe"),new AppPersistence(Path.Combine(root,"extractor-profile")),()=>"",_=>{},_=>{}))
            using(var clips=new MediaClipService(previews,storage.PreviewDir,ffmpeg,ffprobe))
            using(var waveforms=new MediaWaveformService(clips,ffmpeg))
            {
                var result=await waveforms.GetWaveformAsync(new(new(new Uri(origin.Address,"audio.m4a").AbsoluteUri,7.5,20,DurationSeconds:24,AudioOnly:true),false),CancellationToken.None);
                Require(result.Success && Mean(result.Waveform!,9,11)>.02,"Actual bundled yt-dlp metadata must feed the same waveform/PCM path: "+result.Error);
            }
            await CancellationAndLimits(root,ffmpeg);
            Require(File.ReadAllText(foreign)=="FOREIGN USER FILE" && storage.ReadHistory().Count==0,
                "Waveform preparation must preserve user media, the queue and full-download history.");
            Console.WriteLine("PASS: actual waveform PCM silence/levels, source times, direct extractor, audio-only/absent audio, bounded output, joined cancellation and cache ownership");
        }
        finally {Directory.Delete(root,true);}
    }

    private static void ModelsAndTimeMapping()
    {
        var request=new MediaClipRequest("https://wave.invalid/video",10,30,DurationSeconds:100);
        Require(MediaWaveformService.TryContext(new(request,false),out var start) && start!.StartSeconds==2.5 && start.EndSeconds==17.5,
            "An ordinary start boundary must have a centered fifteen-second context.");
        Require(MediaWaveformService.TryContext(new(request,true),out var end) && end!.StartSeconds==22.5 && end.EndSeconds==37.5,
            "An ordinary end boundary must have audio on both sides when duration is known.");
        Require(MediaWaveformService.TryContext(new(request with{DurationSeconds=null},true),out var unknown) && unknown!.StartSeconds==15 && unknown.EndSeconds==30,
            "An unknown source end must never be guessed.");
        foreach(var invalid in new[]{request with{StartSeconds=double.NaN},request with{EndSeconds=10},request with{EndSeconds=101},request with{Url="file:///private"}})
            Require(!MediaWaveformService.TryContext(new(invalid,false),out _),"Invalid ranges/URLs must not start preparation.");
        byte[] pcm=new byte[MediaWaveformService.SampleRate*4*MediaWaveformService.Channels*2];
        for(int index=MediaWaveformService.SampleRate*2;index<MediaWaveformService.SampleRate*4;index++)
            for(int channel=0;channel<MediaWaveformService.Channels;channel++)
                BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(index*MediaWaveformService.Channels*2+channel*2,2),16384);
        var wave=MediaWaveformService.FromPcm(2,6,pcm);
        Require(Mean(wave,2,3.5)==0 && Math.Abs(Mean(wave,4.5,6)-.5)<.00001,"PCM bins must align with source time and preserve amplitude.");
        var shortTrack=MediaWaveformService.FromPcm(10,14,pcm.AsSpan(MediaWaveformService.SampleRate*2*MediaWaveformService.Channels*2));
        Require(shortTrack.DecodedEndSeconds==12 && Math.Abs(Mean(shortTrack,10,11.5)-.5)<.00001 && Mean(shortTrack,12.5,14)==0,
            "Shorter audio must keep its actual end and silent media gap instead of being stretched.");
        var mutable=new[]{new WaveformBin(.5,.25)};var copy=new AudioWaveform(0,1,mutable);mutable[0]=new(1,1);
        Require(copy.Bins[0].Peak==.5,"A waveform result must copy mutable input bins.");
        foreach(var bad in new[]{Array.Empty<byte>(),new byte[3],new byte[MediaWaveformService.MaximumPcmBytes+2]})
        {bool refused=false;try{MediaWaveformService.FromPcm(0,1,bad);}catch(InvalidDataException){refused=true;}Require(refused,"Malformed/oversized PCM must fail without allocation growth.");}
    }

    private static async Task CancellationAndLimits(string root,string ffmpeg)
    {
        string? oldMode=Environment.GetEnvironmentVariable("VD_WAVEFORM_FIXTURE"),oldReady=Environment.GetEnvironmentVariable("VD_WAVEFORM_READY");
        try
        {
            foreach(string mode in new[]{"stall","flood","owner-stop"})
            {
                string path=Path.Combine(root,"owned-"+mode+".mp4"),ready=Path.Combine(root,mode+".ready");File.WriteAllText(path,"owned controlled clip");
                Environment.SetEnvironmentVariable("VD_WAVEFORM_FIXTURE",mode=="owner-stop"?"stall":mode);Environment.SetEnvironmentVariable("VD_WAVEFORM_READY",ready);
                bool cleaned=false;MediaClipPreview? retained=null;
                Task<MediaClipResult> Prepare(MediaClipRequest request,CancellationToken _)=>Task.FromResult(new MediaClipResult(request.Url,request.StartSeconds,request.EndSeconds,
                    retained=new(path,request.StartSeconds,request.EndSeconds,true,true,()=>
                    {
                        Require(!File.Exists(ready) || !Alive(int.Parse(File.ReadAllText(ready),CultureInfo.InvariantCulture)),
                            "The waveform child must exit before the owned clip lease is released.");
                        cleaned=true;File.Delete(path);
                    }),""));
                using var service=new MediaWaveformService(Prepare,TestExecutable());
                using var stop=new CancellationTokenSource();
                var work=service.GetWaveformAsync(new(new("https://wave.invalid/"+mode,1,2,DurationSeconds:4),false),stop.Token);
                if(mode is "stall" or "owner-stop")
                {
                    await Until(()=>File.Exists(ready));
                    if(mode=="owner-stop")
                    {
                        retained!.Dispose();
                        Require(!cleaned && File.Exists(path),"Disposing the clip owner must retain its slot and file until the active decoder has joined.");
                        service.Dispose();
                    }
                    else stop.Cancel();
                    bool cancelled=false;try{await work.WaitAsync(TimeSpan.FromSeconds(10));}catch(OperationCanceledException){cancelled=true;}
                    Require(cancelled,"Explicit cancellation must remain cancellation.");
                }
                else Require(!(await work.WaitAsync(TimeSpan.FromSeconds(15))).Success,"A decoder that floods raw PCM must be rejected by the byte budget.");
                Require(cleaned && !File.Exists(path),"Cancellation and oversized output must release the prepared owned clip.");
            }
            Environment.SetEnvironmentVariable("VD_WAVEFORM_FIXTURE",null);
            using var timed=new MediaWaveformService(async(request,token)=>
                {await Task.Delay(Timeout.Infinite,token);return new(request.Url,request.StartSeconds,request.EndSeconds,null,"");},ffmpeg,TimeSpan.FromMilliseconds(200));
            var failure=await timed.GetWaveformAsync(new(new("https://wave.invalid/deadline",1,2),false),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Require(!failure.Success && failure.Error.Contains("истекло",StringComparison.Ordinal),"One overall deadline must bound waiting for clip preparation.");
        }
        finally{Environment.SetEnvironmentVariable("VD_WAVEFORM_FIXTURE",oldMode);Environment.SetEnvironmentVariable("VD_WAVEFORM_READY",oldReady);}
    }

    private static bool Alive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static async Task Until(Func<bool> condition){var watch=Stopwatch.StartNew();while(!condition() && watch.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(20);Require(condition(),"The owned waveform child did not reach its controlled input.");}
    private static double Mean(AudioWaveform wave,double start,double end)
    {var bins=wave.Bins.Where((_,index)=>wave.StartSeconds+(index+.5)*wave.DurationSeconds/wave.Bins.Count>=start && wave.StartSeconds+(index+.5)*wave.DurationSeconds/wave.Bins.Count<end).ToArray();Require(bins.Length>0,"The requested inspectable waveform interval must contain bins.");return bins.Average(bin=>bin.Rms);}
    private static string TestExecutable()=>Path.Combine(AppContext.BaseDirectory,typeof(MediaWaveformServiceTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Waveform test: "+message);}
    private static async Task Tool(string executable,string working,string[] arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=working,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin"}.Concat(arguments))start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output=await BoundedProcessOutput.ReadAsync(child,4096,128*1024,stop.Token);
        Require(child.Process.ExitCode==0,"Actual audio fixture creation failed: "+Encoding.UTF8.GetString(output.StandardError));
    }

    private sealed class Origin:IAsyncDisposable
    {
        private readonly string _root;private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();private readonly ConcurrentBag<Task> _clients=[];private readonly Task _accept;
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
                if(name.Contains("..",StringComparison.Ordinal) || name.Contains('\\') || name.Contains('/'))return;
                string path=Path.Combine(_root,name);if(!File.Exists(path)){await stream.WriteAsync("HTTP/1.1 404 Missing\r\nContent-Length:0\r\n\r\n"u8.ToArray(),_stop.Token);return;}
                byte[] bytes=await File.ReadAllBytesAsync(path,_stop.Token);long first=0,last=bytes.Length-1;bool range=false;
                string? value=lines.FirstOrDefault(value=>value.StartsWith("Range: bytes=",StringComparison.OrdinalIgnoreCase));
                if(value is not null){var limits=value[13..].Split('-');first=long.Parse(limits[0],CultureInfo.InvariantCulture);if(limits[1].Length>0)last=Math.Min(last,long.Parse(limits[1],CultureInfo.InvariantCulture));range=true;}
                if(first>last || first>=bytes.Length){await stream.WriteAsync("HTTP/1.1 416 Range\r\nContent-Length:0\r\n\r\n"u8.ToArray(),_stop.Token);return;}
                string headers="HTTP/1.1 "+(range?"206 Partial Content":"200 OK")+"\r\nConnection: close\r\nAccept-Ranges: bytes\r\nContent-Type: "+(name.EndsWith(".m4a",StringComparison.Ordinal)?"audio/mp4":"video/mp4")+"\r\nContent-Length: "+(last-first+1)+"\r\n"+(range?"Content-Range: bytes "+first+"-"+last+"/"+bytes.Length+"\r\n":"")+"\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),_stop.Token);if(parts[0]!="HEAD")await stream.WriteAsync(bytes.AsMemory((int)first,(int)(last-first+1)),_stop.Token);
            }
            catch(Exception error) when(error is IOException or SocketException or OperationCanceledException or ObjectDisposedException){ }
        }
        public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_clients.ToArray());_stop.Dispose();}
    }
}
