using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Text;

namespace VideoDownloader.Native;

internal enum BackupSubscriptionsPolicy { Merge, Replace }

internal sealed record BackupSummary(int Sources,int Items,int HistoryEntries,int PrivateSourcesSkipped,
    int PrivateItemsSkipped,int PrivateHistorySkipped,int SanitizedFields,int PrivateFeedsSkipped=0)
{
    internal long SkippedAddresses=>(long)PrivateSourcesSkipped+PrivateItemsSkipped+PrivateHistorySkipped+PrivateFeedsSkipped;
}

internal sealed record ProfileRestoreSummary(int AddedSources,int AddedItems,int AddedHistoryEntries,
    int ReplacedSources,BackupSubscriptionsPolicy Policy);

internal sealed class ProfileBackupArchive
{
    private readonly byte[] _bytes;
    internal byte[] ArchiveBytes=>_bytes.ToArray();
    internal BackupSummary Summary {get;}
    internal DateTimeOffset CreatedAtUtc {get;}
    internal string ApplicationVersion {get;}
    internal ProfileBackupArchive(byte[] bytes,BackupManifest manifest,BackupSummary summary)
    {_bytes=bytes.ToArray();Summary=summary;CreatedAtUtc=manifest.CreatedAtUtc;ApplicationVersion=manifest.ApplicationVersion;}
}

internal sealed class ProfileRestorePlan:IDisposable
{
    private readonly DownloadOptions _settings;
    internal DownloadOptions Settings=>_settings.Snapshot();
    internal ProfileRestoreSummary Summary {get;}
    internal string ProfileRoot {get;}
    internal IReadOnlyDictionary<string,byte[]?> Before {get;}
    internal IReadOnlyDictionary<string,byte[]> After {get;}
    private readonly object _gate=new();
    private int _leases;
    private bool _disposeRequested;
    private bool _disposed;
    internal ProfileRestorePlan(string root,DownloadOptions settings,ProfileRestoreSummary summary,
        Dictionary<string,byte[]?> before,Dictionary<string,byte[]> after)
    {ProfileRoot=root;_settings=settings.Snapshot();Summary=summary;Before=before;After=after;}
    internal void RequireLive(){lock(_gate)if(_disposed || _disposeRequested)throw new ObjectDisposedException(nameof(ProfileRestorePlan));}
    internal IDisposable Lease()
    {
        lock(_gate){RequireLive();_leases++;return new Use(this);}
    }
    private sealed class Use(ProfileRestorePlan owner):IDisposable
    {
        private ProfileRestorePlan? _owner=owner;
        public void Dispose()
        {
            var current=Interlocked.Exchange(ref _owner,null);if(current is null)return;
            lock(current._gate){current._leases--;current.ClearIfReleased();}
        }
    }
    public void Dispose()
    {
        lock(_gate){_disposeRequested=true;ClearIfReleased();}
    }
    private void ClearIfReleased()
    {
        if(_disposed || !_disposeRequested || _leases!=0)return;_disposed=true;
        foreach(var bytes in Before.Values.OfType<byte[]>())Array.Clear(bytes);
        foreach(var bytes in After.Values)Array.Clear(bytes);
    }
}

internal sealed class ProfileRestoreException(string message,bool recoveryRequired,bool applied,Exception? inner=null)
    :IOException(message,inner)
{
    internal bool RecoveryRequired {get;}=recoveryRequired;
    internal bool Applied {get;}=applied;
}

internal sealed class BackupManifest
{
    public string Kind {get;set;}="VideoDownloader.WindowsBackup";
    public int Version {get;set;}=1;
    public DateTimeOffset CreatedAtUtc {get;set;}=DateTimeOffset.UtcNow;
    public string ApplicationVersion {get;set;}="";
    public Dictionary<string,BackupPayloadHash> Files {get;set;}=new(StringComparer.Ordinal);
    public BackupSummary? Summary {get;set;}
}
internal sealed record BackupPayloadHash(long Bytes,string Sha256);

