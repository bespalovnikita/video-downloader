using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class PreviewResetTests
{
    private const string Prefix="https://preview-reset.invalid/";
    private sealed record Call(string Url,int ProcessId,long StartedUtcTicks,bool MetadataOnly,long KernelStartedTicks=0);

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        int cookie=Array.IndexOf(args,"--cookies");
        Require(cookie>=0 && cookie+1<args.Length,"Metadata fixtures need their isolated control file");
        string root=File.ReadAllText(args[cookie+1],Encoding.UTF8),name=url[Prefix.Length..];
        Require(name.Length>0 && name.All(value=>char.IsAsciiLetterOrDigit(value) || value=='-'),"Unexpected fixture identity");
        bool metadataOnly=new[]{"--ignore-config","--dump-single-json","--skip-download"}
            .All(argument=>args.Contains(argument,StringComparer.Ordinal));
        Require(metadataOnly,"A reset/refetch must launch metadata-only children");
        FileStream? active=null;
        try
        {
            try{active=new FileStream(Path.Combine(root,"active.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);}
            catch(IOException)
            {
                File.WriteAllText(Path.Combine(root,"overlap-"+Environment.ProcessId),url);
                Console.Error.WriteLine("Metadata jobs overlapped.");Environment.ExitCode=2;return true;
            }
            using var process=Process.GetCurrentProcess();
            var call=new Call(url,Environment.ProcessId,process.StartTime.ToUniversalTime().Ticks,metadataOnly,KernelStart(Environment.ProcessId));
            string path=Path.Combine(root,"call-"+Environment.ProcessId+"-"+Guid.NewGuid().ToString("N")+".json");
            File.WriteAllText(path+".tmp",JsonSerializer.Serialize(call),Encoding.UTF8);
            File.Move(path+".tmp",path);
            var deadline=Stopwatch.StartNew();
            while(File.Exists(Path.Combine(root,"gate-"+name)) && !File.Exists(Path.Combine(root,"release-"+name)))
            {
                if(deadline.Elapsed>TimeSpan.FromSeconds(25))throw new TimeoutException("Metadata fixture gate did not open: "+name);
                Thread.Sleep(10);
            }
            string titlePath=Path.Combine(root,"title-"+name+".txt");
            string title=File.Exists(titlePath)?File.ReadAllText(titlePath,Encoding.UTF8):"Metadata "+name;
            string address="http://127.0.0.1:9/"+name+"-"+Uri.EscapeDataString(title)+".mp4";
            Console.WriteLine(JsonSerializer.Serialize(new
            {
                title,uploader="Reset fixture",duration=10,extractor="fixture",thumbnail="",
                formats=new[]{new{format_id="18",ext="mp4",url=address,protocol="http",width=640,height=360,
                    fps=25,vcodec="avc1",acodec="mp4a",language="en"}}
            }));
            return true;
        }
        finally{active?.Dispose();}
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-preview-reset-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await ResetSelectedMetadata(root);
            await RefuseProtectedCache(root);
            await RefuseReparseCache(root);
            Console.WriteLine("PASS: explicit metadata reset joins selected children, rejects queued/stale results, refetches cold exact URLs and private sources, preserves unrelated work/cache and protected/reparse canaries");
        }
        finally
        {
            var watch=Stopwatch.StartNew();
            while(true)
            {
                try{Directory.Delete(root,true);break;}
                catch(IOException) when(watch.Elapsed<TimeSpan.FromSeconds(3)){await Task.Delay(20);}
                catch(UnauthorizedAccessException) when(watch.Elapsed<TimeSpan.FromSeconds(3)){await Task.Delay(20);}
            }
        }
    }

    private static async Task ResetSelectedMetadata(string testRoot)
    {
        string root=Path.Combine(testRoot,"selected");Directory.CreateDirectory(root);
        string control=Path.Combine(root,"control.txt");File.WriteAllText(control,root,Encoding.UTF8);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        var ready=new ConcurrentQueue<PreviewData>();
        var failed=new ConcurrentQueue<string>();
        var notifications=new ConcurrentQueue<(PreviewData Data,long Generation)>();
        var service=new PreviewService(Executable(),storage,()=>control,ready.Enqueue,_=>{},
            requestTimeout:TimeSpan.FromSeconds(30),failed:(url,error)=>failed.Enqueue(url+": "+error));
        service.ResultAvailable+=(data,final,generation)=>{if(final)notifications.Enqueue((data,generation));};
        string warm=Prefix+"warm",sibling=Prefix+"Warm",active=Prefix+"active",queued=Prefix+"queued",other=Prefix+"other";
        try
        {
            service.Request(warm);service.Request(sibling);
            await Until(()=>ReadyCount(warm)==1 && ReadyCount(sibling)==1,"Initial exact-case caches must arrive");
            byte[] siblingBytes=File.ReadAllBytes(Cache(storage,sibling));
            var oldFrame=await service.GetFrameSourceAsync(warm,"18",CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            var oldClip=await service.GetClipSourcesAsync(new(warm,0,1,"18"),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
            Require(oldClip.Video?.Address==oldFrame.Address,"Both private source snapshots must originate from the first extraction");
            int originalCalls=Calls(root).Length;
            Require(originalCalls==2,"Source lookup on a current warm URL must not spawn extra metadata jobs");
            await service.ResetAsync([warm,warm]);
            Require(service.Peek(warm) is null && service.Get(warm) is null && !File.Exists(Cache(storage,warm)),
                "Reset must remove both memory and durable metadata before an explicit fresh request");
            Require(service.Get(sibling)?.Title=="Metadata Warm" && File.ReadAllBytes(Cache(storage,sibling)).SequenceEqual(siblingBytes),
                "An exact-case sibling must retain its title, TTL and complete cache bytes");
            var delivered=notifications.Where(value=>service.IsCurrent(value.Data.Url,value.Generation)).ToArray();
            Require(delivered.All(value=>value.Data.Url!=warm) && delivered.Any(value=>value.Data.Url==sibling),
                "Previously queued notifications for the reset URL must fail generation admission while unrelated notifications remain valid");
            Require(Calls(root).Length==originalCalls,"Reset itself must not schedule metadata or downloads");
            File.WriteAllText(Path.Combine(root,"title-warm.txt"),"Fresh metadata",Encoding.UTF8);
            var frameTask=service.GetFrameSourceAsync(warm,"18",CancellationToken.None);
            var clipTask=service.GetClipSourcesAsync(new(warm,0,1,"18"),CancellationToken.None);
            var freshFrame=await frameTask.WaitAsync(TimeSpan.FromSeconds(8));
            var freshClip=await clipTask.WaitAsync(TimeSpan.FromSeconds(8));
            await Until(()=>ReadyCount(warm)==2,"Explicit source lookup must complete a fresh metadata extraction");
            Require(freshFrame.Address!=oldFrame.Address && freshClip.Video?.Address==freshFrame.Address &&
                service.Get(warm)?.Title=="Fresh metadata" && Calls(root).Count(call=>call.Url==warm)==2,
                "Reset must bypass the 72-hour TTL and replace frame/clip source addresses using one fresh child");

            File.WriteAllText(Path.Combine(root,"gate-active"),"");
            service.Request(active);service.Request(queued);service.Request(other);
            await Until(()=>Calls(root).Any(call=>call.Url==active),"The selected gated metadata child must actually start");
            var originalActive=Calls(root).Single(call=>call.Url==active);
            bool originalAlive=Alive(originalActive,out string originalIdentity);
            Require(originalAlive,"The active reset fixture must be alive before cancellation; "+originalIdentity);
            await service.ResetAsync([active,queued]).WaitAsync(TimeSpan.FromSeconds(8));
            Require(!Alive(originalActive),"Reset must join the selected metadata child before returning");
            await Until(()=>ReadyCount(other)==1,"The unrelated pending URL must continue on the original sole worker");
            Require(ReadyCount(active)==0 && ReadyCount(queued)==0 && Calls(root).All(call=>call.Url!=queued) &&
                service.Get(active) is null && service.Get(queued) is null && failed.IsEmpty,
                "A cancelled active URL and removed queued URL must not publish cache, stale data or timeout errors");
            File.Delete(Path.Combine(root,"gate-active"));
            File.WriteAllText(Path.Combine(root,"title-active.txt"),"Active fetched again",Encoding.UTF8);
            service.Request(active);service.Request(queued);
            await Until(()=>ReadyCount(active)==1 && ReadyCount(queued)==1,"Both reset rows must accept a subsequent explicit request");
            Require(Calls(root).Count(call=>call.Url==active)==2 && Calls(root).Count(call=>call.Url==queued)==1 &&
                Calls(root).Last(call=>call.Url==active).ProcessId!=originalActive.ProcessId && service.Get(active)?.Title=="Active fetched again",
                "The resumed metadata request must be a fresh child and the queued reset URL must launch only once after explicit admission");

            string unrelated=Prefix+"unrelated-active";
            File.WriteAllText(Path.Combine(root,"gate-unrelated-active"),"");service.Request(unrelated);
            await Until(()=>Calls(root).Any(call=>call.Url==unrelated),"The unrelated gated child must start");
            var untouched=Calls(root).Single(call=>call.Url==unrelated);
            int before=Calls(root).Length;
            await service.ResetAsync([warm]).WaitAsync(TimeSpan.FromSeconds(5));
            Require(Alive(untouched) && Calls(root).Length==before && service.Get(warm) is null,
                "Resetting another row must neither cancel nor wait for an unrelated metadata child, nor implicitly refetch the selected row");
            File.WriteAllText(Path.Combine(root,"release-unrelated-active"),"");
            await Until(()=>ReadyCount(unrelated)==1,"The unrelated active child must finish normally after its gate opens");
            Require(!Directory.EnumerateFiles(root,"overlap-*").Any() && Calls(root).All(call=>call.MetadataOnly) &&
                !File.Exists(storage.HistoryPath) && !File.Exists(storage.SessionPath),
                "All reset/refetch work must remain serial metadata-only work without changing queue checkpoints or successful history");
        }
        finally
        {
            foreach(string name in new[]{"active","unrelated-active"})File.WriteAllText(Path.Combine(root,"release-"+name),"");
            service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        }
        int ReadyCount(string url)=>ready.Count(value=>value.Url==url);
    }

    private static async Task RefuseProtectedCache(string testRoot)
    {
        string root=Path.Combine(testRoot,"protected");Directory.CreateDirectory(root);
        string control=Path.Combine(root,"control.txt");File.WriteAllText(control,root,Encoding.UTF8);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        string first=Prefix+"first",second=Prefix+"second";
        var ready=new ConcurrentQueue<PreviewData>();
        var service=new PreviewService(Executable(),storage,()=>control,ready.Enqueue,_=>{});
        string protectedPath=Cache(storage,second);
        try
        {
            service.Request(first);service.Request(second);
            await Until(()=>ready.Count==2,"The protected cache fixtures must be populated by real metadata children");
            byte[] firstBytes=File.ReadAllBytes(Cache(storage,first)),protectedBytes=File.ReadAllBytes(protectedPath);
            File.SetAttributes(protectedPath,File.GetAttributes(protectedPath)|FileAttributes.ReadOnly);
            bool refused=false;
            try{await service.ResetAsync([first,second]);}
            catch(UnauthorizedAccessException){refused=true;}
            Require(refused && File.ReadAllBytes(Cache(storage,first)).SequenceEqual(firstBytes) &&
                File.ReadAllBytes(protectedPath).SequenceEqual(protectedBytes) &&
                (File.GetAttributes(protectedPath)&FileAttributes.ReadOnly)!=0,
                "A protected selected cache entry must report refusal before deleting another entry or bypassing its read-only protection");
            File.SetAttributes(protectedPath,File.GetAttributes(protectedPath)&~FileAttributes.ReadOnly);
            await service.ResetAsync([first,second]);
            Require(!File.Exists(Cache(storage,first)) && !File.Exists(protectedPath) && service.Get(first) is null && service.Get(second) is null,
                "A failed cache reset must release its barriers so the user can retry after resolving filesystem protection");
        }
        finally
        {
            if(File.Exists(protectedPath))File.SetAttributes(protectedPath,File.GetAttributes(protectedPath)&~FileAttributes.ReadOnly);
            service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(8));
        }
    }

    private static async Task RefuseReparseCache(string testRoot)
    {
        string root=Path.Combine(testRoot,"reparse"),outside=Path.Combine(root,"outside");Directory.CreateDirectory(outside);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        string url=Prefix+"external",target=Path.Combine(outside,Path.GetFileName(Cache(storage,url)));
        const string canary="FOREIGN CACHE CANARY";File.WriteAllText(target,canary,Encoding.UTF8);
        string link=storage.PreviewDir;Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if(Directory.Exists(link))Directory.Delete(link,true);
        await CreateDirectoryLink(link,outside);
        PreviewService? service=null;
        try
        {
            service=new PreviewService(Executable(),storage,()=>"",_=>{},_=>{});
            bool refused=false;
            try{await service.ResetAsync([url]);}catch(IOException){refused=true;}
            Require(refused && Directory.Exists(link) && File.ReadAllText(target,Encoding.UTF8)==canary,
                "Reset must reject a reparse cache ancestor and preserve the external target and link");
        }
        finally
        {
            if(service is not null){service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(8));}
            Directory.Delete(link);
        }
        Require(File.ReadAllText(target,Encoding.UTF8)==canary,"Cleanup of the fixture link must not touch the external canary");
    }

    private static async Task CreateDirectoryLink(string link,string target)
    {
        if(!OperatingSystem.IsWindows()){Directory.CreateSymbolicLink(link,target);return;}
        var start=new ProcessStartInfo("cmd.exe"){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in new[]{"/d","/c","mklink","/J",link,target})start.ArgumentList.Add(argument);
        using var child=Process.Start(start)!;
        var output=child.StandardOutput.ReadToEndAsync();var error=child.StandardError.ReadToEndAsync();
        await child.WaitForExitAsync();await Task.WhenAll(output,error);
        Require(child.ExitCode==0,"The native junction fixture must be created: "+await error);
    }

    private static Call[] Calls(string root)=>Directory.EnumerateFiles(root,"call-*.json")
        .Select(path=>JsonSerializer.Deserialize<Call>(File.ReadAllText(path,Encoding.UTF8))!).OrderBy(call=>call.StartedUtcTicks).ToArray();
    private static bool Alive(Call call)=>Alive(call,out _);
    private static bool Alive(Call call,out string identity)
    {
        try
        {
            using var process=Process.GetProcessById(call.ProcessId);
            bool exited=process.HasExited;
            long actual=exited?0:process.StartTime.ToUniversalTime().Ticks;
            long kernel=exited?0:KernelStart(call.ProcessId);
            identity=$"pid={call.ProcessId} expected_ticks={call.StartedUtcTicks} actual_ticks={actual} "+
                $"delta_ticks={actual-call.StartedUtcTicks} expected_kernel_ticks={call.KernelStartedTicks} actual_kernel_ticks={kernel} "+
                $"has_exited={exited} now_ticks={DateTime.UtcNow.Ticks} os={Environment.OSVersion.Platform}";
            // Measured .NET Linux wall-clock StartTime values in child/parent
            // differed by 1.5269 ms for the same live PID. Compare its exact
            // kernel start jiffies there;
            // Windows retains the actual creation-time equality, without any
            // tolerance that could accept a reused PID.
            return !exited && (OperatingSystem.IsLinux()
                ? call.KernelStartedTicks>0 && kernel==call.KernelStartedTicks
                : actual==call.StartedUtcTicks);
        }
        catch(ArgumentException error){identity=$"pid={call.ProcessId} expected_ticks={call.StartedUtcTicks} identity_error={error.GetType().Name}";return false;}
        catch(InvalidOperationException error){identity=$"pid={call.ProcessId} expected_ticks={call.StartedUtcTicks} identity_error={error.GetType().Name}";return false;}
    }
    private static long KernelStart(int processId)
    {
        if(!OperatingSystem.IsLinux())return 0;
        try
        {
            string stat=File.ReadAllText($"/proc/{processId}/stat");
            // The comm field can contain spaces and ')'; fields after its last
            // delimiter begin at field 3, making starttime (field 22) index 19.
            int end=stat.LastIndexOf(')');
            string[] fields=end<0?[]:stat[(end+1)..].Split(' ',StringSplitOptions.RemoveEmptyEntries);
            if(fields.Length<=19 || !long.TryParse(fields[19],NumberStyles.None,CultureInfo.InvariantCulture,out long value) || value<=0)
                throw new InvalidDataException("The metadata fixture has no kernel process-start identity.");
            return value;
        }
        catch(FileNotFoundException){return 0;}
        catch(DirectoryNotFoundException){return 0;}
    }
    private static string Cache(AppPersistence storage,string url)=>Path.Combine(storage.PreviewDir,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,
        typeof(PreviewResetTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static async Task Until(Func<bool> condition,string message)
    {
        var watch=Stopwatch.StartNew();
        while(!condition())
        {
            Require(watch.Elapsed<TimeSpan.FromSeconds(12),message);
            await Task.Delay(10);
        }
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Preview reset test failed: "+message);}
}
