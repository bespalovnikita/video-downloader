using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal sealed partial class SubtitleTimelineService
{
    private static readonly Regex Timing=new(@"\A(?<sh>\d+):(?<sm>\d{2}):(?<ss>\d{2}),(?<sf>\d{3}) --> (?<eh>\d+):(?<em>\d{2}):(?<es>\d{2}),(?<ef>\d{3})\z",RegexOptions.CultureInvariant);
    internal static SubtitleCue[] ParseSrt(byte[] bytes)
    {
        if(bytes.Length>MaximumBytes)throw Invalid("Normalized subtitles exceed the 8 MiB limit.");
        string text=new UTF8Encoding(false,true).GetString(bytes).TrimStart('\uFEFF').Replace("\r\n","\n",StringComparison.Ordinal).Trim();
        if(text.Length==0)return [];
        var result=new List<SubtitleCue>();
        foreach(string block in Regex.Split(text,"\n{2,}"))
        {
            string[] lines=block.Split('\n');
            if(lines.Length<3 || !int.TryParse(lines[0],NumberStyles.None,CultureInfo.InvariantCulture,out _))throw Invalid("Invalid normalized subtitle cue.");
            var match=Timing.Match(lines[1]);if(!match.Success)throw Invalid("Invalid normalized subtitle timing.");
            long Time(string prefix)
            {
                long Part(string suffix)=>long.Parse(match.Groups[prefix+suffix].Value,CultureInfo.InvariantCulture);
                if(Part("m")>59 || Part("s")>59)throw Invalid("Invalid subtitle timestamp.");
                try{return checked(((Part("h")*60+Part("m"))*60+Part("s"))*1000+Part("f"));}
                catch(OverflowException error){throw Invalid("Subtitle timestamp exceeds its limit.",error);}
            }
            long start=Time("s"),end=Time("e");
            if(end<=start)throw Invalid("Empty or reversed normalized subtitle cue.");
            result.Add(new(start,end,string.Join('\n',lines.Skip(2))));
            if(result.Count>100_000)throw Invalid("Too many subtitle cues.");
        }
        return result.ToArray();
    }
    internal static SubtitleCue[] Clip(IEnumerable<SubtitleCue> cues,DownloadSection section)
    {
        section.Validate();
        return ClipBounds(cues,(section.StartSeconds,section.EndSeconds));
    }
    internal static SubtitleCue[] ClipBounds(IEnumerable<SubtitleCue> cues,(double Start,double End) bounds)
    {
        ValidateBounds(bounds);
        long start=(long)Math.Round(bounds.Start*1000),end=(long)Math.Round(bounds.End*1000);
        return cues.Where(cue=>cue.End>start && cue.Start<end)
            .Select(cue=>new SubtitleCue(Math.Max(start,cue.Start)-start,Math.Min(end,cue.End)-start,cue.Text)).ToArray();
    }
    internal static string WebVtt(IEnumerable<SubtitleCue> cues)
    {
        var result=new StringBuilder("WEBVTT\n\n");
        foreach(var cue in cues.OrderBy(cue=>cue.Start))result.Append(Timestamp(cue.Start)).Append(" --> ").Append(Timestamp(cue.End)).Append('\n').Append(cue.Text).Append("\n\n");
        return result.ToString();
    }
    internal static SubtitleCue[] Flatten(IReadOnlyList<SubtitleCue> cues)
    {
        var events=new SortedDictionary<long,List<(int Index,bool Start)>>();
        for(int index=0;index<cues.Count;index++)
        {
            foreach(var (time,begin) in new[]{(cues[index].Start,true),(cues[index].End,false)})
            {if(!events.TryGetValue(time,out var changes))events[time]=changes=[];changes.Add((index,begin));}
        }
        var active=new SortedDictionary<int,string>();var result=new List<SubtitleCue>();long previous=0,total=0;
        foreach(var (time,changes) in events)
        {
            if(time>previous && active.Count>0)
            {
                string text=string.Join('\n',active.Values.Distinct(StringComparer.Ordinal));total+=Encoding.UTF8.GetByteCount(text);
                if(total>MaximumBytes || result.Count>=100_000)throw Invalid("Overlapping subtitle dialogue exceeds its limit.");
                if(result.Count>0 && result[^1].End==previous && result[^1].Text==text)result[^1]=result[^1] with {End=time};
                else result.Add(new(previous,time,text));
            }
            foreach(var change in changes)
                if(change.Start)active[change.Index]=cues[change.Index].Text;else active.Remove(change.Index);
            previous=time;
        }
        return result.ToArray();
    }
    private static string Timestamp(long milliseconds)=>string.Create(CultureInfo.InvariantCulture,
        $"{milliseconds/3_600_000:00}:{milliseconds/60_000%60:00}:{milliseconds/1000%60:00}.{milliseconds%1000:000}");
}
