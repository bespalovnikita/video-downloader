using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using VideoDownloader.Native;

internal static class RemoteCoordinatorTests
{
    private const string Prefix="https://remote-coordinator.invalid/";
    private const string ControlVariable="VD_REMOTE_COORDINATOR_CONTROL";

    internal static bool TryRunFixture(string[] args)
    {
        string? url=args.LastOrDefault();
        if(url?.StartsWith(Prefix,StringComparison.Ordinal)!=true)return false;
        string name=url[Prefix.Length..];
        Require(name.Length>0 && name.All(c=>char.IsAsciiLetterOrDigit(c) || c=='-'),"Unexpected fixture URL.");
        string control=Environment.GetEnvironmentVariable(ControlVariable) ??
            throw new InvalidOperationException("Missing fixture control directory.");
        string template=args[Array.IndexOf(args,"-o")+1];
        Require(args.Contains("-N") && args[Array.IndexOf(args,"-N")+1]=="8" &&
            args.Contains("--http-chunk-size") && args[Array.IndexOf(args,"--http-chunk-size")+1]=="4M" &&
            args.Contains("--limit-rate") && args[Array.IndexOf(args,"--limit-rate")+1]=="5M" &&
            Path.GetFileName(template)=="%(id)s.%(ext)s","Local workers must still use the chosen additional options.");
        string media=Path.Combine(Path.GetDirectoryName(template)!,name+".mp4");
        File.WriteAllText(media,"LOCAL FIXTURE MEDIA");
        File.WriteAllText(Path.Combine(control,name+"."+Environment.ProcessId+".starts"),"started");
        while(!File.Exists(Path.Combine(control,name+".release")))Thread.Sleep(20);
        Console.WriteLine("__VD_FILE__:"+media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-remote-coordinator-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previous=Environment.GetEnvironmentVariable(ControlVariable);
        Environment.SetEnvironmentVariable(ControlVariable,root);
        try { await SharesQueueAsync(root); }
        finally
        {
            Environment.SetEnvironmentVariable(ControlVariable,previous);
            Directory.Delete(root,true);
        }
        Console.WriteLine("PASS: shared Windows queue, four VPS slots, local fallback, assigned result recovery, and verified publication before acknowledgement");
    }

    private static async Task SharesQueueAsync(string root)
    {
        var data=new AppPersistence(Path.Combine(root,"profile"));
        var options=new DownloadOptions
        {
            Output=Path.Combine(root,"output"),Threads="1",Fragments="8",UseHttpChunks=true,RateLimit="5M",
            FilenameTemplate="%(id)s.%(ext)s"
        };
        var local=Row("local");
        var first=Row("vps-first");
        var second=Row("vps-second");
        var collection=Row("vps-declined");
        var recovered=Row("vps-recovered");
        recovered.Selection=new DownloadSelection(recovered.Url,new DownloadSection(1,2),DurationSeconds:5);
        var invalid=Row("vps-invalid");
        var initial=new[]{local,first,second,collection,recovered};
        var rows=initial.Append(invalid).ToArray();
        var remote=new FixtureClient(data,root,recovered,collection,invalid);
        var remoteSpeeds=new ConcurrentDictionary<string,string>(StringComparer.Ordinal);
        string executable=Path.Combine(AppContext.BaseDirectory,typeof(RemoteCoordinatorTests).Assembly.GetName().Name+
            (OperatingSystem.IsWindows()?".exe":""));
        await using var engine=new DownloadCoordinator(executable,data,options,row=>
        {
            if(row.Speed.Length>0 && row.Slot.StartsWith("VPS ",StringComparison.Ordinal))remoteSpeeds[row.Url]=row.Speed;
        },_=>{},null,remote);
        foreach(var row in initial)Require(engine.Add(row),"The initial mixed queue must admit every row.");
        Task run=engine.RunAsync();
        try
        {
            await UntilAsync(()=>LocalStarts(root,local)==1 && remote.Active==4,
                "One real local child and four server tasks must be held concurrently.");
            Require(remote.Starts(first)==1 && remote.Starts(second)==1 &&
                remote.Starts(collection)==1 && remote.Starts(recovered)==1 &&
                !remote.Supports(recovered,options) && local.Slot=="T1" &&
                initial.Skip(1).All(row=>row.Slot.StartsWith("VPS ",StringComparison.Ordinal)),
                "The stored assignment must reach a server slot despite the changed selection.");
            Require(engine.Add(invalid),"The running session must accept another queued task.");
            await Task.Delay(150);
            Require(remote.Starts(invalid)==0 && LocalStarts(root,invalid)==0,
                "A fifth server task must wait while all four slots and the local worker are occupied.");

            remote.Release(collection);
            await UntilAsync(()=>remote.Starts(invalid)==1 && collection.Status=="Queued",
                "A confirmed server refusal must release its slot and queue one local attempt.");
            Require(LocalStarts(root,collection)==0 && engine.Outstanding==rows.Length,
                "Fallback must obey the local worker cap and retain its outstanding admission.");
            ReleaseLocal(root,local);
            await UntilAsync(()=>LocalStarts(root,collection)==1,
                "The local attempt must start after the ordinary worker becomes available.");
            ReleaseLocal(root,collection);
            foreach(var row in new[]{first,second,recovered,invalid})remote.Release(row);
            await run.WaitAsync(TimeSpan.FromSeconds(25));

            var completed=rows.Where(row=>!ReferenceEquals(row,invalid)).ToArray();
            var history=data.ReadHistoryLibrary().Entries;
            Require(completed.All(row=>row.Status=="Completed" && row.AttemptFinished && File.Exists(row.Path)) &&
                engine.Outstanding==0 && engine.Completed==completed.Length && history.Count==completed.Length &&
                history.Select(result=>result.Url).ToHashSet(StringComparer.Ordinal).SetEquals(completed.Select(row=>row.Url)),
                "Every verified publication must finish through the common success and history owner once.");
            Require(invalid.Status=="Failed" && invalid.AttemptFinished && invalid.Path.Length==0 && invalid.StageDir.Length==0,
                "A complete transfer containing invalid media must fail local verification and clean its own staging.");
            Require(rows.All(row=>row.Speed.Length==0) && new[]{first,second,recovered,invalid}.All(row=>
                remoteSpeeds.GetValueOrDefault(row.Url)=="1.5 MiB/s"),
                "Windows rows must display the VPS-to-PC rate while receiving and clear it when reception ends.");
            Require(recovered.CompletedSelection is null && !recovered.IsPartialResult && recovered.Selection?.IsPartial==true,
                "The recovered full result must not inherit the row's later fragment selection.");
            Require(rows.All(row=>remote.Starts(row)==(ReferenceEquals(row,local)?0:1)) &&
                rows.All(row=>LocalStarts(root,row)==(ReferenceEquals(row,local)||ReferenceEquals(row,collection)?1:0)),
                "Stale queue tokens must never introduce a second claim or a local download of a server result.");
            Require(remote.Peak==4 && rows.All(row=>remote.Acknowledgements(row)==
                (ReferenceEquals(row,first)||ReferenceEquals(row,second)||ReferenceEquals(row,recovered)?1:0)),
                "Only valid and locally published server results may be acknowledged.");
        }
        finally
        {
            engine.Stop();
            foreach(var row in rows)
            {
                ReleaseLocal(root,row);
                remote.Release(row);
            }
            await run.WaitAsync(TimeSpan.FromSeconds(25));
        }
    }

    private sealed class FixtureClient(AppPersistence data,string root,DownloadItem recovered,DownloadItem collection,DownloadItem invalid)
        :IRemoteDownloadClient
    {
        private readonly ConcurrentDictionary<DownloadItem,TaskCompletionSource> _gates=new();
        private readonly ConcurrentDictionary<DownloadItem,int> _starts=new();
        private readonly ConcurrentDictionary<DownloadItem,int> _acknowledgements=new();
        private readonly ConcurrentDictionary<string,DownloadItem> _jobs=new(StringComparer.Ordinal);
        private readonly object _sync=new();
        private volatile bool _prepared;
        private int _active,_peak;

        internal int Active=>Volatile.Read(ref _active);
        internal int Peak=>Volatile.Read(ref _peak);
        public bool IsAvailable=>_prepared;
        internal int Starts(DownloadItem row)=>_starts.GetValueOrDefault(row);
        internal int Acknowledgements(DownloadItem row)=>_acknowledgements.GetValueOrDefault(row);
        public Task PrepareAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            _prepared=true;
            return Task.CompletedTask;
        }
        public bool Supports(DownloadItem row,DownloadOptions options)=>
            row.Url.StartsWith(Prefix+"vps-",StringComparison.Ordinal) && RemoteDownloadRequest.For(row,options) is not null;
        public bool OwnsAssignment(DownloadItem row)=>_prepared && ReferenceEquals(row,recovered);
        internal void Release(DownloadItem row)=>Gate(row).TrySetResult();
        private TaskCompletionSource Gate(DownloadItem row)=>_gates.GetOrAdd(row,_=>new(TaskCreationOptions.RunContinuationsAsynchronously));

        public async Task<RemoteDownloadArtifact> DownloadAsync(DownloadItem row,DownloadOptions options,string stage,
            Func<CancellationToken,Task> waitForResume,Action<RemoteDownloadProgress> progress,CancellationToken token)
        {
            Require(_prepared,"The server worker must use the prepared client.");
            _starts.AddOrUpdate(row,1,(_,count)=>count+1);
            lock(_sync) { _active++;_peak=Math.Max(_peak,_active); }
            try
            {
                progress(new(RemoteDownloadStage.Downloading,25,0,0));
                await Gate(row).Task.WaitAsync(token);
                await waitForResume(token);
                if(ReferenceEquals(row,collection))throw new RemoteDownloadUnsupportedException();
                string path=Path.Combine(stage,Name(row)+".wav");
                if(ReferenceEquals(row,invalid))File.WriteAllText(path,"RECEIVED INVALID MEDIA");
                else WriteWave(path);
                long size=new FileInfo(path).Length;
                progress(new(RemoteDownloadStage.Receiving,100,size,size,1_572_864));
                string id=Guid.NewGuid().ToString("N");
                _jobs[id]=row;
                return new(id,path,Name(row),size);
            }
            finally{lock(_sync)_active--;}
        }

        public Task AcknowledgeAsync(RemoteDownloadArtifact artifact,string publishedPath,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var row=_jobs[artifact.JobId];
            Require(row.Status=="Completed" && row.Path==publishedPath && row.OwnedPaths.SequenceEqual(new[]{publishedPath}) &&
                OwnedFiles.Inside(Path.Combine(root,"output"),publishedPath) && new FileInfo(publishedPath).Length==artifact.Size &&
                !File.Exists(artifact.MediaPath) && data.ReadHistory().ContainsKey(row.Url),
                "Receipt must follow the common checked publication, ownership manifest, and success history.");
            _acknowledgements.AddOrUpdate(row,1,(_,count)=>count+1);
            return Task.CompletedTask;
        }

        public void Dispose() { }
    }

