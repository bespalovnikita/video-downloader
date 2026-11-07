using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal sealed record JobDiagnosticTarget(string Identity,string Job,string Label,string Url,string Status,string Error)
{
    internal static JobDiagnosticTarget Capture(DownloadItem item)=>new(
        DownloadTraceIdentity.DiagnosticIdentity(item.Url),DownloadTraceIdentity.JobId(item.Url),
        DiagnosticPrivacy.Text(string.IsNullOrWhiteSpace(item.Title)?item.Url:item.Title),
        DiagnosticPrivacy.Text(item.Url),DiagnosticPrivacy.Text(item.Status),DiagnosticPrivacy.Text(item.RawError,true));
}

public sealed record DiagnosticRange(double Start,double End);

// This whitelist deliberately has no URL, cookies, path, template or ownership fields.
public sealed record JobDiagnosticSettings(string Profile,string Quality,string Codec,string Container,
    string AudioTrack,string AudioLanguages,bool AudioOnly,bool Subtitles,bool AutoSubtitles,bool EmbeddedSubtitles,
    int Parallel,int NetworkFragments,string? VideoFormat,string? AudioFormat,IReadOnlyList<DiagnosticRange> Ranges,
    string RateLimit="неизвестно",bool Archive=false,bool EmbeddedThumbnail=false,bool EmbeddedMetadata=false,
    bool EmbeddedChapters=false,bool Sponsor=false,string SubtitleLanguages="неизвестно",string FragmentOutputMode="Separate",bool AllowMergeReencode=false)
{
    internal static JobDiagnosticSettings Capture(DownloadOptions options,DownloadSelection? selection,string? actualQuality=null)=>new(
        Choice(options.Profile,["Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive"]),QualityId(actualQuality??options.Quality),
        Choice(options.Codec,["Auto","H264","VP9","AV1"]),Choice(options.Container,["Auto","MP4","MKV","WebM"]),
        Choice(options.AudioTrack,["Default","Original","Languages"]),Languages(options.AudioLanguages),options.AudioOnly,options.WriteSubs,options.AutoSubs,options.EmbedSubs,
        options.Parallel,options.FragmentCount,selection?.Format is {} video?Id(video.Id):null,
        selection?.AudioFormat is {} audio?Id(audio.Id):null,
        Array.AsReadOnly((selection?.EffectiveSections??Array.Empty<DownloadSection>()).Take(32)
            .Select(range=>new DiagnosticRange(range.StartSeconds,range.EndSeconds)).ToArray()),
        RateLimit:Rate(options.RateLimit),Archive:options.Archive,EmbeddedThumbnail:options.EmbedThumbnail,EmbeddedMetadata:options.EmbedMetadata,
        EmbeddedChapters:options.EmbedChapters,Sponsor:options.Sponsor,SubtitleLanguages:Languages(options.SubtitleLangs,true),
        FragmentOutputMode:selection?.FragmentOutputMode??"Separate",AllowMergeReencode:selection?.AllowMergeReencode??false);
    private static string Id(string? value)=>value is {Length:>0 and <=128} &&
        Regex.IsMatch(value,@"\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z",RegexOptions.CultureInvariant)?value:"неизвестно";
    private static string Choice(string? value,string[] allowed)=>value is not null && allowed.Contains(value,StringComparer.Ordinal)?value:"неизвестно";
    private static string QualityId(string? value)=>value is "Best" or "best" or "manual" || value is {Length:>0 and <=5} && value.All(char.IsAsciiDigit)?value!:"неизвестно";
    private static string Rate(string? value)=>value=="Unlimited" || value is {Length:>0 and <=12} &&
        Regex.IsMatch(value,@"\A[0-9]+(?:\.[0-9]+)?[KMG]?\z",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase)?value!:"неизвестно";
    private static string Languages(string? value,bool subtitles=false)=>value is {Length:<=256} &&
        Regex.IsMatch(value,subtitles?@"\A(?:(?:all|[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,3})(?:\.\*)?(?:\s*,\s*-?(?:all|live_chat|[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,3})(?:\.\*)?){0,15})?\z":@"\A(?:[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,3})?(?:\s*[,;]\s*[A-Za-z]{2,3}(?:-[A-Za-z0-9]{1,8}){0,3}){0,7}\z",RegexOptions.CultureInvariant)
            ?value:"неизвестно";
    internal JobDiagnosticSettings Safe()=>this with
    {
        Profile=Choice(Profile,["Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive"]),Quality=QualityId(Quality),
        Codec=Choice(Codec,["Auto","H264","VP9","AV1"]),Container=Choice(Container,["Auto","MP4","MKV","WebM"]),
        AudioTrack=Choice(AudioTrack,["Default","Original","Languages"]),AudioLanguages=Languages(AudioLanguages),
        VideoFormat=VideoFormat is null?null:Id(VideoFormat),AudioFormat=AudioFormat is null?null:Id(AudioFormat),
        RateLimit=Rate(RateLimit),SubtitleLanguages=Languages(SubtitleLanguages,true),FragmentOutputMode=Choice(FragmentOutputMode,["Separate","Merged"]),
        Parallel=Math.Clamp(Parallel,1,32),NetworkFragments=Math.Clamp(NetworkFragments,1,8),
        Ranges=Array.AsReadOnly((Ranges??Array.Empty<DiagnosticRange>()).Take(32).Where(range=>range is not null &&
            double.IsFinite(range.Start) && double.IsFinite(range.End) && range.Start>=0 && range.End>range.Start)
            .Select(range=>new DiagnosticRange(range.Start,range.End)).ToArray())
    };
    [JsonIgnore] public string DisplayText=>"Профиль: "+(Profile=="неизвестно"?Profile:ProfileCatalog.Display(Profile))+" · качество: "+DisplayValueConverter.Display(Quality)+" · кодек: "+DisplayValueConverter.Display(Codec)+
        " · контейнер: "+DisplayValueConverter.Display(Container)+" · звук: "+(AudioTrack=="неизвестно"?AudioTrack:AudioTrackSelection.Display(AudioTrack))+(AudioLanguages.Length>0?" ("+AudioLanguages+")":"")+
        " · форматы: "+(VideoFormat??"авто")+" / "+(AudioFormat??"авто")+
        Environment.NewLine+"Диапазоны, секунды: "+(Ranges.Count==0?"полностью":string.Join("; ",Ranges.Select(range=>
            range.Start.ToString("0.###",CultureInfo.InvariantCulture)+" — "+range.End.ToString("0.###",CultureInfo.InvariantCulture))))+
        " · режим: "+(FragmentOutputMode switch {"Merged"=>"Один файл","Separate"=>"Отдельные файлы",_=>FragmentOutputMode})+" · перекодирование при склейке: "+YesNo(AllowMergeReencode)+
        Environment.NewLine+"Параллельно: "+Parallel+" · сетевые фрагменты: "+NetworkFragments+" · скорость: "+DisplayValueConverter.Display(RateLimit)+
        " · только аудио: "+YesNo(AudioOnly)+" · архивная папка: "+YesNo(Archive)+
        Environment.NewLine+"Субтитры: "+YesNo(Subtitles)+" · автоматические: "+YesNo(AutoSubtitles)+
        " · встроенные: "+YesNo(EmbeddedSubtitles)+" · языки: "+SubtitleLanguages+
        Environment.NewLine+"Обложка: "+YesNo(EmbeddedThumbnail)+" · метаданные: "+YesNo(EmbeddedMetadata)+
        " · главы: "+YesNo(EmbeddedChapters)+" · SponsorBlock: "+YesNo(Sponsor);
    private static string YesNo(bool value)=>value?"да":"нет";
}