// This is deliberately not DownloadOptions: no paths, cookies, queue state,
// clipboard permission or future options enter an archive by serialization.
internal sealed class PortableProfileSettings
{
    public string Profile {get;set;}="Universal";
    public string Quality {get;set;}="Best";
    public string Codec {get;set;}="Auto";
    public string Container {get;set;}="Auto";
    public string RateLimit {get;set;}="Unlimited";
    public string Threads {get;set;}="4";
    public string Fragments {get;set;}="Auto";
    public string FilenameTemplate {get;set;}="%(title)s [%(id)s].%(ext)s";
    public string SubtitleLangs {get;set;}="ru.*,en.*";
    public string AudioTrack {get;set;}="Default";
    public string AudioLanguages {get;set;}="ru";
    public string LogProfile {get;set;}="Standard";
    public bool WriteSubs {get;set;}
    public bool AutoSubs {get;set;}
    public bool EmbedSubs {get;set;}
    public bool EmbedThumbnail {get;set;}
    public bool EmbedMetadata {get;set;}=true;
    public bool EmbedChapters {get;set;}=true;
    public bool Archive {get;set;}
    public bool Sponsor {get;set;}
    public bool AudioOnly {get;set;}
    public bool ShowVideoTitles {get;set;}
    public double QueuePanelWidth {get;set;}=380d*4/3;
    public double PreviewPanelWidth {get;set;}=390;

    internal static PortableProfileSettings From(DownloadOptions source)=>new()
    {
        Profile=source.Profile,Quality=source.Quality,Codec=source.Codec,Container=source.Container,
        RateLimit=source.RateLimit,Threads=source.Threads,Fragments=source.Fragments,
        FilenameTemplate=source.FilenameTemplate,SubtitleLangs=source.SubtitleLangs,
        AudioTrack=source.AudioTrack,AudioLanguages=source.AudioLanguages,LogProfile=source.LogProfile,
        WriteSubs=source.WriteSubs,AutoSubs=source.AutoSubs,EmbedSubs=source.EmbedSubs,
        EmbedThumbnail=source.EmbedThumbnail,EmbedMetadata=source.EmbedMetadata,EmbedChapters=source.EmbedChapters,
        Archive=source.Archive,Sponsor=source.Sponsor,AudioOnly=source.AudioOnly,ShowVideoTitles=source.ShowVideoTitles,
        QueuePanelWidth=source.QueuePanelWidth,PreviewPanelWidth=source.PreviewPanelWidth
    };
    internal DownloadOptions Apply(DownloadOptions local)
    {
        Validate();
        return new()
        {
            Profile=Profile,Quality=Quality,Codec=Codec,Container=Container,RateLimit=RateLimit,Threads=Threads,
            Fragments=Fragments,FilenameTemplate=FilenameTemplate,SubtitleLangs=SubtitleLangs,
            AudioTrack=AudioTrack,AudioLanguages=AudioLanguages,LogProfile=LogProfile,
            WriteSubs=WriteSubs,AutoSubs=AutoSubs,EmbedSubs=EmbedSubs,EmbedThumbnail=EmbedThumbnail,
            EmbedMetadata=EmbedMetadata,EmbedChapters=EmbedChapters,Archive=Archive,Sponsor=Sponsor,
            AudioOnly=AudioOnly,ShowVideoTitles=ShowVideoTitles,QueuePanelWidth=QueuePanelWidth,
            PreviewPanelWidth=PreviewPanelWidth,Output=local.Output,Cookies=local.Cookies,WatchClipboard=local.WatchClipboard
        };
    }
    internal void Validate()
    {
        if(Profile is not ("Universal" or "4K archive" or "MP4 compatibility" or "Music MP3" or "Subtitles archive") ||
            Quality is not ("Best" or "2160" or "1440" or "1080" or "720") || Codec is not ("Auto" or "H264" or "VP9" or "AV1") ||
            Container is not ("Auto" or "MP4" or "MKV" or "WebM") ||
            !int.TryParse(Threads,out int threads) || threads is <1 or >32 ||
            Fragments!="Auto" && (!int.TryParse(Fragments,out int fragments) || fragments is <1 or >8) ||
            LogProfile is not ("Errors" or "Standard" or "Progress" or "Detailed") ||
            AudioTrack is not ("Default" or "Original" or "Languages") ||
            !double.IsFinite(QueuePanelWidth) || QueuePanelWidth is <100 or >10000 ||
            !double.IsFinite(PreviewPanelWidth) || PreviewPanelWidth is <100 or >10000)
            throw new InvalidDataException("Непригодные переносимые настройки резервной копии.");
        if(RateLimit is null || RateLimit.Length>32 || !Regex.IsMatch(RateLimit,@"\A(?:Unlimited|[0-9]+(?:\.[0-9]+)?[KMGT]?)\z",RegexOptions.CultureInvariant) ||
            SubtitleLangs is null || SubtitleLangs.Length>256 || !Regex.IsMatch(SubtitleLangs,@"\A[A-Za-z0-9*.,_\-]*\z",RegexOptions.CultureInvariant) ||
            AudioLanguages is null || AudioLanguages.Length>256 || !Regex.IsMatch(AudioLanguages,@"\A[A-Za-z0-9,;_\- ]*\z",RegexOptions.CultureInvariant))
            throw new InvalidDataException("Непригодные языки или ограничение скорости в резервной копии.");
        if(FilenameTemplate is null || FilenameTemplate.Length is <1 or >1024 ||
            FilenameTemplate.Any(char.IsControl) || FilenameTemplate.Contains(':') ||
            FilenameTemplate.StartsWith('/') || FilenameTemplate.StartsWith('\\') ||
            FilenameTemplate.Split(['/','\\']).Any(part=>part is ".." or ".") ||
            !BackupPrivacy.IsSafeText(FilenameTemplate))
            throw new InvalidDataException("Шаблон резервной копии должен быть относительным и не содержать приватных данных.");
    }
}

