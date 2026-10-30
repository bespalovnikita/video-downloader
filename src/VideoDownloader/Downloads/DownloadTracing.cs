using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VideoDownloader.Native;

public static class DownloadTraceIdentity
{
    public static string JobId(string url)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)))[..12].ToLowerInvariant();
}

// One sampler belongs to one URL, and is observed under the same lock as that
// URL's counters. A stream switch or reset is evidence, not a reason to hide
// the actual percentage behind a monotonic maximum.
internal sealed class DownloadProgressTrace
{
    private static readonly JsonSerializerOptions TextJson=new(){Encoder=JavaScriptEncoder.UnsafeRelaxedJsonEscaping};
    private readonly Dictionary<string,(long Bytes,long Total,double Percent,bool Known)> _components=new(StringComparer.Ordinal);
    private string? _component;
    private double _shown;
    private long _lastSample;
    private long _skipped;
    private bool _seen;

    internal string? Observe(YtDlpOutputEvent.Progress progress,double shown,long transferred,int attempt,string quality,string source)
    {
        bool known=_components.TryGetValue(progress.Component,out var previous);
        var reasons=new List<string>();
        if(!_seen)reasons.Add("first_sample");
        if(_seen && _component!=progress.Component)reasons.Add("component_change");
        if(known && progress.DownloadedBytes<previous.Bytes)reasons.Add("counter_reset");
        if(progress.PercentKnown && _seen && shown<_shown)reasons.Add("percent_regression");
        if(known && previous.Total!=progress.TotalBytes)reasons.Add("total_change");
        if(!progress.PercentKnown && (!known || previous.Known))reasons.Add("unknown_percent");
        if(progress.PercentKnown && progress.Percent>=100 && (!known || !previous.Known || previous.Percent<100))reasons.Add("component_complete");
        long now=Stopwatch.GetTimestamp();
        bool sample=!_seen || reasons.Count>0 || Stopwatch.GetElapsedTime(_lastSample,now)>=TimeSpan.FromSeconds(1);
        string? result=null;
        if(sample)
        {
            string percent=progress.PercentKnown ? Number(progress.Percent) : "unknown";
            result=$"attempt={attempt} quality={Quoted(quality)} stream={source} component={Quoted(progress.Component)} " +
                $"previous_component={Quoted(_component??"")} raw_pct={percent} raw_text={Quoted(progress.RawPercent)} " +
                $"previous_raw_pct={(known && previous.Known ? Number(previous.Percent) : "unknown")} " +
                $"shown_pct={Number(shown)} previous_shown_pct={Number(_shown)} " +
                $"downloaded={progress.DownloadedBytes} previous_downloaded={(known ? previous.Bytes : 0)} " +
                $"total={progress.TotalBytes} previous_total={(known ? previous.Total : 0)} transferred={transferred} " +
                $"reason={(reasons.Count>0 ? string.Join(',',reasons) : "sample")} samples_skipped={_skipped}";
            _lastSample=now;_skipped=0;
        }
        else _skipped++;
        _components[progress.Component]=(progress.DownloadedBytes,progress.TotalBytes,progress.Percent,progress.PercentKnown);
        _component=progress.Component;_shown=shown;_seen=true;
        return result;
    }

    internal static string Number(double value)=>value.ToString("0.###",CultureInfo.InvariantCulture);
    internal static string Quoted(string value)=>JsonSerializer.Serialize(value,TextJson);
}
