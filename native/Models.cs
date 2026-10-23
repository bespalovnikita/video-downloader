using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace VideoDownloader.Native;

public sealed class DownloadItem : INotifyPropertyChanged
{
    private int _order;
    private string _state = "Queued";
    private string _status = "Queued";
    private double _progress;
    private string _title = "";
    private string _speed = "";
    private string _downloaded = "";
    private string _eta = "";
    private string _path = "";
    private string _rawError = "";
    private string _slot = "";
    private string _url = "";
    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null)
    {
        var dispatcher=System.Windows.Application.Current?.Dispatcher;
        if (dispatcher!=null && !dispatcher.CheckAccess())
            _=dispatcher.BeginInvoke(() => PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name)));
        else PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(name));
    }
    public int Order { get => _order; set { _order = value; Changed(); } }
    public string Url { get => _url; set { _url = value; Changed(); } }
    public string State { get => _state; set { _state = value; Changed(); } }
    public string Status { get => _status; set { _status = value; Changed(); } }
    public double Progress { get => _progress; set { _progress = value; Changed(); } }
    public string Title { get => _title; set { _title = value; Changed(); } }
    public string Speed { get => _speed; set { _speed = value; Changed(); } }
    public string Downloaded { get => _downloaded; set { _downloaded = value; Changed(); } }
    public string ETA { get => _eta; set { _eta = value; Changed(); } }
    public string Path { get => _path; set { _path = value; Changed(); } }
    public string RawError { get => _rawError; set { _rawError = value; Changed(); } }
    public string Slot { get => _slot; set { _slot = value; Changed(); } }
    // Captured when a subscription creates this queue row. Renaming the source
    // later must not redirect an already accepted download or its cleanup.
    public string SubscriptionId { get; set; } = "";
    public string SubscriptionName { get; set; } = "";
    public List<string> OwnedPaths { get; set; } = new();
    // The publication boundary belongs to the result, not the next session.
    public string OutputRoot { get; set; } = "";
    public string RunDir { get; set; } = "";
    public string StageDir { get; set; } = "";
    public List<string> CleanupPaths { get; set; } = new();
    public string CleanupError { get; set; } = "";
    public bool PendingRemoval { get; set; }
    public bool AttemptFinished { get; set; }
    public bool DeferredToNextStart { get; set; }
    [JsonIgnore] public bool CanResume => !PendingRemoval && CleanupError.Length == 0;
    public long TransferredBytes { get; set; }
    public string DisplayState { get => State; set => State = value; }
}

public sealed class DownloadOptions
{
    public string Profile { get; set; } = "Universal";
    public string Quality { get; set; } = "Best";
    public string Codec { get; set; } = "Auto";
    public string Container { get; set; } = "Auto";
    public string RateLimit { get; set; } = "Unlimited";
    public string Threads { get; set; } = "4";
    public string Fragments { get; set; } = "Auto";
    public string FilenameTemplate { get; set; } = "%(title)s [%(id)s].%(ext)s";
    public string Output { get; set; } = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "downloaded-video");
    public string Cookies { get; set; } = "";
    public string SubtitleLangs { get; set; } = "ru.*,en.*";
    public bool WriteSubs { get; set; }
    public bool AutoSubs { get; set; }
    public bool EmbedSubs { get; set; }
    public bool EmbedThumbnail { get; set; }
    public bool EmbedMetadata { get; set; } = true;
    public bool EmbedChapters { get; set; } = true;
    public bool Archive { get; set; }
    public bool Sponsor { get; set; }
    public bool AudioOnly { get; set; }
    public bool WatchClipboard { get; set; }
    public int Parallel => int.TryParse(Threads, out int n) ? Math.Clamp(n,1,32) : 4;
    public int FragmentCount => int.TryParse(Fragments, out int n) ? Math.Clamp(n,1,8) : Math.Min(8,Math.Max(1,Parallel));
    // Resolve exactly once per run. A session spanning midnight must not change
    // its output folder while worker jobs are still publishing and deleting.
    [JsonIgnore] private string? PinnedOutputDirectory { get; set; }
    [JsonIgnore] public string OutputDirectory => PinnedOutputDirectory ??
        (Archive ? System.IO.Path.Combine(Output, DateTime.Now.ToString("yyyy-MM-dd")) : Output);
    public void PinOutputDirectory() => PinnedOutputDirectory=System.IO.Path.GetFullPath(OutputDirectory);
}

public sealed class SessionSnapshot
{
    public int Version { get; set; } = 1;
    public DateTime SavedAtUtc { get; set; } = DateTime.UtcNow;
    public List<DownloadItem> Queue { get; set; } = new();
    public DownloadOptions Settings { get; set; } = new();
    public object? Engine { get; set; }
    // A live worker observed during Restore must keep migration blocked even
    // if it exits before the GUI handles this snapshot. This is not saved state.
    [JsonIgnore] public bool LegacyEngineWasActive { get; set; }
}

public sealed record DownloadProgress(double Percent, double SpeedBytes, long DownloadedBytes, long TotalBytes, long TransferredBytes, string Eta, string? Title = null);
public sealed record DownloadResult(bool Success, bool Cancelled, string Path, IReadOnlyList<string> OwnedPaths, string Error, long TransferredBytes);
public sealed record PreviewData(string Url, string Title, string Uploader, string PlaylistTitle, double Duration, string Extractor, int MaxHeight, double MaxFps, string Codecs, string DynamicRange, string ThumbnailBase64, DateTime SavedAtUtc);
public static class DownloadFormat
{
    public static string Bytes(long bytes)
    {
        if (bytes >= 1L<<40) return $"{bytes/(double)(1L<<40):N2} TB";
        if (bytes >= 1L<<30) return $"{bytes/(double)(1L<<30):N2} GB";
        if (bytes >= 1L<<20) return $"{bytes/(double)(1L<<20):N1} MB";
        if (bytes >= 1L<<10) return $"{bytes/(double)(1L<<10):N0} KB";
        return $"{bytes} B";
    }
    public static string Speed(double bytes)
    {
        if (bytes <= 0) return "0 KB/s";
        if (bytes >= 1L<<30) return $"{bytes/(1L<<30):0.#} GB/s";
        if (bytes >= 1L<<20) return $"{bytes/(1L<<20):0.#} MB/s";
        return $"{bytes/1024:0.#} KB/s";
    }
    public static string Tray(int ok,int left,int errors,double speed,bool running,bool paused)
    {
        string s=$"VD | OK {ok} Left {left} Err {errors}";
        s+=running ? paused ? " | Paused" : $" | {Speed(speed)}" : " | Finished";
        return s.Length <= 63 ? s : s[..63];
    }
}
