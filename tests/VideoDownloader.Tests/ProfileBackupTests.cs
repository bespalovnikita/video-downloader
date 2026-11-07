using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class ProfileBackupTests
{
    private const string PublicUrl="https://example.invalid/Watch?v=AbC&list=PL1&t=40";
    private const string Secret="BACKUP_PRIVATE_CANARY_73054";
    private static readonly CancellationToken None=CancellationToken.None;

    internal static bool TryRunFixture(string[] args)
    {
        if(args.Length!=5 || args[0]!="--profile-backup-crash")return false;
        var storage=new AppPersistence(args[1]);
        var service=new WindowsProfileBackup(storage,phase=>
        {
            if(phase!=args[3])return;
            File.WriteAllText(args[4],"ready");
            Thread.Sleep(Timeout.Infinite);
        });
        var archive=service.LoadAsync(args[2],None).GetAwaiter().GetResult();
        using var plan=service.PrepareRestoreAsync(archive,new DownloadOptions{Output="local-output"},[],BackupSubscriptionsPolicy.Replace,None).GetAwaiter().GetResult();
        service.RestoreAsync(plan,None).GetAwaiter().GetResult();
        throw new InvalidOperationException("The crash fixture did not reach its requested publication boundary.");
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-profile-backup-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var source=NewProfile(root,"source");
            var options=new DownloadOptions{Quality="1080",Threads="3",Output=Path.Combine(root,Secret),Cookies=Path.Combine(root,Secret+".txt"),WatchClipboard=true};
            source.Save([],options);
            WriteSubscriptions(source,[Source("collision",PublicUrl),Source("case",PublicUrl.Replace("Watch","watch",StringComparison.Ordinal)),
                Source("private","https://user:"+Secret+"@example.invalid/secret")],
                [Item("collision","https://example.invalid/video?id=1","Queued"),
                 Item("collision","https://example.invalid/video?token="+Secret),
                 Item("private","https://example.invalid/hidden")]);
            var sourceFile=JsonNode.Parse(File.ReadAllBytes(SubscriptionPath(source)))!.AsObject();
            var first=sourceFile["Subscriptions"]![0]!;
            first["Title"]="C:\\Users\\"+Secret+" token="+Secret;
            first["FeedUrl"]="https://example.invalid/feed?signature="+Secret;
            first["LastError"]="password="+Secret;
            File.WriteAllText(SubscriptionPath(source),sourceFile.ToJsonString());
            AppendHistory(source,PublicUrl,"Imported title",DateTimeOffset.Parse("2025-01-02T00:00:00Z"),[Path.Combine(root,Secret+".mp4")]);
            AppendHistory(source,"https://example.invalid/history?access_token="+Secret,"private",null,[]);
            AppendHistory(source,PublicUrl.Replace("Watch","watch",StringComparison.Ordinal),"lower case",null,[]);
            var service=new WindowsProfileBackup(source);
            var archive=await service.CreateAsync(options,None);
            Require(archive.Summary is {Sources:2,Items:1,HistoryEntries:2,PrivateSourcesSkipped:1,PrivateItemsSkipped:2,PrivateHistorySkipped:1,PrivateFeedsSkipped:1},"Unsafe addresses must be omitted with explicit group counts");
            Require(archive.Summary.SanitizedFields>0,"Private descriptive text is counted when sanitized");
            var entries=ReadZip(archive.ArchiveBytes);
            Require(entries.Count==4,"The portable ZIP has exactly its manifest and three allowlisted sections");
            string payload=string.Join("\n",entries.Values.Select(Encoding.UTF8.GetString));
            foreach(string excluded in new[]{Secret,"\"OwnedPaths\":","\"OutputPaths\":","\"OutputRoot\":","\"WatchClipboard\":","\"Cookies\":","\"Engine\":","\"Queue\":","\"LastError\":"})
                Require(!payload.Contains(excluded,StringComparison.Ordinal),"Archive must exclude private/local field "+excluded);
            var portable=JsonNode.Parse(entries["subscriptions.json"])!;
            Require(portable["Sources"]![0]!["Url"]!.GetValue<string>()==PublicUrl &&
                portable["Sources"]![1]!["Url"]!.GetValue<string>()==PublicUrl.Replace("Watch","watch",StringComparison.Ordinal),"Exact URL identity and public parameters survive export");
            int hidden=0;
            int pathIndex=0;
            foreach(string path in new[]{"path=/etc/"+Secret,"file=/opt/Папка с пробелами/"+Secret,"['/custom/"+Secret+"']","\"/данные/"+Secret+"\"","path=//server/private share/"+Secret})
            {
                Require(!BackupPrivacy.Text(path,4096,ref hidden).Contains(Secret,StringComparison.Ordinal) && !BackupPrivacy.IsSafeText(path),"Arbitrary absolute paths including forward-slash UNC must be private");
                AppendHistory(source,"https://example.invalid/pathprivacy?id="+(pathIndex++),path,null,[]);
            }
            Require(BackupPrivacy.Text(PublicUrl,4096,ref hidden)==PublicUrl && BackupPrivacy.IsSafeText(PublicUrl),"Descriptive public HTTP URLs are not mistaken for local paths");
            AppendHistory(source,"https://example.invalid/pathprivacy","path=/etc/"+Secret,null,[]);
            var pathArchive=await service.CreateAsync(options,None);
            Require(!string.Join("\n",ReadZip(pathArchive.ArchiveBytes).Values.Select(Encoding.UTF8.GetString)).Contains(Secret,StringComparison.Ordinal),"Actual exported ZIP removes arbitrary local path canaries");
            string archivePath=Path.Combine(root,"portable.vdbackup");await service.SaveAsync(archive,archivePath,None);
            await Refuses(()=>service.SaveAsync(archive,archivePath,None),"Save must not overwrite an existing file");
            Require((await service.LoadAsync(archivePath,None)).Summary==archive.Summary,"A real saved ZIP round-trips its summary");
            await InvalidArchives(root,service,archive);
            await RestoreMerge(root,archive);
            await RestoreReplaceAndStale(root,archive);
            await RollbackAndLiveOwner(root,archive);
            await WindowsProtectedTargets(root,archive);
            await CrashRecovery(root,archivePath,archive);
            await DamagedAndForeign(root,archive);
            Console.WriteLine("PASS: portable profile ZIP privacy/bounds/checksums, exact URLs, merge/replace, queue/path isolation, durable rollback, live owner and killed-process recovery");
        }
        finally{Directory.Delete(root,true);}
    }

    private static async Task InvalidArchives(string root,WindowsProfileBackup service,ProfileBackupArchive archive)
    {
        var original=ReadZip(archive.ArchiveBytes);int number=0;
        async Task Invalid(Dictionary<string,byte[]> files,string reason,Action<ZipArchive>? extra=null)
        {
            string path=Path.Combine(root,"invalid-"+(number++)+".zip");
            File.WriteAllBytes(path,Zip(files,extra));
            await Refuses(()=>service.LoadAsync(path,None),reason);
        }
        async Task Mutate(string entry,Func<byte[],byte[]> mutate,string reason)
        {
            var files=original.ToDictionary(pair=>pair.Key,pair=>pair.Value.ToArray());files[entry]=mutate(files[entry]);
            if(entry!="manifest.json")Rehash(files,entry);
            await Invalid(files,reason);
        }
        await Invalid(original,"Extra traversal entry rejects the entire archive",zip=>zip.CreateEntry("../outside.json"));
        await Invalid(original,"Duplicate entries reject the entire archive",zip=>zip.CreateEntry("settings.json"));
        var missing=new Dictionary<string,byte[]>(original);missing.Remove("history.json");await Invalid(missing,"Missing section fails");
        var wrongName=original.ToDictionary(pair=>pair.Key=="history.json"?"History.json":pair.Key,pair=>pair.Value);await Invalid(wrongName,"Case-changing an entry is not a known schema");
        await Mutate("manifest.json",b=>Edit(b,node=>node["Version"]=99),"Future/unknown versions fail closed");
        await Mutate("manifest.json",b=>Edit(b,node=>node["Kind"]="VideoDownloader.Queue"),"Queue format is distinct");
        await Mutate("settings.json",b=>Edit(b,node=>node["Cookies"]=Secret),"Unknown private settings cannot enter a preview");
        await Mutate("settings.json",b=>Edit(b,node=>node.AsObject().Remove("Quality")),"Missing required settings reject incomplete archives");
        await Mutate("settings.json",b=>Edit(b,node=>node["FilenameTemplate"]="C:\\"+Secret),"Absolute/path settings reject imports");
        await Mutate("subscriptions.json",b=>Edit(b,node=>node["Sources"]![0]!["Url"]="https://example.invalid/a?token="+Secret),"Checksummed secret URLs are still rejected");
        await Mutate("history.json",b=>Edit(b,node=>node[0]!["Source"]="/home/"+Secret),"Checksummed local paths are still rejected");
        await Mutate("history.json",b=>Edit(b,node=>node[0]!["Source"]="file=/opt/Папка с пробелами/"+Secret),"Checksummed arbitrary assignment paths reject the entire import");
        await Mutate("history.json",b=>Edit(b,node=>node[0]!["Source"]="path=//server/private share/"+Secret),"Checksummed forward-slash UNC/POSIX roots reject the entire import");
        await Mutate("subscriptions.json",b=>Edit(b,node=>node["Items"]![0]!["State"]="Unknown"),"Unknown subscription states are rejected");
        await Mutate("history.json",b=>Edit(b,node=>node.AsArray().Add(node[0]!.DeepClone())),"Duplicate exact history URLs reject the archive");
        await Mutate("settings.json",b=>Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(b).Insert(1,"\"quality\":\"720\",")),"Duplicate JSON properties with different case fail");
        await Mutate("settings.json",_=>[0xff,0xfe,0x7b],"Invalid UTF-8 fails");
        await Mutate("settings.json",_=>Encoding.UTF8.GetBytes("{truncated"),"Malformed JSON fails");
        var badChecksum=new Dictionary<string,byte[]>(original){["history.json"]=Encoding.UTF8.GetBytes("[]")};await Invalid(badChecksum,"A payload cannot bypass its SHA-256");
        await Mutate("history.json",_=>Encoding.UTF8.GetBytes(new string(' ',2*1024*1024)),"Excessive compression ratio fails before parsing");
        await Mutate("history.json",_=>new byte[WindowsProfileBackup.MaxPayloadBytes+1],"Uncompressed payload bounds fail");
        string link=Path.Combine(root,"symlink.zip");
        using(var output=File.Create(link))using(var zip=new ZipArchive(output,ZipArchiveMode.Create))
            foreach(var pair in original){var entry=zip.CreateEntry(pair.Key);if(pair.Key=="history.json")entry.ExternalAttributes=0xa1ff<<16;using var writer=entry.Open();writer.Write(pair.Value);}
        await Refuses(()=>service.LoadAsync(link,None),"Archive symlinks reject the entire archive");
    }

    private static async Task RestoreMerge(string root,ProfileBackupArchive archive)
    {
        var local=NewProfile(root,"merge");string localRoot=Path.Combine(root,"actual-local-media");Directory.CreateDirectory(localRoot);
        string media=Path.Combine(localRoot,"keep.mp4");File.WriteAllText(media,"LOCAL MEDIA");
        var options=new DownloadOptions{Output=localRoot,Cookies=Path.Combine(root,"current-cookies.txt"),WatchClipboard=true};
        var row=new DownloadItem{Url="https://local.invalid/queue",Title="Current queue",Status="Completed",State="Done",Path=media,OutputRoot=localRoot,OwnedPaths=[media]};
        local.Save([row],options);
        WriteSubscriptions(local,[Source("case","https://local.invalid/source"),Source("same-url",PublicUrl)],[Item("same-url","https://local.invalid/existing")]);
        AppendHistory(local,PublicUrl,"OLD LOCAL",DateTimeOffset.Parse("2020-01-01T00:00:00Z"),[media]);
        byte[] historyBefore=File.ReadAllBytes(local.HistoryPath);var queueBefore=JsonNode.Parse(File.ReadAllBytes(local.SessionPath))!["Queue"]!.ToJsonString();
        var service=new WindowsProfileBackup(local);using var plan=await service.PrepareRestoreAsync(archive,options,[row],BackupSubscriptionsPolicy.Merge,None);
        Require(plan.Summary is {AddedSources:1,AddedItems:1,AddedHistoryEntries:2,ReplacedSources:0},"Preview reports only actual merge additions");
        Require(plan.Settings.Output==localRoot && plan.Settings.Cookies==options.Cookies && plan.Settings.WatchClipboard,"Current-computer local permissions and paths survive preview");
        await service.RestoreAsync(plan,None);
        var restored=local.Restore()!;
        Require(restored.Settings.Quality=="1080" && restored.Settings.Output==localRoot && restored.Settings.Cookies==options.Cookies,"Portable options merge with current local paths");
        Require(JsonNode.Parse(File.ReadAllBytes(local.SessionPath))!["Queue"]!.ToJsonString()==queueBefore,"Existing queue ownership and all row state remain byte-equivalent JSON");
        var subscriptions=new SubscriptionStore(Path.GetDirectoryName(local.SessionPath)!).Read();
        Require(subscriptions.Subscriptions.Count==3 && subscriptions.Items.Count==2,"Merge preserves current sources and discoveries");
        Require(subscriptions.Subscriptions.Single(source=>source.Url.Contains("/watch?",StringComparison.Ordinal)).Id!="case","An imported source ID collision cannot refer to the unrelated local source");
        Require(subscriptions.Items.Single(item=>item.Url=="https://example.invalid/video?id=1") is {SubscriptionId:"same-url",State:"New",InitialQueuePending:false},"Imported queued items become idle discoveries and map onto the existing exact source");
        byte[] historyAfter=File.ReadAllBytes(local.HistoryPath);Require(historyAfter.AsSpan(0,historyBefore.Length).SequenceEqual(historyBefore),"Existing local history is an untouched prefix");
        var library=local.ReadHistoryLibrary("",100,None);
        Require(library.Entries.First(entry=>entry.Url==PublicUrl).OutputPaths.Count==0,"Imported history grants no local file/open/delete authority");
        Require(File.ReadAllText(media)=="LOCAL MEDIA" && !service.RecoveryRequired,"Restore preserves physical media and completes its journal");
    }

    private static async Task RestoreReplaceAndStale(string root,ProfileBackupArchive archive)
    {
        var local=NewProfile(root,"replace");var options=new DownloadOptions();local.Save([],options);
        WriteSubscriptions(local,[Source("collision","https://local.invalid/source")],[]);
        AppendHistory(local,"https://local.invalid/history","Keep",null,[]);
        var service=new WindowsProfileBackup(local);
        using(var stale=await service.PrepareRestoreAsync(archive,options,[],BackupSubscriptionsPolicy.Replace,None))
        {
            local.AppendSuccess("https://local.invalid/late");var before=ProfileBackupTransaction.ReadFiles(Path.GetDirectoryName(local.SessionPath)!);
            await Refuses(()=>service.RestoreAsync(stale,None),"A stale preview must not overwrite a later history append");AssertFiles(local,before);
        }
        using(var plan=await service.PrepareRestoreAsync(archive,options,[],BackupSubscriptionsPolicy.Replace,None))
        {
            Require(plan.Summary.ReplacedSources==1,"Replace preview counts displaced sources");await service.RestoreAsync(plan,None);
        }
        Require(new SubscriptionStore(Path.GetDirectoryName(local.SessionPath)!).Read().Subscriptions.Count==2,"Replace changes subscriptions");
        Require(local.ReadHistory().ContainsKey("https://local.invalid/history") && local.ReadHistory().ContainsKey("https://local.invalid/late"),"Replace never discards the success history");
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Refuses(()=>service.CreateAsync(options,cancelled.Token),"Cancellation is observed before export work");
        await Refuses(()=>service.PrepareRestoreAsync(archive,options,[new DownloadItem{Status="Running"}],BackupSubscriptionsPolicy.Merge,None),"Active queue rows block restore preparation");
        await Refuses(()=>service.PrepareRestoreAsync(archive,options,[new DownloadItem{CleanupPaths=["pending"]}],BackupSubscriptionsPolicy.Merge,None),"Unfinished cleanup blocks restore preparation");
    }

    private static async Task RollbackAndLiveOwner(string root,ProfileBackupArchive archive)
    {
        var local=NewProfile(root,"rollback");local.Save([],new());var before=ProfileBackupTransaction.ReadFiles(Path.GetDirectoryName(local.SessionPath)!);
        var failure=new WindowsProfileBackup(local,phase=>{if(phase=="published:session.json")throw new IOException("actual publication fault");});
        using(var plan=await failure.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None))
            await Refuses(()=>failure.RestoreAsync(plan,None),"Failure after first atomic publication rolls back all three files");
        AssertFiles(local,before);Require(!failure.RecoveryRequired,"Verified rollback removes its recovery pointer");
        string profile=Path.GetDirectoryName(local.SessionPath)!,foreignTemporary="";
        var consumed=new WindowsProfileBackup(local,phase=>
        {
            if(phase!="published:session.json")return;
            string staging=Directory.GetDirectories(profile,".profile-restore-*").Single();
            Require(!File.Exists(Path.Combine(staging,"session.json.after")),"Atomic publication consumes its existing durable stage instead of writing another partial private temp");
            string id=Path.GetFileName(staging)[".profile-restore-".Length..];
            foreignTemporary=Path.Combine(profile,"session.json.restore-"+id+".tmp");File.WriteAllText(foreignTemporary,"FOREIGN TEMPORARY");
            throw new IOException("fault after moving durable stage");
        });
        using(var plan=await consumed.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None))
            await Refuses(()=>consumed.RestoreAsync(plan,None),"A post-move failure rolls back without claiming a foreign temporary");
        AssertFiles(local,before);Require(File.ReadAllText(foreignTemporary)=="FOREIGN TEMPORARY","Cleanup preserves a nonmatching foreign publish-temp path");
        using var cancellation=new CancellationTokenSource();
        var cancelled=new WindowsProfileBackup(local,phase=>{if(phase=="published:session.json")cancellation.Cancel();});
        using(var plan=await cancelled.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None))
            await Refuses(()=>cancelled.RestoreAsync(plan,cancellation.Token),"Cancellation after publication rolls back without cancellation of its recovery");
        AssertFiles(local,before);
        using var ready=new ManualResetEventSlim();using var release=new ManualResetEventSlim();
        var live=new WindowsProfileBackup(local,phase=>{if(phase=="published:session.json"){ready.Set();if(!release.Wait(TimeSpan.FromSeconds(10)))throw new TimeoutException();}});
        var activePlan=await live.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None);
        Task worker=live.RestoreAsync(activePlan,None);
        try
        {
            Require(ready.Wait(TimeSpan.FromSeconds(5)),"Restore reaches the real first publication");
            var second=new AppPersistence(Path.GetDirectoryName(local.SessionPath)!);
            Require(live.RecoveryRequired,"Another same-process constructor cannot roll back a live transaction");
            activePlan.Dispose();Require(activePlan.After["session.json"].Any(value=>value!=0),"Disposing a leased plan cannot erase in-flight bytes");
        }
        finally{release.Set();await worker;activePlan.Dispose();}
        Require(!live.RecoveryRequired && local.Restore()!.Settings.Quality=="1080","The original live owner commits coherently");
        Require(activePlan.After.Values.All(bytes=>bytes.All(value=>value==0)),"A disposed plan clears private local snapshot buffers after its worker releases");
    }

    private static async Task CrashRecovery(string root,string archivePath,ProfileBackupArchive archive)
    {
        foreach(string phase in new[]{"published:session.json","committed"})
        {
            var local=NewProfile(root,"crash-"+phase.Replace(':','-'));local.Save([],new());
            string directory=Path.GetDirectoryName(local.SessionPath)!;var before=ProfileBackupTransaction.ReadFiles(directory);
            string ready=Path.Combine(root,Guid.NewGuid().ToString("N")+".ready");
            using var process=StartFixture(directory,archivePath,phase,ready);
            try
            {
                var watch=Stopwatch.StartNew();while(!File.Exists(ready) && !process.HasExited && watch.Elapsed<TimeSpan.FromSeconds(12))await Task.Delay(20);
                Require(File.Exists(ready),"Actual crash child reaches its durable publication latch: "+(process.HasExited?await process.StandardError.ReadToEndAsync():phase));
            }
            finally{if(!process.HasExited)process.Kill(true);await process.WaitForExitAsync();}
            Require(File.Exists(Path.Combine(directory,".profile-restore.json")),"A killed writer leaves its durable recovery pointer");
            var recovered=new AppPersistence(directory);
            Require(!File.Exists(Path.Combine(directory,".profile-restore.json")),"Startup recovers the transaction before profile reads");
            if(phase.StartsWith("published",StringComparison.Ordinal))AssertFiles(recovered,before);
            else Require(recovered.Restore()!.Settings.Quality=="1080" && recovered.ReadHistory().ContainsKey(PublicUrl),"Commit-point recovery keeps the fully imported profile");
        }
    }

    private static async Task WindowsProtectedTargets(string root,ProfileBackupArchive archive)
    {
        if(!OperatingSystem.IsWindows())return;
        foreach(bool readOnly in new[]{false,true})
        {
            var local=NewProfile(root,readOnly?"readonly-target":"locked-target");local.Save([],new());
            WriteSubscriptions(local,[Source("local","https://local.invalid/subscription")],[]);
            var before=ProfileBackupTransaction.ReadFiles(Path.GetDirectoryName(local.SessionPath)!);
            var service=new WindowsProfileBackup(local);using var plan=await service.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None);
            string path=SubscriptionPath(local);FileAttributes attributes=File.GetAttributes(path);
            FileStream? locked=null;
            try
            {
                if(readOnly)File.SetAttributes(path,attributes|FileAttributes.ReadOnly);
                else locked=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                var timer=Stopwatch.StartNew();
                await Refuses(()=>service.RestoreAsync(plan,None),"A real protected Windows second target must roll back the first target");
                Require(timer.Elapsed<TimeSpan.FromSeconds(5),"A permanent access/share failure has a bounded retry");
                AssertFiles(local,before);Require(!service.RecoveryRequired,"Protected-target rollback is fully verified");
                if(readOnly)Require((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0,"Restore must not clear a user's read-only attribute");
            }
            finally{locked?.Dispose();File.SetAttributes(path,attributes);}
        }
    }

    private static async Task DamagedAndForeign(string root,ProfileBackupArchive archive)
    {
        foreach(string name in ProfileBackupTransaction.FileNames)
        {
            var local=NewProfile(root,"damaged-"+name);local.Save([],new());
            string path=Path.Combine(Path.GetDirectoryName(local.SessionPath)!,name);File.WriteAllText(path,"{BROKEN");
            var before=ProfileBackupTransaction.ReadFiles(Path.GetDirectoryName(local.SessionPath)!);var service=new WindowsProfileBackup(local);
            await Refuses(()=>service.CreateAsync(new(),None),"Export must expose damaged current data");
            await Refuses(()=>service.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None),"Restore must not silently overwrite damaged current data");AssertFiles(local,before);
        }
        var foreign=NewProfile(root,"foreign");foreign.Save([],new());string directory=Path.GetDirectoryName(foreign.SessionPath)!;
        string external="FOREIGN FILE MUST SURVIVE";
        var failed=new WindowsProfileBackup(foreign,phase=>
        {if(phase=="published:session.json"){File.WriteAllText(foreign.SessionPath,external);throw new IOException("external writer");}});
        using(var plan=await failed.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Replace,None))
        {
            Exception error=await Refuses(()=>failed.RestoreAsync(plan,None),"Foreign changes make rollback fail closed");
            Require(error is ProfileRestoreException{RecoveryRequired:true,Applied:false},"Incomplete rollback is explicitly read-only/recovery-required");
            Require(File.ReadAllText(foreign.SessionPath)==external,"Rollback never overwrites a foreign changed target");
        }
        // The current test process is still the recorded owner: startup defers
        // instead of guessing that the failed transaction's owner is dead.
        new AppPersistence(directory);
        Require(File.Exists(Path.Combine(directory,".profile-restore.json")),"The durable backup survives an unresolved foreign modification");
        var marker=NewProfile(root,"bad-marker");marker.Save([],new());string markerRoot=Path.GetDirectoryName(marker.SessionPath)!;
        File.WriteAllText(Path.Combine(markerRoot,".profile-restore.json"),"{\"Version\":99,\"Id\":\"../foreign\"}");
        await Refuses(()=>Task.Run(()=>new AppPersistence(markerRoot)),"Malformed/traversing recovery markers fail before startup reads");
        Require(File.ReadAllText(marker.SessionPath).Contains("Settings",StringComparison.Ordinal),"A malformed marker cannot alter current profile files");
        var staging=NewProfile(root,"staging");staging.Save([],new());string stagingRoot=Path.GetDirectoryName(staging.SessionPath)!;
        var staged=new WindowsProfileBackup(staging,phase=>
        {
            if(phase!="staged")return;
            string owned=Directory.GetDirectories(stagingRoot,".profile-restore-*").Single();
            File.WriteAllText(Path.Combine(owned,"unrelated.txt"),external);
            throw new IOException("cancel before publication");
        });
        using(var plan=await staged.PrepareRestoreAsync(archive,new(),[],BackupSubscriptionsPolicy.Merge,None))
            await Refuses(()=>staged.RestoreAsync(plan,None),"Pre-publication cleanup is bounded to actually created files");
        Require(Directory.GetFiles(stagingRoot,"unrelated.txt",SearchOption.AllDirectories).Select(File.ReadAllText).Single()==external,"Cleanup never recursively deletes foreign directory entries");
    }

    private static Process StartFixture(string profile,string archive,string phase,string ready)
    {
        string executable=Environment.ProcessPath??throw new InvalidOperationException("Missing process path");
        var start=new ProcessStartInfo(executable){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true,CreateNoWindow=true};
        if(Path.GetFileNameWithoutExtension(executable).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        foreach(string arg in new[]{"--profile-backup-crash",profile,archive,phase,ready})start.ArgumentList.Add(arg);
        return Process.Start(start)??throw new InvalidOperationException("Could not start actual crash fixture");
    }
    private static AppPersistence NewProfile(string root,string name)=>new(Path.Combine(root,name));
    private static string SubscriptionPath(AppPersistence storage)=>Path.Combine(Path.GetDirectoryName(storage.SessionPath)!,"subscriptions.json");
    private static SubscriptionDefinition Source(string id,string url)=>new(){Id=id,Url=url,Title="Source",Initialized=true};
    private static SubscriptionItem Item(string id,string url,string state="New")=>new(){SubscriptionId=id,Url=url,Title="Video",State=state};
    private static void WriteSubscriptions(AppPersistence storage,SubscriptionDefinition[] sources,SubscriptionItem[] items)=>
        File.WriteAllText(SubscriptionPath(storage),JsonSerializer.Serialize(new SubscriptionSnapshot{Subscriptions=sources.ToList(),Items=items.ToList()}));
    private static void AppendHistory(AppPersistence storage,string url,string title,DateTimeOffset? date,string[] paths)=>
        File.AppendAllText(storage.HistoryPath,JsonSerializer.Serialize(new{Version=1,Url=url,CompletedAtUtc=date?.ToString("O"),Title=title,Source="Source",OutputPaths=paths,OutputPathsTruncated=false})+"\n");
    private static void AssertFiles(AppPersistence storage,IReadOnlyDictionary<string,byte[]?> expected)
    {
        var actual=ProfileBackupTransaction.ReadFiles(Path.GetDirectoryName(storage.SessionPath)!);
        foreach(var pair in expected)Require(pair.Value is null?actual[pair.Key] is null:actual[pair.Key] is {} bytes && bytes.SequenceEqual(pair.Value),"Previous profile bytes survive: "+pair.Key);
    }
    private static Dictionary<string,byte[]> ReadZip(byte[] bytes)
    {
        using var input=new MemoryStream(bytes);using var zip=new ZipArchive(input,ZipArchiveMode.Read);
        return zip.Entries.ToDictionary(entry=>entry.FullName,entry=>{using var output=new MemoryStream();using var file=entry.Open();file.CopyTo(output);return output.ToArray();});
    }
    private static byte[] Zip(Dictionary<string,byte[]> entries,Action<ZipArchive>? extra=null)
    {
        using var output=new MemoryStream();using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
        {foreach(var pair in entries){var entry=zip.CreateEntry(pair.Key);using var writer=entry.Open();writer.Write(pair.Value);}extra?.Invoke(zip);}return output.ToArray();
    }
    private static byte[] Edit(byte[] bytes,Action<JsonNode> edit){var node=JsonNode.Parse(bytes)!;edit(node);return Encoding.UTF8.GetBytes(node.ToJsonString());}
    private static void Rehash(Dictionary<string,byte[]> files,string name)
    {
        files["manifest.json"]=Edit(files["manifest.json"],manifest=>manifest["Files"]![name]=new JsonObject
        {["Bytes"]=files[name].LongLength,["Sha256"]=Convert.ToHexString(SHA256.HashData(files[name])).ToLowerInvariant()});
    }
    private static async Task<Exception> Refuses(Func<Task> action,string reason)
    {try{await action();}catch(Exception error){return error;}throw new InvalidOperationException("Profile backup test expected failure: "+reason);}
    private static void Require(bool condition,string reason){if(!condition)throw new InvalidOperationException("Profile backup test: "+reason);}
}
