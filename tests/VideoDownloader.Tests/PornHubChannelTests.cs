using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class PornHubChannelTests
{
    private const string Source="https://rt.pornhub.com/model/fixture-model";
    private static string Fixture=>File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","pornhub-discovery.json"));
    internal static bool TryRunFixture(string[] args)
    {
        string? url=args.LastOrDefault();
        bool collection=url==Source;
        if(!collection && url?.StartsWith("https://www.pornhub.com/view_video.php?viewkey=phfixture",StringComparison.Ordinal)!=true)return false;
        int cookie=Array.IndexOf(args,"--cookies");
        using var config=JsonDocument.Parse(File.ReadAllText(args[cookie+1]));
        string mode=config.RootElement.GetProperty("Mode").GetString()!;
        if(collection)
        {
            foreach(string flag in new[]{"--ignore-config","--flat-playlist","--no-plugin-dirs","--plugin-dirs","--use-extractors"})
                Require(args.Contains(flag),"Missing Pornhub collector argument: "+flag);
            Require(!args.Contains("--no-flat-playlist") && !args.Contains("--playlist-end"),"Only the site collector may determine completeness");
            Require(args[Array.IndexOf(args,"--use-extractors")+1]=="videodownloader:pornhub:channel","Only the packaged collector may handle a channel");
            if(mode=="hang") {Thread.Sleep(TimeSpan.FromSeconds(30));return true;}
            Console.WriteLine(config.RootElement.GetProperty("Response").GetString());
            return true;
        }
        if(url!.EndsWith("phfixture2",StringComparison.Ordinal) && mode!="recovered")
        {Console.Error.WriteLine("ERROR: HTTP Error 403: unavailable fixture");Environment.ExitCode=1;return true;}
        string stage=Path.GetDirectoryName(args[Array.IndexOf(args,"-o")+1])!;
        string file=Path.Combine(stage,url[(url.LastIndexOf('=')+1)..]+".mp4");
        File.WriteAllText(file,"FIXTURE MEDIA");Console.WriteLine("__VD_FILE__:"+file);return true;
    }
    internal static async Task RunAsync()
    {
        string directory=Path.Combine(Path.GetTempPath(),"vd-pornhub-tests-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            Require(PornHubChannel.ListingUrl(Source)==Source+"/videos/upload","Regional model URL must select publications");
            Require(PornHubChannel.ListingUrl(Source+"/videos?page=7")==Source+"/videos/upload","Page links must start a complete scan");
            Require(PornHubChannel.ListingUrl("https://www.pornhub.com/channels/fixture")=="https://www.pornhub.com/channels/fixture/videos","Channels must keep their collection");
            foreach(string url in new[]{"https://pornhub.com.evil.example/model/a","https://user@pornhub.com/model/a","https://example.com/model/a",Source+"/videos/favorites"})
                Require(PornHubChannel.ListingUrl(url)==null,"Only supported publication routes may use the site collector");
            string json=Fixture;
            var parsed=PornHubChannel.ParseScan(json,Source);
            Require(parsed.Items.Count==3 && parsed.Items.Select(x=>x.Url).Distinct().Count()==3,"Every publication must become one video URL");
            foreach(Action<JsonNode> change in new Action<JsonNode>[] {
                r=>r["vd_ph_scan"]!["complete"]=false,
                r=>r["vd_ph_scan"]!["verified_first_page"]=false,
                r=>r["vd_ph_scan"]!["source_url"]="https://pornhub.com/model/other",
                r=>r["vd_ph_scan"]!["pages"]![1]!["number"]=3,
                r=>r["vd_ph_scan"]!["pages"]![1]!["has_next"]=true,
                r=>r["vd_ph_scan"]!["item_count"]=2,
                r=>r["entries"]![1]=r["entries"]![0]!.DeepClone(),
                r=>r["entries"]![0]!["url"]="https://other.example/video",
                r=>r["entries"]![0]!["ie_key"]="Generic",
                r=>r["entries"]![0]!["vd_page"]=2 })
            {
                var record=JsonNode.Parse(json)!;change(record);
                Refuses(()=>PornHubChannel.ParseScan(record.ToJsonString(),Source));
            }
            Refuses(()=>PornHubChannel.ParseScan("{\"entries\":[]}",Source));
            Refuses(()=>PornHubChannel.ParseScan(json,Source,maxItems:2));
            Refuses(()=>PornHubChannel.ParseScan(json,Source,maxPages:1));
            string cookies=Path.Combine(directory,"fixture.json");
            void State(string mode,string response)=>File.WriteAllText(cookies,JsonSerializer.Serialize(new{Mode=mode,Response=response}));
            State("ok",json);
            var store=new SubscriptionStore(directory);
            var source=store.Add(Source,true);
            using var scanner=new SubscriptionScanner(Executable());
            var scan=await scanner.ScanAsync(source,cookies,CancellationToken.None);
            var snapshot=store.ApplyScan(source.Id,scan,[]);
            Require(snapshot.Items.All(x=>x.InitialQueuePending && x.State=="New"),"Existing import must await per-video queue acknowledgement");
            string before=File.ReadAllText(store.Path);
            State("ok",json.Replace("\"complete\":true","\"complete\":false"));
            bool rejected=false;
            try {store.ApplyScan(source.Id,await scanner.ScanAsync(source,cookies,CancellationToken.None),[]);}
            catch(InvalidDataException){rejected=true;}
            Require(rejected && File.ReadAllText(store.Path)==before,"An incomplete collection must never replace the saved baseline");
            State("hang",json);
            using(var bounded=new SubscriptionScanner(Executable(),options:new(){ChannelScanTimeout=TimeSpan.FromMilliseconds(250)}))
            {
                var watch=Stopwatch.StartNew();
                bool timeout=false;
                try {await bounded.ScanAsync(source,cookies,CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));}
                catch(TimeoutException){timeout=true;}
                Require(timeout && watch.Elapsed<TimeSpan.FromSeconds(3),"Channel enumeration must have its own bounded deadline");
            }
            using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(250)))
            {
                bool cancelled=false;
                try {await scanner.ScanAsync(source,cookies,cancel.Token).WaitAsync(TimeSpan.FromSeconds(5));}
                catch(OperationCanceledException) when(cancel.IsCancellationRequested){cancelled=true;}
                Require(cancelled,"Channel cancellation must reach the enumeration worker");
            }
            State("ok",json);
            var persistence=new AppPersistence(directory);
            var options=new DownloadOptions{Output=Path.Combine(directory,"output"),Threads="2",Cookies=cookies,EmbedMetadata=false,EmbedChapters=false};
            var rows=parsed.Items.Select(x=>new DownloadItem{Url=x.Url,Title=x.Title}).ToArray();
            store.MarkQueued(source.Id,rows.Select(x=>x.Url));
            await using(var engine=new DownloadCoordinator(Executable(),persistence,options,_=>{},_=>{}))
            {foreach(var row in rows)engine.Add(row);await engine.RunAsync();}
            Require(rows.Count(x=>x.Status=="Completed")==2 && rows.Single(x=>x.Status=="Failed").Url.EndsWith("phfixture2"),
                "One unavailable video must leave other channel downloads successful");
            foreach(var row in rows.Where(x=>x.Status=="Completed"))store.MarkSuccessful(row.Url);
            Require(persistence.ReadHistory().Count==2 && !persistence.ReadHistory().ContainsKey(Source),"History must contain individual successful videos, never the channel URL");
            persistence.Save(rows,options);
            var restored=persistence.Restore()!;
            Require(restored.Queue.Count(x=>x.Status=="Completed")==2,"A restart must retain completed channel downloads");
            State("recovered",json);
            var retry=restored.Queue.Single(x=>x.Status=="Failed");
            await using(var engine=new DownloadCoordinator(Executable(),persistence,options,_=>{},_=>{}))
            {engine.Add(retry);await engine.RunAsync();}
            Require(retry.Status=="Completed" && rows.Where(x=>x.Status=="Completed").All(x=>File.Exists(x.Path)),
                "Explicit retry must recover the failed item while preserving other files");
            Console.WriteLine("PASS: Pornhub collector routing, manifest, limits, cancellation, per-video failure/history/restart/retry");
        }
        finally {Directory.Delete(directory,true);}
    }
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,typeof(PornHubChannelTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static void Refuses(Action action){bool failed=false;try{action();}catch(InvalidDataException){failed=true;}Require(failed,"Incomplete or foreign channel metadata must be rejected");}
}
