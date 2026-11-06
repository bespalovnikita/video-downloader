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

internal static class MultipleFragmentTests
{
    private const string Prefix="https://multiple-fragments.invalid/";
    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        string name=url[Prefix.Length..];
        Require(name.All(character=>char.IsAsciiLetterOrDigit(character)||character=='-'),"Unexpected fixture name");
        string root=File.ReadAllText(Value(args,"--cookies"));
        File.AppendAllText(Path.Combine(root,name+".calls"),JsonSerializer.Serialize(args)+Environment.NewLine);
        var ranges=Values(args,"--download-sections");
        Require(ranges.Length>0 && args.Contains("--force-keyframes-at-cuts"),"The real child must receive numeric section arguments");
        string stage=Path.GetDirectoryName(Value(args,"-o"))!;Directory.CreateDirectory(stage);
        for(int index=0;index<ranges.Length;index++)
        {
            string[] boundary=ranges[index][1..].Split('-');
            double start=double.Parse(boundary[0],CultureInfo.InvariantCulture),end=double.Parse(boundary[1],CultureInfo.InvariantCulture);
            string path=Path.Combine(stage,"range-"+(index+1)+(name=="wrong-container" && index==1?".webm":".mp4"));
            File.WriteAllText(path,"OWNED OFFLINE FRAGMENT "+index);
            Console.WriteLine("__VD_PLAN__:"+JsonSerializer.Serialize(new{id="batch",format_id="muxed",n_entries=1,section_start=start,section_end=end}));
            Console.WriteLine("__VD_PROGRESS__|50.0%|1MiB/s|00:01|100|200|batch:muxed|avc1|aac|"+boundary[0]+"|"+boundary[1]);
            if(name!="missing-marker" || index==0)
                Console.WriteLine("__VD_FILE__:"+(name=="duplicate-marker"?Path.Combine(stage,"range-1.mp4"):path));
            if(name=="cancel" && index==0)
            {
                File.WriteAllText(Path.Combine(root,"cancel.ready"),Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                Thread.Sleep(Timeout.Infinite);
            }
            if(name=="partial-failure" && index==0)
            {
                Console.Error.WriteLine("ERROR: HTTP Error 503: Service Unavailable");Environment.ExitCode=1;return true;
            }
            Console.WriteLine("__VD_PROGRESS__|100.0%|1MiB/s|00:00|200|200|batch:muxed|avc1|aac|"+boundary[0]+"|"+boundary[1]);
        }
        if(name=="extra-media")File.WriteAllText(Path.Combine(stage,"unreported.mp4"),"UNEXPECTED MEDIA");
        return true;
    }
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-multiple-fragments-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            ModelsAndCheckpoint(root);
            await ActualChildContracts(root);
            await ActualMedia(root);
            Console.WriteLine("PASS: immutable multi-fragment plans, legacy restore, actual child snapshots/cancellation, batch publication and actual yt-dlp/FFmpeg outputs");
        }
        finally{Directory.Delete(root,true);}
    }
    private static DownloadSelection Plan(string url)=>new(url,new DownloadSection(5,7),DurationSeconds:8)
        {Sections=new DownloadSectionSet(new[]{new DownloadSection(5,7),new DownloadSection(1,2.5)})};
    private static void ModelsAndCheckpoint(string root)
    {
        const string url=Prefix+"model";
        var input=new[]{new DownloadSection(5,7),new DownloadSection(1,2.5)};
        var set=new DownloadSectionSet(input);input[0]=new DownloadSection(0,.5);
        Require(set[0]==new DownloadSection(5,7),"A mutable caller array must not change an admitted section plan");
        var selection=Plan(url);
        Require(selection.IsPartial && selection.EffectiveSections.Count==2 && selection.EffectiveSections[0].StartSeconds==5,
            "The ordered plan must keep user order instead of sorting it for execution");
        Require(selection==Plan(url),"Equivalent immutable plans must compare structurally for modal admission guards");
        new DownloadSectionSet(new[]{new DownloadSection(0,1),new DownloadSection(1,2)}).Validate(2);
        new DownloadSectionSet(new[]{new DownloadSection(.1+.2,1),new DownloadSection(1,2)}).Validate(2);
        foreach(var ranges in new[]{
            Array.Empty<DownloadSection>(),new[]{new DownloadSection(0,2),new DownloadSection(1,3)},
            new[]{new DownloadSection(1,2),new DownloadSection(1,2)},new[]{new DownloadSection(2,1)},
            Enumerable.Range(0,33).Select(index=>new DownloadSection(index*2,index*2+1)).ToArray(),
        })Refuses(()=>new DownloadSectionSet(ranges),"Empty, overlapping, duplicate, inverted or excessive plans must be refused");
        Require(!(selection with{DurationSeconds=6}).TryValidate(url,new DownloadOptions(),out _),"Every range must obey the known duration");
        Require(!(selection with{Section=new DownloadSection(0,1)}).TryValidate(url,new DownloadOptions(),out _),
            "Conflicting legacy first-range and multi-range fields must fail closed");
        var plan=YtDlpOutputParser.Parse("__VD_PLAN__:{\"id\":\"x\",\"format_id\":\"muxed\",\"n_entries\":null,\"section_start\":1,\"section_end\":null}") as YtDlpOutputEvent.Plan;
        Require(plan is {SectionStart:1,SectionEnd:null} && plan.FormatIds.Single()=="muxed","Null extractor fields must not discard the valid fragment plan");

        var data=new AppPersistence(Path.Combine(root,"checkpoint"));
        var row=new DownloadItem{Url=url,Selection=selection,CompletedSelection=selection,Status="Completed",State="Done"};
        data.Save(new[]{row},new DownloadOptions());
        var restored=data.Restore()!.Queue.Single();
        Require(restored.Selection==selection && restored.CompletedSelection==selection && restored.IsPartialResult,
            "A complete multi-output plan must survive the real session checkpoint with partial-result semantics");
        var old=new DownloadSelection(url,new DownloadSection(1,2));
        row.Selection=old;row.CompletedSelection=old;data.Save(new[]{row},new DownloadOptions());
        var legacy=JsonNode.Parse(File.ReadAllText(data.SessionPath))!;
        foreach(string property in new[]{"Selection","CompletedSelection"})legacy["Queue"]![0]![property]!.AsObject().Remove("Sections");
        File.WriteAllText(data.SessionPath,legacy.ToJsonString());
        Require(data.Restore()!.Queue.Single().Selection!.EffectiveSections.Single()==new DownloadSection(1,2),
            "Old single-range checkpoints must keep their original meaning without migration");
        var invalid=JsonNode.Parse(JsonSerializer.Serialize(selection,AppPersistence.Json))!.AsObject();
        invalid["Sections"]=new JsonArray();
        Refuses(()=>JsonSerializer.Deserialize<DownloadSelection>(invalid.ToJsonString(),AppPersistence.Json),"An empty serialized list is invalid, not a full-video request");
    }
    private static async Task ActualChildContracts(string root)
    {
        string control=Path.Combine(root,"child-control");File.WriteAllText(control,root);
        foreach(string mode in new[]{"snapshot","missing-marker","duplicate-marker","wrong-container","extra-media","partial-failure"})
        {
            string output=Path.Combine(root,"output-"+mode);Directory.CreateDirectory(output);
            string sentinel=Path.Combine(output,"range-1.mp4");File.WriteAllText(sentinel,"FOREIGN FILE");
            var data=new AppPersistence(Path.Combine(root,"profile-"+mode));
            var row=new DownloadItem{Url=Prefix+mode,Selection=Plan(Prefix+mode)};
            var expected=row.Selection;
            var progress=new ConcurrentQueue<double>();
            var options=new DownloadOptions{Output=output,Threads="1",Cookies=control,Container="MP4",EmbedMetadata=false,EmbedChapters=false};
            await using(var engine=new DownloadCoordinator(TestExecutable(),data,options,item=>progress.Enqueue(item.Progress),_=>{}))
            {
                engine.Pause(true);Require(engine.Add(row),"The entire ordered plan must be admitted once");
                var run=engine.RunAsync();
                row.Selection=new DownloadSelection(row.Url,new DownloadSection(0,.5),DurationSeconds:8);
                options.FilenameTemplate="changed.%(ext)s";engine.Pause(false);
                await run.WaitAsync(TimeSpan.FromSeconds(15));
            }
            var calls=File.ReadAllLines(Path.Combine(root,mode+".calls"));
            Require(calls.Length==1,"A partial multi-output invocation must not restart transfer or fall back to another quality: "+mode);
            var args=JsonSerializer.Deserialize<string[]>(calls.Single())!;
            Require(Values(args,"--download-sections").SequenceEqual(new[]{"*5-7","*1-2.5"}) &&
                Value(args,"-o").Contains("section_start*1000+0.5",StringComparison.Ordinal) && !Value(args,"-o").Contains("changed",StringComparison.Ordinal),
                "The real child must receive the admitted range order and collision-free filename plan despite later UI/options edits");
            if(mode=="snapshot")
            {
                Require(row.Status=="Completed" && row.CompletedSelection==expected && row.OwnedPaths.Count==2 && row.IsPartialResult &&
                    row.OwnedPaths.All(File.Exists) && row.OwnedPaths.Contains(row.Path) && !row.OwnedPaths.Contains(sentinel),
                    "Every selected output must be published together with exact ownership and the admitted selection snapshot");
                Require(progress.Any(value=>value is >0 and <100),"Multiple fragments need honest intermediate aggregate progress");
                Require(row.TransferredBytes==400,"Equal stream counters and late final samples must account for both fragments once");
            }
            else Require(row.Status=="Failed" && row.OwnedPaths.Count==0 && Directory.GetFiles(output).Where(path=>Path.GetExtension(path)!=".txt").SequenceEqual(new[]{sentinel}),
                "Missing/duplicate markers, wrong containers and incomplete batches must publish none of their outputs: "+mode+" "+row.RawError);
            Require(File.ReadAllText(sentinel)=="FOREIGN FILE" && data.ReadHistory().Count==0 && data.ReadHistoryLibrary().Entries.Count==0,
                "A batch must preserve unrelated existing media and never mark the full URL downloaded");
        }
        string cancelOutput=Path.Combine(root,"cancel-output");Directory.CreateDirectory(cancelOutput);
        var cancelData=new AppPersistence(Path.Combine(root,"cancel-profile"));
        var cancelled=new DownloadItem{Url=Prefix+"cancel",Selection=Plan(Prefix+"cancel")};
        await using(var engine=new DownloadCoordinator(TestExecutable(),cancelData,new DownloadOptions{Output=cancelOutput,Threads="1",Cookies=control},_=>{},_=>{}))
        {
            Require(engine.Add(cancelled),"The cancellable batch must be admitted");var run=engine.RunAsync();
            await Until(()=>File.Exists(Path.Combine(root,"cancel.ready")),"The batch must reach its first staged fragment");
            engine.Cancel(cancelled);await run.WaitAsync(TimeSpan.FromSeconds(10));
        }
        Require(cancelled.Status=="Interrupted" && cancelled.State=="Cancelled" && Directory.GetFiles(cancelOutput).Length==0 &&
            cancelData.ReadHistory().Count==0 && cancelData.ReadHistoryLibrary().Entries.Count==0 && cancelled.StageDir.Length==0,"Cancelling after one staged fragment must join the child and clean the complete private batch");
    }
    private static async Task ActualMedia(string root)
    {
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        string probe=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
        string extractor=DependencyCenter.Executable("yt-dlp.exe");
        Require(File.Exists(ffmpeg) && File.Exists(probe) && File.Exists(extractor),"Real multi-fragment tests require the actual extractor and media tools");
        string input=Path.Combine(root,"colours.mp4");
        await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i","color=c=red:s=160x90:r=24:d=4",
            "-f","lavfi","-i","color=c=blue:s=160x90:r=24:d=4","-f","lavfi","-i","sine=frequency=440:sample_rate=44100:duration=8",
            "-filter_complex","[0:v][1:v]concat=n=2:v=1:a=0[v]","-map","[v]","-map","2:a","-c:v","libx264","-preset","ultrafast",
            "-pix_fmt","yuv420p","-c:a","aac","-movflags","+faststart",input});
        await using var source=new MediaServer(await File.ReadAllBytesAsync(input));
        string output=Path.Combine(root,"real-output");Directory.CreateDirectory(output);
        string sentinel=Path.Combine(output,"unrelated.mp4");File.WriteAllText(sentinel,"KEEP");
        var data=new AppPersistence(Path.Combine(root,"real-profile"));
        var row=new DownloadItem{Url=source.Url,Selection=Plan(source.Url)};
        await using(var engine=new DownloadCoordinator(extractor,data,new DownloadOptions{Output=output,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=false},_=>{},_=>{}))
        {Require(engine.Add(row),"The actual two-range video must be admitted");await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(90));}
        Require(row.Status=="Completed" && row.IsPartialResult && row.OwnedPaths.Count==2,
            "Actual yt-dlp must publish both separate fragments: "+row.RawError);
        Require(Path.GetFileName(row.Path).Contains("0000000005000-0000000007000ms",StringComparison.Ordinal),
            "The primary output must correspond to the first user-selected range rather than sorted chronological order");
        foreach(var path in row.OwnedPaths)
        {
            byte[] inspection=await Tool(probe,new[]{"-v","error","-show_entries","format=duration:stream=codec_type","-of","json",path});
            using var metadata=JsonDocument.Parse(inspection);
            double duration=double.Parse(metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
            var streams=metadata.RootElement.GetProperty("streams").EnumerateArray().Select(stream=>stream.GetProperty("codec_type").GetString()).ToHashSet();
            bool blue=Path.GetFileName(path).Contains("0000000005000-0000000007000ms",StringComparison.Ordinal);
            Require(Math.Abs(duration-(blue?2:1.5))<.25 && streams.Contains("video") && streams.Contains("audio"),
                "Each published file must contain its own requested video/audio duration");
            byte[] pixel=await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-i",path,"-frames:v","1","-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"});
            Require(pixel.Length==3 && (blue?pixel[2]>200 && pixel[0]<80:pixel[0]>200 && pixel[2]<80),
                "The actual selected time ranges must contain the expected blue/red source frames");
        }
        Require(data.ReadHistory().Count==0 && data.ReadHistoryLibrary().Entries.Count==0 && File.ReadAllText(sentinel)=="KEEP","A real multi-clip result must preserve foreign files and full-video history, including its searchable library");
        var options=new DownloadOptions{Output=output};data.Save(new[]{row},options);
        Require(data.Restore()!.Queue.Single().CompletedSelection==row.CompletedSelection,"The complete published plan must survive restart");

        // Match the explicit Retry URL admission boundary: the previous success
        // stays on disk, while the new attempt owns only its new batch.
        var prior=row.OwnedPaths.ToDictionary(path=>path,path=>File.ReadAllBytes(path));
        row.Selection=new DownloadSelection(row.Url,new DownloadSection(5,7),DurationSeconds:8)
            {Sections=new DownloadSectionSet(new[]{new DownloadSection(5,7),new DownloadSection(2,3)})};
        var retrySelection=row.Selection;
        row.Status="Queued";row.State="Queued";row.Path="";row.OwnedPaths.Clear();row.Progress=0;
        await using(var engine=new DownloadCoordinator(extractor,data,new DownloadOptions{Output=output,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=false},_=>{},_=>{}))
        {Require(engine.Add(row),"An explicit retry must admit the newly edited complete plan");await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(90));}
        Require(row.Status=="Completed" && row.CompletedSelection==retrySelection && row.OwnedPaths.Count==2 &&
            row.OwnedPaths.All(path=>!prior.ContainsKey(path)) && prior.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)) &&
            data.ReadHistory().Count==0,"A retried multi-range batch must use the new snapshot, preserve every previous file, and own only new outputs");
        string edited=row.OwnedPaths.Single(path=>Path.GetFileName(path).Contains("0000000002000-0000000003000ms",StringComparison.Ordinal));
        using var retryMetadata=JsonDocument.Parse(await Tool(probe,new[]{"-v","error","-show_entries","format=duration","-of","json",edited}));
        double retryDuration=double.Parse(retryMetadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        Require(Math.Abs(retryDuration-1)<.25,"The explicit retry must produce the newly edited range's actual duration");
    }
    private static async Task<byte[]> Tool(string executable,IEnumerable<string> args)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in args)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output=await BoundedProcessOutput.ReadAsync(child,1024*1024,1024*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"Actual media tool failed: "+Encoding.UTF8.GetString(output.StandardError));return output.StandardOutput;
    }
    private static string TestExecutable()=>Path.Combine(AppContext.BaseDirectory,typeof(MultipleFragmentTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static string Value(string[] args,string key)=>Values(args,key).FirstOrDefault()??"";
    private static string[] Values(string[] args,string key)=>args.Select((value,index)=>(value,index)).Where(pair=>pair.value==key && pair.index+1<args.Length).Select(pair=>args[pair.index+1]).ToArray();
    private static async Task Until(Func<bool> predicate,string message)
    {var timer=Stopwatch.StartNew();while(!predicate()){Require(timer.Elapsed<TimeSpan.FromSeconds(10),message);await Task.Delay(15);}}
    private static void Refuses(Action action,string message){bool failed=false;try{action();}catch(Exception){failed=true;}Require(failed,message);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Multiple-fragment test: "+message);}

    private sealed class MediaServer:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _handlers=new();
        private readonly byte[] _media;
        private readonly Task _accept;
        internal string Url {get;}
        internal MediaServer(byte[] media)
        {_media=media;_listener.Start();Url="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/source.mp4";_accept=Accept();}
        private async Task Accept()
        {
            try{while(!_stop.IsCancellationRequested){var client=await _listener.AcceptTcpClientAsync(_stop.Token);_handlers.Add(Respond(client));}}
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(SocketException) when(_stop.IsCancellationRequested){}
        }
        private async Task Respond(TcpClient client)
        {
            try
            {
                using(client)
                {
                    var stream=client.GetStream();using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,leaveOpen:true);
                    string request=await reader.ReadLineAsync(_stop.Token)??"";string? range=null;int read=request.Length;
                    while(true)
                    {
                        string? line=await reader.ReadLineAsync(_stop.Token);if(string.IsNullOrEmpty(line))break;
                        read+=line.Length;if(read>16*1024)throw new IOException("Fixture header budget exceeded");
                        if(line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase))range=line[6..].Trim();
                    }
                    int first=0,last=_media.Length-1;bool partial=false;
                    if(range?.StartsWith("bytes=",StringComparison.Ordinal)==true)
                    {
                        var parts=range[6..].Split('-');
                        if(parts.Length!=2 || !int.TryParse(parts[0],out first) || first<0 || first>=_media.Length)throw new IOException("Invalid fixture range");
                        if(parts[1].Length>0 && !int.TryParse(parts[1],out last))throw new IOException("Invalid fixture range end");
                        last=Math.Min(last,_media.Length-1);partial=true;
                    }
                    int count=last-first+1;
                    string response="HTTP/1.1 "+(partial?"206 Partial Content":"200 OK")+"\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: "+count+
                        (partial?"\r\nContent-Range: bytes "+first+"-"+last+"/"+_media.Length:"")+"\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(response),_stop.Token);
                    if(!request.StartsWith("HEAD ",StringComparison.Ordinal))await stream.WriteAsync(_media.AsMemory(first,count),_stop.Token);
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException){}
            catch(SocketException){}
        }
        public async ValueTask DisposeAsync()
        {_stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_handlers);_stop.Dispose();}
    }
}
