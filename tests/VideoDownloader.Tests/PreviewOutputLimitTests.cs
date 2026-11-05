using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class PreviewOutputLimitTests
{
    private const string Prefix="https://preview-output-limit.invalid/";
    private const string PrivatePayload="PRIVATE_PREVIEW_PAYLOAD";
    private const string SleepArgument="--preview-output-limit-sleep";
    private const string ParentArgument="--preview-output-limit-parent-exit";
    private const string Cover="iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAADklEQVR4nGP4DwYMEAoAU7oL9ZisIGcAAAAASUVORK5CYII=";

    internal static bool TryRunFixture(string[] args)
    {
        if(args.Length==2 && args[0]==SleepArgument)
        {
            RecordProcess(args[1]);
            Thread.Sleep(Timeout.Infinite);
            return true;
        }
        if(args.Length==2 && args[0]==ParentArgument)
        {
            using var descendant=Process.Start(Sleeper(args[1]))!;
            WaitForFile(args[1]);
            return true;
        }
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        Require(args.Contains("--dump-single-json") && args.Contains("--skip-download"),"Preview must request metadata only");
        int cookie=Array.IndexOf(args,"--cookies");
        Require(cookie>=0 && cookie+1<args.Length,"The fixture needs its isolated control directory");
        string root=File.ReadAllText(args[cookie+1],Encoding.UTF8);
        string mode=url[Prefix.Length..];
        RecordProcess(Path.Combine(root,mode+".json"));
        if(mode=="utf8")
        {
            using var output=Console.OpenStandardOutput();
            output.Write(Encoding.UTF8.GetBytes("{\"title\":\""));
            byte[] chunk=Encoding.UTF8.GetBytes(new string('Ж',4096));
            for(int written=0;written<=PreviewService.MaxMetadataBytes;written+=chunk.Length)output.Write(chunk);
            output.Write(Encoding.UTF8.GetBytes("\",\"thumbnail\":\"\"}"));
            return true;
        }
        if(mode=="stdout" || mode=="stderr" || mode=="cached-stdout")
        {
            using var output=mode=="stderr"?Console.OpenStandardError():Console.OpenStandardOutput();
            byte[] chunk=Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat(PrivatePayload,2048)));
            while(true)output.Write(chunk); // Intentionally no newline and no EOF.
        }
        Console.WriteLine(JsonSerializer.Serialize(new {title="After bounded output",thumbnail=""}));
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-preview-output-limits-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string unrelatedRecord=Path.Combine(root,"unrelated.json");
        using var unrelated=Process.Start(Sleeper(unrelatedRecord))!;
        try
        {
            WaitForFile(unrelatedRecord);
            await RejectFlood(root,"stdout",PreviewService.MaxMetadataBytes);
            Require(!unrelated.HasExited,"A stdout limit must not terminate an unrelated process");
            await RejectFlood(root,"stderr",PreviewService.MaxMetadataErrorBytes);
            Require(!unrelated.HasExited,"A stderr limit must not terminate an unrelated process");
            await RejectFlood(root,"utf8",PreviewService.MaxMetadataBytes);
            await PreserveKnownPreview(root);
            if(OperatingSystem.IsWindows())await TerminateExitedParent(root,unrelated);
            Console.WriteLine("PASS: raw preview stdout/stderr byte limits, newline-free floods, worker recovery, retained covers and isolated child-job termination");
        }
        finally
        {
            if(!unrelated.HasExited)unrelated.Kill(true);
            await unrelated.WaitForExitAsync();
            Directory.Delete(root,true);
        }
    }

    private static async Task RejectFlood(string root,string mode,int byteLimit)
    {
        string stream=mode=="stderr"?"stderr":"stdout";
        string directory=Path.Combine(root,mode);Directory.CreateDirectory(directory);
        string control=Path.Combine(directory,"control.txt");File.WriteAllText(control,directory);
        var data=new AppPersistence(Path.Combine(directory,"profile"));
        var failures=new ConcurrentQueue<(string Url,string Error)>();
        var logs=new ConcurrentQueue<string>();
        var next=new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
        string failedUrl=Prefix+mode, nextUrl=Prefix+mode+"-next";
        using var service=new PreviewService(Executable(),data,()=>control,
            value=>{if(value.Url==nextUrl)next.TrySetResult(value);},logs.Enqueue,
            requestTimeout:TimeSpan.FromSeconds(20),failed:(url,error)=>failures.Enqueue((url,error)),thumbnailValidator:_=>true);
        service.Request(failedUrl);service.Request(nextUrl);
        await next.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Require(failures.Count==1 && failures.Single().Url==failedUrl &&
            failures.Single().Error.Contains($"{stream} exceeds {byteLimit} bytes",StringComparison.Ordinal),
            "A raw byte overflow must report its exact stream/limit before the request deadline");
        Require(logs.Any(log=>log.Contains($"{stream} exceeds {byteLimit} bytes",StringComparison.Ordinal)) &&
            logs.All(log=>!log.Contains(PrivatePayload,StringComparison.Ordinal)),"Overflow diagnostics must not retain the flooded payload");
        Require(service.Get(failedUrl)==null,"Oversized metadata must not become a successful cache entry");
        Require(service.Get(nextUrl)?.Title=="After bounded output","The same preview worker must deliver the following URL");
        Require(!RecordedProcessIsAlive(Path.Combine(directory,mode+".json")),"A flooding metadata child must be joined before the next request");
    }

    private static async Task PreserveKnownPreview(string root)
    {
        string directory=Path.Combine(root,"cached");Directory.CreateDirectory(directory);
        string control=Path.Combine(directory,"control.txt");File.WriteAllText(control,directory);
        var data=new AppPersistence(Path.Combine(directory,"profile"));
        Directory.CreateDirectory(data.PreviewDir);
        string url=Prefix+"cached-stdout", nextUrl=Prefix+"cached-next";
        string path=Path.Combine(data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        string cached=JsonSerializer.Serialize(new {
            CacheVersion=1,PreviewFetchVersion=2,Url=url,SavedAtUtc=DateTime.UtcNow,Title="Known title",Uploader="Known author",
            PlaylistTitle="",Duration=1,Extractor="fixture",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
            ThumbnailBase64=Cover,ThumbnailsBase64=new[]{Cover}
        });
        File.WriteAllText(path,cached);
        var ready=new ConcurrentQueue<PreviewData>();
        var logs=new ConcurrentQueue<string>();
        var next=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var service=new PreviewService(Executable(),data,()=>control,value=>
        {
            ready.Enqueue(value);
            if(value.Url==nextUrl)next.TrySetResult();
        },logs.Enqueue,requestTimeout:TimeSpan.FromSeconds(20),thumbnailValidator:_=>true);
        service.Request(url);service.Request(nextUrl);
        await next.Task.WaitAsync(TimeSpan.FromSeconds(12));
        Require(ready.Any(value=>value.Url==url && value.Title=="Known title" && value.ThumbnailBase64==Cover),
            "A failed metadata upgrade must keep the known title and usable gallery");
        Require(File.ReadAllText(path)==cached,"A metadata overflow must not overwrite the previous good disk cache");
        Require(logs.Any(log=>log.Contains("stdout exceeds",StringComparison.Ordinal)),"The retained preview must still report its failed upgrade");
    }

    private static async Task TerminateExitedParent(string root,Process unrelated)
    {
        string record=Path.Combine(root,"orphan.json");
        var start=new ProcessStartInfo(Executable()) {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            ArgumentList={ParentArgument,record}
        };
        using var child=ManagedChildProcess.Start(start);
        try
        {
            await child.Process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            Require(RecordedProcessIsAlive(record),"The descendant fixture must outlive its root before job termination");
            child.Terminate();child.Terminate();
            var deadline=Stopwatch.StartNew();
            while(RecordedProcessIsAlive(record) && deadline.Elapsed<TimeSpan.FromSeconds(5))await Task.Delay(10);
            Require(!RecordedProcessIsAlive(record),"Closing the owned job must terminate descendants even after the root exits");
            Require(!unrelated.HasExited,"Terminating an exited root's job must preserve an unrelated process");
            await Task.WhenAll(child.StandardOutput.ReadToEndAsync(),child.StandardError.ReadToEndAsync())
                .WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { child.Terminate(); }
    }

    private static ProcessStartInfo Sleeper(string record)=>new(Executable()) {
        UseShellExecute=false,CreateNoWindow=true,ArgumentList={SleepArgument,record}
    };
    private static string Executable()
    {
        string candidate=Path.Combine(AppContext.BaseDirectory,OperatingSystem.IsWindows()?"VideoDownloader.Tests.exe":"VideoDownloader.Tests");
        return File.Exists(candidate)?candidate:Environment.ProcessPath!;
    }
    private static void RecordProcess(string path)
    {
        using var process=Process.GetCurrentProcess();
        File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new {Pid=process.Id,StartedUtc=process.StartTime.ToUniversalTime()}));
        File.Move(path+".tmp",path);
    }
    private static bool RecordedProcessIsAlive(string path)
    {
        Require(File.Exists(path),"The fixture must record its process identity");
        using var record=JsonDocument.Parse(File.ReadAllText(path));
        try
        {
            using var process=Process.GetProcessById(record.RootElement.GetProperty("Pid").GetInt32());
            return !process.HasExited && process.StartTime.ToUniversalTime()==record.RootElement.GetProperty("StartedUtc").GetDateTime();
        }
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
        catch(System.ComponentModel.Win32Exception){return false;}
    }
    private static void WaitForFile(string path)
    {
        var deadline=Stopwatch.StartNew();
        while(!File.Exists(path))
        {
            if(deadline.Elapsed>TimeSpan.FromSeconds(10))throw new TimeoutException("The fixture did not start");
            Thread.Sleep(10);
        }
    }
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("Preview output limit test failed: "+message);
    }
}
