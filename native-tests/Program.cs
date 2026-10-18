using System.IO;
using VideoDownloader.Native;

static void Assert(bool value,string why)
{
    if (!value) throw new Exception(why);
}
static void Refuses(Action action,string why)
{
    bool threw=false;
    try{action();}catch(Exception){threw=true;}
    Assert(threw,why);
}
var temp=Path.Combine(Path.GetTempPath(),"video-downloader-native-tests-"+Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temp);
try
{
    Assert(DownloadFormat.Tray(12,5,2,2.5*1024*1024,true,false)=="VD | OK 12 Left 5 Err 2 | 2.5 MB/s","Correct tray field order");
    Assert(DownloadFormat.Tray(int.MaxValue,int.MaxValue,int.MaxValue,double.MaxValue,true,false).Length<=63,"Windows tray limit");
    Assert(DownloadFormat.Tray(1,2,3,300,true,true).EndsWith("Paused"),"Pause indicator");
    Assert(Math.Abs(DownloadCoordinator.ParseSize("1.5MiB/s")-1.5*1024*1024)<1,"Native progress speed parser");
    Assert(Math.Abs(DownloadCoordinator.ParseSize("550 KiB/s")-550*1024)<1,"Progress KiB parser");
    var data=new AppPersistence(Path.Combine(temp,"localdata"));
    Assert(data.ReadHistory().Count==0,"Fresh install has empty history");
    data.AppendSuccess("https://example.com/Video");
    data.AppendSuccess("https://example.com/Video");
    data.AppendSuccess("https://example.com/video");
    Assert(data.ReadHistory().Count==2,"History compares URLs ordinal and preserves successes");
    File.AppendAllText(data.HistoryPath,"not-json\n");
    Refuses(()=>data.ReadHistory(),"Damaged history must block repeat decisions");
    File.WriteAllText(data.HistoryPath,"");
    Assert(data.ReadHistory().Count==0,"Existing empty history should work");
    var opts=new DownloadOptions{Output=Path.Combine(temp,"output"),Quality="1080",Threads="3"};
    data.Save([new DownloadItem{Url="https://example.com/one",Order=1,State="50%",Status="Running"},
               new DownloadItem{Url="https://example.com/done",Order=2,State="Done",Status="Completed"}],opts);
    var restored=data.Restore()!;
    Assert(restored.Queue.Count==2 && restored.Queue[0].Status=="Interrupted" && restored.Queue[1].Status=="Completed",
        "Existing checkpoint must recover safely without restarting completed rows");
    Assert(restored.Settings.Quality=="1080","Settings preserved");

    var output=Path.Combine(temp,"output");
    var outside=Path.Combine(temp,"outside");
    var stage=Path.Combine(temp,"stage");
    Directory.CreateDirectory(output);Directory.CreateDirectory(outside);Directory.CreateDirectory(stage);
    var sentinel=Path.Combine(outside,"windows-important-sentinel.txt");
    File.WriteAllText(sentinel,"NEVER DELETE");
    var existing=Path.Combine(output,"old-user-file.mp4");
    File.WriteAllText(existing,"PRESERVE");
    foreach(string candidate in new[]{
        sentinel,
        Path.Combine(output,"..","outside","windows-important-sentinel.txt"),
        Path.Combine(temp,"output-lookalike","windows-important-sentinel.txt")
    })
        Refuses(()=>OwnedFiles.DeleteOwned(output,[candidate]),"Must reject path outside download root");
    Assert(File.ReadAllText(sentinel)=="NEVER DELETE","Escaped deletion may not touch the outside sentinel");
    var stageMedia=Path.Combine(stage,"new.mp4");
    File.WriteAllText(stageMedia,"NEW");
    var published=OwnedFiles.Publish(stage,output,stageMedia);
    Assert(published.Path==Path.Combine(output,"new.mp4") && published.OwnedPaths.Count==1,"Stage publishes owned media");
    OwnedFiles.DeleteOwned(output,published.OwnedPaths);
    Assert(!File.Exists(published.Path) && File.ReadAllText(existing)=="PRESERVE","Only own files deleted");
    File.WriteAllText(Path.Combine(stage,"old-user-file.mp4"),"NEW");
    Refuses(()=>OwnedFiles.Publish(stage,output,null),"Must reject collisions with old files");
    Assert(File.ReadAllText(existing)=="PRESERVE","Collision must not overwrite user files");
    Refuses(()=>OwnedFiles.ValidateTemplate("../outside.mp4"),"Disallow traversal filename template");
    Refuses(()=>OwnedFiles.ValidateTemplate("C:\\Windows\\test.mp4"),"Disallow rooted filename template");

    if (OperatingSystem.IsWindows())
    {
        string junction=Path.Combine(output,"junction");
        // mklink /J does not require developer mode or admin privileges.
        using(var p=System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe",
            $"/c mklink /J \"{junction}\" \"{outside}\""){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true}))
        {
            p!.WaitForExit();
            Assert(p.ExitCode==0,"Windows CI junction fixture creation");
        }
        Refuses(()=>OwnedFiles.DeleteOwned(output,[Path.Combine(junction,"windows-important-sentinel.txt")]),
            "Junction traversal must never delete external files");
        Refuses(()=>OwnedFiles.Publish(stage,Path.Combine(junction,"new"),null),
            "Publishing through junction must be refused");
        Assert(File.ReadAllText(sentinel)=="NEVER DELETE","Protected sentinel must survive junction attacks");
        Directory.Delete(junction);
    }
    // End-to-end test with the bundled yt-dlp.exe, no public network and no pwsh.exe.
    string ytDlp=Path.GetFullPath("yt-dlp.exe");
    Assert(File.Exists(ytDlp),"Bundled yt-dlp.exe is available for native integration tests");
    var inputA=Path.Combine(temp,"source-A.mp4");
    var inputB=Path.Combine(temp,"source-B.mp4");
    File.WriteAllBytes(inputA,new byte[192*1024]);
    File.WriteAllBytes(inputB,new byte[256*1024]);
    var urlA=new Uri(inputA).AbsoluteUri;
    var urlB=new Uri(inputB).AbsoluteUri;
    var realOptions=new DownloadOptions {
        Output=Path.Combine(temp,"real-downloads"),
        Threads="2",RateLimit="100K",Quality="Best",
        EmbedChapters=false,EmbedMetadata=false
    };
    var one=new DownloadItem{Url=urlA};
    var two=new DownloadItem{Url=urlB};
    await using(var engine=new DownloadCoordinator(ytDlp,data,realOptions,_=>{},_=>{}))
    {
        Assert(engine.Add(one)&&engine.Add(two),"Native engine must accept two parallel URLs");
        await engine.RunAsync();
    }
    Assert(one.Status=="Completed"&&two.Status=="Completed","Native engine must successfully download two local files");
    Assert(one.Progress==100 && two.Progress==100,"Native progress must reach 100%");
    Assert(File.Exists(one.Path) && File.Exists(two.Path),"Successful URLs must publish real media");
    Assert(one.OwnedPaths.Count>0 && two.OwnedPaths.Count>0,"Native results must record owned files");
    Assert(new FileInfo(one.Path).Length==192*1024 && new FileInfo(two.Path).Length==256*1024,
        "Native download must preserve transferred file bytes");
    OwnedFiles.DeleteOwned(realOptions.Output,one.OwnedPaths);
    Assert(!File.Exists(one.Path) && File.Exists(two.Path),"Deleting one native download must leave another untouched");

    var longInput=Path.Combine(temp,"slow-cancel.mp4");
    var shortInput=Path.Combine(temp,"short-keep.mp4");
    File.WriteAllBytes(longInput,new byte[2*1024*1024]);
    File.WriteAllBytes(shortInput,new byte[96*1024]);
    var cancelRow=new DownloadItem{Url=new Uri(longInput).AbsoluteUri};
    var keepRow=new DownloadItem{Url=new Uri(shortInput).AbsoluteUri};
    await using(var engine=new DownloadCoordinator(ytDlp,data,new DownloadOptions {
        Output=Path.Combine(temp,"cancel-output"),Threads="2",RateLimit="100K",
        EmbedMetadata=false,EmbedChapters=false
    },_=>{},_=>{}))
    {
        engine.Add(cancelRow);engine.Add(keepRow);
        var task=engine.RunAsync();
        var until=DateTime.UtcNow.AddSeconds(25);
        while(cancelRow.Status!="Running" && DateTime.UtcNow<until)await Task.Delay(100);
        Assert(cancelRow.Status=="Running","Target download must start before cancellation");
        await Task.Delay(1300);
        engine.Cancel(cancelRow);
        await task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    Assert(cancelRow.Status=="Interrupted","Cancelling one URL must mark just that row Interrupted");
    Assert(keepRow.Status=="Completed","Other parallel URL must continue and finish");
    Assert(cancelRow.OwnedPaths.Count==0 && File.Exists(keepRow.Path),"Cancelled job must leave no owned output files");
    Assert(File.ReadAllText(sentinel)=="NEVER DELETE","No download or cancellation may affect files outside output");
    Assert(Directory.GetFiles(realOptions.Output,"VideoDownloader-*-success.txt").Length>0,
        "Successful native downloads must write durable per-session success lists");

    // A queued row removed before a free slot becomes available must not download.
    var skipRow=new DownloadItem{Url=new Uri(inputA).AbsoluteUri};
    var keepQueued=new DownloadItem{Url=new Uri(inputB).AbsoluteUri};
    await using(var engine=new DownloadCoordinator(ytDlp,data,new DownloadOptions {
        Output=Path.Combine(temp,"queued-removal-output"),Threads="1",
        EmbedMetadata=false,EmbedChapters=false
    },_=>{},_=>{}))
    {
        engine.Add(skipRow);engine.Add(keepQueued);
        engine.Cancel(skipRow);
        await engine.RunAsync();
    }
    Assert(skipRow.Status=="Interrupted"&&skipRow.Path=="",
        "A URL removed while Queued must never start downloading");
    Assert(keepQueued.Status=="Completed","Removing a pending URL must not block next item");

    // The running C# coordinator can ingest clipboard-equivalent links
    // without restarting the worker or writing a shared queue.txt.
    var liveBase=new DownloadItem{Url=new Uri(inputA).AbsoluteUri};
    var liveAdded=new DownloadItem{Url=new Uri(inputB).AbsoluteUri};
    await using(var engine=new DownloadCoordinator(ytDlp,data,new DownloadOptions {
        Output=Path.Combine(temp,"live-add-output"),Threads="1",
        RateLimit="100K",EmbedMetadata=false,EmbedChapters=false
    },_=>{},_=>{}))
    {
        engine.Add(liveBase);
        var task=engine.RunAsync();
        var until=DateTime.UtcNow.AddSeconds(20);
        while(liveBase.Status!="Running" && DateTime.UtcNow<until)await Task.Delay(100);
        Assert(liveBase.Status=="Running","Base download must be active before live addition");
        Assert(engine.Add(liveAdded),"Live URL must be accepted by the active C# engine");
        await task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    Assert(liveBase.Status=="Completed"&&liveAdded.Status=="Completed",
        "Both initial and dynamically enqueued URLs must finish");
    Assert(File.ReadAllText(sentinel)=="NEVER DELETE","All native flows must keep external sentinel unchanged");
    // Exercise suspend/resume on a real yt-dlp process tree without freezing the UI.
    var pauseSource=Path.Combine(temp,"pause-resume.mp4");
    File.WriteAllBytes(pauseSource,new byte[384*1024]);
    var pauseRow=new DownloadItem{Url=new Uri(pauseSource).AbsoluteUri};
    await using(var engine=new DownloadCoordinator(ytDlp,data,new DownloadOptions {
        Output=Path.Combine(temp,"pause-output"),Threads="1",RateLimit="100K",
        EmbedMetadata=false,EmbedChapters=false
    },_=>{},_=>{}))
    {
        engine.Add(pauseRow);
        var task=engine.RunAsync();
        var until=DateTime.UtcNow.AddSeconds(20);
        while(pauseRow.Status!="Running" && DateTime.UtcNow<until)await Task.Delay(100);
        Assert(pauseRow.Status=="Running","Pause fixture must have started");
        engine.Pause(true);
        Assert(engine.IsPaused,"Native engine must mark a suspended session paused");
        await Task.Delay(700);
        engine.Pause(false);
        Assert(!engine.IsPaused,"Native resume must clear paused state");
        await task.WaitAsync(TimeSpan.FromSeconds(60));
    }
    Assert(pauseRow.Status=="Completed","Paused and resumed download must complete successfully");
    Console.WriteLine("Native C# session/history/filesystem and live/cancel/pause/parallel yt-dlp integration tests passed.");
}
finally{try{Directory.Delete(temp,true);}catch(IOException){}}
