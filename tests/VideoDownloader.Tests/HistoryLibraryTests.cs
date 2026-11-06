using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class HistoryLibraryTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-history-library-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try
        {
            Persistence(Path.Combine(root,"records"));
            Corruption(Path.Combine(root,"corruption"));
            await FileChecks(Path.Combine(root,"files"));
            Console.WriteLine("PASS: history metadata and legacy events, complete-search limits, corrupt-history preservation, safe paths and bounded file availability");
        }
        finally{Directory.Delete(root,true);}
    }
    private static void Require(bool value,string why){if(!value)throw new InvalidOperationException("History library: "+why);}
    private static void Refuses(Action action,string why)
    {try{action();}catch(Exception){return;}throw new InvalidOperationException("History library: "+why);}
    private static DownloadItem Completed(string root,string url,string title)
    {
        Directory.CreateDirectory(root);string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".mp4");File.WriteAllText(path,"published media sentinel");
        return new DownloadItem{Url=url,Title=title,SubscriptionName="Канал",Status="Completed",State="Done",Path=path,OwnedPaths=[path],OutputRoot=root};
    }
    private static void Persistence(string root)
    {
        var data=new AppPersistence(root);
        Require(data.ReadHistoryLibrary().Entries.Count==0 && !File.Exists(data.HistoryPath),"Viewing a fresh history must not create or start anything");
        const string legacy="https://history.invalid/Video?token=AbC";
        const string lower="https://history.invalid/video?token=AbC";
        File.WriteAllText(data.HistoryPath,"\uFEFF{\"Version\":1,\"Url\":\""+legacy+"\"}\n"+
            JsonSerializer.Serialize(new{Version=1,Url=legacy,CompletedAtUtc="2020-01-01T00:00:00Z"}));
        data.AppendSuccess(lower);
        var dictionary=data.ReadHistory();var library=data.ReadHistoryLibrary();
        Require(dictionary.Count==2 && dictionary[legacy]!.Value.Year==2020 && library.TotalCount==3 && library.Entries.Count==3,
            "Legacy events, unknown dates, repeated attempts and ordinal URL identity must coexist");
        Require(library.Entries.Last().CompletedAtUtc==null && library.Entries.Last().OutputPaths.Count==0,
            "Legacy paths and time must remain unknown rather than invented");
        Require(File.ReadAllText(data.HistoryPath).Contains("}\n{",StringComparison.Ordinal),"An unterminated valid record needs a separator before append");
        var item=Completed(root,"https://history.invalid/new","Тестовый ролик Ёж");
        string sidecar=Path.Combine(root,"captions.srt");File.WriteAllText(sidecar,"subtitle");item.OwnedPaths.Add(sidecar);
        data.AppendSuccess(item);
        var found=data.ReadHistoryLibrary("ёж");
        Require(found.MatchCount==1 && found.Entries.Single().Title==item.Title && found.Entries.Single().Source=="Канал" &&
            found.Entries.Single().OutputPaths.SequenceEqual(new[]{item.Path}),"Confirmed metadata must retain the actual title/source/media paths without sidecars");
        string iso=found.Entries.Single().CompletedAtUtc!.Value.ToLocalTime().ToString("yyyy-MM-dd");
        Require(data.ReadHistoryLibrary(iso).Entries.Any(row=>row.Url==item.Url),"Date search must use the displayed local day");
        Require(data.ReadHistoryLibrary("канал").Entries.Count==1 && data.ReadHistoryLibrary("/Video?token=AbC").Entries.Count==3,
            "Search may ignore display case without merging exact URL identities or attempts");
        var partial=Completed(root,"https://history.invalid/partial","Partial");
        partial.CompletedSelection=new DownloadSelection(partial.Url,new DownloadSection(1,2));
        byte[] before=File.ReadAllBytes(data.HistoryPath);data.AppendSuccess(partial);
        Require(File.ReadAllBytes(data.HistoryPath).SequenceEqual(before) && !data.ReadHistory().ContainsKey(partial.Url),
            "A partial result must never be promoted to full-URL successful history");
        var invalid=Completed(root,"https://history.invalid/unconfirmed","Not done");invalid.Status="Running";
        Refuses(()=>data.AppendSuccess(invalid),"Unconfirmed jobs must not create a successful event");
        for(int index=0;index<20;index++)data.AppendSuccess(Completed(root,"https://history.invalid/row-"+index,"Новый "+index));
        var recent=data.ReadHistoryLibrary(limit:3);
        Require(recent.Truncated && recent.Entries.Count==3 && recent.TotalCount==24 && recent.Entries[0].Title=="Новый 19",
            "A bounded library page must identify how many earlier events exist");
        Require(data.ReadHistoryLibrary("Тестовый",limit:3).Entries.Single().Url==item.Url,
            "Search must scan the complete valid history, not just the newest visible page");
        File.Delete(item.Path);
        Require(data.ReadHistory().ContainsKey(item.Url) && data.ReadHistoryLibrary("Тестовый").Entries.Single().OutputPaths.Count==1,
            "A moved or missing file cannot erase success or its last known path");
        Require(!File.Exists(data.SessionPath),"History operations must not create a queue checkpoint");
    }
    private static void Corruption(string root)
    {
        foreach(string corrupt in new[]{"not-json","not-json\n","{\"Version\":2,\"Url\":\"https://history.invalid/x\"}\n",
            "{\"Version\":1,\"Url\":\"https://history.invalid/x\",\"OutputPaths\":42}\n",new string('x',1024*1024+1)})
        {
            var data=new AppPersistence(Path.Combine(root,Guid.NewGuid().ToString("N")));File.WriteAllText(data.HistoryPath,corrupt);
            byte[] before=File.ReadAllBytes(data.HistoryPath);
            Refuses(()=>data.ReadHistoryLibrary(),"A damaged library must report its error");
            Refuses(()=>data.ReadHistory(),"The full success lookup must fail closed on the same damaged data");
            Refuses(()=>data.AppendSuccess("https://history.invalid/another"),"Appending must not modify damaged or unterminated data");
            Require(before.SequenceEqual(File.ReadAllBytes(data.HistoryPath)),"A damaged history must survive byte for byte");
        }
    }
    private static async Task FileChecks(string root)
    {
        Directory.CreateDirectory(root);string path=Path.Combine(root,"result.mp4");File.WriteAllText(path,"media");
        var files=new HistoryFileAccess();
        Require((await files.CheckAsync(path,false,CancellationToken.None)).State==HistoryFileState.Present,"A real local file must be available");
        File.Delete(path);
        Require((await files.CheckAsync(path,false,CancellationToken.None)).State==HistoryFileState.Missing,"Proven absence must be distinct from an inaccessible file");
        Require((await files.CheckAsync(path,true,CancellationToken.None)).State==HistoryFileState.Present,"A missing media file can still have an available containing folder");
        string disguised=Path.Combine(root,"disguised.mp4");File.WriteAllBytes(disguised,new byte[]{(byte)'M',(byte)'Z',0,0});
        Require((await files.CheckAsync(disguised,false,CancellationToken.None)).State==HistoryFileState.Unsafe,
            "A renamed executable must not become a safe shell target merely by using a media extension");
        File.WriteAllBytes(disguised,new byte[]{0x4c,0,0,0,1,0x14,2,0,0,0,0,0,0xc0,0,0,0,0,0,0,0x46});
        Require((await files.CheckAsync(disguised,false,CancellationToken.None)).State==HistoryFileState.Unsafe,
            "A renamed Shell Link must remain an unsafe launch target");
        var denied=new HistoryFileAccess(new ThrowingProbe());
        Require((await denied.CheckAsync(path,false,CancellationToken.None)).State==HistoryFileState.Unavailable,"Access denial is not a missing file");
        foreach(string bad in new[]{"https://history.invalid/video.mp4","relative.mp4",Path.Combine(root,"unsafe.exe"),Path.Combine(root,"file.mp4.lnk"),
            Path.Combine(root,"..","escape.mp4")})
            Require((await files.CheckAsync(bad,false,CancellationToken.None)).State==HistoryFileState.Unsafe,"A historical path cannot authorize arbitrary shell input: "+bad);
        using var held=new BlockingProbe();
        var slow=new HistoryFileAccess(held,TimeSpan.FromMilliseconds(100));
        var waiting=Enumerable.Range(0,9).Select(_=>slow.CheckAsync(path,false,CancellationToken.None)).ToArray();
        var states=await Task.WhenAll(waiting).WaitAsync(TimeSpan.FromSeconds(3));
        Require(states.All(state=>state.State==HistoryFileState.Unavailable) && held.Peak<=4,
            "Timeouts must leave at most four outstanding OS probes, even across queued requests");
        held.Release.Set();await held.Finished().WaitAsync(TimeSpan.FromSeconds(3));
        using var another=new BlockingProbe();
        var cancellable=new HistoryFileAccess(another,TimeSpan.FromSeconds(3));using var stop=new CancellationTokenSource();
        var check=cancellable.CheckAsync(path,false,stop.Token);
        await another.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));stop.Cancel();
        bool cancelled=false;try{await check;}catch(OperationCanceledException){cancelled=true;}
        finally{another.Release.Set();await another.Finished().WaitAsync(TimeSpan.FromSeconds(3));}
        Require(cancelled,"User cancellation must remain cancellation rather than inventing a missing file");
    }
    private sealed class ThrowingProbe:IHistoryFileProbe
    {public HistoryFileAvailability Check(string path,bool directory)=>throw new UnauthorizedAccessException("denied fixture");}
    private sealed class BlockingProbe:IDisposable,IHistoryFileProbe
    {
        internal readonly ManualResetEventSlim Release=new();
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active,_peak;
        internal int Peak=>Volatile.Read(ref _peak);
        public HistoryFileAvailability Check(string path,bool directory)
        {
            int active=Interlocked.Increment(ref _active);
            int previous;do{previous=_peak;if(active<=previous)break;}while(Interlocked.CompareExchange(ref _peak,active,previous)!=previous);
            Started.TrySetResult();
            try{Release.Wait();return new(HistoryFileState.Present,"fixture available");}
            finally{Interlocked.Decrement(ref _active);}
        }
        internal async Task Finished(){while(Volatile.Read(ref _active)>0)await Task.Delay(10);}
        public void Dispose()=>Release.Dispose();
    }
}
