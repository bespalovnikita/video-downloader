using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

/// <summary>Owns the append-only successful-download history and its cross-process lock.</summary>
internal sealed class SuccessfulDownloadHistory
{
    private readonly string _path;

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
        if (!File.Exists(_path)) return seen;
        int line=0;
        foreach (var raw in File.ReadLines(_path,Encoding.UTF8))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc=JsonDocument.Parse(raw);
                var item=doc.RootElement;
                if (item.GetProperty("Version").GetInt32()!=1) throw new InvalidDataException("Bad version");
                var url=item.GetProperty("Url").GetString() ?? "";
                if (!AppPersistence.ValidUrl(url)) throw new InvalidDataException("Invalid URL");
                DateTimeOffset? timestamp=null;
                if (item.TryGetProperty("CompletedAtUtc",out var at) && at.ValueKind==JsonValueKind.String)
                    timestamp=DateTimeOffset.Parse(at.GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                if (!seen.TryGetValue(url,out var previous) ||
                    timestamp.HasValue && (!previous.HasValue || timestamp.Value>previous.Value))
                    seen[url]=timestamp;
            }
            catch(Exception e) { throw new InvalidDataException($"Damaged download history at line {line}. No changes made.",e); }
        }
        return seen;
    });
    public void AppendSuccess(string url)
    {
        if (!AppPersistence.ValidUrl(url)) throw new ArgumentException("Cannot record a non-HTTP URL.",nameof(url));
        WithHistoryLock(()=>
        {
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
            // The mutex is reentrant. Validate before changing an unterminated
            // tail, preserving corrupt/truncated history byte for byte.
            if (separator) ReadHistory();
            var obj=new {Version=1,Url=url,CompletedAtUtc=DateTime.UtcNow.ToString("O")};
            var data=Encoding.UTF8.GetBytes((separator?"\n":"")+JsonSerializer.Serialize(obj,AppPersistence.Json)+"\n");
            using var stream=new FileStream(_path,FileMode.Append,FileAccess.Write,FileShare.Read);
            stream.Write(data);
            stream.Flush(true);
            return 0;
        });
    }
}
