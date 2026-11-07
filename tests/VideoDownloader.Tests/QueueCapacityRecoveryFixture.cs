using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

// Shared by the native and real WPF tests. The legacy case expands the fixed
// v1 corpus; only the title padding and isolated filesystem paths are dynamic.
internal sealed record QueueCapacityRecoveryFixture(AppPersistence Storage,string PaddedUrl,string OriginalOutput,
    string EventMedia,string SavedMedia,string FinishedMedia,string UnownedMedia,byte[] Checkpoint,string EventsPath,byte[] Events)
{
    internal static QueueCapacityRecoveryFixture Create(string root,bool legacy)
    {
        Directory.CreateDirectory(root);
        var data=new AppPersistence(Path.Combine(root,"profile"));
        string original=Path.Combine(root,"original-output"),selected=Path.Combine(root,"selected-output");
        string run=Path.Combine(root,"legacy-run"),events=Path.Combine(run,"events.jsonl");
        string savedStage=Path.Combine(run,"download-1-11111111111111111111111111111111");
        string eventStage=Path.Combine(run,"download-2-22222222222222222222222222222222");
        string finishedStage=Path.Combine(run,"download-3-33333333333333333333333333333333");
        string savedMedia=Path.Combine(original,"saved.mp4"),eventMedia=Path.Combine(original,"event.mp4");
        string finishedMedia=Path.Combine(original,"finished.mp4"),unownedMedia=Path.Combine(original,"unowned.mp4");
        var tokens=new Dictionary<string,string>
        {
            ["SELECTED_OUTPUT"]=selected,["ORIGINAL_OUTPUT"]=original,["COOKIES_FILE"]=Path.Combine(root,"cookies.txt"),
            ["RUN_DIR"]=run,["CANCEL_DIR"]=Path.Combine(run,"cancel"),["EVENTS_FILE"]=events,
            ["SAVED_STAGE"]=savedStage,["EVENT_STAGE"]=eventStage,["FINISHED_STAGE"]=finishedStage,
            ["SAVED_MEDIA"]=savedMedia,["EVENT_MEDIA"]=eventMedia,["FINISHED_MEDIA"]=finishedMedia,
            ["FINISHED_SIDECAR"]=Path.Combine(original,"finished.en.srt"),["UNOWNED_MEDIA"]=unownedMedia
        };
        foreach(string directory in new[]{original,selected,savedStage,eventStage,finishedStage})Directory.CreateDirectory(directory);
        foreach(string file in new[]{savedMedia,eventMedia,finishedMedia,tokens["FINISHED_SIDECAR"],unownedMedia})File.WriteAllText(file,"KEEP "+Path.GetFileName(file));
        File.WriteAllText(Path.Combine(savedStage,"partial.part"),"partial");
        File.WriteAllText(Path.Combine(eventStage,"partial.part"),"partial");
        string Materialize(string name)
        {
            string contents=File.ReadAllText(Path.Combine(AppContext.BaseDirectory,"fixtures","legacy",name));
            foreach(var(key,value) in tokens)
                contents=contents.Replace("__"+key+"__",JsonSerializer.Serialize(value,AppPersistence.Json)[1..^1],StringComparison.Ordinal);
            return contents;
        }
        JsonNode document;
        string paddedUrl;
        int paddedIndex;
        if(legacy)
        {
            document=JsonNode.Parse(Materialize("session-v1.json"))!;
            File.WriteAllText(events,Materialize("events-v1.jsonl"),new UTF8Encoding(false));
            paddedUrl="https://legacy.example/next-start";paddedIndex=1;
        }
        else
        {
            paddedUrl="https://capacity.invalid/old-queued";paddedIndex=0;
            document=JsonSerializer.SerializeToNode(new {Version=1,Settings=new {Output=selected,ShowVideoTitles=false},
                Queue=new[]{new {Url=paddedUrl,Status="Queued",Title="",OutputRoot=""},
                    new {Url="https://capacity.invalid/keeper",Status="Queued",Title="",OutputRoot=""}}},AppPersistence.Json)!;
        }
        document["Settings"]!["ShowVideoTitles"]=false;
        var padded=document["Queue"]![paddedIndex]!;padded["Title"]="";
        int baseline=Encoding.UTF8.GetByteCount(document.ToJsonString(AppPersistence.Json));
        padded["Title"]=new string('x',AppPersistence.MaxCheckpointBytes-baseline-128);
        File.WriteAllText(data.SessionPath,document.ToJsonString(AppPersistence.Json),new UTF8Encoding(false));
        return new(data,paddedUrl,original,eventMedia,savedMedia,finishedMedia,unownedMedia,
            File.ReadAllBytes(data.SessionPath),events,File.Exists(events)?File.ReadAllBytes(events):[]);
    }
}
