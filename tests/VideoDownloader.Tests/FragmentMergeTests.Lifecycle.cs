using System.IO;
using System.Diagnostics;
using System.Text.Json;
using VideoDownloader.Native;

internal static partial class FragmentMergeTests
{
    private static async Task OwnedCancellation(string root)
    {
        string stage=Path.Combine(root,"cancel-stage");Directory.CreateDirectory(stage);
        string one=Path.Combine(stage,"one.mp4"),two=Path.Combine(stage,"two.mp4");
        await MakeMedia(one,"blue",880,160,1);await MakeMedia(two,"red",440,160,1);
        ManagedChildProcess? active=null;int starts=0,activePid=0;bool wait=false;
        async Task<FragmentMergeToolOutput> Run(ProcessStartInfo start,int stdout,int stderr,CancellationToken token)
        {
            starts++;
            if(wait && Path.GetFileNameWithoutExtension(start.FileName)=="ffmpeg")
            {
                // A real owned encoder blocks on an endless media input. The
                // production bounded runner must terminate and join this child.
                start.ArgumentList.Clear();foreach(string arg in new[]{"-hide_banner","-loglevel","error","-re","-f","lavfi","-i","color=s=160x90:r=24","-f","null","-"})start.ArgumentList.Add(arg);
                using var child=ManagedChildProcess.Start(start);active=child;activePid=child.Process.Id;
                var output=await BoundedProcessOutput.ReadAsync(child,stdout,stderr,token);return new(child.Process.ExitCode,output.StandardOutput,output.StandardError);
            }
            using var ordinary=ManagedChildProcess.Start(start);
            var result=await BoundedProcessOutput.ReadAsync(ordinary,stdout,stderr,token);return new(ordinary.Process.ExitCode,result.StandardOutput,result.StandardError);
        }
        wait=true;using var cancel=new CancellationTokenSource();
        var service=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"),Run);
        var task=service.MergeAsync(stage,new[]{one,two},false,false,false,cancel.Token);
        var until=DateTime.UtcNow.AddSeconds(10);while(active is null && DateTime.UtcNow<until)await Task.Delay(15);
        Require(active is not null,"Cancellation must reach the actual owned encoder");int pid=active!.Process.Id;
        cancel.Cancel();bool cancelled=false;try{await task.WaitAsync(TimeSpan.FromSeconds(5));}catch(OperationCanceledException){cancelled=true;}
        Require(cancelled && !Alive(pid) && File.Exists(one) && File.Exists(two),"Cancellation must join exactly its own child before returning and retain inputs for caller cleanup");
        active=null;bool paused=true;
        var pauseBudget=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"),Run,()=>paused,TimeSpan.FromMilliseconds(300));
        var pausedTask=pauseBudget.MergeAsync(stage,new[]{one,two},false,false,false,CancellationToken.None);
        until=DateTime.UtcNow.AddSeconds(10);while(active is null && DateTime.UtcNow<until)await Task.Delay(15);
        Require(active is not null,"Pause budget test must reach an actual encoder");await Task.Delay(500);
        Require(!pausedTask.IsCompleted,"Paused time must not consume the processing active deadline");paused=false;
        bool resumedTimeout=false;try{await pausedTask.WaitAsync(TimeSpan.FromSeconds(5));}catch(TimeoutException){resumedTimeout=true;}
        Require(resumedTimeout && !Alive(activePid),"After resume the deadline must kill and join its owned child");active=null;
        var timed=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"),Run,timeout:TimeSpan.FromMilliseconds(300));
        bool expired=false;try{await timed.MergeAsync(stage,new[]{one,two},false,false,false,CancellationToken.None);}catch(TimeoutException){expired=true;}
        Require(expired && active is not null && !Alive(activePid),"Active deadline must terminate and join the actual owned encoder");
        Require(starts>=6,"Cancellation and timeout must include actual FFprobe inspection");
    }
    private static bool Alive(int pid){try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}

    internal static async Task Coordinator(string root,string url)
    {
        string output=Path.Combine(root,"merged-output");Directory.CreateDirectory(output);
        string sentinel=Path.Combine(output,"foreign.mp4");File.WriteAllText(sentinel,"KEEP");
        var data=new AppPersistence(Path.Combine(root,"merged-profile"));var original=Plan(url);
        var row=new DownloadItem{Url=url,Selection=original};
        async Task Run()
        {
            await using var engine=new DownloadCoordinator(DependencyCenter.Executable("yt-dlp.exe"),data,new DownloadOptions{
                Output=output,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=true},_=>{},_=>{});
            Require(engine.Add(row),"Merged selection must admit one exact URL");var run=engine.RunAsync();
            row.Selection=Plan(url,true) with{FragmentOutputMode="Separate",AllowMergeReencode=false};
            await run.WaitAsync(TimeSpan.FromSeconds(90));
        }
        await Run();
        Require(row.Status=="Completed" && row.OwnedPaths.Count==1 && row.CompletedSelection==original && row.IsPartialResult,
            "Actual yt-dlp must publish one merged result with the admitted policy: "+row.RawError);
        using(var probe=JsonDocument.Parse(await Tool(ToolPath("ffprobe"),new[]{"-v","error","-show_entries","format=duration:stream=codec_type","-of","json",row.Path})))
        {
            double duration=double.Parse(probe.RootElement.GetProperty("format").GetProperty("duration").GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            Require(Math.Abs(duration-3.5)<.3 && probe.RootElement.GetProperty("streams").EnumerateArray().Any(s=>s.GetProperty("codec_type").GetString()=="audio"),"Actual merged downloader output must preserve both durations and its audio track");
        }
        var prior=row.OwnedPaths.ToDictionary(path=>path,File.ReadAllBytes);var completed=row.CompletedSelection;
        row.Selection=Plan(url,true);data.Save(new[]{row},new DownloadOptions{Output=output});
        Require(data.Restore()!.Queue.Single().CompletedSelection==completed && prior.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"Editing mode cannot relabel or clear previous files");
        row.Status="Queued";row.State="Queued";row.Path="";row.OwnedPaths.Clear();row.Progress=0;original=row.Selection;
        await Run();
        Require(row.Status=="Completed" && row.OwnedPaths.Count==1 && row.CompletedSelection==original &&
            row.OwnedPaths.All(path=>!prior.ContainsKey(path)) && prior.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),"Explicit retry must preserve old files and own only its newly published merged file: "+row.RawError);
        Require(data.ReadHistory().Count==0 && data.ReadHistoryLibrary().Entries.Count==0 && File.ReadAllText(sentinel)=="KEEP", "A merged partial result must never enter full-URL history or delete foreign files");
    }
}
