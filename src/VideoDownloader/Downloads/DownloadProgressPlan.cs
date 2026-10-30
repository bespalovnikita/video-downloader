namespace VideoDownloader.Native;

// A percentage from yt-dlp belongs to one selected format, not to the finished
// output file. Keep the component fractions separate; never turn a completed
// video stream followed by an empty audio stream into a new file download.
internal sealed class DownloadProgressPlan
{
    private YtDlpOutputEvent.Plan? _plan;
    private readonly Dictionary<string,double> _fractions=new(StringComparer.Ordinal);
    private readonly List<string> _observed=[];

    internal void Reset()
    {
        _plan=null;_fractions.Clear();_observed.Clear();
    }

    internal void Begin(YtDlpOutputEvent.Plan plan)
    {
        _plan=plan;
        _fractions.Clear();
        _observed.Clear();
    }

    internal (double Percent,string Detail) Observe(YtDlpOutputEvent.Progress progress,double previous)
    {
        if(!_observed.Contains(progress.Component,StringComparer.Ordinal))_observed.Add(progress.Component);
        int part=_observed.IndexOf(progress.Component)+1;
        int parts=Math.Max(_plan?.FormatIds.Count??0,_observed.Count);
        if(progress.PercentKnown)_fractions[progress.Component]=Math.Clamp(progress.Percent,0,100);
        double percent=previous;
        if(progress.PercentKnown)
        {
            // Without a plan, preserve the previous component semantics while
            // reserving 100% for verified publication. Native yt-dlp supplies
            // the plan before its first transfer sample.
            percent=_plan==null ? Math.Min(99,Math.Clamp(progress.Percent,0,100)) :
                _fractions.Values.Sum()/Math.Max(1,parts)*0.99;
            if(_plan is {MediaIndex:>0,MediaCount:>1})
                percent=99d*(_plan.MediaIndex-1+percent/99)/_plan.MediaCount;
        }
        string kind=CodecPresent(progress.VideoCodec)
            ? CodecPresent(progress.AudioCodec) ? ErrorMessages.Text("Видео и аудио","Video and audio") : ErrorMessages.Text("Видео","Video")
            : CodecPresent(progress.AudioCodec) ? ErrorMessages.Text("Аудио","Audio") : ErrorMessages.Text("Поток","Stream");
        string raw=progress.PercentKnown ? progress.Percent.ToString("0.0",System.Globalization.CultureInfo.CurrentCulture)+"%" : "?";
        string detail=$"{kind} {part}/{Math.Max(1,parts)} · {raw}";
        if(_plan is {MediaIndex:>0,MediaCount:>1})
            detail=ErrorMessages.Text($"Материал {_plan.MediaIndex}/{_plan.MediaCount}",$"Item {_plan.MediaIndex}/{_plan.MediaCount}")+" · "+detail;
        return (percent,detail);
    }

    private static bool CodecPresent(string value)=>value.Length>0 && value is not "none" and not "NA" and not "N/A";
}