public sealed record JobDiagnosticEvent(int Version,string Journal,long Sequence,DateTimeOffset CapturedAt,
    JournalLevel Level,string Category,string Session,string Job,string Identity,string Worker,
    string Event,int? Attempt,int? Invocation,string Message,JobDiagnosticSettings? Settings)
{
    [JsonIgnore] public string AttemptLabel=>Invocation is {} invocation
        ?Session+" · запуск "+invocation+(Attempt is {} attempt?" · попытка "+attempt:"")
        :Session+" · до запуска / неизвестно";
    [JsonIgnore] public string DisplayText=>"["+CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture)+"] ["+
        Level+"/"+Category+"] session="+Session+" job="+Job+" worker="+Worker+
        (Invocation is {} invocation?" invocation="+invocation:"")+(Attempt is {} attempt?" attempt="+attempt:"")+" event="+Event+" "+
        Message.Replace(Environment.NewLine,Environment.NewLine+"    ",StringComparison.Ordinal);
}

internal sealed record JobDiagnosticSnapshot(JobDiagnosticTarget Target,IReadOnlyList<JobDiagnosticEvent> Events,
    IReadOnlyList<string> Warnings,int OmittedEvents,int MalformedRecords,bool Truncated,long JournalSequence)
{
    internal string Text=>string.Join(Environment.NewLine,Events.Select(entry=>entry.DisplayText));
    internal static JobDiagnosticSnapshot Empty(JobDiagnosticTarget target)=>new(target,Array.Empty<JobDiagnosticEvent>(),
        Array.AsReadOnly(new[]{"Событий этой задачи пока нет. Старые текстовые журналы не содержат нового структурированного индекса."}),0,0,false,0);
}

