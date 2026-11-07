using System.IO;
using System.Text.Json;
using VideoDownloader.Native;

internal static partial class FragmentMergeTests
{
    private const string FixtureUrl="https://fragment-merge.invalid/incompatible";
    internal static bool TryRunFixture(string[] args)
    {
        if(args.LastOrDefault()!=FixtureUrl)return false;
        string Value(string key)=>args[Array.IndexOf(args,key)+1];
        string control=File.ReadAllText(Value("--cookies"));File.AppendAllText(Path.Combine(control,"merge.calls"),"invoke\n");
        string stage=Path.GetDirectoryName(Value("-o"))!;Directory.CreateDirectory(stage);
        var ranges=new[]{(Start:5d,End:7d,Colour:"blue",Width:160),(Start:1d,End:2.5d,Colour:"red",Width:128)};
        foreach(var range in ranges)
        {
            string path=Path.Combine(stage,$"fragment-{range.Start*1000:0000000000000}-{range.End*1000:0000000000000}ms fixture.mp4");
            MakeMedia(path,range.Colour,440,range.Width,range.End-range.Start).GetAwaiter().GetResult();
            Console.WriteLine("__VD_PLAN__:"+JsonSerializer.Serialize(new{id="fixture",format_id="muxed",n_entries=1,section_start=range.Start,section_end=range.End}));
            Console.WriteLine("__VD_FILE__:"+path);
        }
        return true;
    }
    private static async Task CoordinatorFailureAndRetry(string root)
    {
        string output=Path.Combine(root,"failure-output");Directory.CreateDirectory(output);
        string sentinel=Path.Combine(output,"merged-2-fragments fixture.mp4");File.WriteAllText(sentinel,"FOREIGN RESULT");
        string control=Path.Combine(root,"merge.control");File.WriteAllText(control,root);
        var data=new AppPersistence(Path.Combine(root,"failure-profile"));
        var row=new DownloadItem{Url=FixtureUrl,Selection=Plan(FixtureUrl)};
        var options=new DownloadOptions{Output=output,Cookies=control,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=false};
        async Task Run()
        {
            string executable=Path.Combine(AppContext.BaseDirectory,typeof(FragmentMergeTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
            await using var engine=new DownloadCoordinator(executable,data,options,_=>{},_=>{});
            Require(engine.Add(row),"A complete actual media plan must be admitted");await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }
        await Run();
        Require(row.Status=="Failed" && row.RawError.Contains("stream parameters differ",StringComparison.Ordinal) && row.OwnedPaths.Count==0 &&
            row.StageDir.Length==0 && Directory.GetFiles(output,"*.mp4").SequenceEqual(new[]{sentinel}),
            "Actual incompatible clips must fail without publication and clean all owned staging: "+row.RawError);
        Require(File.ReadAllLines(Path.Combine(root,"merge.calls")).Length==1,"Merge failure must never silently redownload or retry another quality");
        row.Selection=Plan(FixtureUrl,true);row.Status="Queued";row.State="Queued";row.Progress=0;await Run();
        Require(row.Status=="Completed" && row.CompletedSelection is {IsMerged:true,AllowMergeReencode:true} && row.OwnedPaths.Count==1 &&
            row.Path!=sentinel && File.ReadAllText(sentinel)=="FOREIGN RESULT" && row.StageDir.Length==0 &&
            File.ReadAllLines(Path.Combine(root,"merge.calls")).Length==2 && data.ReadHistory().Count==0 && data.ReadHistoryLibrary().Entries.Count==0,
            "An explicit consented retry must publish one collision-safe owned result and preserve full history: "+row.RawError);
    }
}
