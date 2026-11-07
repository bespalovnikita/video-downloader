using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

// Serializes profile maintenance with the existing subscription/history locks.
// The small pointer is a commit/recovery journal, never part of an export.
internal static class ProfileBackupTransaction
{
    internal static readonly string[] FileNames=["session.json","subscriptions.json","successful-downloads.jsonl"];
    internal const long MaxProfileBytes=192L*1024*1024;
    private const string PointerName=".profile-restore.json";
    private const string Kind="VideoDownloader.ProfileRestore";
    private static readonly object ActiveGate=new();
    private static readonly HashSet<string> Active=new(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
    private static readonly JsonSerializerOptions Json=CreateJson();
    private static readonly Regex Hash=new(@"\A[0-9a-f]{64}\z",RegexOptions.CultureInvariant);
    private static readonly long Started=Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;

    private sealed class Pointer
    {
        public int Version {get;set;}=1;
        public string Id {get;set;}="";
    }
    private sealed class Journal
    {
        public string Kind {get;set;}=ProfileBackupTransaction.Kind;
        public int Version {get;set;}=1;
        public string Id {get;set;}="";
        public string ProfileIdentity {get;set;}="";
        public int OwnerPid {get;set;}
        public long OwnerStarted {get;set;}
        public string State {get;set;}="Prepared";
        public List<FileRecord> Files {get;set;}=[];
    }
    private sealed class FileRecord
    {
        public string Name {get;set;}="";
        public bool Existed {get;set;}
        public long BeforeBytes {get;set;}
        public string BeforeHash {get;set;}="";
        public long AfterBytes {get;set;}
        public string AfterHash {get;set;}="";
    }

    internal static string Root(string directory)
    {
        string full=Path.GetFullPath(directory),trimmed=full.TrimEnd(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar);
        string volume=Path.GetPathRoot(full)!;return trimmed.Length<volume.Length?volume:trimmed;
    }
    private static string Identity(string root)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        OperatingSystem.IsWindows()?root.ToUpperInvariant():root))).ToLowerInvariant();
    private static Mutex ProfileMutex(string root)=>new(false,@"Local\VideoDownloader.ProfileMaintenance."+Identity(root));
    private static string DirectoryPath(string root,string id)=>Path.Combine(root,".profile-restore-"+id);
    private static string JournalPath(string root,string id)=>Path.Combine(DirectoryPath(root,id),"manifest.json");
    private static string PointerPath(string root)=>Path.Combine(root,PointerName);
    private static string BeforePath(string root,string id,string name)=>Path.Combine(DirectoryPath(root,id),name+".before");
    private static string AfterPath(string root,string id,string name)=>Path.Combine(DirectoryPath(root,id),name+".after");
    private static string TemporaryPath(string root,string id,string name)=>Path.Combine(root,name+".restore-"+id+".tmp");

    private sealed class Locks:IDisposable
    {
        private readonly List<Mutex> _held=[];
        internal Locks(string root,bool all,CancellationToken cancellationToken,bool immediate=false)
        {
            var names=new List<Mutex>{ProfileMutex(root)};
            if(all){names.Add(new(false,@"Local\VideoDownloader.Subscriptions"));names.Add(new(false,@"Local\VideoDownloader.SuccessHistory"));}
            try
            {
                foreach(var mutex in names)
                {
                    bool acquired=false;var watch=Stopwatch.StartNew();
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try{acquired=mutex.WaitOne(immediate?0:50);}catch(AbandonedMutexException){acquired=true;}
                    }while(!acquired && !immediate && watch.Elapsed<TimeSpan.FromSeconds(15));
                    if(!acquired)throw new ProfileBusyException();
                    _held.Add(mutex);
                }
            }
            catch
            {
                foreach(var mutex in names.Except(_held))mutex.Dispose();
                Dispose();throw;
            }
        }
        public void Dispose()
        {
            for(int index=_held.Count-1;index>=0;index--){_held[index].ReleaseMutex();_held[index].Dispose();}
            _held.Clear();
        }
    }
    private sealed class ProfileBusyException():IOException("Данные профиля заняты другой операцией. Повторите после её завершения.");

    internal static T Access<T>(string directory,Func<T> action)
    {
        string root=Root(directory);
        using var lease=new Locks(root,false,CancellationToken.None);
        if(File.Exists(PointerPath(root)))
        {
            lock(ActiveGate)if(Active.Contains(root))throw new IOException("Восстановление профиля ещё выполняется.");
            using var all=new Locks(root,true,CancellationToken.None);
            RecoverCore(root);
        }
        return action();
    }
    internal static T Snapshot<T>(string directory,Func<T> action,CancellationToken cancellationToken)
    {
        string root=Root(directory);
        using var lease=new Locks(root,true,cancellationToken);
        if(File.Exists(PointerPath(root)))RecoverCore(root);
        cancellationToken.ThrowIfCancellationRequested();return action();
    }
    internal static void RecoverPending(string directory)
    {
        string root=Root(directory);
        // A secondary GUI constructs AppPersistence before acquiring its GUI
        // singleton. Defer recovery while its live owner works, so that process
        // can still forward arguments. Every subsequent data access takes the
        // profile lock and cannot read or save a partial transaction.
        lock(ActiveGate)if(Active.Contains(root))return;
        if(!File.Exists(PointerPath(root)))return;
        try
        {
            using var lease=new Locks(root,true,CancellationToken.None,immediate:true);
            RecoverCore(root,deferLiveOwner:true);
        }
        catch(ProfileBusyException){ }
    }
    internal static bool HasPending(string directory)=>File.Exists(PointerPath(Root(directory)));

    internal static Dictionary<string,byte[]?> ReadFiles(string directory)
    {
        string root=Root(directory);long total=0;
        var files=new Dictionary<string,byte[]?>(StringComparer.Ordinal);
        foreach(string name in FileNames)
        {
            string path=Path.Combine(root,name);OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
            byte[]? bytes=null;
            try
            {
                using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(input.Length>MaxProfileBytes-total)throw new InvalidDataException("Данные профиля превышают предел резервного копирования 192 МиБ.");
                using var output=new MemoryStream();byte[] buffer=new byte[8192];int count;
                while((count=input.Read(buffer))>0)
                {if(output.Length+count>MaxProfileBytes-total)throw new InvalidDataException("Данные профиля превышают предел резервного копирования 192 МиБ.");output.Write(buffer,0,count);}
                bytes=output.ToArray();total+=bytes.LongLength;
                if(total>MaxProfileBytes)throw new InvalidDataException("Данные профиля превышают предел резервного копирования 192 МиБ.");
            }
            catch(FileNotFoundException){ }
            catch(DirectoryNotFoundException){ }
            files.Add(name,bytes);
        }
        return files;
    }
    private static string Digest(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    private static bool Matches(string path,bool existed,long length,string hash)
    {
        OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
        try
        {
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
            if(!existed || input.Length!=length)return false;
            using var sha=IncrementalHash.CreateHash(HashAlgorithmName.SHA256);byte[] buffer=new byte[8192];long read=0;int count;
            while((count=input.Read(buffer))>0){read+=count;if(read>length)return false;sha.AppendData(buffer,0,count);}
            return read==length && string.Equals(Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant(),hash,StringComparison.Ordinal);
        }
        catch(FileNotFoundException){return !existed;}
        catch(DirectoryNotFoundException){return !existed;}
    }
    private static void WriteDurable(string path,byte[] bytes,Dictionary<string,byte[]>? created=null)
    {
        OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
        using var output=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        created?.Add(path,bytes);
        output.Write(bytes);output.Flush(true);
    }
    private static void WriteJournal(string root,Journal journal,Dictionary<string,byte[]>? created=null)
    {
        string path=JournalPath(root,journal.Id),temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        var owned=created??new Dictionary<string,byte[]>(StringComparer.Ordinal);
        try{WriteDurable(temp,JsonSerializer.SerializeToUtf8Bytes(journal,Json),owned);Move(temp,path,true);}
        finally{if(created is null)CleanupCreated(DirectoryPath(root,journal.Id),owned,deleteDirectory:false);}
    }
    private static void Move(string source,string destination,bool overwrite)
    {
        for(int attempt=0;;attempt++)
        {
            try{File.Move(source,destination,overwrite);return;}
            catch(Exception error) when(OperatingSystem.IsWindows() && attempt<5 &&
                error is IOException or UnauthorizedAccessException && (error.HResult&0xffff) is 5 or 32 or 33)
            {Thread.Sleep(20*(attempt+1));}
        }
    }
    internal static void Apply(ProfileRestorePlan plan,CancellationToken cancellationToken,Action<string>? checkpoint=null)
    {
        plan.RequireLive();string root=Root(plan.ProfileRoot);
        using var locks=new Locks(root,true,cancellationToken);
        if(File.Exists(PointerPath(root)))RecoverCore(root);
        foreach(string name in FileNames)
        {
            if(!plan.Before.ContainsKey(name) || !plan.After.ContainsKey(name))throw new InvalidDataException("Неполный план восстановления.");
            var before=plan.Before[name];
            if(!Matches(Path.Combine(root,name),before is not null,before?.LongLength??0,before is null?"":Digest(before)))
                throw new IOException("Данные профиля изменились после предварительного просмотра. Загрузите копию повторно.");
        }
        cancellationToken.ThrowIfCancellationRequested();
        lock(ActiveGate){if(!Active.Add(root))throw new IOException("Восстановление этого профиля уже выполняется.");}
        var journal=new Journal{Id=Guid.NewGuid().ToString("N"),ProfileIdentity=Identity(root),OwnerPid=Environment.ProcessId,OwnerStarted=Started};
        string directory=DirectoryPath(root,journal.Id);bool pointer=false,directoryCreated=false;
        var created=new Dictionary<string,byte[]>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        try
        {
            OwnedFiles.NoReparseAncestors(directory);if(Directory.Exists(directory)||File.Exists(directory))throw new IOException("Каталог восстановления уже существует.");
            Directory.CreateDirectory(directory);directoryCreated=true;
            foreach(string name in FileNames)
            {
                byte[]? before=plan.Before[name];byte[] after=plan.After[name];
                if(before is not null)WriteDurable(BeforePath(root,journal.Id,name),before,created);
                WriteDurable(AfterPath(root,journal.Id,name),after,created);
                journal.Files.Add(new(){Name=name,Existed=before is not null,BeforeBytes=before?.LongLength??0,
                    BeforeHash=before is null?"":Digest(before),AfterBytes=after.LongLength,AfterHash=Digest(after)});
            }
            WriteJournal(root,journal,created);checkpoint?.Invoke("staged");cancellationToken.ThrowIfCancellationRequested();
            string pointerTemporary=Path.Combine(root,".profile-restore-"+journal.Id+".pointer.tmp");
            WriteDurable(pointerTemporary,JsonSerializer.SerializeToUtf8Bytes(new Pointer{Id=journal.Id},Json),created);
            Move(pointerTemporary,PointerPath(root),false);pointer=true;
            checkpoint?.Invoke("pointer");journal.State="Applying";WriteJournal(root,journal);
            foreach(var item in journal.Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string destination=Path.Combine(root,item.Name);
                if(!Matches(destination,item.Existed,item.BeforeBytes,item.BeforeHash))
                    throw new IOException("Файл профиля изменён другой программой во время восстановления.");
                // The durable staged after-copy is on this same filesystem.
                // Publish it directly: a second write here would introduce a
                // partially written private temp after the recovery pointer.
                string staged=AfterPath(root,journal.Id,item.Name);
                if(!Matches(staged,true,item.AfterBytes,item.AfterHash))
                    throw new InvalidDataException("Подготовленная копия восстановления повреждена.");
                cancellationToken.ThrowIfCancellationRequested();Move(staged,destination,true);
                checkpoint?.Invoke("published:"+item.Name);
            }
            journal.State="Applied";WriteJournal(root,journal);checkpoint?.Invoke("committed");
            Finish(root,journal,true);
        }
        catch(Exception error)
        {
            if(journal.State=="Applied")
                throw new ProfileRestoreException("Данные восстановлены, но очистка восстановления не завершена. Перезапустите приложение перед работой с профилем.",true,true,error);
            if(pointer)
            {
                try{Rollback(root,journal);Finish(root,journal,false);}
                catch(Exception rollback)
                {throw new ProfileRestoreException("Восстановление прервано; откат не завершён. Прежние данные сохранены для восстановления при следующем запуске.",true,false,new AggregateException(error,rollback));}
            }
            else if(directoryCreated)CleanupCreated(directory,created);
            throw;
        }
        finally{lock(ActiveGate)Active.Remove(root);}
    }

    private static void RecoverCore(string root,bool deferLiveOwner=false)
    {
        lock(ActiveGate)if(Active.Contains(root))throw new IOException("Восстановление профиля ещё выполняется.");
        string pointerPath=PointerPath(root);OwnedFiles.NoReparseAncestors(pointerPath);OwnedFiles.RegularOrAbsent(pointerPath);
        if(!File.Exists(pointerPath))return;
        if(new FileInfo(pointerPath).Length>4096)throw new InvalidDataException("Повреждён указатель восстановления профиля; файлы оставлены без изменений.");
        byte[] pointerBytes=File.ReadAllBytes(pointerPath);using var pointerJson=WindowsProfileBackup.ParseJson(pointerBytes);
        var pointer=pointerJson.RootElement.Deserialize<Pointer>(Json)??throw new InvalidDataException("Пустой указатель восстановления.");
        if(pointer.Version!=1 || !Guid.TryParseExact(pointer.Id,"N",out _))throw new InvalidDataException("Непригодный указатель восстановления профиля.");
        string manifest=JournalPath(root,pointer.Id);OwnedFiles.NoReparseAncestors(manifest);OwnedFiles.RegularOrAbsent(manifest);
        if(new FileInfo(manifest).Length>16*1024)throw new InvalidDataException("Журнал восстановления слишком велик.");
        byte[] journalBytes=File.ReadAllBytes(manifest);using var journalJson=WindowsProfileBackup.ParseJson(journalBytes);
        var journal=journalJson.RootElement.Deserialize<Journal>(Json)??throw new InvalidDataException("Пустой журнал восстановления.");
        Validate(root,pointer,journal);
        if(journal.State is "Prepared" or "Applying" or "RollingBack" or "RollbackFailed")
        {
            if(IsRunning(journal.OwnerPid,journal.OwnerStarted))
            {
                if(deferLiveOwner)return;
                throw new IOException("Владелец незавершённого восстановления ещё работает. Завершите его перед восстановлением профиля.");
            }
            Rollback(root,journal);Finish(root,journal,false);
        }
        else Finish(root,journal,journal.State=="Applied");
    }
    private static void Validate(string root,Pointer pointer,Journal journal)
    {
        if(journal.Kind!=Kind || journal.Version!=1 || journal.Id!=pointer.Id || journal.ProfileIdentity!=Identity(root) ||
            journal.OwnerPid<=0 || journal.OwnerStarted<=0 || journal.State is not ("Prepared" or "Applying" or "RollingBack" or "RollbackFailed" or "Applied" or "RolledBack") ||
            journal.Files is null || journal.Files.Count!=FileNames.Length || journal.Files.Select(f=>f.Name).Distinct(StringComparer.Ordinal).Count()!=FileNames.Length)
            throw new InvalidDataException("Непригодный журнал восстановления; файлы не изменены.");
        foreach(var item in journal.Files)
            if(!FileNames.Contains(item.Name,StringComparer.Ordinal) || item.BeforeBytes<0 || item.BeforeBytes>MaxProfileBytes || item.AfterBytes<0 || item.AfterBytes>MaxProfileBytes ||
                !Hash.IsMatch(item.AfterHash) || (item.Existed?!Hash.IsMatch(item.BeforeHash):item.BeforeHash.Length!=0 || item.BeforeBytes!=0))
                throw new InvalidDataException("Непригодные файлы журнала восстановления.");
    }
    private static bool IsRunning(int pid,long ticks)
    {
        try{using var process=Process.GetProcessById(pid);return !process.HasExited && process.StartTime.ToUniversalTime().Ticks==ticks;}
        catch(ArgumentException){return false;}catch(InvalidOperationException){return false;}
        catch(System.ComponentModel.Win32Exception){return true;}
    }
    private static void Rollback(string root,Journal journal)
    {
        journal.State="RollingBack";WriteJournal(root,journal);
        try
        {
            foreach(var item in journal.Files.AsEnumerable().Reverse())
            {
                string destination=Path.Combine(root,item.Name);
                if(Matches(destination,item.Existed,item.BeforeBytes,item.BeforeHash))continue;
                if(!Matches(destination,true,item.AfterBytes,item.AfterHash))
                    throw new IOException("Файл профиля изменён вне восстановления. Автоматический откат его не перезаписывает.");
                if(item.Existed)
                {
                    string before=BeforePath(root,journal.Id,item.Name);
                    if(!Matches(before,true,item.BeforeBytes,item.BeforeHash))throw new InvalidDataException("Прежняя копия файла восстановления повреждена.");
                    Move(before,destination,true);
                }
                else File.Delete(destination);
            }
            journal.State="RolledBack";WriteJournal(root,journal);
        }
        catch{journal.State="RollbackFailed";WriteJournal(root,journal);throw;}
    }
    private static void Finish(string root,Journal journal,bool applied)
    {
        foreach(var item in journal.Files)
            if(!Matches(Path.Combine(root,item.Name),applied||item.Existed,applied?item.AfterBytes:item.BeforeBytes,applied?item.AfterHash:item.BeforeHash))
                throw new IOException("Файлы профиля не совпадают с завершённым восстановлением.");
        // Remove the pointer only after the entire profile has one coherent state.
        byte[] pointer=JsonSerializer.SerializeToUtf8Bytes(new Pointer{Id=journal.Id},Json);
        if(!Matches(PointerPath(root),true,pointer.LongLength,Digest(pointer)))
            throw new IOException("Указатель восстановления изменён другой программой; он оставлен без изменений.");
        File.Delete(PointerPath(root));CleanupUnpublished(root,journal);
    }
    private static void CleanupUnpublished(string root,Journal journal)
    {
        string directory=DirectoryPath(root,journal.Id);
        try
        {
            foreach(var item in journal.Files)
            {
                string before=BeforePath(root,journal.Id,item.Name);
                if(item.Existed && Matches(before,true,item.BeforeBytes,item.BeforeHash))File.Delete(before);
                foreach(string path in new[]{AfterPath(root,journal.Id,item.Name),TemporaryPath(root,journal.Id,item.Name)})
                    if(Matches(path,true,item.AfterBytes,item.AfterHash))File.Delete(path);
            }
            string manifest=JournalPath(root,journal.Id);byte[] expected=JsonSerializer.SerializeToUtf8Bytes(journal,Json);
            if(Matches(manifest,true,expected.LongLength,Digest(expected)))File.Delete(manifest);
            // A foreign extra entry is never deleted recursively.
            if(Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);
        }
        catch(IOException){ }catch(UnauthorizedAccessException){ }
    }
    private static void CleanupCreated(string directory,Dictionary<string,byte[]> created,bool deleteDirectory=true)
    {
        // Only paths for which CreateNew succeeded belong to this attempt.
        // A colliding or subsequently replaced file is left untouched.
        foreach(var item in created)
        {
            try
            {
                OwnedFiles.NoReparseAncestors(item.Key);OwnedFiles.RegularOrAbsent(item.Key);
                using(var input=new FileStream(item.Key,FileMode.Open,FileAccess.Read,FileShare.Read))
                {
                    if(input.Length>item.Value.LongLength)continue;
                    byte[] buffer=new byte[8192];long offset=0;int count;bool same=true;
                    while((count=input.Read(buffer))>0)
                    {if(offset+count>item.Value.LongLength || !buffer.AsSpan(0,count).SequenceEqual(item.Value.AsSpan((int)offset,count))){same=false;break;}offset+=count;}
                    if(!same)continue;
                }
                File.Delete(item.Key);
            }
            catch(IOException){ }catch(UnauthorizedAccessException){ }
        }
        try{if(deleteDirectory && Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())Directory.Delete(directory);}
        catch(IOException){ }catch(UnauthorizedAccessException){ }
    }
    private static JsonSerializerOptions CreateJson()
    {
        var resolver=new DefaultJsonTypeInfoResolver();
        resolver.Modifiers.Add(info=>{if(info.Kind==JsonTypeInfoKind.Object)foreach(var property in info.Properties)property.IsRequired=true;});
        return new(){UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,MaxDepth=16,TypeInfoResolver=resolver};
    }
}