internal sealed record DiagnosticComponentVersion(string Component,string Status,string Version);
internal sealed record DiagnosticExportResult(int Events,long Bytes,IReadOnlyList<DiagnosticComponentVersion> Components);

internal static partial class DiagnosticPrivacy
{
    // A diagnostic bundle never needs an absolute path. A possibly unquoted
    // path is removed through the rest of its line rather than leaking spaces.
    private static readonly Regex WindowsPath=new(@"(?<![A-Za-z0-9])(?:[A-Za-z]:[\\/]|\\\\)[^\r\n]*",RegexOptions.CultureInvariant);
    private static readonly Regex UnixPath=new(@"(?<![A-Za-z0-9:/])/(?!\s)[^\r\n]*",RegexOptions.CultureInvariant);
    private static readonly Regex HttpUrl=new(@"\bhttps?://[^\s<>""']+",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    private static readonly Regex FileUrl=new(@"\bfile:[^\r\n]*",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    private static readonly Regex CommandLine=new(@"\b(?:command(?:\s+line)?|argv|arguments|args)\s*(?:[:=]|['""\[])[^\r\n]*",RegexOptions.CultureInvariant|RegexOptions.IgnoreCase);
    internal static string Text(string? value,bool multiline=false)
    {
        string text=JournalSanitizer.Redact(value,multiline);
        if(multiline)text=text.Replace(Environment.NewLine+"    ",Environment.NewLine,StringComparison.Ordinal);
        var urls=new List<string>();string marker="VDURL"+Guid.NewGuid().ToString("N")+"X";
        text=HttpUrl.Replace(text,match=>{urls.Add(match.Value);return marker+(urls.Count-1).ToString(CultureInfo.InvariantCulture)+"X";});
        text=CommandLine.Replace(text,"команда: [аргументы скрыты]");
        text=FileUrl.Replace(text,"[локальный путь скрыт]");
        text=WindowsPath.Replace(text,"[локальный путь скрыт]");
        text=UnixPath.Replace(text,"[локальный путь скрыт]");
        for(int index=0;index<urls.Count;index++)text=text.Replace(marker+index.ToString(CultureInfo.InvariantCulture)+"X",urls[index],StringComparison.Ordinal);
        return text;
    }
}
