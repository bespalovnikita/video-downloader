using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class JobDiagnosticsTests
{
    private const string FixtureVariable="VD_DIAGNOSTIC_VERSION_FIXTURE";
    private const string VersionSecret="VERSION_STDERR_PRIVATE_CANARY";
    internal static bool TryRunFixture(string[] args)
    {
        string? mode=Environment.GetEnvironmentVariable(FixtureVariable);
        if(mode is null || args.Length!=1 || args[0] is not ("--version" or "-version"))return false;
        if(mode.StartsWith("stall|",StringComparison.Ordinal))
        {
            string marker=mode[6..];File.WriteAllText(marker+".tmp",Environment.ProcessId.ToString());File.Move(marker+".tmp",marker);
            Thread.Sleep(TimeSpan.FromSeconds(30));return true;
        }
        Console.Error.WriteLine(VersionSecret+@" C:\Users\Private\tool.exe");
        if(mode=="flood")Console.Out.Write(new string('x',20000));
        else Console.Out.WriteLine(mode=="normal"?"2026.10.14":VersionSecret);
        if(mode=="failed")Environment.ExitCode=1;
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-job-diagnostics-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await IdentityAndArchive(root);
            await CapturedCoordinator(root);
            await BoundsAndConcurrency(root);
            await ExportPrivacyAndCancellation(root);
            await VersionProcesses(root);
            Console.WriteLine("PASS: job diagnostics typed identity, real attempts/settings, bounded archives, private ZIP snapshots and owned version children");
        }
        finally{Directory.Delete(root,true);}
    }

    private static JobDiagnosticTarget Target(string url="https://example.org/watch?v=Selected&token=URL_PRIVATE_CANARY")=>
        JobDiagnosticTarget.Capture(new DownloadItem{Url=url,Title="Selected job",Status="Failed"});
    private static JournalRecord Record(JobDiagnosticTarget target,string name="sample",int invocation=1,string session="session-a",
        JournalLevel level=JournalLevel.Information,string? message=null,JobDiagnosticSettings? settings=null)=>new(
            level,"download",message??"typed diagnostic",session,target.Job,"T1",target.Identity,name,1,invocation,settings);
    private static Task<IReadOnlyList<DiagnosticComponentVersion>> Versions(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<DiagnosticComponentVersion>>(new[]{
            new DiagnosticComponentVersion("Video Downloader","Доступен","2.0.48"),
            new DiagnosticComponentVersion("yt-dlp","Доступен","2026.10.14"),
            new DiagnosticComponentVersion("FFmpeg","Не установлен","")});
    }

    private static async Task IdentityAndArchive(string root)
    {
        var target=Target();var other=Target("https://example.org/watch?v=selected&token=URL_PRIVATE_CANARY");
        Require(target.Identity!=other.Identity && target.Job==target.Identity[..12],"Exact URL case/query identity is preserved");
        // Simulate a legacy 48-bit hash collision without requiring 2^24 jobs.
        var collision=target with {Identity=target.Identity[..12]+new string(target.Identity[12]=='a'?'b':'a',52)};
        string directory=Path.Combine(root,"identity");
        var journal=new DiagnosticJournal(directory,JournalProfile.Detailed);
        journal.Write(Record(target,"job_queued",message:"selected-marker"));
        journal.Write(Record(collision,message:"COLLIDING_OTHER_JOB"));
        journal.Write(Record(other,message:"OTHER_CASE_JOB"));
        journal.Write(Record(target,"attempt_start",invocation:1));
        journal.Write(Record(target,"attempt_start",invocation:2));
        journal.Write(Record(target,"attempt_start",invocation:1,session:"session-b"));
        journal.Write(new JournalRecord(JournalLevel.Error,"download","legacy untyped selected-marker",Job:target.Job));
        await journal.DisposeAsync();
        var service=new JobDiagnosticsService(journal,"missing",Versions);
        var snapshot=await service.ReadAsync(target,CancellationToken.None);
        Require(snapshot.Events.Count==4 && snapshot.Events.All(entry=>entry.Identity==target.Identity),
            "Real disk and memory records deduplicate and exclude full-digest collisions, unrelated URLs and untyped old messages");
        Require(snapshot.Events.Count(entry=>entry.Event=="attempt_start")==3 && snapshot.Events.Select(entry=>(entry.Session,entry.Invocation)).Distinct().Count()==3,
            "All available sessions/invocations are typed, not inferred from natural diagnostic strings");
        long queued=snapshot.Events.First(entry=>entry.Event=="job_queued").Sequence;
        Require(snapshot.Events.Select(entry=>entry.Sequence).SequenceEqual(snapshot.Events.Select(entry=>entry.Sequence).Order()),"Within-journal sequence is stable");
        journal.Clear();
        var diskOnly=await service.ReadAsync(target,CancellationToken.None);
        Require(diskOnly.Events.Count==4 && diskOnly.Events.Any(entry=>entry.Sequence==queued),"Clear does not remove indexed records already on disk");
        File.AppendAllText(journal.StructuredFilePath,"{broken\n"+new string('x',JobDiagnosticCodec.MaxLineBytes+1)+"\n");
        var malformed=await service.ReadAsync(target,CancellationToken.None);
        Require(malformed.Events.Count==4 && malformed.MalformedRecords==2 && malformed.Truncated,
            "Malformed/oversized real JSONL records are bounded and disclosed while valid records remain available");
        using var json=JsonDocument.Parse(File.ReadLines(journal.StructuredFilePath).First());
        Require(json.RootElement.GetProperty("identity").GetString()==target.Identity && json.RootElement.GetProperty("invocation").GetInt32()==1,
            "On-disk JSONL carries explicit identity and invocation fields");
        await using var errors=new DiagnosticJournal(Path.Combine(root,"errors-only"),JournalProfile.Errors);
        errors.Write(Record(target,"attempt_start"));errors.Write(Record(target,"diagnostic",level:JournalLevel.Error));
        var filtered=await new JobDiagnosticsService(errors,"missing").ReadAsync(target,CancellationToken.None);
        Require(filtered.Events.Count==1 && filtered.Events[0].Level==JournalLevel.Error && filtered.Warnings.Any(value=>value.Contains("Начальная запись")),
            "Profiles limit actual captured history; missing attempts are not invented");
    }

    private static async Task CapturedCoordinator(string root)
    {
        string directory=Path.Combine(root,"coordinator");Directory.CreateDirectory(directory);
        string? previous=Environment.GetEnvironmentVariable("VD_NATIVE_CONTRACT_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_CONTRACT_LOG",directory);
        try
        {
            var data=new AppPersistence(Path.Combine(directory,"profile"));
            var options=new DownloadOptions{Quality="1080",Codec="H264",Container="MP4",Threads="1",Fragments="3",Output=Path.Combine(directory,"output"),
                FilenameTemplate="PRIVATE_TEMPLATE_CANARY.%(ext)s",Cookies="",EmbedMetadata=false,EmbedChapters=false};
            var row=new DownloadItem{Url="https://native-contract.invalid/diagnostic-settings"};
            var format=new MediaFormat("137","mp4",1920,1080,30,"avc1","none","",null,true);
            var audio=new MediaFormat("140","m4a",0,0,0,"none","mp4a","en",null,true);
            row.Selection=new DownloadSelection(row.Url,new DownloadSection(1.25,3.75),Format:format,AudioFormat:audio);
            await using var journal=new DiagnosticJournal(Path.Combine(directory,"logs"),JournalProfile.Detailed);
            await using(var coordinator=new DownloadCoordinator(TestExecutable(),data,options,_=>{},_=>{},journal.Write))
            {
                Require(coordinator.Add(row),"Real coordinator accepts diagnostic row");
                options.Quality="720";options.Container="MKV";row.Selection=null;
                await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            Require(row.Status=="Completed" && File.Exists(row.Path) && !data.ReadHistory().ContainsKey(row.Url),
                "Optional diagnostics preserve actual partial publication without claiming full-URL history");
            var snapshot=await new JobDiagnosticsService(journal,TestExecutable(),Versions).ReadAsync(JobDiagnosticTarget.Capture(row),CancellationToken.None);
            var admitted=snapshot.Events.Single(entry=>entry.Event=="job_queued");
            var started=snapshot.Events.Single(entry=>entry.Event=="attempt_start");
            Require(admitted.Settings is {Quality:"1080",Container:"MP4",VideoFormat:"137",AudioFormat:"140"} &&
                started.Settings is {Quality:"manual",Container:"MP4",VideoFormat:"137",AudioFormat:"140"} && started.Invocation==1 && started.Attempt==1 &&
                admitted.Settings.Ranges.Single() is {Start:1.25,End:3.75} && started.Settings.Ranges.Single() is {Start:1.25,End:3.75},
                "Typed settings capture the admitted immutable options/row selection and actual invocation, not later UI edits");
            string serialized=JsonSerializer.Serialize(started.Settings);
            Require(!serialized.Contains("PRIVATE_TEMPLATE_CANARY") && !serialized.Contains(directory) && !serialized.Contains("Cookies"),
                "The settings schema excludes output, cookies and filename template");
            var mergedRow=new DownloadItem{Url="https://native-contract.invalid/diagnostic-merged"};
            mergedRow.Selection=new DownloadSelection(mergedRow.Url,new DownloadSection(1.25,3.75),Format:format,AudioFormat:audio)
            {Sections=new DownloadSectionSet([new(1.25,3.75),new(5,6)]),FragmentOutputMode="Merged",AllowMergeReencode=true};
            await using(var coordinator=new DownloadCoordinator(TestExecutable(),data,new DownloadOptions{Threads="1",Output=Path.Combine(directory,"merged-output")},_=>{},_=>{},journal.Write))
            {
                Require(coordinator.Add(mergedRow),"A real merged row is admitted before execution");
                mergedRow.Selection=mergedRow.Selection with {FragmentOutputMode="Separate",AllowMergeReencode=false,Sections=null};
                var merged=await new JobDiagnosticsService(journal,"missing").ReadAsync(JobDiagnosticTarget.Capture(mergedRow),CancellationToken.None);
                var captured=merged.Events.Single(entry=>entry.Event=="job_queued").Settings;
                Require(captured is {FragmentOutputMode:"Merged",AllowMergeReencode:true,Ranges.Count:2} &&
                    captured.Ranges[0] is {Start:1.25,End:3.75} && captured.Ranges[1] is {Start:5,End:6},
                    "Admitted merge mode, reencode consent and ordered ranges remain immutable after editing the row");
                coordinator.Cancel(mergedRow);await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Require(!data.ReadHistory().ContainsKey(mergedRow.Url),"Cancelling a diagnostics-only merged fixture never records a full-URL success");
            }
            var retryRow=new DownloadItem{Url="https://native-contract.invalid/quality-retry"};
            var retryOptions=new DownloadOptions{Quality="2160",Threads="1",Output=Path.Combine(directory,"retry-output"),EmbedMetadata=false,EmbedChapters=false};
            await using var errors=new DiagnosticJournal(Path.Combine(directory,"retry-logs"),JournalProfile.Errors);
            await using(var coordinator=new DownloadCoordinator(TestExecutable(),data,retryOptions,_=>{},_=>{},errors.Write))
            {Require(coordinator.Add(retryRow),"Fallback row admitted");await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));}
            var retry=await new JobDiagnosticsService(errors,"missing").ReadAsync(JobDiagnosticTarget.Capture(retryRow),CancellationToken.None);
            Require(retryRow.Status=="Completed" && data.ReadHistory().ContainsKey(retryRow.Url) && !retry.Events.Any(entry=>entry.Event=="attempt_start") &&
                retry.Events.Any(entry=>entry.Invocation==1 && entry.Attempt==1 && entry.Settings?.Quality=="2160") &&
                retry.Events.Any(entry=>entry.Invocation==2 && entry.Attempt==1 && entry.Settings?.Quality=="1440"),
                "Errors-only profile preserves actual failed invocation settings across quality fallback without inventing omitted start events");
        }
        finally{Environment.SetEnvironmentVariable("VD_NATIVE_CONTRACT_LOG",previous);}
    }

    private static async Task BoundsAndConcurrency(string root)
    {
        var target=Target();const int memory=40*1024,file=4096;
        var journal=new DiagnosticJournal(Path.Combine(root,"bounds"),JournalProfile.Detailed,
            new JournalLimits(MemoryEntries:100,MemoryBytes:memory,FileBytes:file,FileQueueEntries:5000));
        var service=new JobDiagnosticsService(journal,"missing",Versions);
        var producers=Task.WhenAll(Enumerable.Range(0,4).Select(worker=>Task.Run(()=>
        {
            for(int index=0;index<100;index++)
            {
                journal.Write(Record(target,message:$"worker={worker} number={index} "+new string('x',80)));
                Require(journal.PendingFileBytes<=memory*2,"One shared pending writer queue observes the combined byte bound");
            }
        })));
        var first=await service.ReadAsync(target,CancellationToken.None);await producers;await journal.DisposeAsync();
        int retained=Encoding.UTF8.GetByteCount(journal.Snapshot())+journal.SnapshotJobEvents().Sum(entry=>JobDiagnosticCodec.Encode(entry).Length+1);
        Require(retained<=memory,"Text plus structured memory share the original total budget");
        foreach(string suffix in new[]{"",".1",".2"})
        {
            long size=Size(Path.Combine(Path.GetDirectoryName(journal.FilePath)!,"journal"+suffix+".log"))+
                Size(Path.Combine(Path.GetDirectoryName(journal.FilePath)!,"job-events"+suffix+".jsonl"));
            Require(size<=file,"Paired rotated text/JSONL files share one disk budget");
        }
        var snapshot=await service.ReadAsync(target,CancellationToken.None);
        Require(snapshot.Events.Count>0 && snapshot.Events.Count<=JobDiagnosticsService.MaxEvents &&
            snapshot.Events.Select(entry=>(entry.Journal,entry.Sequence)).Distinct().Count()==snapshot.Events.Count,"Concurrent reads remain bounded and duplicate-free");
        Require(snapshot.Warnings.Any(value=>value.Contains("ротация")),"Archive availability is explicit after rotation");
        // A real oversized archive tests the independent reader cap, without huge live logging allocations.
        var large=new DiagnosticJournal(Path.Combine(root,"large"));await large.DisposeAsync();
        string id=Guid.NewGuid().ToString("N");
        var sample=JobDiagnosticCodec.Capture(Record(target),id,1,DateTimeOffset.UtcNow)!;
        using(var stream=new StreamWriter(large.StructuredFilePath,false,new UTF8Encoding(false)))
            for(int index=1;index<=JobDiagnosticsService.MaxEvents+50;index++)stream.WriteLine(Encoding.UTF8.GetString(JobDiagnosticCodec.Encode(sample with {Sequence=index})));
        var limited=await new JobDiagnosticsService(large,"missing").ReadAsync(target,CancellationToken.None);
        Require(limited.Events.Count==JobDiagnosticsService.MaxEvents && limited.OmittedEvents==50 && limited.Truncated && limited.Events[0].Sequence==51,
            "Reader pages retain the latest 2000 actual records and count omissions");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await MustCancel(()=>service.ReadAsync(target,cancelled.Token),"Cancelled reads stop without changing journal or downloads");
        static long Size(string path)=>File.Exists(path)?new FileInfo(path).Length:0;
    }

    private static async Task ExportPrivacyAndCancellation(string root)
    {
        string directory=Path.Combine(root,"export");Directory.CreateDirectory(directory);
        const string diagnostic="ERROR: HTTP Error 403 https://name:USERINFO_CANARY@example.org/watch?v=Selected&signature=SIGNED_CANARY#FRAGMENT_CANARY\n"+
            "Authorization: Bearer HEADER_CANARY\n"+"cookie: COOKIE_CANARY\n"+
            "file C:\\Users\\Имя Фамилия\\WINDOWS_PATH_CANARY.mp4\n"+
            "file \\\\server\\private share\\UNC_PATH_CANARY.mp4\n"+
            "path=//server/private share/FORWARD_UNC_CANARY.mp4\n"+
            "file /tmp/Приватная папка/POSIX_PATH_CANARY.mp4\n"+
            "file:///C:/Users/FILE_URI_CANARY.mp4\ncommand line: ffmpeg -i relative-ARGV_CANARY.mp4\n"+
            "Command '['ffmpeg', '-i', 'RELATIVE_COMMAND_CANARY']' returned non-zero status";
        var row=new DownloadItem{Url="https://example.org/watch?v=Selected&private=URL_PRIVATE_CANARY",Title="Chosen",RawError=diagnostic,Status="Failed"};
        var target=JobDiagnosticTarget.Capture(row);
        var journal=new DiagnosticJournal(Path.Combine(directory,"logs"),JournalProfile.Detailed);
        var settings=JobDiagnosticSettings.Capture(new DownloadOptions{Output="OUTPUT_SETTING_CANARY",Cookies="COOKIE_SETTING_CANARY",FilenameTemplate="TEMPLATE_SETTING_CANARY"},
            new DownloadSelection(row.Url,new DownloadSection(1.25,3.75)));
        journal.Write(Record(target,"job_queued",message:diagnostic,settings:settings));await journal.DisposeAsync();
        Require(File.ReadAllText(journal.FilePath).Contains("WINDOWS_PATH_CANARY"),"Existing local text journal remains readable, so bundle privacy must come from its dedicated whitelist");
        var service=new JobDiagnosticsService(journal,"missing",Versions);
        var snapshot=await service.ReadAsync(target,CancellationToken.None);
        string zip=Path.Combine(directory,"selected.zip");var result=await service.ExportAsync(target,snapshot,zip,CancellationToken.None);
        Require(result.Events==1 && result.Bytes>0,"A real ZIP reports its captured event count and actual bytes");
        using(var archive=ZipFile.OpenRead(zip))
        {
            Require(archive.Entries.Select(entry=>entry.FullName).Order().SequenceEqual(new[]{"error.txt","events.jsonl","journal.txt","manifest.json","settings.json"}),
                "The portable bundle contains only five fixed safe ZIP entry names");
            foreach(var entry in archive.Entries)
            {
                using var reader=new StreamReader(entry.Open());string text=await reader.ReadToEndAsync();
                foreach(string canary in new[]{"USERINFO_CANARY","SIGNED_CANARY","FRAGMENT_CANARY","HEADER_CANARY","COOKIE_CANARY","WINDOWS_PATH_CANARY","UNC_PATH_CANARY",
                    "FORWARD_UNC_CANARY","POSIX_PATH_CANARY","FILE_URI_CANARY","ARGV_CANARY","RELATIVE_COMMAND_CANARY","URL_PRIVATE_CANARY","OUTPUT_SETTING_CANARY","COOKIE_SETTING_CANARY","TEMPLATE_SETTING_CANARY"})
                    Require(!text.Contains(canary,StringComparison.Ordinal),"Bundle entry "+entry.FullName+" must omit private canary "+canary);
            }
            using var manifest=JsonDocument.Parse(archive.GetEntry("manifest.json")!.Open());
            Require(manifest.RootElement.GetProperty("eventCount").GetInt32()==1 && manifest.RootElement.GetProperty("components").GetArrayLength()==3,
                "Manifest identifies actual captured events and honest dependency availability");
        }
        byte[] before=File.ReadAllBytes(zip);
        await MustFail<IOException>(()=>service.ExportAsync(target,snapshot,zip,CancellationToken.None),"Default export never overwrites an existing destination");
        Require(before.SequenceEqual(File.ReadAllBytes(zip)),"An existing ZIP survives a refused overwrite");
        bool invoked=false;
        var slow=new JobDiagnosticsService(journal,"missing",async token=>{invoked=true;await Task.Delay(30000,token);return await Versions(token);});
        using(var cancellation=new CancellationTokenSource(100))
            await MustCancel(()=>slow.ExportAsync(target,snapshot,zip,cancellation.Token,overwrite:true),"Cancel interrupts dependency collection without replacing an existing ZIP");
        Require(invoked && before.SequenceEqual(File.ReadAllBytes(zip)) && !Directory.GetFiles(directory,".vd-diagnostics-*.tmp").Any(),
            "Cancelled optional export preserves destination and removes only owned staging files");
        using(var cancellation=new CancellationTokenSource())
        {
            var stoppedAfterVersions=new JobDiagnosticsService(journal,"missing",async token=>
            {var values=await Versions(token);cancellation.Cancel();return values;});
            await MustCancel(()=>stoppedAfterVersions.ExportAsync(target,snapshot,zip,cancellation.Token,overwrite:true),
                "Cancellation immediately before ZIP writing cleans the actual CreateNew temporary");
            Require(before.SequenceEqual(File.ReadAllBytes(zip)) && !Directory.GetFiles(directory,".vd-diagnostics-*.tmp").Any(),
                "Cancelled ZIP creation cannot replace the earlier destination or retain an owned temporary");
        }
        string blocked=Path.Combine(directory,"not-a-folder");File.WriteAllText(blocked,"unrelated sentinel");
        await MustFail<IOException>(()=>service.ExportAsync(target,snapshot,Path.Combine(blocked,"export.zip"),CancellationToken.None),
            "An unavailable destination is an optional export error");
        Require(File.ReadAllText(blocked)=="unrelated sentinel","Export error preserves unrelated existing data");
        var changed=target with {Identity=new string('0',64),Job=new string('0',12)};
        await MustFail<InvalidDataException>(()=>service.ExportAsync(changed,snapshot,Path.Combine(directory,"wrong.zip"),CancellationToken.None),"Snapshot identity cannot be replaced by a later row selection");
        // Snapshot remains fixed even when later records arrive for the same job.
        var later=new DiagnosticJournal(Path.Combine(directory,"later"));
        later.Write(Record(target,message:"after-snapshot"));await later.DisposeAsync();
        Require(snapshot.Events.Count==1 && snapshot.Events.All(entry=>!entry.Message.Contains("after-snapshot")),"Exports use the captured file snapshot");
        string untrusted=diagnostic+"\n[Warning/forged] fake";
        string safe=DiagnosticPrivacy.Text(untrusted,true);
        Require(DiagnosticPrivacy.Text(safe,true)==safe,"Privacy masking is idempotent for copy, view and export");
    }

    private static async Task VersionProcesses(string root)
    {
        string? previous=Environment.GetEnvironmentVariable(FixtureVariable);
        try
        {
            Environment.SetEnvironmentVariable(FixtureVariable,"normal");
            var good=await JobDiagnosticsService.ProbeVersionAsync("yt-dlp",TestExecutable(),"--version",CancellationToken.None);
            Require(good.Version=="2026.10.14" && !JsonSerializer.Serialize(good).Contains(VersionSecret),"Actual owned version process retains only a recognized version, never stderr");
            foreach(string mode in new[]{"invalid","failed","flood"})
            {
                Environment.SetEnvironmentVariable(FixtureVariable,mode);
                var unknown=await JobDiagnosticsService.ProbeVersionAsync("yt-dlp",TestExecutable(),"--version",CancellationToken.None);
                Require(unknown.Version.Length==0 && !JsonSerializer.Serialize(unknown).Contains(VersionSecret),"Invalid/failed/oversized child version output is safely unavailable: "+mode);
            }
            var missing=await JobDiagnosticsService.ProbeVersionAsync("yt-dlp",Path.Combine(root,"missing.exe"),"--version",CancellationToken.None);
            Require(missing.Status=="Не установлен" && missing.Version.Length==0,"Missing dependencies are reported without invented versions");
            string pidPath=Path.Combine(root,"version-child.pid");
            Environment.SetEnvironmentVariable(FixtureVariable,"stall|"+pidPath);
            using var cancellation=new CancellationTokenSource();
            var pending=JobDiagnosticsService.ProbeVersionAsync("yt-dlp",TestExecutable(),"--version",cancellation.Token);
            var deadline=Stopwatch.StartNew();while(!File.Exists(pidPath) && deadline.Elapsed<TimeSpan.FromSeconds(10))await Task.Delay(20);
            Require(File.Exists(pidPath),"Cancellation test observes the actual owned version child");
            int pid=int.Parse(File.ReadAllText(pidPath));cancellation.Cancel();
            await MustCancel(()=>pending,"Cancellation propagates after the owned child and its output pipes are joined");
            bool alive;try{using var child=Process.GetProcessById(pid);alive=!child.HasExited;}catch(ArgumentException){alive=false;}
            Require(!alive,"Cancelled version probe cannot leave its process running");
            File.Delete(pidPath);
            var timed=await JobDiagnosticsService.ProbeVersionAsync("yt-dlp",TestExecutable(),"--version",CancellationToken.None);
            Require(timed.Status=="Таймаут проверки версии" && timed.Version.Length==0 && File.Exists(pidPath),
                "A real stalled version child reaches the bounded deadline and reports unavailable data");
            pid=int.Parse(File.ReadAllText(pidPath));
            try{using var child=Process.GetProcessById(pid);alive=!child.HasExited;}catch(ArgumentException){alive=false;}
            Require(!alive,"Timed-out version child is joined before reporting its status");
        }
        finally{Environment.SetEnvironmentVariable(FixtureVariable,previous);}
    }

    private static string TestExecutable()=>Path.Combine(AppContext.BaseDirectory,typeof(JobDiagnosticsTests).Assembly.GetName().Name+(OperatingSystem.IsWindows()?".exe":""));
    private static async Task MustCancel(Func<Task> action,string reason)
    {try{await action();}catch(OperationCanceledException){return;}throw new InvalidOperationException(reason);}
    private static async Task MustFail<T>(Func<Task> action,string reason) where T:Exception
    {try{await action();}catch(T){return;}throw new InvalidOperationException(reason);}
    private static void Require(bool condition,string reason)
    {if(!condition)throw new InvalidOperationException("Job diagnostics test failed: "+reason);}
}