internal sealed class PortableSubscriptions
{
    public List<PortableSubscription> Sources {get;set;}=[];
    public List<PortableSubscriptionItem> Items {get;set;}=[];
}
internal sealed record PortableSubscription(string Id,string Url,string Title,string ShortName,string FeedUrl,
    bool DownloadExisting,bool Initialized,bool Paused,DateTimeOffset? LastAttemptUtc,DateTimeOffset? LastSuccessUtc,
    int ScheduleIntervalMinutes=30,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)] string? InitialAction=null);
internal sealed record PortableSubscriptionItem(string SubscriptionId,string Url,string Title,string State,
    DateTimeOffset DiscoveredAtUtc,bool InitialQueuePending,bool SuccessAttributed,string DuplicateSubscriptionId,bool DuplicateNotified,
    [property: JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)] bool ManuallyCompleted=false);
internal sealed record PortableHistoryEntry(string Url,DateTimeOffset? CompletedAtUtc,string Title,string Source);
internal sealed record BackupDocument(BackupManifest Manifest,PortableProfileSettings Settings,
    PortableSubscriptions Subscriptions,List<PortableHistoryEntry> History);

internal static class BackupPrivacy
{
    private static readonly Regex LocalPath=new(@"(?:\b[A-Za-z]:[\\/][^\r\n""'<>]*|\\\\[^\r\n""'<>]*|\bfile:/{1,3}[^\r\n""'<>]*|(?<![A-Za-z0-9:/])/[^\r\n""'<>]*)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant,TimeSpan.FromMilliseconds(100));
    private static readonly Regex WebUrl=new(@"\bhttps?://[^\s<>""']+",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant|RegexOptions.NonBacktracking);
    private static string HidePaths(string value)
    {
        // Keep descriptive public HTTP URLs intact while recognizing arbitrary
        // absolute paths in assignments, quotes and brackets as well as prose.
        var result=new StringBuilder(value.Length);int previous=0;
        foreach(Match url in WebUrl.Matches(value))
        {result.Append(LocalPath.Replace(value[previous..url.Index],"[локальный путь скрыт]"));result.Append(url.Value);previous=url.Index+url.Length;}
        result.Append(LocalPath.Replace(value[previous..],"[локальный путь скрыт]"));return result.ToString();
    }
    internal static bool IsSafeUrl(string? url)=>url is {Length:>0 and <=4096} &&
        AppPersistence.ValidUrl(url) && url==url.Trim() && !url.Any(char.IsControl) &&
        Uri.TryCreate(url,UriKind.Absolute,out var address) && address.UserInfo.Length==0 &&
        string.Equals(JournalSanitizer.Redact(url),url,StringComparison.Ordinal);
    internal static string Text(string? value,int limit,ref int sanitized)
    {
        string original=value??"";
        string text=HidePaths(JournalSanitizer.Redact(original));
        if(text.Length>limit)text=text[..(char.IsHighSurrogate(text[limit-1])?limit-1:limit)];
        if(text!=original)sanitized++;
        return text;
    }
    internal static bool IsSafeText(string? value)=>value is not null &&
        string.Equals(HidePaths(value),value,StringComparison.Ordinal) && string.Equals(JournalSanitizer.Redact(value),value,StringComparison.Ordinal);
}
