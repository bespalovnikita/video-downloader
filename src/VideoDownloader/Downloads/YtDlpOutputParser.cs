using System.Globalization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal abstract record YtDlpOutputEvent
{
    internal sealed record Title(string Value) : YtDlpOutputEvent;
    internal sealed record MediaPath(string Value) : YtDlpOutputEvent;
    internal sealed record Progress(double Percent,string Speed,string Eta,long DownloadedBytes,long TotalBytes,string Component)
        : YtDlpOutputEvent
    {
        internal bool PercentKnown { get; init; } = true;
        internal string RawPercent { get; init; } = "";
    }
    internal sealed record Diagnostic(string? Error,bool Merging) : YtDlpOutputEvent;
}

/// <summary>Interprets child output without mutating download or publication state.</summary>
internal static class YtDlpOutputParser
{
    internal static YtDlpOutputEvent? Parse(string line)
    {
        if (line.StartsWith("__VD_TITLE__:",StringComparison.Ordinal))
            return new YtDlpOutputEvent.Title(line[13..].Trim());
        if (line.StartsWith("__VD_FILE__:",StringComparison.Ordinal))
            return new YtDlpOutputEvent.MediaPath(line[12..].Trim());
        if (line.StartsWith("__VD_PROGRESS__|",StringComparison.Ordinal))
        {
            var segments=line.Split('|',7);
            if (segments.Length<7) return null;
            var percentText=Regex.Replace(segments[1],@"[\x1B\[][0-9;]*m","").Replace("%","").Trim();
            bool percentKnown=double.TryParse(percentText,NumberStyles.Float,CultureInfo.InvariantCulture,out double pct)
                && double.IsFinite(pct);
            if (!percentKnown) pct=0;
            long.TryParse(segments[4],NumberStyles.Integer,CultureInfo.InvariantCulture,out long bytes);
            long.TryParse(segments[5],NumberStyles.Integer,CultureInfo.InvariantCulture,out long total);
            return new YtDlpOutputEvent.Progress(pct,segments[2].Trim(),segments[3].Trim(),bytes,total,segments[6])
                { PercentKnown=percentKnown, RawPercent=percentText };
        }
        bool diagnostic=line.StartsWith("ERROR:",StringComparison.OrdinalIgnoreCase)
            || line.StartsWith("WARNING:",StringComparison.OrdinalIgnoreCase)
            || line.Contains("connection",StringComparison.OrdinalIgnoreCase);
        bool merging=line.Contains("[Merger]",StringComparison.Ordinal);
        return diagnostic || merging ? new YtDlpOutputEvent.Diagnostic(diagnostic?line:null,merging) : null;
    }

    private static readonly Regex Transfer = new(@"^\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>KiB|MiB|GiB|TiB|KB|MB|GB|B)?(?:/s)?\s*$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    internal static double ParseSize(string raw)
    {
        var m=Transfer.Match(raw.Trim());
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups["value"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double v)) return 0;
        string unit=m.Groups["unit"].Value.ToUpperInvariant();
        double factor=unit switch
        {
            "TIB"=>Math.Pow(1024,4),"GIB"=>Math.Pow(1024,3),"MIB"=>1024*1024,"KIB"=>1024,
            "GB"=>1e9,"MB"=>1e6,"KB"=>1000,_=>1
        };
        return v*factor;
    }

    internal static bool IsTransient(string diagnostic)
    {
        const RegexOptions flags=RegexOptions.IgnoreCase|RegexOptions.CultureInvariant;
        // Permanent/auth errors win even if the diagnostic includes a network
        // phrase or an unrelated video ID containing e.g. "503".
        if (Regex.IsMatch(diagnostic,@"http(?:\s+error)?\s+(400|401|403|404|405|410|451)\b|private video|sign in|login required|video unavailable|video has been removed|requested format is not available",flags))
            return false;
        return Regex.IsMatch(diagnostic,@"timeout|timed out|temporary failure|connection reset|connection aborted|connection refused|remote host|network is unreachable|dns|http(?:\s+error)?\s+(429|5\d\d)\b|too many requests|server error|temporarily unavailable|tls|ssl",flags);
    }

    internal static bool FormatUnavailable(string diagnostic) =>
        diagnostic.Contains("Requested format is not available",StringComparison.OrdinalIgnoreCase);
}
