using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class PreviewSchedulingTests
{
    private const string Prefix="https://preview-scheduling.invalid/";

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        int cookies=Array.IndexOf(args,"--cookies");
        Require(cookies>=0 && cookies+1<args.Length,"The scheduling fixture needs its control directory");
        string root=File.ReadAllText(args[cookies+1],Encoding.UTF8);
        string name=url[Prefix.Length..];
        Require(new[]{"first","A","B","C"}.Contains(name,StringComparer.Ordinal),"Unexpected scheduling fixture URL");
        bool metadataOnly=new[]{"--ignore-config","--dump-single-json","--skip-download"}
            .All(required=>args.Contains(required,StringComparer.Ordinal));
        Require(metadataOnly,"Scheduling must start metadata-only children, never downloads");
        FileStream? active=null;
        try
        {
            try { active=new FileStream(Path.Combine(root,"active.lock"),FileMode.CreateNew,FileAccess.Write,FileShare.None); }
            catch(IOException)
            {
                File.WriteAllText(Path.Combine(root,"overlap-"+Environment.ProcessId),url);
                Console.Error.WriteLine("More than one metadata child was active.");
                Environment.ExitCode=2;
                return true;
            }
            File.AppendAllText(Path.Combine(root,"calls.jsonl"),JsonSerializer.Serialize(new {
                Url=url,Arguments=args,ProcessId=Environment.ProcessId,MetadataOnly=metadataOnly
            })+Environment.NewLine,Encoding.UTF8);
            File.WriteAllText(Path.Combine(root,"started-"+name),Environment.ProcessId.ToString());
            var deadline=Stopwatch.StartNew();
            while(!File.Exists(Path.Combine(root,"release-"+name)))
            {
                if(deadline.Elapsed>TimeSpan.FromSeconds(20))throw new TimeoutException("Fixture gate was not released: "+name);
                Thread.Sleep(10);
            }
            Console.WriteLine(JsonSerializer.Serialize(new {title="Metadata "+name,uploader="Scheduling fixture",duration=1}));
            return true;
        }
        finally
        {
            if(active!=null)
            {
                active.Dispose();
                File.Delete(Path.Combine(root,"active.lock"));
            }
        }
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-preview-scheduling-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string cookies=Path.Combine(root,"preview-scheduling-control.txt");
        File.WriteAllText(cookies,root,Encoding.UTF8);
        var ready=new ConcurrentQueue<PreviewData>();
        var failed=new ConcurrentQueue<string>();
        var data=new AppPersistence(Path.Combine(root,"profile"));
        string executable=Path.Combine(AppContext.BaseDirectory,
            typeof(PreviewSchedulingTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
        Require(File.Exists(executable),"Native test apphost is required for the scheduling fixture");
        PreviewService? service=null;
        string[] expected=["first","C","A","B"];
        try
        {
            service=new PreviewService(executable,data,()=>cookies,ready.Enqueue,_=>{},
                requestTimeout:TimeSpan.FromSeconds(25),failed:(url,error)=>failed.Enqueue(url+": "+error));
            async Task Started(string name,int expectedCalls)
            {
                var deadline=Stopwatch.StartNew();
                while(!File.Exists(Path.Combine(root,"started-"+name)))
                {
                    Require(failed.IsEmpty && !Directory.EnumerateFiles(root,"overlap-*").Any(),
                        "Only one metadata worker may run: "+string.Join("; ",failed));
                    Require(deadline.Elapsed<TimeSpan.FromSeconds(12),"The expected next metadata child did not start: "+name);
                    await Task.Delay(10);
                }
                Require(Directory.EnumerateFiles(root,"started-*").Count()==expectedCalls,
                    "A gated metadata child must prevent another child from starting");
            }
            void Release(string name)=>File.WriteAllText(Path.Combine(root,"release-"+name),"");

            service.Request(Prefix+"first");
            await Started("first",1);
            service.Request(Prefix+"A");
            service.Request(Prefix+"B");
            service.Request(Prefix+"C",priority:true);
            service.Request(Prefix+"C",priority:true);
            service.Request(Prefix+"first",priority:true);
            service.Request(Prefix+"first",priority:true);
            Require(ready.IsEmpty,"The first worker must remain gated while priority requests are queued");
            Release("first");
            await Started("C",2);
            Release("C");
            await Started("A",3);
            Release("A");
            await Started("B",4);
            Release("B");
            var completion=Stopwatch.StartNew();
            while(ready.Count<4)
            {
                Require(failed.IsEmpty,"Scheduling must not lose metadata requests: "+string.Join("; ",failed));
                Require(completion.Elapsed<TimeSpan.FromSeconds(12),"All metadata results must reach the real ready callback");
                await Task.Delay(10);
            }
            Require(ready.Select(result=>result.Url).SequenceEqual(expected.Select(name=>Prefix+name),StringComparer.Ordinal),
                "The single worker must finish first,C,A,B exactly once despite repeated priority requests");
            Require(ready.All(result=>result.Title=="Metadata "+result.Url[Prefix.Length..]),
                "Every ready callback must receive metadata parsed from its actual child process");
            foreach(string name in expected)
                Require(service.Get(Prefix+name)?.Title=="Metadata "+name,"Completed metadata must populate the exact URL cache");
            var calls=File.ReadAllLines(Path.Combine(root,"calls.jsonl"),Encoding.UTF8)
                .Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(calls.Length==4 && calls.Select(call=>call.RootElement.GetProperty("Url").GetString())
                    .SequenceEqual(expected.Select(name=>Prefix+name),StringComparer.Ordinal),
                    "Priority and running duplicates must execute only four children in first,C,A,B order");
                Require(calls.All(call=>call.RootElement.GetProperty("MetadataOnly").GetBoolean() &&
                    call.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "All scheduling requests must run as actual metadata-only managed children");
            }
            finally { foreach(var call in calls)call.Dispose(); }
            Require(!Directory.EnumerateFiles(root,"overlap-*").Any() && failed.IsEmpty && !File.Exists(data.HistoryPath),
                "Scheduling must never overlap metadata workers or record a download");
            Console.WriteLine("PASS: native preview scheduling first,C,A,B; priority/running dedup; 4 serial metadata-only children, 0 downloads");
        }
        finally
        {
            foreach(string name in expected)File.WriteAllText(Path.Combine(root,"release-"+name),"");
            service?.Dispose();
            // Cancellation kills managed children synchronously, but Windows
            // can briefly retain their open file handles while they exit.
            var cleanup=Stopwatch.StartNew();
            while(true)
            {
                try { Directory.Delete(root,true);break; }
                catch(IOException) when(cleanup.Elapsed<TimeSpan.FromSeconds(3)){await Task.Delay(20);}
                catch(UnauthorizedAccessException) when(cleanup.Elapsed<TimeSpan.FromSeconds(3)){await Task.Delay(20);}
            }
        }
    }

    private static void Require(bool condition,string error)
    {
        if(!condition)throw new InvalidOperationException("Preview scheduling test failed: "+error);
    }
}
