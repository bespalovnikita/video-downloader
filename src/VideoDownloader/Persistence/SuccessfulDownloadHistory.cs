using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

/// <summary>Owns the append-only successful-download history and its cross-process lock.</summary>
internal sealed class SuccessfulDownloadHistory
{
    private const int MaxRecordBytes=1024*1024;
    private const int MaxPaths=1024;
    private const int MaxMetadataCharacters=16*1024;
    private static readonly UTF8Encoding StrictUtf8=new(false,true);
    private readonly string _path;
    private (long Length,DateTime LastWrite)? _validated;

    internal SuccessfulDownloadHistory(string path) => _path = path;

    private static Mutex HistoryMutex() => new(false,@"Local\VideoDownloader.SuccessHistory");
    private static T WithHistoryLock<T>(Func<T> action)
    {
        using var mutex=HistoryMutex();
        bool locked=false;
        try
        {
            try { locked=mutex.WaitOne(TimeSpan.FromSeconds(15)); }
            catch(AbandonedMutexException) { locked=true; }
            if (!locked) throw new TimeoutException("Cannot acquire download-history lock.");
            return action();
        }
        finally { if (locked) mutex.ReleaseMutex(); }
    }
    public Dictionary<string,DateTimeOffset?> ReadHistory() => WithHistoryLock(() =>
    {
        var seen = new Dictionary<string,DateTimeOffset?>(StringComparer.Ordinal);
        foreach(var entry in ReadEntries(CancellationToken.None))
        {
            if(!seen.TryGetValue(entry.Url,out var previous) ||
                entry.CompletedAtUtc.HasValue && (!previous.HasValue || entry.CompletedAtUtc.Value>previous.Value))
                seen[entry.Url]=entry.CompletedAtUtc;
        }
        _validated=Stamp();
        return seen;
    });

    public DownloadHistoryLibrary ReadLibrary(string query,int limit,CancellationToken cancellationToken)=>WithHistoryLock(()=>
    {
        if(query.Length>4096)throw new ArgumentException("History search is too long.",nameof(query));
        if(limit is <1 or >10000)throw new ArgumentOutOfRangeException(nameof(limit));
        const int characterBudget=8*1024*1024;
        var retained=new Queue<DownloadHistoryEntry>();
        long total=0,matches=0,characters=0;
        query=query.Trim();
        foreach(var entry in ReadEntries(cancellationToken))
        {
            total++;
            if(!entry.Matches(query))continue;
            matches++;
            retained.Enqueue(entry);characters+=Size(entry);
            while(retained.Count>limit || characters>characterBudget)
                characters-=Size(retained.Dequeue());
        }
        _validated=Stamp();
        return new DownloadHistoryLibrary(retained.Reverse().ToArray(),total,matches);
    });

    private static long Size(DownloadHistoryEntry entry)=>entry.Url.Length+entry.Title.Length+entry.Source.Length+
        entry.OutputPaths.Sum(path=>(long)path.Length+32)+128;

