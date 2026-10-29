using System.Text;
using System.Text.Json;
using VideoDownloader.Native.Compatibility;

namespace VideoDownloader.Native;

public sealed class AppPersistence
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true, WriteIndented = false };
    internal const int MaxCheckpointBytes=16*1024*1024;
    internal const int MaxQueueItems=100_000;
    private readonly string _dir;
    private readonly SuccessfulDownloadHistory _history;
    public string SessionPath => Path.Combine(_dir, "session.json");
    public string HistoryPath => Path.Combine(_dir, "successful-downloads.jsonl");
    public string PreviewDir => Path.Combine(_dir,"preview-cache");
    public AppPersistence(string? directory = null)
    {
        _dir = directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"VideoDownloader");
        Directory.CreateDirectory(_dir);
        _history = new SuccessfulDownloadHistory(HistoryPath);
    }

    public SessionSnapshot? Restore()
    {
        if (!File.Exists(SessionPath)) return null;
        if (new FileInfo(SessionPath).Length > MaxCheckpointBytes) throw new InvalidDataException("Session checkpoint exceeds 16 MiB.");
        var session = JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(SessionPath,Encoding.UTF8),Json)
                      ?? throw new InvalidDataException("Empty session checkpoint.");
        if (session.Version != 1 || session.Queue is null || session.Queue.Count > MaxQueueItems || session.Settings is null)
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
            if (row.SubscriptionId is null || row.SubscriptionName is null ||
                (row.SubscriptionName.Length>0 && !SubscriptionNames.IsValid(row.SubscriptionName)))
                throw new InvalidDataException("Invalid subscription folder in session checkpoint.");
        }
        LegacySessionReader.Reconcile(session);
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

    public static bool LegacyEngineIsAlive(object? engine) => LegacySessionReader.IsEngineAlive(engine);

    public void Save(IReadOnlyCollection<DownloadItem> queue, DownloadOptions settings)
    {
        using var json=new CheckpointBuffer();
        SerializeCheckpoint(json,queue,settings,DateTime.UtcNow);
        AtomicWrite(SessionPath,Encoding.UTF8.GetString(json.GetBuffer(),0,checked((int)json.Length)));
    }
    internal static void ValidateCanSave(IReadOnlyCollection<DownloadItem> queue,DownloadOptions settings)
    {
        // Use the longest UTC timestamp representation so the subsequent Save
        // cannot cross the byte boundary merely because its timestamp changed.
        using var json=new CheckpointBuffer();
        SerializeCheckpoint(json,queue,settings,new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc).AddTicks(1));
    }
    private static void SerializeCheckpoint(Stream stream,IReadOnlyCollection<DownloadItem> queue,DownloadOptions settings,DateTime savedAtUtc)
    {
        if(queue.Count>MaxQueueItems)throw new InvalidDataException("Очередь не помещается в сохранение: допустимо не более 100 000 ссылок. Перенеси её частями.");
        JsonSerializer.Serialize(stream,new SessionSnapshot{Queue=queue.ToList(),Settings=settings,Engine=null,SavedAtUtc=savedAtUtc},Json);
    }
    private sealed class CheckpointBuffer:MemoryStream
    {
        private void Check(int count)
        {
            if(count<0 || Length+count>MaxCheckpointBytes)
                throw new InvalidDataException("Очередь не помещается в сохранение размером 16 МиБ. Перенеси её частями или сократи названия.");
        }
        public override void Write(byte[] buffer,int offset,int count){Check(count);base.Write(buffer,offset,count);}
        public override void Write(ReadOnlySpan<byte> buffer){Check(buffer.Length);base.Write(buffer);}
        public override void WriteByte(byte value){Check(1);base.WriteByte(value);}
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

    public Dictionary<string,DateTimeOffset?> ReadHistory() => _history.ReadHistory();
    public void AppendSuccess(string url) => _history.AppendSuccess(url);
}
