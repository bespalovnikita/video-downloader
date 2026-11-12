using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class CollectionVideoLinkTests
{
    private const string Prefix="https://collection-video-links.invalid/";
    private const string First="https://video-page.invalid/watch?v=First";
    private const string Second="https://video-page.invalid/watch?v=Second";
    private const string PrivateMedia="https://cdn.invalid/private/path-secret/video.mp4?token=media-secret";

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        string root=File.ReadAllText(Value(args,"--cookies"));
        File.AppendAllText(Path.Combine(root,"requests.jsonl"),JsonSerializer.Serialize(args)+"\n");
        string name=url[Prefix.Length..];
        if(args.Contains("--dump-single-json",StringComparer.Ordinal))
        {
            if(name=="nested")
            {
                Console.WriteLine(JsonSerializer.Serialize(new{_type="playlist",title="Outer",entries=new[]{new
                {
                    _type="playlist",title="Nested collection",extractor="FixtureSite",extractor_key="FixtureSite",
                    webpage_url="https://video-page.invalid/list?list=Nested",entries=Array.Empty<object>()
                }}}));
                return true;
            }
            bool generic=name=="generic";
            string webpage=generic?"https://cdn.invalid/private/path-secret/video.mp4":First;
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                _type="playlist",title="Collection",entries=new[]{new
                {
                    id="first",title="First video",extractor=generic?"generic":"FixtureSite",
                    extractor_key=generic?"Generic":"FixtureSite",webpage_url=webpage,
                    url=name=="format-page"?First:PrivateMedia,
                    formats=new[]{new{format_id="muxed",url=name=="format-page"?First:PrivateMedia,ext="mp4",vcodec="avc1",acodec="aac"}}
                }}
            }));
            return true;
        }
        string stage=Path.GetDirectoryName(Value(args,"-o"))!;
        Directory.CreateDirectory(stage);
        if(!File.Exists(Path.Combine(root,"next")))
        {
            PrintPlan("first",First,1);
            File.WriteAllText(Path.Combine(stage,"fixture.mp4.part"),"RETAINED PARTIAL");
            Console.WriteLine("__VD_PROGRESS__|10%|1KiB/s|1|10|100|first:muxed|avc1|aac");
            Console.Out.Flush();
            var deadline=DateTime.UtcNow.AddSeconds(30);
            while(!File.Exists(Path.Combine(root,"next")))
            {
                if(DateTime.UtcNow>deadline)throw new TimeoutException("Collection fixture was not released");
                Thread.Sleep(10);
            }
        }
        PrintPlan("second",Second,2);
        string media=Path.Combine(stage,"second.mp4");
        File.WriteAllText(media,"PUBLISHED COLLECTION FIXTURE");
        Console.WriteLine("__VD_FILE__:"+media);
        return true;
    }

    private static void PrintPlan(string id,string page,int index)
    {
        Console.WriteLine("__VD_TITLE__:"+id);
        Console.WriteLine("__VD_PLAN__:"+JsonSerializer.Serialize(new
        {
            id,format_id="muxed",playlist_id="collection",playlist_autonumber=index,n_entries=2,
            extractor_key="FixtureSite",webpage_url=page,
            // Deliberately hostile extra data: the parser must ignore it and
            // diagnostics must never retain raw plan JSON.
            url=PrivateMedia,http_headers=new{Authorization="Bearer header-secret"}
        }));
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-collection-links-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            ParserAndIdentity(root);
            await Metadata(root);
            await RuntimeAndResume(root);
            Console.WriteLine("PASS: public collection video links, typed provenance/privacy, first-entry metadata, current-material lifecycle, retained resume and original task identity");
        }
        finally{Directory.Delete(root,true);}
    }

    private static void ParserAndIdentity(string root)
    {
        YtDlpOutputEvent.Plan Parse(object value)=>(YtDlpOutputEvent.Plan)YtDlpOutputParser.Parse("__VD_PLAN__:"+JsonSerializer.Serialize(value))!;
        var known=Parse(new{id="first",format_id="muxed",playlist_autonumber=1,n_entries=2,extractor_key="Youtube",webpage_url=First,url=PrivateMedia});
        Require(known.IsCollection && known.WebpageUrl==First,"A typed collection plan must expose its public page without format URL fallback");
        var unknown=Parse(new{id="first",format_id="muxed",playlist_index=1,n_entries=(int?)null,extractor_key="Youtube",webpage_url=First});
        Require(unknown.IsCollection && unknown.MediaCount==0 && unknown.WebpageUrl==First,"Unknown counts must retain the raw playlist context");
        var one=Parse(new{id="first",format_id="muxed",playlist_id="one-entry",extractor="youtube",webpage_url=First});
        Require(one.IsCollection && one.WebpageUrl==First,"A one-entry collection with unknown count still has its own material page");
        foreach(string extractor in new[]{"","Generic","generic","HTML5MediaEmbed","direct"})
            Require(Parse(new{id="first",format_id="muxed",playlist_index=1,extractor_key=extractor,
                webpage_url="https://cdn.invalid/private/path-secret/video.mp4"}).WebpageUrl.Length==0,
                "Generic/direct/unknown sources cannot prove a public page: "+extractor);
        foreach(string page in new[]{PrivateMedia,"file:///private/video.mp4","https://user:password@video.invalid/watch",First+"#secret",First+"\n",new string('x',4097)})
            Require(Parse(new{id="first",format_id="muxed",playlist_index=1,extractor_key="Youtube",webpage_url=page}).WebpageUrl.Length==0,
                "Private or malformed extracted page must be rejected");
        Require(Parse(new{id="first",format_id="muxed",webpage_url=First,extractor_key="Youtube"}).WebpageUrl.Length==0,
            "An ordinary video keeps its exact original input, including user parameters");
        Require(Parse(new{id="legacy",format_id="muxed"}).WebpageUrl.Length==0,"Old plans remain compatible");
        const string original=Prefix+"collection?list=ExactCase";
        var row=new DownloadItem{Url=original};
        var notifications=new List<string?>();row.PropertyChanged+=(_,e)=>notifications.Add(e.PropertyName);
        row.SetPreviewVideoUrl(First);Require(row.DisplayUrl==First && row.Url==original,"First preview changes presentation only");
        row.SetCurrentVideoUrl(Second);row.SetPreviewVideoUrl(First);
        Require(row.DisplayUrl==Second && notifications.Contains(nameof(DownloadItem.DisplayUrl)),"A runtime page wins and notifies WPF/filter bindings");
        var storage=new AppPersistence(Path.Combine(root,"identity-profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"identity-output")};
        storage.Save([row],options);
        var restored=storage.Restore()!.Queue.Single();
        Require(restored.Url==original && restored.DisplayUrl==Second && restored.HasCurrentVideoContext,"The current public page survives restart without changing task identity");
        string checkpoint=File.ReadAllText(storage.SessionPath);
        Require(!checkpoint.Contains("DisplayUrl",StringComparison.Ordinal) && !checkpoint.Contains("PreviewVideoUrl",StringComparison.Ordinal),"Derived/private preview context is not serialized");
        var portable=PortableQueueCodec.Decode(PortableQueueCodec.Encode([new PortableQueueEntry(row.Url,row.Title)]));
        Require(portable.Single().Url==original,"Portable exchange retains the original collection admission URL");
        row.SetCurrentVideoUrl("");row.SetPreviewVideoUrl(First);
        Require(row.DisplayUrl==original,"A new material without a safe page must not inherit the preceding/first video page");
        row.ResetCurrentVideo();row.SetPreviewVideoUrl(First);
        Require(row.DisplayUrl==First && row.CurrentVideoUrl is null && !row.HasCurrentVideoContext,"Reset clears runtime provenance and permits fresh preview");
        var legacy=JsonSerializer.Deserialize<DownloadItem>("{\"Url\":\"https://example.invalid/old\"}",AppPersistence.Json)!;
        Require(legacy.DisplayUrl==legacy.Url && legacy.CurrentVideoUrl is null,"Old checkpoints retain original links");
    }

    private static async Task Metadata(string root)
    {
        string control=Path.Combine(root,"metadata-control");File.WriteAllText(control,root);
        var data=new AppPersistence(Path.Combine(root,"metadata-profile"));
        var received=new ConcurrentDictionary<string,PreviewData>(StringComparer.Ordinal);
        using var service=new PreviewService(Executable(),data,()=>control,item=>received[item.Url]=item,_=>{});
        try
        {
            string legacyUrl=Prefix+"legacy";
            var old=new PreviewData(legacyUrl,"Legacy first","","Collection",0,"FixtureSite",0,0,"","","",DateTime.UtcNow){IsCollection=true};
            var envelope=JsonSerializer.SerializeToNode(old,AppPersistence.Json)!.AsObject();
            envelope.Remove(nameof(PreviewData.WebpageUrl));envelope["CacheVersion"]=1;envelope["PreviewFetchVersion"]=3;
            string legacyPath=Path.Combine(data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(legacyUrl))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(legacyPath,envelope.ToJsonString());
            Require(service.Get(legacyUrl)?.Title=="Legacy first","Legacy collection cache remains readable before enrichment");
            foreach(string name in new[]{"collection","generic","format-page","legacy","nested"})service.Request(Prefix+name);
            await Until(()=>received.Count==5,"Cold metadata must finish and upgrade a legacy collection cache");
            Require(received[Prefix+"collection"].WebpageUrl==First,"Before Start metadata must retain the first video's public page");
            Require(received[legacyUrl].WebpageUrl==First,"A fresh legacy cache lacking the page field must be upgraded once");
            Require(received[Prefix+"generic"].WebpageUrl.Length==0 && received[Prefix+"format-page"].WebpageUrl.Length==0,
                "Metadata cannot persist Generic signed-path or exact format URLs as public pages");
            Require(received[Prefix+"nested"].WebpageUrl.Length==0,"A nested collection cannot be mistaken for a video's page before Start");
            foreach(string file in Directory.GetFiles(data.PreviewDir,"*.json"))
            {
                string json=File.ReadAllText(file);
                Require(!json.Contains("path-secret",StringComparison.Ordinal) && !json.Contains("media-secret",StringComparison.Ordinal),"Preview cache must exclude private media paths/query values");
            }
        }
        finally{service.Dispose();await service.Completion;}
        using var disk=new PreviewService("must-not-start",data,()=>"",_=>{},_=>{});
        try
        {
            Require(disk.Get(Prefix+"collection")?.WebpageUrl==First,"A cold cache read retains the safe first page");
            string genericUrl=Prefix+"generic";
            string genericPath=Path.Combine(data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(genericUrl))).ToLowerInvariant()+".json");
            var hostile=JsonNode.Parse(File.ReadAllText(genericPath))!.AsObject();
            hostile[nameof(PreviewData.WebpageUrl)]="https://cdn.invalid/private/path-secret/video.mp4";
            AppPersistence.AtomicWrite(genericPath,hostile.ToJsonString());
            Require(disk.Get(genericUrl)?.WebpageUrl.Length==0,"An untrusted cache cannot introduce a Generic signed-path page into memory");
        }
        finally{disk.Dispose();await disk.Completion;}
    }

    private static async Task RuntimeAndResume(string root)
    {
        string control=Path.Combine(root,"runtime-control");File.WriteAllText(control,root);
        var data=new AppPersistence(Path.Combine(root,"runtime-profile"));
        var options=new DownloadOptions{Output=Path.Combine(root,"runtime-output"),Cookies=control,Threads="1",EmbedMetadata=false,EmbedChapters=false};
        var row=new DownloadItem{Url=Prefix+"runtime",CurrentVideoUrl=Second};
        var journal=new ConcurrentQueue<JournalRecord>();
        bool freshCleared=false;
        await using(var engine=new DownloadCoordinator(Executable(),data,options,item=>{if(item.DisplayUrl==item.Url)freshCleared=true;},_=>{},journal.Enqueue))
        {
            Require(engine.Add(row),"Collection fixture must be admitted under its original URL");
            Task run=engine.RunAsync();
            await Until(()=>row.CurrentVideoUrl==First && row.TransferredBytes>0,"The first actual before_dl marker must update the current page");
            Require(freshCleared && await engine.SkipAsync(row).WaitAsync(TimeSpan.FromSeconds(10)),"A fresh attempt clears the old page; Skip joins only this worker");
            await run.WaitAsync(TimeSpan.FromSeconds(10));
            Require(row.Status=="Skipped" && row.DisplayUrl==First && row.ResumeState is not null && data.ReadHistory().Count==0,"Skip retains current page and partial plan without success");
        }
        data.Save([row],options);row=data.Restore()!.Queue.Single();
        Require(row.DisplayUrl==First,"Skipped current page survives checkpoint restore");
        File.WriteAllText(Path.Combine(root,"next"),"");
        await using(var resumed=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{},journal.Enqueue))
        {
            Require(resumed.Add(row) && row.DisplayUrl==First,"Retained admission preserves the last page until a new material marker");
            await resumed.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
        }
        Require(row.Status=="Completed" && row.CurrentVideoUrl==Second && row.Url==Prefix+"runtime" && File.Exists(row.Path),"The next material changes its display page and publishes under the original task");
        Require(data.ReadHistory().Keys.SequenceEqual(new[]{row.Url}),"Only the original collection URL enters successful history");
        foreach(string request in File.ReadAllLines(Path.Combine(root,"requests.jsonl")))
            Require(JsonSerializer.Deserialize<string[]>(request)!.Last().StartsWith(Prefix,StringComparison.Ordinal),"No display page can replace a child process task URL");
        Require(journal.All(record=>!record.Message.Contains("path-secret",StringComparison.Ordinal) &&
            !record.Message.Contains("media-secret",StringComparison.Ordinal) && !record.Message.Contains("header-secret",StringComparison.Ordinal)),"Detailed diagnostics cannot retain raw private plan fields");
    }

    private static async Task Until(Func<bool> condition,string reason)
    {
        var clock=Stopwatch.StartNew();
        while(!condition() && clock.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(10);
        Require(condition(),reason);
    }
    private static string Value(string[] args,string name)=>args[Array.IndexOf(args,name)+1];
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,"VideoDownloader.Tests.exe");
    private static void Require(bool condition,string reason){if(!condition)throw new InvalidOperationException(reason);}
}