    internal IReadOnlyList<DownloadHistoryEntry> ReadBackup(CancellationToken cancellationToken)=>WithHistoryLock(() =>
    {
        var latest=new Dictionary<string,DownloadHistoryEntry>(StringComparer.Ordinal);long characters=0;
        foreach(var entry in ReadEntries(cancellationToken,ProfileBackupTransaction.MaxProfileBytes))
        {
            if(latest.TryGetValue(entry.Url,out var old) && old.CompletedAtUtc.HasValue &&
                (!entry.CompletedAtUtc.HasValue || old.CompletedAtUtc.Value>entry.CompletedAtUtc.Value))continue;
            characters-=old is null?0:(long)old.Url.Length+old.Title.Length+old.Source.Length;
            var retained=entry with {OutputPaths=Array.Empty<string>(),OutputPathsTruncated=false};
            latest[entry.Url]=retained;characters+=(long)retained.Url.Length+retained.Title.Length+retained.Source.Length;
            if(latest.Count>WindowsProfileBackup.MaxRecords || characters>WindowsProfileBackup.MaxPayloadBytes)
                throw new InvalidDataException("История превышает предел резервной копии: 100 000 URL или 32 МиБ метаданных.");
        }
        return latest.Values.ToArray();
    });
    private IEnumerable<DownloadHistoryEntry> ReadEntries(CancellationToken cancellationToken,long maxBytes=long.MaxValue)
    {
        using var input=OpenExisting();
        if(input is null)yield break;
        using var line=new MemoryStream();
        byte[] buffer=new byte[8192];
        long number=1;
        int count;long totalBytes=0;
        while((count=input.Read(buffer))!=0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            totalBytes+=count;if(totalBytes>maxBytes)throw new InvalidDataException("История превышает предел чтения резервной копии.");
            for(int index=0;index<count;index++)
            {
                if(buffer[index]==10)
                {
                    var entry=ParseLine(line,number++);
                    line.SetLength(0);line.Position=0;
                    if(entry is not null)yield return entry;
                }
                else
                {
                    if(line.Length>=MaxRecordBytes)throw Damaged(number,new InvalidDataException("History record exceeds 1 MiB."));
                    line.WriteByte(buffer[index]);
                }
            }
        }
        if(line.Length>0 && ParseLine(line,number) is {} tail)yield return tail;
    }
    private FileStream? OpenExisting()
    {
        try {return new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }
    private static InvalidDataException Damaged(long number,Exception cause)=>
        new($"Damaged download history at line {number}. No changes made.",cause);
    private static DownloadHistoryEntry? ParseLine(MemoryStream bytes,long number)
    {
        try
        {
            string raw=StrictUtf8.GetString(bytes.GetBuffer(),0,checked((int)bytes.Length));
            if(number==1)raw=raw.TrimStart('\uFEFF');
            if(string.IsNullOrWhiteSpace(raw))return null;
            using var doc=JsonDocument.Parse(raw,new JsonDocumentOptions{MaxDepth=16});
            var item=doc.RootElement;
            if(item.GetProperty("Version").GetInt32()!=1)throw new InvalidDataException("Bad version");
            string url=item.GetProperty("Url").GetString()??"";
            if(!AppPersistence.ValidUrl(url))throw new InvalidDataException("Invalid URL");
            DateTimeOffset? timestamp=null;
            if(item.TryGetProperty("CompletedAtUtc",out var at) && at.ValueKind==JsonValueKind.String)
                timestamp=DateTimeOffset.Parse(at.GetString()!,System.Globalization.CultureInfo.InvariantCulture);
            string Text(string name,int max)
            {
                if(!item.TryGetProperty(name,out var value) || value.ValueKind==JsonValueKind.Null)return "";
                if(value.ValueKind!=JsonValueKind.String || value.GetString()!.Length>max)throw new InvalidDataException("Invalid history metadata.");
                return value.GetString()!;
            }
            var paths=new List<string>();
            if(item.TryGetProperty("OutputPaths",out var stored) && stored.ValueKind!=JsonValueKind.Null)
            {
                if(stored.ValueKind!=JsonValueKind.Array || stored.GetArrayLength()>MaxPaths)throw new InvalidDataException("Invalid history paths.");
                foreach(var path in stored.EnumerateArray())
                {
                    if(path.ValueKind!=JsonValueKind.String || path.GetString()!.Length>32768)throw new InvalidDataException("Invalid history path.");
                    paths.Add(path.GetString()!);
                }
            }
            bool truncated=false;
            if(item.TryGetProperty("OutputPathsTruncated",out var flag))
            {
                if(flag.ValueKind is not (JsonValueKind.True or JsonValueKind.False))throw new InvalidDataException("Invalid history path flag.");
                truncated=flag.GetBoolean();
            }
            return new(number,url,timestamp,Text("Title",4096),Text("Source",1024),paths.ToArray(),truncated);
        }
        catch(Exception error) when(error is not OperationCanceledException){throw Damaged(number,error);}
    }
    private (long Length,DateTime LastWrite)? Stamp()
    {
        var file=new FileInfo(_path);
        try {return (file.Length,file.LastWriteTimeUtc);}
        catch(FileNotFoundException){return null;}
        catch(DirectoryNotFoundException){return null;}
    }
    public void AppendSuccess(string url,string title="",string source="",IEnumerable<string>? outputPaths=null)
    {
        if (!AppPersistence.ValidUrl(url)) throw new ArgumentException("Cannot record a non-HTTP URL.",nameof(url));
        title=Truncate(title,4096);source=Truncate(source,1024);
        var paths=new List<string>();
        int characters=title.Length+source.Length;bool truncated=false;
        foreach(string path in outputPaths??[])
        {
            if(paths.Count>=MaxPaths || path.Length>32768 || characters+path.Length>MaxMetadataCharacters)
            {truncated=true;continue;}
            paths.Add(path);characters+=path.Length;
        }
        WithHistoryLock(()=>
        {
            var stamp=Stamp();
            if(_validated!=stamp)
            {
                foreach(var _ in ReadEntries(CancellationToken.None)) { }
                _validated=stamp;
            }
            bool separator=false;
            if (File.Exists(_path))
            {
                using var existing=new FileStream(_path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
                if (existing.Length>0)
                {
                    existing.Seek(-1,SeekOrigin.End);
                    separator=existing.ReadByte() is not (10 or 13);
                }
            }
            var obj=new {Version=1,Url=url,CompletedAtUtc=DateTime.UtcNow.ToString("O"),
                Title=title,Source=source,OutputPaths=paths,OutputPathsTruncated=truncated};
            var data=Encoding.UTF8.GetBytes((separator?"\n":"")+JsonSerializer.Serialize(obj,AppPersistence.Json)+"\n");
            if(data.Length>MaxRecordBytes)throw new InvalidDataException("History record exceeds 1 MiB.");
            using(var stream=new FileStream(_path,FileMode.Append,FileAccess.Write,FileShare.Read))
            {
                stream.Write(data);
                stream.Flush(true);
            }
            _validated=Stamp();
            return 0;
        });
    }
    private static string Truncate(string value,int limit)
    {
        if(value.Length<=limit)return value;
        if(char.IsHighSurrogate(value[limit-1]))limit--;
        return value[..limit];
    }
}
