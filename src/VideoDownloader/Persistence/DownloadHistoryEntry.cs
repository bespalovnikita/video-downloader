using System.Globalization;

namespace VideoDownloader.Native;

/// <summary>A successful attempt. Paths are historical metadata, never a proof of file ownership.</summary>
public sealed record DownloadHistoryEntry(long LineNumber,string Url,DateTimeOffset? CompletedAtUtc,
    string Title,string Source,IReadOnlyList<string> OutputPaths,bool OutputPathsTruncated=false)
{
    public string DisplayTitle=>string.IsNullOrWhiteSpace(Title)?Url:Title;
    public string DisplayDate=>CompletedAtUtc?.ToLocalTime().ToString("g",CultureInfo.CurrentCulture)??"Дата неизвестна";
    public string DisplaySource=>Source.Length>0?Source:new Uri(Url).Host;
    public string DisplayFileCount=>OutputPaths.Count==0?"Пути не записаны":OutputPaths.Count+(OutputPathsTruncated?"+":"");
    internal bool Matches(string query)=>query.Length==0 ||
        Title.Contains(query,StringComparison.OrdinalIgnoreCase) || Url.Contains(query,StringComparison.OrdinalIgnoreCase) ||
        DisplaySource.Contains(query,StringComparison.OrdinalIgnoreCase) ||
        (CompletedAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss",CultureInfo.InvariantCulture).Contains(query,StringComparison.OrdinalIgnoreCase)??false);
}

public sealed record DownloadHistoryLibrary(IReadOnlyList<DownloadHistoryEntry> Entries,long TotalCount,long MatchCount)
{
    public bool Truncated=>Entries.Count<MatchCount;
}
