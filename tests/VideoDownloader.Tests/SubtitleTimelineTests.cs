using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class SubtitleTimelineTests
{
    internal static bool TryRunFixture(string[] arguments)
    {
        if(arguments.FirstOrDefault()!="--subtitle-timeline-wait")return false;
        Console.WriteLine("owned subtitle processor");Console.Out.Flush();
        Thread.Sleep(60_000);return true;
    }
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-subtitle-timeline-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        string ffprobe=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
        Require(File.Exists(ffmpeg) && File.Exists(ffprobe),"Real subtitle tests require FFmpeg/FFprobe");
        try
        {
            CueBoundaries();await ArgumentContract(root);
            await LocalFormats(root,ffmpeg,ffprobe);
            await OwnedCancellation(root,ffmpeg,ffprobe);
            await RealHls(root,ffmpeg,ffprobe);
            Console.WriteLine("PASS: clipped/rebased subtitle sidecars and actual embedded cues, overlapping dialogue, language/format constraints, separate ranges, cancellation and safe publication");
        }
        finally{Directory.Delete(root,true);}
    }
    private static void CueBoundaries()
    {
        var clipped=SubtitleTimelineService.Clip(new[]{new SubtitleCue(1000,2000,"BEFORE"),new SubtitleCue(4000,5500,"LEFT"),
            new SubtitleCue(5500,6500,"MIDDLE"),new SubtitleCue(6500,8000,"RIGHT"),new SubtitleCue(4000,8000,"OVERLAP"),
            new SubtitleCue(15000,16000,"AFTER"),new SubtitleCue(4000,5000,"ENDS_AT_START"),new SubtitleCue(7000,8000,"STARTS_AT_END")},new(5,7));
        Require(clipped.Select(cue=>(cue.Start,cue.End)).SequenceEqual(new[]{(0L,500L),(500L,1500L),(1500L,2000L),(0L,2000L)}),
            "Cues must intersect [start,end), clip both overlaps, and rebase precisely");
        var visible=SubtitleTimelineService.Flatten(clipped);
        Require(visible.Length==3 && visible[0].Text=="LEFT\nOVERLAP" && visible[1].Text=="MIDDLE\nOVERLAP" && visible[2].Text=="RIGHT\nOVERLAP",
            "Overlapping dialogue must remain visible in containers without simultaneous subtitle samples");
        var zero=SubtitleTimelineService.Clip(new[]{new SubtitleCue(0,1000,"START")},new(0,.5));
        Require(zero.Single() is {Start:0,End:500},"A zero-origin clip still trims its final cue");
        Require(SubtitleTimelineService.Clip(clipped,new(10,11)).Length==0,"No overlap is an empty subtitle selection");
    }
    private static async Task ArgumentContract(string root)
    {
        var options=new DownloadOptions {EmbedSubs=true,SubtitleLangs="en.*,ru.*",EmbedMetadata=false,EmbedChapters=false};
        string url="https://subtitle-timeline.invalid/media";
        string[] full=new YtDlpArguments(options).Build(url,root,"best").ToArray();
        Require(full.Contains("--embed-subs") && !full.Any(value=>value.Contains("__VD_SUBTITLES__",StringComparison.Ordinal)),"Full-download embedding stays with yt-dlp");
        string[] partial=new YtDlpArguments(options).Build(url,root,"best",new(url,new(5,7))).ToArray();
        Require(partial.Contains("--write-subs") && !partial.Contains("--embed-subs") && partial.Contains("vtt/srt/ass/ssa/best") &&
            partial.Any(value=>value.Contains("requested_subtitles.:.filepath",StringComparison.Ordinal) && !value.Contains(".url",StringComparison.Ordinal)),
            "Partial subtitle processing must defer embedding and print only the bounded local manifest");
        var parsed=YtDlpOutputParser.Parse("__VD_SUBTITLES__:en|{}") as YtDlpOutputEvent.Subtitles;
        Require(parsed?.Value=="en|{}","The typed marker must preserve its first language character");
        var merged=new DownloadSelection(url,new(1,2)){Sections=new([new(1,2),new(3,4)]),FragmentOutputMode="Merged"};
        Refuses(()=>merged.Validate(url,options),"Merged subtitle output remains an explicit admission error");
        options.Sponsor=true;
        var partialSelection=new DownloadSelection(url,new(5,7));
        Refuses(()=>partialSelection.Validate(url,options),"SponsorBlock plus partial subtitles must fail before launch");
        await using(var coordinator=new DownloadCoordinator("must-not-launch",new AppPersistence(Path.Combine(root,"sponsor-profile")),options,_=>{},_=>{}))
        {
            var row=new DownloadItem {Url=url,Selection=partialSelection};
            Refuses(()=>coordinator.Add(row),"Incompatible SponsorBlock subtitle combination must fail at admission");
            Require(coordinator.Outstanding==0 && row.StageDir.Length==0,"Admission refusal must not launch or create a per-job stage");
        }
        new DownloadSelection(url).Validate(url,options);
        partialSelection.Validate(url,new DownloadOptions {Sponsor=true});
    }
    private static string Marker(string media,DownloadSection section,params (string Language,string Extension,string Path)[] tracks)=>
        (tracks.Length==0?"NA":string.Join(", ",tracks.Select(track=>track.Language)))+"|"+JsonSerializer.Serialize(new Dictionary<string,object?> {
            ["filepath"]=media,["section_start"]=section.StartSeconds,["section_end"]=section.EndSeconds,
            ["requested_subtitles.:.ext"]=tracks.Select(track=>track.Extension).ToArray(),
            ["requested_subtitles.:.filepath"]=tracks.Select(track=>track.Path).ToArray(),
        });
    private static async Task LocalFormats(string root,string ffmpeg,string ffprobe)
    {
        foreach(string extension in new[]{"srt","ass","ssa","vtt"})
        {
            string stage=Path.Combine(root,"local-"+extension);Directory.CreateDirectory(stage);
            string media=Path.Combine(stage,"clip.webm");
            await Tool(ffmpeg,["-hide_banner","-loglevel","error","-f","lavfi","-i","color=s=160x90:r=24:d=2","-an","-c:v","libvpx-vp9","-deadline","realtime",media]);
            string source=Path.Combine(stage,"source.srt");
            File.WriteAllText(source,"1\n00:00:04,000 --> 00:00:08,000\nOVERLAP\n\n2\n00:00:05,500 --> 00:00:06,500\nMIDDLE\n\n");
            string subtitle=extension=="srt"?source:Path.Combine(stage,"source."+extension);
            if(extension!="srt")await Tool(ffmpeg,["-hide_banner","-loglevel","error","-i",source,subtitle]);
            var section=extension=="ssa"?new DownloadSection(5.042,7.042):new DownloadSection(5,7);
            var native=extension=="ssa"?new DownloadSection(5.041666667,7.041666667):section;
            await new SubtitleTimelineService(ffmpeg,ffprobe).ProcessAsync(stage,[media],[section],[Marker(media,native,("en",extension,subtitle))],true,CancellationToken.None,
                [(native.StartSeconds,native.EndSeconds)]);
            string extracted=await Tool(ffmpeg,["-hide_banner","-loglevel","error","-i",media,"-map","0:s:0","-f","srt","pipe:1"]);
            var cues=SubtitleTimelineService.ParseSrt(Encoding.UTF8.GetBytes(extracted));
            Require(cues.Length==3 && cues[1].Text.Contains("MIDDLE",StringComparison.Ordinal) && cues.All(cue=>cue.Start>=0 && cue.End<=2000),
                "Real "+extension+" normalization and WebM embedding must retain overlapping dialogue on the clip timeline: "+extracted);
            Require(File.ReadAllText(Path.Combine(stage,"clip.en.vtt")).Contains(extension=="ssa"?"00:00:00.458 --> 00:00:01.458":"00:00:00.500 --> 00:00:01.500",StringComparison.Ordinal),"Normalized sidecars must use the precise native origin, with millisecond text timestamps");
        }
        string failure=Path.Combine(root,"unsupported");Directory.CreateDirectory(failure);
        string raw=Path.Combine(failure,"clip.mp4"),json=Path.Combine(failure,"source.json3");File.WriteAllText(raw,"MEDIA");File.WriteAllText(json,"{}");
        var range=new DownloadSection(5,7);
        await RefusesAsync(()=>new SubtitleTimelineService(ffmpeg,ffprobe).ProcessAsync(failure,[raw],[range],[Marker(raw,range,("en","json3",json))],true,CancellationToken.None),"JSON3 must fail before modifying or publishing media");
        string ttml=Path.Combine(failure,"source.ttml");File.WriteAllText(ttml,"<tt/>");
        await RefusesAsync(()=>new SubtitleTimelineService(ffmpeg,ffprobe).ProcessAsync(failure,[raw],[range],[Marker(raw,range,("en","ttml",ttml))],false,CancellationToken.None),"TTML must fail explicitly before publication");
        Require(File.ReadAllText(raw)=="MEDIA" && File.ReadAllText(json)=="{}","Unsupported formats preserve their local inputs");
        await RefusesAsync(()=>new SubtitleTimelineService(ffmpeg,ffprobe).ProcessAsync(failure,[raw],[range],[Marker(raw,range,("en","vtt",Path.Combine(root,"outside.vtt")))],false,CancellationToken.None),"Unowned subtitle input must fail");
        await RefusesAsync(()=>new SubtitleTimelineService(ffmpeg,ffprobe).ProcessAsync(failure,[raw],[range],[Marker(raw,new(4,7),("en","json3",json))],false,CancellationToken.None),"Manifest bounds must match the admitted range");
    }
    private static async Task OwnedCancellation(string root,string ffmpeg,string ffprobe)
    {
        string stage=Path.Combine(root,"cancel");Directory.CreateDirectory(stage);
        string media=Path.Combine(stage,"clip.mp4"),subtitle=Path.Combine(stage,"source.vtt");
        File.WriteAllText(media,"MEDIA");File.WriteAllText(subtitle,"WEBVTT\n\n00:00:05.500 --> 00:00:06.500\nTEXT\n");
        var section=new DownloadSection(5,7);
        foreach(bool timeout in new[]{false,true})
        {
            var started=new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool joined=false,paused=!timeout;
            using var stop=new CancellationTokenSource();
            async Task<FragmentMergeToolOutput> Run(ProcessStartInfo ignored,int stdout,int stderr,CancellationToken token)
            {
                string executable=Path.Combine(AppContext.BaseDirectory,typeof(SubtitleTimelineTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
                var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};start.ArgumentList.Add("--subtitle-timeline-wait");
                using var child=ManagedChildProcess.Start(start);started.TrySetResult(child.Process.Id);
                try
                {
                    var captured=await BoundedProcessOutput.ReadAsync(child,stdout,stderr,token);
                    return new(child.Process.ExitCode,captured.StandardOutput,captured.StandardError);
                }
                finally{joined=child.Process.HasExited;}
            }
            var service=new SubtitleTimelineService(ffmpeg,ffprobe,Run,()=>paused,TimeSpan.FromMilliseconds(250));
            var task=service.ProcessAsync(stage,[media],[section],[Marker(media,section,("en","vtt",subtitle))],false,stop.Token);
            int pid=await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if(!timeout)
            {
                await Task.Delay(500);Require(!task.IsCompleted,"Paused time must not consume the processing budget");stop.Cancel();
                try{await task;throw new InvalidOperationException("Expected cancellation");}catch(OperationCanceledException){}
            }
            else await RefusesAsync(()=>task,"An expired active budget must fail without publication");
            Require(joined && !Alive(pid) && !Directory.GetDirectories(stage).Any() && File.ReadAllText(media)=="MEDIA", "Cancellation/timeout must join the owned process and both pipes, remove scratch and preserve original media");
        }
    }
    private static bool Alive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
    private static async Task RealHls(string root,string ffmpeg,string ffprobe)
    {
        string source=Path.Combine(root,"hls");Directory.CreateDirectory(source);
        string video=Path.Combine(source,"source.mp4");
        await Tool(ffmpeg,["-hide_banner","-loglevel","error","-f","lavfi","-i","testsrc2=s=160x90:r=24:d=20","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=20","-c:v","libx264","-preset","ultrafast","-g","48","-c:a","aac","-shortest",video]);
        await Tool(ffmpeg,["-hide_banner","-loglevel","error","-i",video,"-c","copy","-f","hls","-hls_time","4","-hls_playlist_type","vod","-hls_segment_filename",Path.Combine(source,"part%02d.ts"),Path.Combine(source,"video.m3u8")]);
        string captions="WEBVTT\n\n00:00:01.000 --> 00:00:02.000\nBEFORE\n\n00:00:04.000 --> 00:00:05.500\nLEFT\n\n00:00:05.500 --> 00:00:06.500\nIN_SELECTED_RANGE\n\n00:00:06.500 --> 00:00:08.000\nRIGHT\n\n00:00:15.000 --> 00:00:16.000\nAFTER\n";
        File.WriteAllText(Path.Combine(source,"captions.vtt"),captions);
        File.WriteAllText(Path.Combine(source,"german.vtt"),captions.Replace("IN_SELECTED_RANGE","DE_IN_RANGE",StringComparison.Ordinal));
        File.WriteAllText(Path.Combine(source,"empty.vtt"),"WEBVTT\n\n00:00:15.000 --> 00:00:16.000\nOUTSIDE_ONLY\n");
        foreach(string name in new[]{"en","ru","de"})File.WriteAllText(Path.Combine(source,name+".m3u8"),"#EXTM3U\n#EXT-X-TARGETDURATION:20\n#EXT-X-PLAYLIST-TYPE:VOD\n#EXTINF:20,\n"+(name=="en"?"captions.vtt":name=="de"?"german.vtt":"empty.vtt")+"\n#EXT-X-ENDLIST\n");
        File.WriteAllText(Path.Combine(source,"master.m3u8"),"#EXTM3U\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"English\",LANGUAGE=\"en\",URI=\"en.m3u8\"\n#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"Russian\",LANGUAGE=\"ru\",URI=\"ru.m3u8\"\n#EXT-X-STREAM-INF:BANDWIDTH=300000,RESOLUTION=160x90,CODECS=\"avc1.42c00a,mp4a.40.2\",SUBTITLES=\"subs\"\nvideo.m3u8\n");
        await using var server=new Origin(source);
        string url=server.Url;
        string ytDlp=DependencyCenter.Executable("yt-dlp.exe");Require(File.Exists(ytDlp),"Packaged yt-dlp is required");
        string master=Path.Combine(source,"master.m3u8");
        File.WriteAllText(master,File.ReadAllText(master).Replace("#EXT-X-STREAM-INF:","#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=\"subs\",NAME=\"German\",LANGUAGE=\"de\",URI=\"de.m3u8\"\n#EXT-X-STREAM-INF:",StringComparison.Ordinal));
        foreach(var (name,write,embed,multiple,language) in new[]{("sidecar",true,false,false,"en,de,ru"),("embedded",false,true,false,"en,de,ru"),("both",true,true,false,"en,de,ru"),("multiple",true,true,true,"en"),("empty",true,true,false,"ru"),("unavailable",true,true,false,"fr")})
        {
            string folder=Path.Combine(root,name);var data=new AppPersistence(Path.Combine(folder,"profile"));
            var selection=new DownloadSelection(url,new(5,7),DurationSeconds:20);
            if(multiple)selection=selection with {Sections=new([new(5,7),new(15,17)])};
            var row=new DownloadItem {Url=url,Selection=selection};
            var options=new DownloadOptions {Output=Path.Combine(folder,"output"),Threads="1",Container=multiple?"MKV":"MP4",WriteSubs=write,EmbedSubs=embed,SubtitleLangs=language,EmbedMetadata=false,EmbedChapters=false};
            var records=new ConcurrentQueue<JournalRecord>();
            await using(var coordinator=new DownloadCoordinator(ytDlp,data,options,_=>{},_=>{},records.Enqueue))
            {Require(coordinator.Add(row),"Subtitle fixture must be admitted");await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(100));}
            Require(row.Status=="Completed",name+" real HLS subtitle pipeline failed: "+row.RawError);
            string[] outputs=row.OwnedPaths.Where(path=>Path.GetExtension(path)==(multiple?".mkv":".mp4")).ToArray();
            Require(outputs.Length==(multiple?2:1) && data.ReadHistory().Count==0 && row.IsPartialResult,"Partial subtitle publication must retain exact media count and empty full history");
            Require(row.OwnedPaths.Count==(language=="fr"?1:language=="ru"?2:4),"Publication must contain exactly media and clipped sidecars, without full-source subtitles or scratch");
            Require(row.OwnedPaths.All(path=>!path.Contains("subtitles-",StringComparison.Ordinal)) && !row.OwnedPaths.Any(path=>Path.GetFileName(path).StartsWith("fragment-NA",StringComparison.Ordinal)),"Original full sidecars and scratch must not be published");
            foreach(string media in outputs)
            {
                bool second=multiple && Path.GetFileName(media).StartsWith("fragment-0000000015000",StringComparison.Ordinal);
                string expected=second?"AFTER":"IN_SELECTED_RANGE";
                string sidecar=Path.ChangeExtension(media,null)+".en.vtt";
                if(language.Contains("en",StringComparison.Ordinal))
                {
                    string text=File.ReadAllText(sidecar);
                    Require(text.Contains(expected,StringComparison.Ordinal) && !text.Contains("BEFORE",StringComparison.Ordinal) &&
                        (second || !text.Contains("AFTER",StringComparison.Ordinal)) && text.Contains(second?"00:00:00.000 --> 00:00:01.000":"00:00:00.500 --> 00:00:01.500",StringComparison.Ordinal),"Each sidecar must use its own range origin: "+text);
                    if(embed)
                    {
                        string actual=await Tool(ffmpeg,["-hide_banner","-loglevel","error","-i",media,"-map","0:s:0","-f","srt","pipe:1"]);
                        Require(actual.Contains(expected,StringComparison.Ordinal) && !actual.Contains("BEFORE",StringComparison.Ordinal) && (second || !actual.Contains("AFTER",StringComparison.Ordinal)),"Embedded dialogue must match this range: "+actual);
                        if(language.Contains("de",StringComparison.Ordinal))
                        {
                            string german=await Tool(ffmpeg,["-hide_banner","-loglevel","error","-i",media,"-map","0:s:1","-f","srt","pipe:1"]);
                            Require(german.Contains("DE_IN_RANGE",StringComparison.Ordinal),"A second selected language must retain its own dialogue and track");
                        }
                    }
                }
            }
            if(language.Contains("ru",StringComparison.Ordinal))Require(File.ReadAllText(Path.ChangeExtension(row.Path,null)+".ru.vtt").Trim()=="WEBVTT","A language without overlapping cues must have an explicit empty sidecar");
            if(language=="ru")
            {
                using var inspection=JsonDocument.Parse(await Tool(ffprobe,["-v","error","-select_streams","s","-show_entries","stream=index","-of","json",row.Path]));
                Require(inspection.RootElement.GetProperty("streams").GetArrayLength()==0,"An all-empty selection must not claim an embedded subtitle track");
            }
            if(language=="fr")Require(row.OwnedPaths.Count==1,"An unavailable requested language must retain existing no-subtitle semantics");
            Require(records.Any(record=>record.Message.Contains("event=subtitle_timeline_ready",StringComparison.Ordinal)),"Production coordinator must complete subtitle processing before publication");
        }
        // A processing failure and cancellation preserve an earlier owned result.
        string failed=Path.Combine(root,"failed");Directory.CreateDirectory(failed);string sentinel=Path.Combine(failed,"previous.mp4");File.WriteAllText(sentinel,"PREVIOUS");
        File.WriteAllText(Path.Combine(source,"captions.vtt"),"not a subtitle");
        var failedData=new AppPersistence(Path.Combine(root,"failed-profile"));
        var failedRow=new DownloadItem {Url=url,Selection=new(url,new(5,7),DurationSeconds:20)};
        var failedOptions=new DownloadOptions {Output=failed,Threads="1",Container="MP4",WriteSubs=true,EmbedSubs=true,SubtitleLangs="en",EmbedMetadata=false,EmbedChapters=false};
        await using(var coordinator=new DownloadCoordinator(ytDlp,failedData,failedOptions,_=>{},_=>{}))
        {coordinator.Add(failedRow);await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(100));}
        Require(failedRow.Status=="Failed" && failedRow.RawError.Contains("invalid header",StringComparison.Ordinal) && failedRow.OwnedPaths.Count==0 && failedData.ReadHistory().Count==0 &&
            File.ReadAllText(sentinel)=="PREVIOUS" && Directory.GetFiles(failed).All(path=>path==sentinel || Path.GetExtension(path)==".txt"),
            "Failed subtitle conversion must publish nothing, leave history empty and preserve previous files: "+failedRow.Status+" "+failedRow.RawError);
        File.WriteAllText(Path.Combine(source,"captions.vtt"),captions);
        DownloadCoordinator? cancelCoordinator=null;bool cancelled=false;
        var cancelledRow=new DownloadItem {Url=url,Selection=new(url,new(5,7),DurationSeconds:20)};
        await using(cancelCoordinator=new DownloadCoordinator(ytDlp,failedData,failedOptions,item=>{if(!cancelled && item.State=="Processing" && item.ProgressDetail=="Субтитры выбранных фрагментов"){cancelled=true;cancelCoordinator!.Cancel(item);}},_=>{}))
        {cancelCoordinator.Add(cancelledRow);await cancelCoordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(100));}
        Require(cancelled && cancelledRow.Status=="Interrupted" && cancelledRow.OwnedPaths.Count==0 && failedData.ReadHistory().Count==0 && File.ReadAllText(sentinel)=="PREVIOUS","Cancellation before subtitle processing must preserve publication/history boundaries");
    }
    private static async Task<string> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true};foreach(string value in arguments)start.ArgumentList.Add(value);
        using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(60));using var child=ManagedChildProcess.Start(start);
        var captured=await BoundedProcessOutput.ReadAsync(child,8*1024*1024,128*1024,stop.Token);
        Require(child.Process.ExitCode==0,Path.GetFileName(executable)+": "+Encoding.UTF8.GetString(captured.StandardError));return Encoding.UTF8.GetString(captured.StandardOutput);
    }
    private sealed class Origin:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _responses=new();private readonly Task _accept;private readonly string _root;
        internal string Url {get;}
        internal Origin(string root){_root=root;_listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/master.m3u8";_accept=Accept();}
        private async Task Accept()
        {try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_responses.Add(Respond(client));}}catch(OperationCanceledException) when(_stop.IsCancellationRequested){}catch(SocketException) when(_stop.IsCancellationRequested){}}
        private async Task Respond(TcpClient client)
        {
            try
            {
                using(client)
                {
                    var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,true);
                    string line=await reader.ReadLineAsync(_stop.Token)??"";string request=line;int length=line.Length;
                    while(!string.IsNullOrEmpty(line=await reader.ReadLineAsync(_stop.Token)??"")){length+=line.Length;if(length>16384)throw new IOException("Fixture headers too large");}
                    string name=Uri.UnescapeDataString(request.Split(' ')[1].TrimStart('/'));
                    if(name.Contains('/') || name.Contains('\\') || name.Contains("..",StringComparison.Ordinal))throw new IOException("Invalid fixture path");
                    byte[] bytes=await File.ReadAllBytesAsync(Path.Combine(_root,name),_stop.Token);
                    string content=name.EndsWith("m3u8",StringComparison.Ordinal)?"application/vnd.apple.mpegurl":name.EndsWith("vtt",StringComparison.Ordinal)?"text/vtt":"video/mp2t";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: "+content+"\r\nContent-Length: "+bytes.Length+"\r\nConnection: close\r\n\r\n"),_stop.Token);
                    if(!request.StartsWith("HEAD ",StringComparison.Ordinal))await stream.WriteAsync(bytes,_stop.Token);
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}catch(IOException){}catch(SocketException){}
        }
        public async ValueTask DisposeAsync(){_stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_responses);_stop.Dispose();}
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Subtitle timeline: "+message);}
    private static void Refuses(Action action,string message){try{action();}catch(ArgumentException){return;}throw new InvalidOperationException(message);}
    private static async Task RefusesAsync(Func<Task> action,string message){try{await action();}catch(InvalidDataException){return;}throw new InvalidOperationException(message);}
}