    private static void WriteWave(string path)
    {
        const int sampleRate=8000,bytes=1600;
        using var output=new BinaryWriter(File.Create(path));
        output.Write(Encoding.ASCII.GetBytes("RIFF"));output.Write(36+bytes);
        output.Write(Encoding.ASCII.GetBytes("WAVEfmt "));output.Write(16);
        output.Write((short)1);output.Write((short)1);output.Write(sampleRate);output.Write(sampleRate*2);
        output.Write((short)2);output.Write((short)16);
        output.Write(Encoding.ASCII.GetBytes("data"));output.Write(bytes);output.Write(new byte[bytes]);
    }

    private static DownloadItem Row(string name)=>new(){Url=Prefix+name};
    private static string Name(DownloadItem row)=>row.Url[Prefix.Length..];
    private static int LocalStarts(string root,DownloadItem row)=>Directory.GetFiles(root,Name(row)+".*.starts").Length;
    private static void ReleaseLocal(string root,DownloadItem row)=>File.WriteAllText(Path.Combine(root,Name(row)+".release"),"");
    private static async Task UntilAsync(Func<bool> condition,string message)
    {
        var elapsed=Stopwatch.StartNew();
        while(!condition() && elapsed.Elapsed<TimeSpan.FromSeconds(15))await Task.Delay(20);
        Require(condition(),message);
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Remote coordinator test: "+message);}
}
