using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace VideoDownloader.Native;

internal sealed class WindowsProfileBackup
{
    internal const string Extension=".vdbackup";
    internal const long MaxArchiveBytes=64L*1024*1024;
    internal const int MaxRecords=100_000;
    internal const int MaxPayloadBytes=32*1024*1024;
    private static readonly string[] Entries=["manifest.json","settings.json","subscriptions.json","history.json"];
    private static readonly UTF8Encoding Utf8=new(false,true);
    internal static readonly JsonSerializerOptions Json=CreateJson();
    private readonly AppPersistence _storage;
    private readonly string _root;
    private readonly Action<string>? _checkpoint;

    internal WindowsProfileBackup(AppPersistence storage,Action<string>? checkpoint=null)
    {_storage=storage;_root=ProfileBackupTransaction.Root(Path.GetDirectoryName(storage.SessionPath)!);_checkpoint=checkpoint;}
    internal bool RecoveryRequired=>ProfileBackupTransaction.HasPending(_root);

    internal Task<ProfileBackupArchive> CreateAsync(DownloadOptions options,CancellationToken cancellationToken)
    {
        DownloadOptions snapshot=options.Snapshot();
        return Task.Run(()=>ProfileBackupTransaction.Snapshot(_root,()=>CreateCore(snapshot,cancellationToken),cancellationToken),cancellationToken);
    }
    private ProfileBackupArchive CreateCore(DownloadOptions options,CancellationToken cancellationToken)
    {
        ValidateCurrent();
        var settings=PortableProfileSettings.From(options);settings.Validate();
        var stored=new SubscriptionStore(_root).Read();
        var history=_storage.ReadBackupHistory(cancellationToken);
        int privateSources=0,privateItems=0,privateHistory=0,privateFeeds=0,sanitized=0;
        var sources=new List<PortableSubscription>();var items=new List<PortableSubscriptionItem>();
        var retainedIds=new HashSet<string>(StringComparer.Ordinal);
        foreach(var source in stored.Subscriptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!BackupPrivacy.IsSafeUrl(source.Url)){privateSources++;continue;}
            string feed=source.FeedUrl;
            if(feed.Length>0 && !BackupPrivacy.IsSafeUrl(feed)){feed="";privateFeeds++;}
            string shortName=BackupPrivacy.Text(source.ShortName,80,ref sanitized);
            if(shortName.Length>0)shortName=SubscriptionNames.Normalize(shortName);
            sources.Add(new(source.Id,source.Url,BackupPrivacy.Text(source.Title,4096,ref sanitized),shortName,feed,
                source.DownloadExisting,source.Initialized,source.Paused,source.LastAttemptUtc,source.LastSuccessUtc,
                source.ScheduleIntervalMinutes));
            retainedIds.Add(source.Id);
        }
        foreach(var item in stored.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!retainedIds.Contains(item.SubscriptionId) || !BackupPrivacy.IsSafeUrl(item.Url)){privateItems++;continue;}
            items.Add(new(item.SubscriptionId,item.Url,BackupPrivacy.Text(item.Title,4096,ref sanitized),item.State,
                item.DiscoveredAtUtc,item.InitialQueuePending,item.SuccessAttributed,item.DuplicateSubscriptionId,item.DuplicateNotified));
        }
        // One latest success per exact URL is sufficient to preserve repeat
        // decisions. Export never carries its local files or deletion rights.
        var latest=new Dictionary<string,DownloadHistoryEntry>(StringComparer.Ordinal);
        foreach(var entry in history)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if(!latest.TryGetValue(entry.Url,out var old) ||
                entry.CompletedAtUtc.HasValue && (!old.CompletedAtUtc.HasValue || entry.CompletedAtUtc.Value>=old.CompletedAtUtc.Value))latest[entry.Url]=entry;
        }
        var portableHistory=new List<PortableHistoryEntry>();
        foreach(var entry in latest.Values)
        {
            if(!BackupPrivacy.IsSafeUrl(entry.Url)){privateHistory++;continue;}
            portableHistory.Add(new(entry.Url,entry.CompletedAtUtc,BackupPrivacy.Text(entry.Title,4096,ref sanitized),
                BackupPrivacy.Text(entry.Source,1024,ref sanitized)));
        }
        var summary=new BackupSummary(sources.Count,items.Count,portableHistory.Count,privateSources,privateItems,privateHistory,sanitized,privateFeeds);
        var manifest=new BackupManifest{ApplicationVersion=typeof(AppPersistence).Assembly.GetName().Version?.ToString()??"unknown",Summary=summary};
        var document=new BackupDocument(manifest,settings,new(){Sources=sources,Items=items},portableHistory);
        ValidateDocument(document);
        return Encode(document,cancellationToken);
    }
    internal Task<ProfileBackupArchive> LoadAsync(string path,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        RequireLocalPath(path);OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length>MaxArchiveBytes)throw new InvalidDataException("Архив резервной копии превышает 64 МиБ.");
        using var output=new MemoryStream();CopyBounded(input,output,MaxArchiveBytes,cancellationToken);
        byte[] bytes=output.ToArray();var document=Decode(bytes,cancellationToken);
        return new ProfileBackupArchive(bytes,document.Manifest,document.Manifest.Summary!);
    },cancellationToken);
    internal Task SaveAsync(ProfileBackupArchive archive,string destination,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        byte[] bytes=archive.ArchiveBytes;Decode(bytes,cancellationToken);
        string path=Path.GetFullPath(destination),parent=Path.GetDirectoryName(path)!;
        RequireLocalPath(path);OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
        if(!Directory.Exists(parent))throw new DirectoryNotFoundException("Каталог резервной копии не существует.");
        // Publish only a complete durable archive. A crash may leave an owned
        // temporary file, but never a partially written final backup.
        string temporary=Path.Combine(parent,".vdbackup-"+Guid.NewGuid().ToString("N")+".tmp");bool created=false;
        try
        {
            using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.Read))
            {created=true;cancellationToken.ThrowIfCancellationRequested();output.Write(bytes);output.Flush(true);}
            cancellationToken.ThrowIfCancellationRequested();File.Move(temporary,path,false);
        }
        finally{if(created && File.Exists(temporary))File.Delete(temporary);}
    },cancellationToken);

    internal Task<ProfileRestorePlan> PrepareRestoreAsync(ProfileBackupArchive archive,DownloadOptions localOptions,
        IReadOnlyList<DownloadItem> currentQueue,BackupSubscriptionsPolicy policy,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        if(policy is not (BackupSubscriptionsPolicy.Merge or BackupSubscriptionsPolicy.Replace))throw new ArgumentOutOfRangeException(nameof(policy));
        if(currentQueue.Any(row=>row.Status=="Running" || row.PendingRemoval || row.CleanupPaths.Count>0 || row.CleanupError.Length>0))
            throw new InvalidOperationException("Сначала завершите загрузки и очистку файлов.");
        var document=Decode(archive.ArchiveBytes,cancellationToken);
        return ProfileBackupTransaction.Snapshot(_root,()=>
        {
            ValidateCurrent();
            var before=ProfileBackupTransaction.ReadFiles(_root);
            var currentSubscriptions=new SubscriptionStore(_root).Read();
            var known=_storage.ReadBackupHistory(cancellationToken).ToDictionary(entry=>entry.Url,entry=>entry.CompletedAtUtc,StringComparer.Ordinal);
            var reserved=currentQueue.Select(row=>row.SubscriptionId).Where(id=>!string.IsNullOrEmpty(id)).ToHashSet(StringComparer.Ordinal);
            var merged=MergeSubscriptions(currentSubscriptions,document.Subscriptions,policy,out int addedSources,out int addedItems,reserved);
            DownloadOptions settings=document.Settings.Apply(localOptions);
            byte[] session=SessionBytes(before["session.json"],document.Settings,settings,currentQueue);
            byte[] subscriptionBytes=JsonSerializer.SerializeToUtf8Bytes(merged,AppPersistence.Json);
            if(subscriptionBytes.Length>ProfileBackupTransaction.MaxProfileBytes)throw new InvalidDataException("Восстановленные подписки превышают допустимый объём.");
            using var history=new MemoryStream();
            if(before["successful-downloads.jsonl"] is {} previous)history.Write(previous);
            int addedHistory=0;
            foreach(var entry in document.History)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if(known.TryGetValue(entry.Url,out var old) && (!entry.CompletedAtUtc.HasValue || old.HasValue && old.Value>=entry.CompletedAtUtc.Value))continue;
                if(history.Length>0 && history.GetBuffer()[checked((int)history.Length)-1] is not (10 or 13))history.WriteByte(10);
                var record=new{Version=1,entry.Url,CompletedAtUtc=entry.CompletedAtUtc?.ToString("O"),entry.Title,entry.Source,
                    OutputPaths=Array.Empty<string>(),OutputPathsTruncated=false};
                byte[] line=JsonSerializer.SerializeToUtf8Bytes(record,AppPersistence.Json);
                if(history.Length+line.Length+1>ProfileBackupTransaction.MaxProfileBytes)throw new InvalidDataException("Восстановленная история слишком велика.");
                history.Write(line);history.WriteByte(10);addedHistory++;
            }
            var after=new Dictionary<string,byte[]>(StringComparer.Ordinal)
            { ["session.json"]=session,["subscriptions.json"]=subscriptionBytes,["successful-downloads.jsonl"]=history.ToArray() };
            if(after.Values.Sum(bytes=>bytes.LongLength)>ProfileBackupTransaction.MaxProfileBytes)
                throw new InvalidDataException("Восстановленный профиль превышает 192 МиБ.");
            return new ProfileRestorePlan(_root,settings,new(addedSources,addedItems,addedHistory,
                policy==BackupSubscriptionsPolicy.Replace?currentSubscriptions.Subscriptions.Count:0,policy),before,after);
        },cancellationToken);
    },cancellationToken);
    internal Task RestoreAsync(ProfileRestorePlan plan,CancellationToken cancellationToken)=>Task.Run(()=>
    {
        using var lease=plan.Lease();
        if(!string.Equals(ProfileBackupTransaction.Root(plan.ProfileRoot),_root,OperatingSystem.IsWindows()?StringComparison.OrdinalIgnoreCase:StringComparison.Ordinal))
            throw new InvalidDataException("План принадлежит другому профилю.");
        ProfileBackupTransaction.Apply(plan,cancellationToken,_checkpoint);
    },cancellationToken);

    private void ValidateCurrent()
    {
        long total=0;
        foreach(string name in ProfileBackupTransaction.FileNames)
        {
            string path=Path.Combine(_root,name);OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
            var file=new FileInfo(path);if(file.Exists)total+=file.Length;
            if(total>ProfileBackupTransaction.MaxProfileBytes)throw new InvalidDataException("Данные профиля превышают предел резервного копирования 192 МиБ.");
        }
        var session=_storage.Restore();
        if(session?.LegacyEngineWasActive==true || AppPersistence.LegacyEngineIsAlive(session?.Engine))
            throw new InvalidOperationException("Работник прежней версии ещё использует профиль.");
        if(session?.Queue.Any(row=>row.PendingRemoval || row.CleanupPaths.Count>0 || row.CleanupError.Length>0)==true)
            throw new InvalidOperationException("Сначала завершите отложенную очистку файлов.");
        new SubscriptionStore(_root).Read();_storage.ReadBackupHistory(CancellationToken.None);
    }
    private static byte[] SessionBytes(byte[]? current,PortableProfileSettings portable,DownloadOptions settings,IReadOnlyList<DownloadItem> queue)
    {
        if(current is null)
            return JsonSerializer.SerializeToUtf8Bytes(new SessionSnapshot{Queue=queue.ToList(),Settings=settings},AppPersistence.Json);
        using var document=ParseJson(current);
        var node=JsonNode.Parse(current) as JsonObject??throw new InvalidDataException("Неверный checkpoint профиля.");
        string key=node.Select(pair=>pair.Key).SingleOrDefault(name=>name.Equals("Settings",StringComparison.OrdinalIgnoreCase))
            ??throw new InvalidDataException("В checkpoint отсутствуют настройки.");
        var options=node[key] as JsonObject??throw new InvalidDataException("Неверные настройки checkpoint.");
        var restored=JsonSerializer.SerializeToNode(portable,Json)!.AsObject();
        void Set(string name,JsonNode? value)
        {
            string existing=options.Select(pair=>pair.Key).SingleOrDefault(candidate=>candidate.Equals(name,StringComparison.OrdinalIgnoreCase))??name;
            options[existing]=value;
        }
        foreach(var property in restored)Set(property.Key,property.Value?.DeepClone());
        // Local-only values are taken from this GUI, never from the archive.
        Set("Output",JsonValue.Create(settings.Output));Set("Cookies",JsonValue.Create(settings.Cookies));Set("WatchClipboard",JsonValue.Create(settings.WatchClipboard));
        byte[] bytes=JsonSerializer.SerializeToUtf8Bytes(node,AppPersistence.Json);
        if(bytes.Length>AppPersistence.MaxCheckpointBytes)throw new InvalidDataException("Восстановленный checkpoint превышает 16 МиБ.");
        return bytes;
    }
    private static SubscriptionSnapshot MergeSubscriptions(SubscriptionSnapshot current,PortableSubscriptions imported,
        BackupSubscriptionsPolicy policy,out int addedSources,out int addedItems,IReadOnlySet<string>? reservedQueueIds=null)
    {
        var result=policy==BackupSubscriptionsPolicy.Merge?current:new SubscriptionSnapshot();
        var sourceByUrl=result.Subscriptions.ToDictionary(source=>source.Url,StringComparer.Ordinal);
        var currentByUrl=current.Subscriptions.ToDictionary(source=>source.Url,StringComparer.Ordinal);
        // Replacing definitions must not give an unrelated imported source the
        // identity still referenced by an untouched local queue row.
        var sourceIds=current.Subscriptions.Select(source=>source.Id).ToHashSet(StringComparer.Ordinal);
        if(reservedQueueIds is not null)sourceIds.UnionWith(reservedQueueIds);
        var idMap=new Dictionary<string,string>(StringComparer.Ordinal);addedSources=0;addedItems=0;
        foreach(var source in imported.Sources)
        {
            if(sourceByUrl.TryGetValue(source.Url,out var existing)){idMap.Add(source.Id,existing.Id);continue;}
            string id;
            if(policy==BackupSubscriptionsPolicy.Replace && currentByUrl.TryGetValue(source.Url,out var sameSource))id=sameSource.Id;
            else if(sourceIds.Add(source.Id))id=source.Id;
            else{do{id=Guid.NewGuid().ToString("N");}while(!sourceIds.Add(id));}
            sourceIds.Add(id);idMap.Add(source.Id,id);
            var restored=new SubscriptionDefinition{Id=id,Url=source.Url,Title=source.Title,ShortName=source.ShortName,FeedUrl=source.FeedUrl,
                DownloadExisting=source.DownloadExisting,Initialized=source.Initialized,Paused=source.Paused,
                LastAttemptUtc=source.LastAttemptUtc,LastSuccessUtc=source.LastSuccessUtc,
                ScheduleEnabled=false,ScheduleIntervalMinutes=source.ScheduleIntervalMinutes};
            result.Subscriptions.Add(restored);sourceByUrl.Add(restored.Url,restored);addedSources++;
        }
        var keys=result.Items.Select(item=>(item.SubscriptionId,item.Url)).ToHashSet();
        foreach(var item in imported.Items)
        {
            string id=idMap[item.SubscriptionId];if(!keys.Add((id,item.Url)))continue;
            string duplicate=item.DuplicateSubscriptionId.Length==0?"":idMap.TryGetValue(item.DuplicateSubscriptionId,out var mapped)?mapped:
                "backup-"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(item.DuplicateSubscriptionId))).ToLowerInvariant()[..24];
            result.Items.Add(new(){SubscriptionId=id,Url=item.Url,Title=item.Title,State=item.State=="Queued"?"New":item.State,
                DiscoveredAtUtc=item.DiscoveredAtUtc,InitialQueuePending=false,SuccessAttributed=item.SuccessAttributed,
                DuplicateSubscriptionId=duplicate,DuplicateNotified=item.DuplicateNotified});addedItems++;
        }
        SubscriptionStore.ValidateBackupSnapshot(result);return result;
    }

    private static ProfileBackupArchive Encode(BackupDocument document,CancellationToken cancellationToken)
    {
        var payload=new Dictionary<string,byte[]>(StringComparer.Ordinal)
        {
            ["settings.json"]=JsonSerializer.SerializeToUtf8Bytes(document.Settings,Json),
            ["subscriptions.json"]=JsonSerializer.SerializeToUtf8Bytes(document.Subscriptions,Json),
            ["history.json"]=JsonSerializer.SerializeToUtf8Bytes(document.History,Json)
        };
        if(payload.Values.Any(bytes=>bytes.Length>MaxPayloadBytes) || payload.Values.Sum(bytes=>bytes.LongLength)>MaxArchiveBytes)
            throw new InvalidDataException("Данные резервной копии превышают предел 64 МиБ, один раздел — 32 МиБ.");
        foreach(var pair in payload)document.Manifest.Files.Add(pair.Key,new(pair.Value.LongLength,Digest(pair.Value)));
        payload.Add("manifest.json",JsonSerializer.SerializeToUtf8Bytes(document.Manifest,Json));
        using var output=new MemoryStream();
        using(var archive=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(var pair in payload)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var entry=archive.CreateEntry(pair.Key,CompressionLevel.Optimal);using var file=entry.Open();file.Write(pair.Value);
            }
        if(output.Length>MaxArchiveBytes)throw new InvalidDataException("Архив резервной копии превышает 64 МиБ.");
        byte[] bytes=output.ToArray();Decode(bytes,cancellationToken);
        return new(bytes,document.Manifest,document.Manifest.Summary!);
    }
    private static BackupDocument Decode(byte[] bytes,CancellationToken cancellationToken)
    {
        if(bytes.LongLength>MaxArchiveBytes)throw new InvalidDataException("Архив резервной копии превышает 64 МиБ.");
        using var input=new MemoryStream(bytes,false);using var archive=new ZipArchive(input,ZipArchiveMode.Read,false);
        if(archive.Entries.Count!=Entries.Length)throw new InvalidDataException("Неполный или посторонний состав резервной копии.");
        var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);long total=0;
        foreach(var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int unixType=(entry.ExternalAttributes>>16)&0xf000;
            if(!Entries.Contains(entry.FullName,StringComparer.Ordinal) || files.ContainsKey(entry.FullName) ||
                unixType is not (0 or 0x8000) || (entry.ExternalAttributes&(0x8|0x10|0x40|0x400))!=0 || entry.Length<0 ||
                entry.Length>(entry.FullName=="manifest.json"?32*1024:MaxPayloadBytes) || total+entry.Length>MaxArchiveBytes ||
                entry.Length>1024*1024 && (entry.CompressedLength==0 || entry.Length/Math.Max(1,entry.CompressedLength)>200))
                throw new InvalidDataException("Небезопасный путь, тип, размер или степень сжатия записи резервной копии.");
            using var stream=entry.Open();using var output=new MemoryStream();CopyBounded(stream,output,entry.Length,cancellationToken);
            if(output.Length!=entry.Length)throw new InvalidDataException("Неполная запись резервной копии.");
            files.Add(entry.FullName,output.ToArray());total+=entry.Length;
        }
        var manifest=Deserialize<BackupManifest>(files["manifest.json"]);
        if(manifest.Kind!="VideoDownloader.WindowsBackup" || manifest.Version!=1 || manifest.ApplicationVersion is null ||
            manifest.ApplicationVersion.Length>64 || !BackupPrivacy.IsSafeText(manifest.ApplicationVersion) ||
            manifest.Files is null || manifest.Files.Count!=3 || manifest.Summary is null)
            throw new InvalidDataException("Неподдерживаемый формат резервной копии Windows.");
        foreach(string name in Entries.Where(name=>name!="manifest.json"))
            if(!manifest.Files.TryGetValue(name,out var expected) || expected is null || expected.Bytes!=files[name].LongLength ||
                !string.Equals(expected.Sha256,Digest(files[name]),StringComparison.Ordinal))
                throw new InvalidDataException("Контрольная сумма резервной копии не совпадает.");
        var document=new BackupDocument(manifest,Deserialize<PortableProfileSettings>(files["settings.json"]),
            Deserialize<PortableSubscriptions>(files["subscriptions.json"]),Deserialize<List<PortableHistoryEntry>>(files["history.json"]));
        ValidateDocument(document);return document;
    }
    private static T Deserialize<T>(byte[] bytes)
    {
        using var document=ParseJson(bytes);
        return document.RootElement.Deserialize<T>(Json)??throw new InvalidDataException("Пустой раздел резервной копии.");
    }
    internal static JsonDocument ParseJson(byte[] bytes)
    {
        // Strict UTF-8 and duplicate-name checks apply before DTO materializing.
        _=Utf8.GetCharCount(bytes);var document=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=24});
        try{CheckNames(document.RootElement);return document;}catch{document.Dispose();throw;}
    }
    private static void CheckNames(JsonElement element)
    {
        if(element.ValueKind==JsonValueKind.Object)
        {
            var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var property in element.EnumerateObject())
            {if(!names.Add(property.Name))throw new InvalidDataException("Повторяющееся поле JSON резервной копии.");CheckNames(property.Value);}
        }
        else if(element.ValueKind==JsonValueKind.Array)foreach(var value in element.EnumerateArray())CheckNames(value);
    }
    private static void ValidateDocument(BackupDocument document)
    {
        document.Settings.Validate();var data=document.Subscriptions;
        if(data.Sources is null || data.Items is null || document.History is null || data.Sources.Count>MaxRecords || data.Items.Count>MaxRecords || document.History.Count>MaxRecords)
            throw new InvalidDataException("В резервной копии более 100 000 записей раздела.");
        var ids=new HashSet<string>(StringComparer.Ordinal);var urls=new HashSet<string>(StringComparer.Ordinal);
        foreach(var source in data.Sources)
        {
            if(source is null || string.IsNullOrWhiteSpace(source.Id) || source.Id.Length>256 || !BackupPrivacy.IsSafeText(source.Id) ||
                !ids.Add(source.Id) || !BackupPrivacy.IsSafeUrl(source.Url) || !urls.Add(source.Url) ||
                source.Title is null || source.Title.Length>4096 || !BackupPrivacy.IsSafeText(source.Title) ||
                source.ShortName is null || !BackupPrivacy.IsSafeText(source.ShortName) || source.ShortName.Length>0 && !SubscriptionNames.IsValid(source.ShortName) ||
                source.FeedUrl is null || source.FeedUrl.Length>0 && !BackupPrivacy.IsSafeUrl(source.FeedUrl) ||
                source.ScheduleIntervalMinutes is <5 or >10080 || !source.Initialized && source.LastSuccessUtc.HasValue)
                throw new InvalidDataException("Повреждённая, повторная или приватная подписка в резервной копии.");
        }
        var keys=new HashSet<(string,string)>();
        foreach(var item in data.Items)
            if(item is null || !ids.Contains(item.SubscriptionId) || !BackupPrivacy.IsSafeUrl(item.Url) || !keys.Add((item.SubscriptionId,item.Url)) ||
                item.Title is null || item.Title.Length>4096 || !BackupPrivacy.IsSafeText(item.Title) ||
                item.State is not ("Baseline" or "New" or "Queued" or "Processed" or "AlreadyProcessed" or "Skipped") ||
                item.DuplicateSubscriptionId is null || item.DuplicateSubscriptionId.Length>256 || !BackupPrivacy.IsSafeText(item.DuplicateSubscriptionId))
                throw new InvalidDataException("Повреждённый или приватный материал подписки в резервной копии.");
        urls.Clear();
        foreach(var entry in document.History)
            if(entry is null || !BackupPrivacy.IsSafeUrl(entry.Url) || !urls.Add(entry.Url) ||
                entry.Title is null || entry.Title.Length>4096 || !BackupPrivacy.IsSafeText(entry.Title) ||
                entry.Source is null || entry.Source.Length>1024 || !BackupPrivacy.IsSafeText(entry.Source))
                throw new InvalidDataException("Повреждённая, повторная или приватная история резервной копии.");
        var summary=document.Manifest.Summary;
        if(summary is null || summary.Sources!=data.Sources.Count || summary.Items!=data.Items.Count || summary.HistoryEntries!=document.History.Count ||
            summary.PrivateSourcesSkipped is <0 or >MaxRecords || summary.PrivateItemsSkipped is <0 or >MaxRecords ||
            summary.PrivateHistorySkipped is <0 or >MaxRecords || summary.PrivateFeedsSkipped is <0 or >MaxRecords ||
            summary.SanitizedFields is <0 or >1_000_000)
            throw new InvalidDataException("Сводка резервной копии не соответствует её содержимому.");
        // Validate cross-state invariants with the same native subscription model.
        _=MergeSubscriptions(new(),data,BackupSubscriptionsPolicy.Replace,out _,out _);
    }
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static JsonSerializerOptions CreateJson()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>
        {
            if(info.Kind==JsonTypeInfoKind.Object)foreach(var property in info.Properties)property.IsRequired=true;
        });
        return new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=24,TypeInfoResolver=resolver};
    }
    private static void RequireLocalPath(string path)
    {
        if(path.StartsWith(@"\\",StringComparison.Ordinal) || path.StartsWith("//",StringComparison.Ordinal) ||
            OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!).DriveType==DriveType.Network)
            throw new InvalidDataException("Резервные копии читаются и записываются в локальный файл. Сетевой архив сначала скопируйте на компьютер.");
    }
    private static void CopyBounded(Stream input,Stream output,long limit,CancellationToken cancellationToken)
    {
        byte[] buffer=new byte[8192];long total=0;int count;
        while((count=input.Read(buffer))>0)
        {cancellationToken.ThrowIfCancellationRequested();total+=count;if(total>limit)throw new InvalidDataException("Превышен предел данных резервной копии.");output.Write(buffer,0,count);}
    }
}
