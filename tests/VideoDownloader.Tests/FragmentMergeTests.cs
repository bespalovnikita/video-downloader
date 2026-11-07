using System.IO;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static partial class FragmentMergeTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-fragment-merge-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        try {Models(root);await ActualMerge(root);await ActualTrackPreservation(root);await OwnedCancellation(root);await CoordinatorFailureAndRetry(root);Console.WriteLine("PASS: actual fragment stream copy/reencode, ordered frames/audio, bounded owned children, immutable merge checkpoints");}
        finally{Directory.Delete(root,true);}
    }
    internal static DownloadSelection Plan(string url,bool encode=false)=>new(url,new DownloadSection(5,7),DurationSeconds:8)
        {Sections=new DownloadSectionSet(new[]{new DownloadSection(5,7),new DownloadSection(1,2.5)}),FragmentOutputMode="Merged",AllowMergeReencode=encode};
    private static void Models(string root)
    {
        const string url="https://merge.invalid/Exact?Case=YES";
        var plan=Plan(url);var options=new DownloadOptions();plan.Validate(url,options);
        Require(plan.IsPartial && plan.IsMerged && plan.EffectiveSections[0].StartSeconds==5,"Merged output remains an ordered partial selection");
        foreach(var invalid in new[]{plan with{FragmentOutputMode="unknown"},plan with{FragmentOutputMode="Separate",AllowMergeReencode=true},
            plan with{Sections=null},plan with{Url=url.ToLowerInvariant()},plan with{Section=null,Sections=null}})
            Refuses(()=>invalid.Validate(url,options),"Unknown modes, misplaced consent, single/full video and another exact URL must fail closed");
        foreach(var subtitle in new[]{new DownloadOptions{WriteSubs=true},new DownloadOptions{AutoSubs=true},new DownloadOptions{EmbedSubs=true}})
            Refuses(()=>plan.Validate(url,subtitle),"Unavailable subtitle retiming must be explained before transfer");
        var data=new AppPersistence(Path.Combine(root,"model-profile"));
        var old=new DownloadSelection(url,new DownloadSection(0,1));
        var row=new DownloadItem{Url=url,Selection=plan,CompletedSelection=old,Status="Completed",State="Done"};
        data.Save(new[]{row},options);var restored=data.Restore()!.Queue.Single();
        Require(restored.Selection==plan && restored.CompletedSelection==old && restored.IsPartialResult,"Editing mode must round-trip without relabelling a completed result");
        row.Selection=old;row.CompletedSelection=old;data.Save(new[]{row},options);
        var json=JsonNode.Parse(File.ReadAllText(data.SessionPath))!;
        foreach(string property in new[]{"Selection","CompletedSelection"})
        {json["Queue"]![0]![property]!.AsObject().Remove("FragmentOutputMode");json["Queue"]![0]![property]!.AsObject().Remove("AllowMergeReencode");}
        File.WriteAllText(data.SessionPath,json.ToJsonString());
        Require(data.Restore()!.Queue.Single().Selection is {FragmentOutputMode:"Separate",AllowMergeReencode:false},"Old single-range snapshots retain legacy output meaning");
    }
    private static string ToolPath(string name)=>DependencyCenter.Executable(OperatingSystem.IsWindows()?name+".exe":name);
    private static async Task ActualMerge(string root)
    {
        string stage=Path.Combine(root,"stage ' quoted");Directory.CreateDirectory(stage);
        string blue=Path.Combine(stage,"blue.mp4"),red=Path.Combine(stage,"red.mp4"),foreign=Path.Combine(root,"foreign.mp4");
        await MakeMedia(blue,"blue",880,160,1);await MakeMedia(red,"red",440,160,1.5);File.WriteAllText(foreign,"KEEP");
        var service=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"));
        var result=await service.MergeAsync(stage,new[]{blue,red},false,true,true,CancellationToken.None);
        Require(!result.Reencoded && Directory.GetFiles(result.PublishStage,"*",SearchOption.AllDirectories).SequenceEqual(new[]{result.MediaPath}),"Compatible clips must produce one output without reencoding or publishing inputs");
        Require(Math.Abs(result.DurationSeconds-2.5)<.15,"Actual joined duration must preserve both clips");
        await AssertFrameAndAudio(result.MediaPath,.2,true,880);await AssertFrameAndAudio(result.MediaPath,1.25,false,440);
        using(var probe=JsonDocument.Parse(await Tool(ToolPath("ffprobe"),new[]{"-v","error","-show_chapters","-show_streams","-of","json",result.MediaPath})))
        {
            Require(probe.RootElement.GetProperty("streams").EnumerateArray().Count(s=>s.GetProperty("codec_type").GetString()=="audio")==1,"The full original audio track must survive the join");
            var chapters=probe.RootElement.GetProperty("chapters");Require(chapters.GetArrayLength()==2 && double.Parse(chapters[1].GetProperty("start_time").GetString()!,CultureInfo.InvariantCulture)>.95,"Chapters must follow the user order and rebase to the joined timeline");
        }
        Require(File.Exists(blue) && File.Exists(red) && File.ReadAllText(foreign)=="KEEP","Processing must preserve owned inputs until transaction cleanup and never touch foreign files");
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{blue,foreign},true,false,false,CancellationToken.None),"An external file must never become a merge input");
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{blue,blue},true,false,false,CancellationToken.None),"A duplicate path is not a second owned fragment");
        string different=Path.Combine(stage,"different.mp4");await MakeMedia(different,"red",440,128,1.5);
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{blue,different},false,false,false,CancellationToken.None),"Incompatible actual video parameters require explicit consent");
        var normalized=await service.MergeAsync(stage,new[]{blue,different},true,false,false,CancellationToken.None);
        Require(normalized.Reencoded && Math.Abs(normalized.DurationSeconds-2.5)<.2,"Explicit fallback must normalize and verify the complete output");
        await AssertFrameAndAudio(normalized.MediaPath,.2,true,880);await AssertFrameAndAudio(normalized.MediaPath,1.3,false,440);
        string silent=Path.Combine(stage,"silent.mp4");await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-i",red,"-an","-c:v","copy",silent});
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{blue,silent},true,false,false,CancellationToken.None),"Consent must never silently remove a source audio track");
        string bad=Path.Combine(stage,"invalid.mp4");File.WriteAllText(bad,"not media");
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{blue,bad},true,false,false,CancellationToken.None),"Uninspectable media must publish no result");
    }
    private static Task<byte[]> MakeMedia(string target,string colour,int frequency,int width,double duration)=>Tool(ToolPath("ffmpeg"),new[]{
        "-hide_banner","-loglevel","error","-nostdin","-f","lavfi","-i",$"color=c={colour}:s={width}x90:r=24:d={duration.ToString(CultureInfo.InvariantCulture)}",
        "-f","lavfi","-i",$"sine=frequency={frequency}:sample_rate=48000:duration={duration.ToString(CultureInfo.InvariantCulture)}","-c:v","libx264","-preset","ultrafast","-pix_fmt","yuv420p","-c:a","aac","-movflags","+faststart",target});
    private static async Task AssertFrameAndAudio(string path,double time,bool blue,int frequency)
    {
        string timestamp=time.ToString(CultureInfo.InvariantCulture);
        byte[] pixel=await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-ss",timestamp,"-i",path,"-frames:v","1","-vf","scale=1:1","-f","rawvideo","-pix_fmt","rgb24","pipe:1"});
        Require(pixel.Length==3 && (blue?pixel[2]>200 && pixel[0]<80:pixel[0]>200 && pixel[2]<80),"Merged frames must retain exact user range order");
        byte[] audio=await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-ss",timestamp,"-i",path,"-t","0.1","-vn","-ar","8000","-ac","1","-f","s16le","pipe:1"});
        double Energy(int hz){double real=0,imaginary=0;for(int i=0;i<audio.Length/2;i++){double sample=BitConverter.ToInt16(audio,i*2);real+=sample*Math.Cos(2*Math.PI*hz*i/8000);imaginary+=sample*Math.Sin(2*Math.PI*hz*i/8000);}return real*real+imaginary*imaginary;}
        Require(audio.Length>1000 && Energy(frequency)>10*Energy(frequency==440?880:440),"The source audio must stay aligned with each ordered colour fragment");
    }
    private static async Task<byte[]> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var output=await BoundedProcessOutput.ReadAsync(child,1024*1024,1024*1024,deadline.Token);
        Require(child.Process.ExitCode==0,"Actual media tool failed: "+Encoding.UTF8.GetString(output.StandardError));return output.StandardOutput;
    }
    private static void Refuses(Action action,string message){bool failed=false;try{action();}catch(ArgumentException){failed=true;}Require(failed,message);}
    private static async Task RefusesAsync(Func<Task> action,string message){bool failed=false;try{await action();}catch(InvalidDataException){failed=true;}Require(failed,message);}
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException("Fragment merge test: "+message);}
}
