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
            row.CleanupPaths ??= new List<string>();
        }
        // A live legacy worker still owns its checkpoint and may hold the
        // append-only event stream open. Let the GUI see its identity without
        // racing that writer or turning a sharing violation into a missed guard.
        session.LegacyEngineWasActive=LegacyEngineIsAlive(session.Engine);
        if (!session.LegacyEngineWasActive) MergeLegacyResultEvents(session);
        foreach (var row in session.Queue)
        {
            if (!session.LegacyEngineWasActive) row.AttemptFinished=true;
            if (row.CleanupError.Length>0 || row.PendingRemoval || row.CleanupPaths.Count>0)
            {
                row.PendingRemoval=true;row.Status="Interrupted";
                row.State=row.CleanupError.Length>0 ? "Cleanup failed — Remove to retry" : "Removal pending — use Remove";
            }
            // Older v1 snapshots have no per-result root. The saved base output
            // also contains dated archive folders, regardless of today's date.
            if (string.IsNullOrWhiteSpace(row.OutputRoot))
                row.OutputRoot=Path.GetFullPath(session.Settings.Output);
        }
        return session;
    }

    public static bool LegacyEngineIsAlive(object? engine)
    {
        if (engine is not JsonElement record || record.ValueKind!=JsonValueKind.Object ||
            !record.TryGetProperty("Pid",out var pidToken) || pidToken.ValueKind!=JsonValueKind.Number ||
            !pidToken.TryGetInt32(out int pid) ||
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
                    !ev.TryGetProperty("Kind",out var kind) || kind.ValueKind!=JsonValueKind.String || kind.GetString()!="Result" ||
                    !ev.TryGetProperty("Url",out var urlValue) || urlValue.ValueKind!=JsonValueKind.String ||
                    !rows.TryGetValue(urlValue.GetString()??"",out var row))
                    continue;
                if (row.DeferredToNextStart) continue;
                // Validate terminal fields before changing the row. A partial
                // or malformed Result is not proof of success, failure or cancel.
                if (!ev.TryGetProperty("Success",out var success) ||
                    success.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                bool hasCancelled=ev.TryGetProperty("Cancelled",out var cancelled);
                if (hasCancelled && cancelled.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                if (success.ValueKind==JsonValueKind.True && hasCancelled && cancelled.ValueKind==JsonValueKind.True) continue;
                if (ev.TryGetProperty("Path",out var media) && media.ValueKind!=JsonValueKind.String) continue;
                bool hasOwned=ev.TryGetProperty("OwnedPaths",out var owned);
                if (hasOwned && (owned.ValueKind!=JsonValueKind.Array ||
                    owned.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String))) continue;
                if (ev.TryGetProperty("CleanupPaths",out var cleanup) &&
                    (cleanup.ValueKind!=JsonValueKind.Array || cleanup.EnumerateArray().Any(v=>v.ValueKind!=JsonValueKind.String))) continue;
                if (new[]{"OutputRoot","StageDir","CleanupError"}.Any(name=>
                    ev.TryGetProperty(name,out var value) && value.ValueKind!=JsonValueKind.String)) continue;
                row.AttemptFinished=true;
                if (ev.TryGetProperty("OutputRoot",out var output)) row.OutputRoot=output.GetString()??"";
                if (engine.TryGetProperty("RunDir",out var run) && run.ValueKind==JsonValueKind.String)
                    row.RunDir=run.GetString()??"";
                if (ev.TryGetProperty("StageDir",out var stage)) row.StageDir=stage.GetString()??"";
                if (cleanup.ValueKind==JsonValueKind.Array)
                    row.CleanupPaths=cleanup.EnumerateArray().Select(v=>v.GetString()!).ToList();
                if (ev.TryGetProperty("CleanupError",out var error)) row.CleanupError=error.GetString()??"";
                if (row.CleanupError.Length>0)
                {
                    row.PendingRemoval=true;row.Status="Interrupted";row.State="Cleanup failed — Remove to retry";
                    if (hasOwned) row.OwnedPaths=owned.EnumerateArray().Select(v=>v.GetString()!).ToList();
                    continue;
                }
                if (success.ValueKind==JsonValueKind.True)
                {
                    row.Status="Completed";row.State="Done";row.Progress=100;
                    if (media.ValueKind==JsonValueKind.String)
                        row.Path=media.GetString()??"";
                    if (hasOwned)
                        row.OwnedPaths=owned.EnumerateArray()
                            .Where(v=>v.ValueKind==JsonValueKind.String)
                            .Select(v=>v.GetString()??"")
                            .Where(v=>!string.IsNullOrWhiteSpace(v)).ToList();
                }
                else if (hasCancelled && cancelled.ValueKind==JsonValueKind.True)
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
        if (!ValidUrl(url)) throw new ArgumentException("Cannot record a non-HTTP URL.",nameof(url));
        WithHistoryLock(()=>
        {
            bool separator=false;
            if (File.Exists(HistoryPath))
            {
                using var existing=new FileStream(HistoryPath,FileMode.Open,FileAccess.Read,FileShare.ReadWrite);
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
            var data=Encoding.UTF8.GetBytes((separator?"\n":"")+JsonSerializer.Serialize(obj,Json)+"\n");
            using var stream=new FileStream(HistoryPath,FileMode.Append,FileAccess.Write,FileShare.Read);
            stream.Write(data);
            stream.Flush(true);
            return 0;
        });
    }
}
