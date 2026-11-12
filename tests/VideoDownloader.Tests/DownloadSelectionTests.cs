using System.Collections.Concurrent;
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

// These tests inspect requests received by real managed children, and ask the
// packaged yt-dlp to resolve actual offline format metadata. No public site is used.
internal static class DownloadSelectionTests
{
    private const string Prefix="https://download-selection.invalid/";
    private static readonly MediaFormat Video=new("video-h264","mp4",1920,1080,60,"avc1.640028","none","",12_000_000,false);
    private static readonly MediaFormat Audio=new("audio-ru-high","m4a",0,0,0,"none","mp4a.40.2","ru-RU",1_200_000,true);
    private static readonly MediaFormat Combined=new("combined-ru","mp4",1280,720,30,"avc1","aac","ru-RU",null,false);

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        string control=Value(args,"--cookies");
        string root=File.ReadAllText(control,Encoding.UTF8);
        string name=url[Prefix.Length..];
        Require(name.Length>0 && name.All(c=>char.IsAsciiLetterOrDigit(c)||c=='-'),"Unexpected fixture name");
        string calls=Path.Combine(root,name+".calls.jsonl");
        File.AppendAllText(calls,JsonSerializer.Serialize(args)+Environment.NewLine,Encoding.UTF8);
        if(args.Contains("--dump-single-json",StringComparer.Ordinal))
        {
            Require(args.Contains("--skip-download",StringComparer.Ordinal),"Format discovery must be metadata-only");
            var item=new JsonObject {
                ["id"]="catalog",["title"]="Actual formats",["duration"]=125.5,["extractor"]="fixture",
                ["formats"]=name=="large-catalog"?LargeCatalogJson():CatalogJson(),
            };
            if(name=="collection")
                Console.WriteLine(new JsonObject { ["_type"]="playlist",["title"]="Collection",["entries"]=new JsonArray(item) }.ToJsonString());
            else Console.WriteLine(item.ToJsonString());
            return true;
        }
        if(name=="unavailable" || name=="transient" && File.ReadAllLines(calls).Length==1)
        {
            Console.Error.WriteLine(name=="unavailable"?"ERROR: Requested format is not available":"ERROR: HTTP Error 503: Service unavailable");
            Environment.ExitCode=1;
            return true;
        }
        string stage=Path.GetDirectoryName(Value(args,"-o"))!;
        Directory.CreateDirectory(stage);
        string extension=args.Contains("-x",StringComparer.Ordinal)?"mp3":OptionalValue(args,"--remux-video")??(name=="raw-audio"?"m4a":"mp4");
        string media=Path.Combine(stage,"selection."+extension);
        File.WriteAllText(media,"OFFLINE ARGUMENT CONTRACT MEDIA");
        Console.WriteLine("__VD_TITLE__:Selected request");
        Console.WriteLine("__VD_FILE__:"+media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-download-selection-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            TimeAndSelectionValidation();
            CatalogAndCheckpoint(root);
            await ColdCatalog(root);
            await LegacyCatalogUpgrade(root);
            await NativeRequests(root);
            await RealFormatSelectors(root);
            Console.WriteLine("PASS: per-URL selections, clip validation, bounded metadata formats, strict native requests/retries and offline yt-dlp selection");
        }
        finally { Directory.Delete(root,true); }
    }

    private static void TimeAndSelectionValidation()
    {
        Require(DownloadSection.TryParse("","",null,out var full,out _) && full is null,
            "Two empty range fields mean the full material and must permit a format-only selection");
        Require(DownloadSection.TryParse("1:02.25","01:02:03.5",4000,out var section,out _),"Minute/hour timestamps with fractions must parse");
        Require(section!=null && Math.Abs(section.StartSeconds-62.25)<.0001 && Math.Abs(section.EndSeconds-3723.5)<.0001,
            "Timestamp components must retain their exact meaning");
        Require(DownloadSection.TryParse("0:00","0:01.5",null,out var fromZero,out _) && fromZero?.StartSeconds==0,
            "A clip may begin at the start of a video with unknown duration");
        foreach(var (start,end,duration) in new (string,string,double?)[] {
            ("","2:00",null),("0:00","",null),("-1:00","2:00",null),("0:60","2:00",null),("0:00:60","2:00",null),
            ("NaN","2:00",null),("0:00","Infinity",null),("1e2","2:00",null),
            ("2:00","2:00",null),("3:00","2:00",null),("0:00","0:00",null),("0:00","2:01",120),
        })
            Require(!DownloadSection.TryParse(start,end,duration,out _,out string error) && !string.IsNullOrWhiteSpace(error),
                "Invalid range must explain its refusal: "+start+" / "+end);
        Refuses(()=>new DownloadSection(double.NaN,10).Validate(null),"Non-finite range must fail validation");
        Refuses(()=>new DownloadSection(0,double.PositiveInfinity).Validate(null),"Infinite range must fail validation");
        Refuses(()=>new DownloadSection(.0001,.0002).Validate(null),"Submillisecond bounds must not round into an empty native range");
        new DownloadSection(.1+.2,1).Validate(null);
        const string url=Prefix+"validation";
        var options=new DownloadOptions();
        new DownloadSelection(url,new DownloadSection(1.25,3.75),Video,Audio,125.5).Validate(url,options);
        new DownloadSelection(url,Format:Combined).Validate(url,options);
        new DownloadSelection(url,Format:Audio).Validate(url,options);
        new DownloadSelection(url,Format:Audio).Validate(url,new DownloadOptions {AudioOnly=true});
        foreach(double invalidFps in new[]{double.NaN,double.PositiveInfinity,0,-1,1001})
            Refuses(()=>new DownloadSelection(url,new DownloadSection(.042,.083)){FrameRate=invalidFps}.ValidatePersisted(url),
                "Persisted inferred frame rates must be finite and bounded");
        var grid=new DownloadSelection(url,new DownloadSection(.042,.083)){FrameRate=24};
        Require(grid.SectionArgument(grid.Section!)=="*0.041666667-0.083333333",
            "Export must recover exact nominal boundaries from rounded 24 FPS labels");
        var restoredGrid=JsonSerializer.Deserialize<DownloadSelection>(JsonSerializer.Serialize(grid,AppPersistence.Json),AppPersistence.Json)!;
        Require(restoredGrid.FrameRate==24 && restoredGrid.SectionArgument(restoredGrid.Section!)==grid.SectionArgument(grid.Section!),
            "The inferred automatic frame grid must survive checkpoint JSON");
        Require(new DownloadSelection(url,new DownloadSection(.042,.083)).SectionArgument(new(.042,.083))=="*0.042-0.083",
            "A legacy automatic selection without inferred FPS must retain its millisecond boundaries");
        Require(!JsonSerializer.Serialize(new DownloadSelection(url,new DownloadSection(.042,.083)),AppPersistence.Json)
            .Contains("FrameRate",StringComparison.OrdinalIgnoreCase),"An absent frame grid must not add null fields to a legacy checkpoint");
        Refuses(()=>new DownloadSelection(url,Format:Combined){FrameRate=24}.ValidatePersisted(url),
            "An inferred FPS must not override the explicit selected format's pinned FPS");
        foreach(var invalid in new[] {
            new DownloadSelection(url+"?different",Format:Combined),new DownloadSelection(url.ToUpperInvariant(),Format:Combined),
            new DownloadSelection(url,Format:Video),new DownloadSelection(url,Format:Combined,AudioFormat:Audio),
            new DownloadSelection(url,Format:Audio,AudioFormat:Audio),new DownloadSelection(url,AudioFormat:Audio),
            new DownloadSelection(url,new DownloadSection(2,200),DurationSeconds:125.5),
            new DownloadSelection(url,Format:Video with {Id="video-h264/best"},AudioFormat:Audio),
            new DownloadSelection(url,Format:Video with {Id="x]+ba"},AudioFormat:Audio),
            new DownloadSelection(url,Format:Video with {Id="video\n--exec"},AudioFormat:Audio),
        })
        {
            Require(!invalid.TryValidate(url,options,out string error) && error.Length>0,"Invalid selection must have a useful validation error");
            Refuses(()=>invalid.Validate(url,options),"Invalid selection must fail before launch");
        }
        Refuses(()=>new DownloadSelection(url,Format:Combined).Validate(url,new DownloadOptions {AudioOnly=true}),
            "MP3 extraction must not silently discard an explicitly chosen video format");
        Refuses(()=>new DownloadSelection(url,Format:Video,AudioFormat:Audio).Validate(url,new DownloadOptions {AudioTrack="Original"}),
            "Explicit formats cannot silently override a conflicting original-audio request");
        Refuses(()=>new DownloadSelection(url,Format:Video,AudioFormat:Audio).Validate(url,new DownloadOptions {AudioTrack="Languages",AudioLanguages="en"}),
            "Explicit formats cannot silently override a conflicting language request");
    }

    private static void CatalogAndCheckpoint(string root)
    {
        using var json=JsonDocument.Parse(new JsonObject {["formats"]=CatalogJson()}.ToJsonString());
        var formats=MediaFormatCatalog.Parse(json.RootElement,out bool smallTruncated);
        Require(!smallTruncated,"A complete small catalogue must not claim truncation");
        Require(formats.Length==3 && formats.Select(f=>f.Id).ToHashSet(StringComparer.Ordinal).SetEquals(new[]{Video.Id,Audio.Id,Combined.Id}),
            "Only unique downloadable non-DRM formats with safe actual IDs may be offered");
        var video=formats.Single(f=>f.Id==Video.Id);
        var audio=formats.Single(f=>f.Id==Audio.Id);
        var combined=formats.Single(f=>f.Id==Combined.Id);
        Require(video.Width==1920 && video.Height==1080 && video.Fps==60 && video.IsVideoOnly && !video.HasAudio && video.DynamicRange=="HDR10",
            "Resolution, FPS, video codec and dynamic range must come from the same actual format");
        Require(audio.IsAudioOnly && audio.Language=="ru-RU" && audio.EstimatedSizeBytes==1_200_000 && audio.SizeIsApproximate,
            "Audio language and approximate size must not become properties of another format");
        Require(combined.HasVideo && combined.HasAudio && combined.EstimatedSizeBytes==null,
            "Missing size stays unknown rather than becoming zero or a bitrate guess");
        var many=LargeCatalogJson();
        using var large=JsonDocument.Parse(new JsonObject {["formats"]=many}.ToJsonString());
        var bounded=MediaFormatCatalog.Parse(large.RootElement,out bool truncated);
        Require(truncated && bounded.Length==MediaFormatCatalog.MaxFormats && bounded.Select(format=>format.Id).Distinct().Count()==bounded.Length,
            "An oversized catalogue must retain a bounded unique list and honestly report truncation");
        Require(bounded.All(format=>format.Id!="safe-0") && bounded.Any(format=>format.Id=="late-4k" && format.Height==2160 && format.Fps==60) &&
            bounded.Any(format=>format.Id=="late-audio" && format.IsAudioOnly),
            "Later preferred 4K and audio formats must survive a catalogue larger than the picker limit, including duplicate IDs");
        var legacy=JsonSerializer.Deserialize<DownloadItem>("{\"Url\":\"https://legacy.invalid/video\"}",AppPersistence.Json)!;
        Require(legacy.Selection==null && legacy.CompletedSelection==null && !legacy.IsPartialResult,"Legacy rows remain full automatic downloads");
        var chosen=new DownloadSelection(Prefix+"restore",new DownloadSection(1.25,3.75),Video,Audio,125.5);
        var row=new DownloadItem {Url=chosen.Url,Selection=chosen,CompletedSelection=chosen,Status="Completed",State="Done"};
        var data=new AppPersistence(Path.Combine(root,"selection-checkpoint"));
        data.Save(new[]{row},new DownloadOptions());
        var restored=data.Restore()!.Queue.Single();
        Require(restored.Selection==chosen && restored.CompletedSelection==chosen && restored.IsPartialResult,
            "Queue and completed request selections must survive checkpoint restore without promoting a partial result");
    }

    private static async Task ColdCatalog(string root)
    {
        string folder=Path.Combine(root,"catalog-cold");Directory.CreateDirectory(folder);
        string control=Path.Combine(folder,"control");File.WriteAllText(control,folder);
        var ready=new ConcurrentQueue<PreviewData>();
        var errors=new ConcurrentQueue<string>();
        var data=new AppPersistence(Path.Combine(folder,"profile"));
        using(var service=new PreviewService(TestExecutable(),data,()=>control,ready.Enqueue,errors.Enqueue))
        {
            service.Request(Prefix+"catalog");
            service.Request(Prefix+"collection");
            service.Request(Prefix+"large-catalog");
            await Until(()=>ready.Count==3,TimeSpan.FromSeconds(15),"All cold metadata requests must complete: "+string.Join("; ",errors));
            var single=ready.Single(item=>item.Url==Prefix+"catalog");
            Require(!single.IsCollection && single.Formats.Length==3 && single.Duration==125.5,
                "The existing metadata child must expose its actual single-video format catalogue");
            var collection=ready.Single(item=>item.Url==Prefix+"collection");
            Require(collection.IsCollection && collection.Formats.Length==0,
                "First-entry playlist preview must not offer that video's IDs for the whole collection");
            var large=ready.Single(item=>item.Url==Prefix+"large-catalog");
            Require(large.FormatsTruncated && large.Formats.Length==MediaFormatCatalog.MaxFormats &&
                large.Formats.Any(format=>format.Id=="late-4k") && large.Formats.Any(format=>format.Id=="late-audio"),
                "The native metadata child must expose truncation and retain later preferred formats");
            using(var warmed=new PreviewService(TestExecutable(),data,()=>control,_=>{},_=>{}))
                Require(warmed.Get(large.Url) is {FormatsTruncated:true} cached && cached.Formats.Length==MediaFormatCatalog.MaxFormats,
                    "The disk cache must preserve both the bounded catalogue and its truncation notice");
            Require(data.ReadHistory().Count==0,"Reading formats must not claim any downloaded media");
        }
        Require(File.ReadAllLines(Path.Combine(folder,"catalog.calls.jsonl")).Length==1 &&
            File.ReadAllLines(Path.Combine(folder,"collection.calls.jsonl")).Length==1,
            "A format catalogue must reuse one existing metadata request per URL");
    }

    private static async Task NativeRequests(string root)
    {
        async Task<(DownloadItem Row,AppPersistence Data,string[][] Calls)> Run(string name,bool partial,bool mutate=false,bool rawAudio=false)
        {
            string folder=Path.Combine(root,name);Directory.CreateDirectory(folder);
            string cookies=Path.Combine(folder,"control");File.WriteAllText(cookies,folder);
            var data=new AppPersistence(Path.Combine(folder,"profile"));
            var options=new DownloadOptions {Output=Path.Combine(folder,"output"),Cookies=cookies,Threads="1",Quality="2160",
                FilenameTemplate="selected-%(id)s.%(ext)s",EmbedMetadata=false,EmbedChapters=false};
            var selection=rawAudio?new DownloadSelection(Prefix+name,Format:Audio):
                new DownloadSelection(Prefix+name,partial?new DownloadSection(1.25,3.75):null,Video,Audio,125.5);
            var row=new DownloadItem {Url=selection.Url,Selection=selection};
            await using(var engine=new DownloadCoordinator(TestExecutable(),data,options,_=>{},_=>{}))
            {
                Require(engine.Add(row),"Selection row must be admitted");
                if(mutate)
                {
                    row.Selection=new DownloadSelection(row.Url,Format:Combined);
                    options.Output=Path.Combine(folder,"late-output");options.FilenameTemplate="../outside.%(ext)s";
                    options.Container="WebM";options.Quality="720";
                }
                await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(25));
            }
            string[][] calls=File.ReadAllLines(Path.Combine(folder,name+".calls.jsonl"))
                .Select(line=>JsonSerializer.Deserialize<string[]>(line)!).ToArray();
            foreach(string[] call in calls)
            {
                string selector=Value(call,"-f");
                Require((rawAudio?!selector.Contains(Video.Id,StringComparison.Ordinal):selector.Contains(Video.Id,StringComparison.Ordinal)) && selector.Contains(Audio.Id,StringComparison.Ordinal) &&
                    !selector.Contains('/',StringComparison.Ordinal),"Manual formats must reach every child without an automatic fallback");
                if(rawAudio)Require(!call.Contains("-x",StringComparer.Ordinal),"An explicit raw audio format must not silently enable MP3 extraction");
                Require(call[^1]==row.Url,"The exact selected URL must survive the native argument boundary");
                Require(Value(call,"-o").Contains("selected-%(id)s.%(ext)s",StringComparison.Ordinal),
                    "The session must retain its settings snapshot after admission");
                if(partial)Require(Value(call,"--download-sections")=="*1.25-3.75",
                    "The exact fractional clip bounds must reach each child in invariant syntax");
                else Require(!call.Contains("--download-sections",StringComparer.Ordinal),"Full downloads must not acquire a clip range");
            }
            if(row.Status=="Completed")
            {
                Require(File.Exists(row.Path) && row.OwnedPaths.Contains(row.Path) && row.CompletedSelection==selection,
                    "Published ownership and completed selection must describe the request that actually ran");
                Require(row.IsPartialResult==partial,"The result's partial flag must follow its immutable completed request");
                Require(data.ReadHistory().ContainsKey(row.Url)==!partial,
                    "Only a full validated result may enter the full-URL successful history");
                Require(!mutate || !Directory.Exists(Path.Combine(folder,"late-output")),"Late settings edits must not redirect publication");
            }
            return (row,data,calls);
        }
        var partial=await Run("snapshot",true,mutate:true);
        Require(partial.Row.Status=="Completed" && partial.Calls.Length==1,"An admitted clip must keep its original manual selection");
        var full=await Run("full",false);
        Require(full.Row.Status=="Completed" && full.Calls.Length==1,"A successful full manual download must retain ordinary history semantics");
        var raw=await Run("raw-audio",false,rawAudio:true);
        Require(raw.Row.Status=="Completed" && Path.GetExtension(raw.Row.Path)==".m4a" && raw.Calls.Length==1,
            "A manual audio-only stream must publish its original audio container when MP3 only is disabled");
        var unavailable=await Run("unavailable",true);
        Require(unavailable.Row.Status=="Failed" && unavailable.Calls.Length==1 && unavailable.Row.OwnedPaths.Count==0 &&
            !unavailable.Data.ReadHistory().ContainsKey(unavailable.Row.Url),
            "An unavailable explicit ID must fail once instead of falling back to another quality");
        var transient=await Run("transient",true);
        Require(transient.Row.Status=="Completed" && transient.Calls.Length==2 &&
            Value(transient.Calls[0],"-f")==Value(transient.Calls[1],"-f") &&
            Value(transient.Calls[0],"--download-sections")==Value(transient.Calls[1],"--download-sections"),
            "A transient retry must retain both exact format IDs and the requested time range");
    }

    private static async Task LegacyCatalogUpgrade(string root)
    {
        string folder=Path.Combine(root,"catalog-upgrade");Directory.CreateDirectory(folder);
        string control=Path.Combine(folder,"control");File.WriteAllText(control,folder);
        const string url=Prefix+"upgrade";
        var data=new AppPersistence(Path.Combine(folder,"profile"));
        Directory.CreateDirectory(data.PreviewDir);
        string cache=Path.Combine(data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        File.WriteAllText(cache,JsonSerializer.Serialize(new {
            CacheVersion=1,PreviewFetchVersion=3,Url=url,SavedAtUtc=DateTime.UtcNow,Success=true,ThumbnailError="",
            Title="Existing title",Uploader="",PlaylistTitle="",Duration=125.5,Extractor="fixture",MaxHeight=1080,MaxFps=60,
            Codecs="H264",DynamicRange="",ThumbnailBase64="",ThumbnailsBase64=Array.Empty<string>(),
        }));
        var ready=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var service=new PreviewService(TestExecutable(),data,()=>control,item=>ready.TrySetResult(item),_=>{}))
        {
            Require(service.Get(url) is {FormatsLoaded:false,Title:"Existing title"},"A version-3 cache without formats must remain readable");
            for(int index=0;index<20;index++)service.RequestFormats(url);
            var upgraded=await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Require(upgraded.FormatsLoaded && upgraded.Formats.Length==3,"An explicit request must upgrade the old metadata cache");
            for(int index=0;index<20;index++)service.RequestFormats(url);
            service.Request(url);
            await Task.Delay(150);
            Require(File.ReadAllLines(Path.Combine(folder,"upgrade.calls.jsonl")).Length==1,
                "Repeated format requests must share one worker and reuse its completed catalogue");
        }
        ready=new(TaskCreationOptions.RunContinuationsAsynchronously);
        using(var service=new PreviewService(TestExecutable(),data,()=>control,item=>ready.TrySetResult(item),_=>{}))
        {
            service.RequestFormats(url);
            var warm=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Require(warm.FormatsLoaded && warm.Formats.Length==3 && File.ReadAllLines(Path.Combine(folder,"upgrade.calls.jsonl")).Length==1,
                "The upgraded disk cache must retain its catalogue without another metadata child");
        }
    }

    // The caller provisions the same native tools required by the application.
    // This method deliberately has no implicit download or network dependency.
    internal static async Task RunNativeMediaAsync()
    {
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        string ffprobe=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
        Require(File.Exists(ffmpeg) && File.Exists(ffprobe),"The real clip test requires FFmpeg and ffprobe in PATH or beside the test application");
        string ytDlp=DependencyCenter.Executable("yt-dlp.exe");
        Require(File.Exists(ytDlp),"The packaged yt-dlp is required for the real clip test");
        string root=Path.Combine(Path.GetTempPath(),"vd-real-clip-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string input=Path.Combine(root,"source.mp4");
            await Tool(ffmpeg,new[]{"-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i","testsrc2=size=160x90:rate=24",
                "-f","lavfi","-i","sine=frequency=440:sample_rate=44100","-t","8","-c:v","libx264","-preset","ultrafast",
                "-pix_fmt","yuv420p","-c:a","aac","-shortest","-movflags","+faststart",input});
            Require(new FileInfo(input).Length<=4*1024*1024,"The generated local media fixture must stay within its bounded HTTP buffer");
            await using var server=new LocalMediaServer(await File.ReadAllBytesAsync(input));
            var data=new AppPersistence(Path.Combine(root,"profile"));
            string output=Path.Combine(root,"output");Directory.CreateDirectory(output);
            string sentinel=Path.Combine(output,"unrelated.mp4");File.WriteAllText(sentinel,"UNRELATED FILE");
            var row=new DownloadItem {Url=server.Url,Selection=new DownloadSelection(server.Url,new DownloadSection(1.25,3.75),DurationSeconds:8)};
            var options=new DownloadOptions {Output=output,Threads="1",EmbedMetadata=false,EmbedChapters=false};
            await using(var engine=new DownloadCoordinator(ytDlp,data,options,_=>{},_=>{}))
            {
                Require(engine.Add(row),"The real clip must be accepted");
                await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(90));
            }
            Require(row.Status=="Completed" && row.IsPartialResult && File.Exists(row.Path),"Real yt-dlp/FFmpeg clipping must publish its verified result: "+row.RawError);
            string inspection=await Tool(ffprobe,new[]{"-v","error","-show_entries","format=duration:stream=codec_type","-of","json",row.Path});
            using var metadata=JsonDocument.Parse(inspection);
            double duration=double.Parse(metadata.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
            var types=metadata.RootElement.GetProperty("streams").EnumerateArray().Select(stream=>stream.GetProperty("codec_type").GetString()).ToHashSet();
            Require(Math.Abs(duration-2.5)<.25 && types.Contains("video") && types.Contains("audio"),
                "The published media must contain the requested 2.5-second video/audio clip, not the full source: "+inspection);
            Require(data.ReadHistory().Count==0 && File.ReadAllText(sentinel)=="UNRELATED FILE" && row.OwnedPaths.Contains(row.Path) && !row.OwnedPaths.Contains(sentinel),
                "A real partial result must retain exact ownership, preserve unrelated files and leave full-URL history empty");
            data.Save(new[]{row},options);
            Require(data.Restore()!.Queue.Single().IsPartialResult,"A real completed clip must remain partial after restart");
            await RealCollectionPlans(root,server.Url);
            Console.WriteLine("PASS: actual yt-dlp/FFmpeg clip, ffprobe duration/audio/video, partial history and safe publication");
        }
        finally {Directory.Delete(root,true);}
    }

    private static async Task RealCollectionPlans(string root,string mediaUrl)
    {
        const string collection="https://collection-plan.invalid/list?list=Original";
        string[] pages=["https://collection-plan.invalid/watch?v=First","https://collection-plan.invalid/watch?v=Second"];
        var entries=pages.Select((page,index)=>new JsonObject
        {
            ["id"]="entry-"+index,["title"]="Collection entry "+index,["extractor"]="FixtureSite",["extractor_key"]="FixtureSite",
            ["webpage_url"]=page,["formats"]=new JsonArray(new JsonObject
            {
                ["format_id"]="muxed",["url"]=mediaUrl+"?token=private-format-secret",["ext"]="mp4",
                ["vcodec"]="avc1",["acodec"]="aac",["width"]=160,["height"]=90,
                ["http_headers"]=new JsonObject{["X-Fixture-Private"]="private-header-secret"}
            })
        }).ToArray();
        string info=Path.Combine(root,"collection-plan.json");
        File.WriteAllText(info,new JsonObject{["_type"]="playlist",["id"]="two-entries",["title"]="Two entries",
            ["extractor"]="FixtureCollection",["extractor_key"]="FixtureCollection",
            ["webpage_url"]=collection,["entries"]=new JsonArray(entries.Cast<JsonNode>().ToArray())}.ToJsonString());
        string stage=Path.Combine(root,"collection-plan-downloads");Directory.CreateDirectory(stage);
        var options=new DownloadOptions{Output=stage,EmbedMetadata=false,EmbedChapters=false};
        var arguments=new YtDlpArguments(options).Build(collection,stage,"best").SkipLast(1)
            .Concat(new[]{"--no-clean-info-json","--load-info-json",info});
        string stdout=await Tool(DependencyCenter.Executable("yt-dlp.exe"),arguments);
        string[] raw=stdout.Split('\n').Select(line=>line.Trim()).Where(line=>line.StartsWith("__VD_PLAN__:",StringComparison.Ordinal)).ToArray();
        var plans=raw.Select(line=>YtDlpOutputParser.Parse(line) as YtDlpOutputEvent.Plan).ToArray();
        Require(plans.Length==2 && plans.Select(plan=>plan?.WebpageUrl).SequenceEqual(pages) &&
            plans.All(plan=>plan is{IsCollection:true,MediaCount:2}) && plans[0]!.MediaIndex==1 && plans[1]!.MediaIndex==2,
            "Actual bundled yt-dlp must project the two current public pages and real playlist positions: "+stdout);
        Require(raw.All(line=>!line.Contains("private-format-secret",StringComparison.Ordinal) &&
            !line.Contains("private-header-secret",StringComparison.Ordinal) && !line.Contains(mediaUrl,StringComparison.Ordinal)),
            "The actual before_dl template must exclude format URLs/headers rather than relying on downstream redaction");
        Require(stdout.Split('\n').Count(line=>line.StartsWith("__VD_FILE__:",StringComparison.Ordinal))==2 && Directory.GetFiles(stage,"*.mp4").Length==2,
            "The projection proof must download both real loopback media entries");
        Console.WriteLine("PASS: actual bundled yt-dlp two-entry loopback playlist, before_dl public-page projection and private-format exclusion (loaded metadata)");
    }

    private static async Task<string> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output=await BoundedProcessOutput.ReadAsync(child,1024*1024,1024*1024,deadline.Token);
        Require(child.Process.ExitCode==0,Path.GetFileName(executable)+" failed: "+Encoding.UTF8.GetString(output.StandardError));
        return Encoding.UTF8.GetString(output.StandardOutput);
    }

    private sealed class LocalMediaServer:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _responses=new();
        private readonly byte[] _media;
        internal string Url {get;}
        internal LocalMediaServer(byte[] media)
        {
            _media=media;_listener.Start();
            Url="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/source.mp4";
            _accept=Accept();
        }
        private async Task Accept()
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _responses.Add(Respond(client));
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(SocketException) when(_stop.IsCancellationRequested){}
        }
        private async Task Respond(TcpClient client)
        {
            try
            {
                using(client)
                {
                    var stream=client.GetStream();
                    using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,leaveOpen:true);
                    string request=await reader.ReadLineAsync(_stop.Token)??"";
                    string? range=null;
                    int length=request.Length;
                    while(true)
                    {
                        string? line=await reader.ReadLineAsync(_stop.Token);
                        if(string.IsNullOrEmpty(line))break;
                        length+=line.Length;
                        if(length>16*1024)throw new IOException("Fixture request headers exceed their bound");
                        if(line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase))range=line[6..].Trim();
                    }
                    bool head=request.StartsWith("HEAD ",StringComparison.Ordinal);
                    int first=0,last=_media.Length-1;
                    bool partial=false;
                    if(range?.StartsWith("bytes=",StringComparison.Ordinal)==true)
                    {
                        string[] parts=range[6..].Split('-');
                        if(parts.Length!=2 || !int.TryParse(parts[0],out first) || first<0 || first>=_media.Length)
                            throw new IOException("Unexpected fixture byte range");
                        if(parts[1].Length>0 && !int.TryParse(parts[1],out last))throw new IOException("Unexpected fixture byte range end");
                        last=Math.Min(last,_media.Length-1);partial=true;
                    }
                    int count=last-first+1;
                    string headers="HTTP/1.1 "+(partial?"206 Partial Content":"200 OK")+"\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: "+count+
                        (partial?"\r\nContent-Range: bytes "+first+"-"+last+"/"+_media.Length:"")+"\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),_stop.Token);
                    if(!head)await stream.WriteAsync(_media.AsMemory(first,count),_stop.Token);
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException) { /* A probe may close its connection after reading the headers. */ }
            catch(SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();
            await _accept;
            await Task.WhenAll(_responses);
            _stop.Dispose();
        }
    }

    private static async Task RealFormatSelectors(string root)
    {
        string fixture=Path.Combine(AppContext.BaseDirectory,"fixtures","audio-tracks","formats.json");
        Require(File.Exists(fixture),"The existing audio/video format fixture must be copied to the test output");
        var original=JsonNode.Parse(File.ReadAllText(fixture))!;
        foreach(var scenario in new[] {
            (Name:"named",VideoId:Video.Id,AudioId:Audio.Id,Missing:false),
            (Name:"missing",VideoId:Video.Id,AudioId:Audio.Id,Missing:true),
            (Name:"numeric",VideoId:"137",AudioId:"140",Missing:false),
            (Name:"reserved",VideoId:"best",AudioId:"all",Missing:false),
        })
        {
            var metadata=original.DeepClone();
            foreach(var format in metadata["formats"]!.AsArray())
            {
                string id=format!["format_id"]!.GetValue<string>();
                if(id==Video.Id)format["format_id"]=scenario.VideoId;
                if(id==Audio.Id)format["format_id"]=scenario.AudioId;
            }
            if(scenario.Missing)
                metadata["formats"]=new JsonArray(metadata["formats"]!.AsArray().Where(format=>format!["format_id"]!.GetValue<string>()!=scenario.VideoId)
                    .Select(format=>format!.DeepClone()).ToArray());
            string info=Path.Combine(root,"strict-"+scenario.Name+".json");
            File.WriteAllText(info,metadata.ToJsonString());
            var options=new DownloadOptions {Quality="2160",Codec="AV1",EmbedMetadata=false,EmbedChapters=false};
            const string url="https://audio-track-fixture.invalid/video";
            var selection=new DownloadSelection(url,Format:Video with {Id=scenario.VideoId},AudioFormat:Audio with {Id=scenario.AudioId});
            var arguments=new YtDlpArguments(options).Build(url,root,"2160",selection).SkipLast(1)
                .Concat(new[]{"--simulate","--load-info-json",info,"--print","video:__STRICT_FORMAT__:%(format_id)s"});
            var start=new ProcessStartInfo(DependencyCenter.Executable("yt-dlp.exe")) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            };
            foreach(string argument in arguments)start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var output=await BoundedProcessOutput.ReadAsync(child,1024*1024,1024*1024,deadline.Token);
            string stdout=Encoding.UTF8.GetString(output.StandardOutput),stderr=Encoding.UTF8.GetString(output.StandardError);
            if(scenario.Missing)
                Require(child.Process.ExitCode!=0 && !stdout.Contains("__STRICT_FORMAT__:",StringComparison.Ordinal),
                    "Real yt-dlp must refuse the missing explicit format instead of picking another one: "+stdout+stderr);
            else
                Require(child.Process.ExitCode==0 && stdout.Split('\n').Any(line=>line.Trim()=="__STRICT_FORMAT__:"+scenario.VideoId+"+"+scenario.AudioId),
                    "Real yt-dlp must select exact "+scenario.Name+" IDs despite automatic preferences or selector keywords: "+stdout+stderr);
        }
    }

    private static JsonArray LargeCatalogJson()
    {
        var formats=new JsonArray(Enumerable.Range(0,MediaFormatCatalog.MaxFormats+100).Select(index=>(JsonNode)new JsonObject {
            ["format_id"]="safe-"+index,["url"]="https://formats.invalid/"+index,["ext"]="mp4",["vcodec"]="avc1",["acodec"]="aac",
        }).ToArray());
        formats.Add(new JsonObject { ["format_id"]="late-4k",["ext"]="mp4",["vcodec"]="av01",["acodec"]="none",["height"]=2160,["fps"]=60 });
        formats.Add(new JsonObject { ["format_id"]="late-audio",["ext"]="m4a",["vcodec"]="none",["acodec"]="aac",["language"]="ru" });
        for(int repeat=0;repeat<20;repeat++)formats.Add(formats[^2]!.DeepClone());
        return formats;
    }
    private static JsonArray CatalogJson()=>new(
        JsonSerializer.SerializeToNode(new {format_id=Video.Id,url="https://formats.invalid/video.mp4",ext="mp4",width=1920,height=1080,
            fps=60,vcodec="avc1.640028",acodec="none",filesize=12_000_000,dynamic_range="HDR10"}),
        JsonSerializer.SerializeToNode(new {format_id=Audio.Id,url="https://formats.invalid/audio.m4a",ext="m4a",vcodec="none",acodec="mp4a.40.2",
            language="ru-RU",filesize_approx=1_200_000}),
        JsonSerializer.SerializeToNode(new {format_id=Combined.Id,url="https://formats.invalid/combined.mp4",ext="mp4",vcodec="avc1",acodec="aac",height=720}),
        JsonSerializer.SerializeToNode(new {format_id=Video.Id,url="https://formats.invalid/duplicate.mp4",ext="mp4",vcodec="avc1",acodec="none",
            height="unknown",filesize="unknown"}),
        JsonSerializer.SerializeToNode(new {format_id="drm-format",url="https://formats.invalid/drm.mp4",ext="mp4",vcodec="avc1",acodec="aac",has_drm=true}),
        JsonSerializer.SerializeToNode(new {format_id="137/best",url="https://formats.invalid/unsafe.mp4",ext="mp4",vcodec="avc1",acodec="aac"}),
        JsonSerializer.SerializeToNode(new {format_id="x]+ba",url="https://formats.invalid/unsafe2.mp4",ext="mp4",vcodec="avc1",acodec="aac"}),
        JsonSerializer.SerializeToNode(new {format_id="storyboard",url="https://formats.invalid/storyboard",ext="mhtml",vcodec="none",acodec="none"})
    );

    private static string TestExecutable()
    {
        string path=Path.Combine(AppContext.BaseDirectory,typeof(DownloadSelectionTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
        Require(File.Exists(path),"Native test apphost is required for process-boundary selection tests");return path;
    }
    private static async Task Until(Func<bool> predicate,TimeSpan timeout,string message)
    {
        var watch=Stopwatch.StartNew();
        while(!predicate()) {Require(watch.Elapsed<timeout,message);await Task.Delay(20);}
    }
    private static string? OptionalValue(string[] args,string option)
    {
        int index=Array.IndexOf(args,option);return index>=0 && index+1<args.Length?args[index+1]:null;
    }
    private static string Value(string[] args,string option)=>OptionalValue(args,option)??throw new InvalidOperationException("Missing option: "+option);
    private static void Refuses(Action action,string message)
    {
        try {action();}catch(ArgumentException){return;}catch(InvalidDataException){return;}
        throw new InvalidOperationException("Download selection: "+message);
    }
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Download selection: "+message);
    }
}
