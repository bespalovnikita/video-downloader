using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class StartupDownloadRegressionTests
{
    private const string Prefix="https://startup-download.invalid/";
    internal static bool TryRunFixture(string[] args)
    {
        if(args.FirstOrDefault()=="--managed-echo")
        {
            Console.OutputEncoding=Encoding.UTF8;
            Console.WriteLine(JsonSerializer.Serialize(new{Args=args.Skip(1),Value=Environment.GetEnvironmentVariable("VD_MANAGED_ECHO"),Directory=Environment.CurrentDirectory}));
            Console.Error.WriteLine("stderr-ж");return true;
        }
        if(args.Length==2 && args[0]=="--job-sleeper")
        {
            File.WriteAllText(args[1],Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite);return true;
        }
        if(args.Length==2 && args[0]=="--job-parent")
        {
            using var child=ManagedChildProcess.Start(StartInfo("--job-tree",args[1]));
            Thread.Sleep(Timeout.Infinite);return true;
        }
        if(args.Length==2 && args[0]=="--job-tree")
        {
            using var grandchild=Process.Start(StartInfo("--job-sleeper",Path.Combine(args[1],"grandchild.pid")))!;
            File.WriteAllText(Path.Combine(args[1],"child.pid"),Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite);return true;
        }
        if(args.LastOrDefault()?.StartsWith(Prefix,StringComparison.Ordinal)!=true) return false;
        string stage=Path.GetDirectoryName(args[Array.IndexOf(args,"-o")+1])!;
        string extension="mp4";
        int remux=Array.IndexOf(args,"--remux-video");
        if(remux>=0)
        {
            extension=args[remux+1];
            Require(args[Array.IndexOf(args,"--merge-output-format")+1]==extension,"Merge and remux must agree");
        }
        if(args[^1].EndsWith("wrong-container",StringComparison.Ordinal)) extension=extension=="webm"?"mp4":"webm";
        string media=Path.Combine(stage,"fixture.video."+extension);
        File.WriteAllText(media,"NEW MEDIA");
        File.WriteAllText(Path.Combine(stage,"fixture.video.en.srt"),"SUBTITLES");
        Console.WriteLine("__VD_FILE__:"+media);return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-startup-download-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string tool=Path.Combine(root,"tool.exe");File.WriteAllText(tool,"fixture");
            bool missing=false;
            try { DependencyCenter.ValidateDownloadTools(tool,Path.Combine(root,"ffmpeg.exe"),tool); }
            catch(FileNotFoundException error) { missing=error.Message.Contains("ffmpeg.exe") && error.Message.Contains("Install FFmpeg"); }
            Require(missing,"Missing media tools must be explained before any worker is launched");
            DependencyCenter.ValidateDownloadTools(tool,tool,tool);

            var data=new AppPersistence(Path.Combine(root,"profile"));
            var options=new DownloadOptions {Output=Path.Combine(root,"output"),Threads="1",EmbedMetadata=false,EmbedChapters=false};
            var first=await Download(data,options,Prefix+"repeat");
            var second=await Download(data,options,Prefix+"repeat");
            var third=await Download(data,options,Prefix+"repeat");
            Require(first.Status=="Completed" && second.Status=="Completed" && third.Status=="Completed","Approved repeat downloads must complete");
            Require(first.Path!=second.Path && second.Path!=third.Path,"Each copy needs a free media name");
            Require(Path.GetFileName(second.Path)=="fixture (2).video.mp4" &&
                second.OwnedPaths.Any(p=>Path.GetFileName(p)=="fixture (2).video.en.srt"),"Copies must keep subtitle and media stems aligned");
            OwnedFiles.DeleteOwned(options.Output,second.OwnedPaths);
            Require(File.Exists(first.Path) && File.Exists(third.Path),"Removing a copy must preserve both adjacent attempts");
            var parallel=await Task.WhenAll(Download(data,options,Prefix+"parallel-a"),Download(data,options,Prefix+"parallel-b"));
            Require(parallel.All(row=>row.Status=="Completed") && parallel[0].Path!=parallel[1].Path,"Concurrent publishers must reserve distinct copy names");

            foreach(string container in new[]{"MP4","MKV","WebM"})
            {
                var config=new DownloadOptions {Output=Path.Combine(root,container),Threads="1",Container=container,EmbedMetadata=false,EmbedChapters=false};
                var row=await Download(data,config,Prefix+"container");
                Require(row.Status=="Completed" && Path.GetExtension(row.Path)=="."+container.ToLowerInvariant(),"Every selected container must be applied to a single-file download");
                row=await Download(data,config,Prefix+"wrong-container");
                Require(row.Status=="Failed" && !data.ReadHistory().ContainsKey(row.Url),"An unconverted file must not be published or recorded as success");
            }

            var echo=StartInfo("--managed-echo","space here","quote\"here","trailing\\","строка\tтекста","");
            echo.WorkingDirectory=root;echo.Environment["VD_MANAGED_ECHO"]="значение";
            using(var child=ManagedChildProcess.Start(echo))
            {
                var stdout=child.StandardOutput.ReadToEndAsync();var stderr=child.StandardError.ReadToEndAsync();
                await Task.WhenAll(stdout,stderr,child.Process.WaitForExitAsync()).WaitAsync(TimeSpan.FromSeconds(10));
                using var result=JsonDocument.Parse(stdout.Result);
                Require(result.RootElement.GetProperty("Args").EnumerateArray().Select(a=>a.GetString()).SequenceEqual(echo.ArgumentList.Skip(1)),"Child arguments must preserve quotes, slashes, Unicode and empty strings");
                Require(result.RootElement.GetProperty("Value").GetString()=="значение" &&
                    result.RootElement.GetProperty("Directory").GetString()==root && stderr.Result.Contains("stderr-ж"),"Environment, working directory and both UTF-8 pipes must survive protected launch");
            }
            if(OperatingSystem.IsWindows()) await Crash(root);
            Console.WriteLine("PASS: startup dependencies, repeated/concurrent publication, container enforcement and managed child lifetime");
        }
        finally { Directory.Delete(root,true); }
    }

    private static async Task Crash(string root)
    {
        using var sibling=Process.Start(StartInfo("--job-sleeper",Path.Combine(root,"sibling.pid")))!;
        using var parent=Process.Start(StartInfo("--job-parent",root))!;
        Process? child=null,grandchild=null;
        try
        {
            int childId=0,grandchildId=0;
            await Until(()=>ReadPid(Path.Combine(root,"child.pid"),out childId) && ReadPid(Path.Combine(root,"grandchild.pid"),out grandchildId));
            child=Process.GetProcessById(childId);
            grandchild=Process.GetProcessById(grandchildId);
            Require(!child.HasExited && !grandchild.HasExited,"Crash fixture must own a running tree");
            parent.Kill(); // Deliberately kill only the owner, never its tree.
            await parent.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Until(()=>child.HasExited && grandchild.HasExited);
            Require(!sibling.HasExited,"A crashed worker owner must not terminate an unrelated process");
        }
        finally
        {
            foreach(var process in new[]{parent,child,grandchild,sibling})
                if(process!=null) {if(!process.HasExited)process.Kill(true);process.Dispose();}
        }
    }
    private static bool ReadPid(string path,out int pid)
    {
        pid=0;
        try {return int.TryParse(File.ReadAllText(path),out pid) && pid>0;}
        catch(IOException) {return false;}
    }
    private static async Task Until(Func<bool> condition)
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while(!condition()) await Task.Delay(25,deadline.Token);
    }
    private static async Task<DownloadItem> Download(AppPersistence data,DownloadOptions options,string url)
    {
        var row=new DownloadItem {Url=url};
        await using var engine=new DownloadCoordinator(Executable(),data,options,_=>{},_=>{});
        engine.Add(row);await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));return row;
    }
    private static ProcessStartInfo StartInfo(params string[] arguments)
    {
        var start=new ProcessStartInfo(Executable()) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8};
        foreach(var argument in arguments)start.ArgumentList.Add(argument);return start;
    }
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,typeof(StartupDownloadRegressionTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
}
