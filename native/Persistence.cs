using System.Text;
using System.Text.Json;
using System.Diagnostics;

namespace VideoDownloader.Native;

public sealed class AppPersistence
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
    private readonly string _dir;
    public string SessionPath => Path.Combine(_dir, "session.json");
    public string HistoryPath => Path.Combine(_dir, "successful-downloads.jsonl");
    public string PreviewDir => Path.Combine(_dir,"preview-cache");
    public AppPersistence(string? directory = null)
    {
        _dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VideoDownloader");
        Directory.CreateDirectory(_dir);
    }

    public SessionSnapshot? Restore()
    {
        if (!File.Exists(SessionPath)) return null;
        if (new FileInfo(SessionPath).Length > 16*1024*1024) throw new InvalidDataException("Session checkpoint exceeds 16 MiB.");
        var session = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(SessionPath,Encoding.UTF8),Json)
                      ?? throw new InvalidDataException("Empty session checkpoint.");
        if (session.Version != 1 || session.Queue is null || session.Queue.Count > 100000 || session.Settings is null)
            throw new InvalidDataException("Invalid session checkpoint format.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in session.Queue)
        {
            if (!ValidUrl(row.Url) || !seen.Add(row.Url) ||
                row.Status is not ("Queued" or "Running" or "Completed" or "Failed" or "Interrupted"))
                throw new InvalidDataException("Invalid or duplicate checkpoint URL/status.");
            if (row.Status == "Running") { row.Status="Interrupted"; row.State="Interrupted"; }
            if (row.Status == "Completed") { row.State="Done"; row.Progress=100; }
            row.OwnedPaths ??= new List<string>();
        }
        MergeLegacyResultEvents(session);
        return session;
    }

    public static bool LegacyEngineIsAlive(object? engine)
    {
        if (engine is not JsonElement record || record.ValueKind!=JsonValueKind.Object ||
            !record.TryGetProperty("Pid",out var pidToken) || !pidToken.TryGetInt32(out int pid) ||
            pid<=0 || !record.TryGetProperty("StartedUtc",out var startedToken) ||
            startedToken.ValueKind!=JsonValueKind.String ||
            !DateTimeOffset.TryParse(startedToken.GetString(),out var recorded))
            return false;
        try
        {
            using var process=Process.GetProcessById(pid);
            return process.ProcessName.Equals("pwsh",StringComparison.OrdinalIgnoreCase) &&
                Math.Abs((process.StartTime.ToUniversalTime()-recorded.UtcDateTime).TotalSeconds)<3;
        }
        catch(ArgumentException){return false;}
        catch(InvalidOperationException){return false;}
        catch(System.ComponentModel.Win32Exception){return false;}
    }

    private static void MergeLegacyResultEvents(SessionSnapshot session)
    {
        if (session.Engine is not JsonElement engine || engine.ValueKind!=JsonValueKind.Object ||
            !engine.TryGetProperty("EventsPath",out var eventsProp) ||
            eventsProp.ValueKind!=JsonValueKind.String) return;
        var path=eventsProp.GetString();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
        // An old append-only events file can contain a truncated tail line.
        // Ignore malformed event lines, but never infer success from progress.
        if (new FileInfo(path).Length>128*1024*1024) return;
        var rows=session.Queue.ToDictionary(row=>row.Url,StringComparer.Ordinal);
        foreach(var line in File.ReadLines(path,Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))continue;
            try
            {
                using var document=JsonDocument.Parse(line);
                var ev=document.RootElement;
                if (ev.ValueKind!=JsonValueKind.Object ||
                    !ev.TryGetProperty("Kind",out var kind) || kind.GetString()!="Result" ||
                    !ev.TryGetProperty("Url",out var urlValue) ||
                    !rows.TryGetValue(urlValue.GetString()??"",out var row))
                    continue;
                if (ev.TryGetProperty("Success",out var success) && success.ValueKind==JsonValueKind.True)
                {
                    row.Status="Completed";row.State="Done";row.Progress=100;
                    if (ev.TryGetProperty("Path",out var media) && media.ValueKind==JsonValueKind.String)
                        row.Path=media.GetString()??"";
                    if (ev.TryGetProperty("OwnedPaths",out var owned) && owned.ValueKind==JsonValueKind.Array)
                        row.OwnedPaths=owned.EnumerateArray()
                            .Where(v=>v.ValueKind==JsonValueKind.String)
                            .Select(v=>v.GetString()??"")
                            .Where(v=>!string.IsNullOrWhiteSpace(v)).ToList();
                }
                else if (ev.TryGetProperty("Cancelled",out var cancelled) && cancelled.ValueKind==JsonValueKind.True)
                {
                    if (row.Status!="Completed") {row.Status="Interrupted";row.State="Cancelled";}
                }
                else if (row.Status!="Completed")
                {
                    row.Status="Failed";row.State="Failed";
                }
            }
            catch(JsonException){} // a partial JSONL tail after a crash
            catch(InvalidOperationException){} // malformed event property types
        }
    }

    public void Save(IReadOnlyCollection<DownloadItem> queue, DownloadOptions settings)
    {
        var snapshot = new SessionSnapshot { Queue=queue.ToList(),Settings=settings,Engine=null };
        var json = JsonSerializer.Serialize(snapshot,Json);
        AtomicWrite(SessionPath,json);
    }
    public static void AtomicWrite(string path,string contents)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temp=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            File.WriteAllText(temp,contents,new UTF8Encoding(false));
            File.Move(temp,path,true);
        }
        finally { if(File.Exists(temp)) File.Delete(temp); }
    }
    public static bool ValidUrl(string? candidate) =>
        Uri.TryCreate(candidate,UriKind.Absolute,out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && !string.IsNullOrEmpty(uri.Host);

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
        if (!File.Exists(HistoryPath)) return seen;
        int line=0;
        foreach (var raw in File.ReadLines(HistoryPath,Encoding.UTF8))
        {
            line++;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                using var doc=JsonDocument.Parse(raw);
                var item=doc.RootElement;
                if (item.GetProperty("Version").GetInt32()!=1) throw new InvalidDataException("Bad version");
                var url=item.GetProperty("Url").GetString() ?? "";
                if (!ValidUrl(url)) throw new InvalidDataException("Invalid URL");
                DateTimeOffset? timestamp=null;
                if (item.TryGetProperty("CompletedAtUtc",out var at) && at.ValueKind==JsonValueKind.String)
                    timestamp=DateTimeOffset.Parse(at.GetString()!,System.Globalization.CultureInfo.InvariantCulture);
                if (!seen.ContainsKey(url) || timestamp>seen[url]) seen[url]=timestamp;
            }
            catch(Exception e) { throw new InvalidDataException($"Damaged download history at line {line}. No changes made.",e); }
        }
        return seen;
    });
    public void AppendSuccess(string url)
    {
        if (!ValidUrl(url)) throw new ArgumentException("Cannot record a non-HTTP URL.",nameof(url));
        WithHistoryLock(()=>
        {
            var obj=new {Version=1,Url=url,CompletedAtUtc=DateTime.UtcNow.ToString("O")};
            var data=Encoding.UTF8.GetBytes(JsonSerializer.Serialize(obj,Json)+"\n");
            using var stream=new FileStream(HistoryPath,FileMode.Append,FileAccess.Write,FileShare.Read);
            stream.Write(data);
            stream.Flush(true);
            return 0;
        });
    }
}
