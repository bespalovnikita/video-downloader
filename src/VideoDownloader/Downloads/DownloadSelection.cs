using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>A bounded, displayable description of a single actual extractor format; never a media URL.</summary>
public sealed record MediaFormat(string Id,string Extension,int Width,int Height,double Fps,
    string VideoCodec,string AudioCodec,string Language,long? EstimatedSizeBytes,bool SizeIsApproximate)
{
    public string DynamicRange { get; init; }="";
    [JsonIgnore] public bool HasVideo=>VideoCodec.Length>0 && VideoCodec!="none";
    [JsonIgnore] public bool HasAudio=>AudioCodec.Length>0 && AudioCodec!="none";
    [JsonIgnore] public bool IsVideoOnly=>HasVideo && !HasAudio;
    [JsonIgnore] public bool IsAudioOnly=>HasAudio && !HasVideo;
    [JsonIgnore] public string DisplayName
    {
        get
        {
            string picture=HasVideo ? (Height>0?$"{(Width>0?Width+"×":"")}{Height}":"видео")+
                (Fps>0?$" · {Fps:0.###} FPS":"") : "аудио";
            string codecs=string.Join(" / ",new[]{HasVideo?VideoCodec:"",HasAudio?AudioCodec:""}.Where(value=>value.Length>0));
            return string.Join(" · ",new[]{Id,Extension,picture,codecs,DynamicRange,Language,
                EstimatedSizeBytes is {} bytes?(SizeIsApproximate?"≈ ":"")+DownloadFormat.Bytes(bytes):"размер неизвестен"}.Where(value=>value.Length>0));
        }
    }
}

/// <summary>Finite, nonnegative start and strictly later end in source seconds.</summary>
public sealed record DownloadSection(double StartSeconds,double EndSeconds)
{
    private static readonly Regex Timestamp=new(@"\A[0-9]+(?::[0-9]{1,2}){0,2}(?:\.[0-9]{1,3})?\z",RegexOptions.CultureInvariant);
    public static bool TryParse(string start,string end,double? duration,out DownloadSection? section,out string error)
    {
        section=null;error="";
        if(string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))return true;
        if(!TrySeconds(start?.Trim(),out double first) || !TrySeconds(end?.Trim(),out double last))
        {error="Укажите время в формате ЧЧ:ММ:СС, М:СС или секунд, с точностью до миллисекунд.";return false;}
        try {section=new(first,last);section.Validate(duration);return true;}
        catch(ArgumentException exception){section=null;error=exception.Message;return false;}
    }
    private static bool TrySeconds(string? text,out double seconds)
    {
        seconds=0;
        if(text is null || text.Length>32 || !Timestamp.IsMatch(text))return false;
        string[] parts=text.Split(':');
        for(int index=0;index<parts.Length;index++)
        {
            if(!double.TryParse(parts[index],NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out double value) ||
                !double.IsFinite(value) || index>0 && value>=60)return false;
            seconds=seconds*60+value;
        }
        return double.IsFinite(seconds) && seconds<TimeSpan.MaxValue.TotalSeconds;
    }
    public void Validate(double? duration=null)
    {
        if(!double.IsFinite(StartSeconds) || !double.IsFinite(EndSeconds) || StartSeconds<0 ||
            EndSeconds<=StartSeconds || EndSeconds>=TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentException("Начало должно быть неотрицательным, а конец — больше начала фрагмента.");
        if(Math.Abs(StartSeconds-Math.Round(StartSeconds,3))>1e-9 ||
            Math.Abs(EndSeconds-Math.Round(EndSeconds,3))>1e-9 || Math.Round(EndSeconds,3)<=Math.Round(StartSeconds,3))
            throw new ArgumentException("Укажите границы фрагмента с точностью до миллисекунд.");
        if(duration is {} known && (!double.IsFinite(known) || known<=0))
            throw new ArgumentException("Некорректная длительность исходного видео.");
        if(duration is {} limit && EndSeconds>limit)
            throw new ArgumentException("Конец фрагмента выходит за длительность исходного видео.");
    }
    public static string FormatTimestamp(double seconds)
    {
        if(!double.IsFinite(seconds) || seconds<0 || seconds>=TimeSpan.MaxValue.TotalSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds));
        long hours=(long)(seconds/3600);
        return hours.ToString("00",CultureInfo.InvariantCulture)+":"+((int)(seconds/60)%60).ToString("00",CultureInfo.InvariantCulture)+":"+
            (seconds%60).ToString("00.###",CultureInfo.InvariantCulture);
    }
    internal string Argument=>"*"+StartSeconds.ToString("0.###",CultureInfo.InvariantCulture)+"-"+EndSeconds.ToString("0.###",CultureInfo.InvariantCulture);
}

/// <summary>Immutable per-row selection. The exact URL binds format IDs to the metadata that supplied them.</summary>
public sealed record DownloadSelection(string Url,DownloadSection? Section=null,MediaFormat? Format=null,
    MediaFormat? AudioFormat=null,double? DurationSeconds=null)
{
    [JsonIgnore] public string Summary=>(Format is null?"Автоматический формат":Format.Id+(AudioFormat is null?"":" + "+AudioFormat.Id))+
        (Section is null?" · Полностью":" · "+DownloadSection.FormatTimestamp(Section.StartSeconds)+" — "+DownloadSection.FormatTimestamp(Section.EndSeconds));
    public bool TryValidate(string url,DownloadOptions options,out string error)
    {
        try {Validate(url,options);error="";return true;}
        catch(ArgumentException exception){error=exception.Message;return false;}
    }
    public void Validate(string url,DownloadOptions options)
    {
        ValidatePersisted(url);
        if(Format is null)return;
        if(options.AudioOnly && (!Format.IsAudioOnly || AudioFormat is not null))
            throw new ArgumentException("Для «MP3 only» выберите аудиоформат без видео или автоматический формат.");
        if(options.AudioTrack!="Default")
            throw new ArgumentException("Конкретный формат уже задаёт аудиодорожку. Выберите аудиодорожку «По умолчанию» либо автоматический формат.");
        if(!options.AudioOnly)ValidateContainer(options.Container,Format,AudioFormat);
    }
    internal void ValidatePersisted(string url)
    {
        if(!AppPersistence.ValidUrl(Url) || !string.Equals(Url,url,StringComparison.Ordinal))
            throw new ArgumentException("Выбор формата и фрагмента относится к другому URL.");
        if(DurationSeconds is {} duration && (!double.IsFinite(duration) || duration<=0 || duration>=TimeSpan.MaxValue.TotalSeconds))
            throw new ArgumentException("Некорректная длительность исходного видео.");
        Section?.Validate(DurationSeconds);
        if(Format is null)
        {
            if(AudioFormat is not null)throw new ArgumentException("Сначала выберите основной формат.");
            return;
        }
        MediaFormatCatalog.Validate(Format);
        if(AudioFormat is not null)MediaFormatCatalog.Validate(AudioFormat);
        if(Format.IsVideoOnly && AudioFormat is null)
            throw new ArgumentException("Для формата без звука выберите отдельный аудиоформат.");
        if(AudioFormat is not null && (!Format.IsVideoOnly || !AudioFormat.IsAudioOnly || Format.Id==AudioFormat.Id))
            throw new ArgumentException("Отдельный аудиоформат можно добавить только к формату видео без звука.");
    }
    private static void ValidateContainer(string container,MediaFormat primary,MediaFormat? audio)
    {
        if(container is "Auto" or "MKV")return;
        string video=primary.HasVideo?primary.VideoCodec.ToLowerInvariant():"";
        string sound=(audio??primary).HasAudio?(audio??primary).AudioCodec.ToLowerInvariant():"";
        bool compatible=container switch
        {
            "WebM"=>(video.Length==0 || new[]{"vp8","vp9","vp09","av01","av1"}.Any(video.StartsWith)) &&
                (sound.Length==0 || new[]{"opus","vorbis"}.Any(sound.StartsWith)),
            "MP4"=>(video.Length==0 || new[]{"avc","h264","hev","hvc","hevc","av01","av1","vp9","vp09","mpeg4"}.Any(video.StartsWith)) &&
                (sound.Length==0 || new[]{"mp4a","aac","mp3","ac3","eac3","alac"}.Any(sound.StartsWith)),
            _=>false
        };
        if(!compatible)throw new ArgumentException("Выбранные кодеки несовместимы с контейнером "+container+". Выберите Auto/MKV или другой формат.");
    }
    // A filter, rather than a bare identifier, also handles real IDs such as
    // "best" without allowing the extractor to introduce selector operators.
    internal string? FormatSelector=>Format is null?null:"best*[format_id='"+Format.Id+"']"+
        (AudioFormat is null?"":"+best*[format_id='"+AudioFormat.Id+"']");
}

internal static class MediaFormatCatalog
{
    internal const int MaxFormats=1024;
    private static readonly Regex Identifier=new(@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z",RegexOptions.CultureInvariant);
    private static bool SafeText(string? value,int limit)=>value is not null && value.Length<=limit && !value.Any(char.IsControl);
    internal static void Validate(MediaFormat format)
    {
        if(!Identifier.IsMatch(format.Id??"") || !SafeText(format.Extension,24) || !SafeText(format.VideoCodec,96) ||
            !SafeText(format.AudioCodec,96) || !SafeText(format.Language,48) || !SafeText(format.DynamicRange,48) ||
            format.Width is <0 or >131072 || format.Height is <0 or >131072 || !double.IsFinite(format.Fps) ||
            format.Fps is <0 or >1000 || format.EstimatedSizeBytes is <=0 || !format.HasVideo && !format.HasAudio)
            throw new ArgumentException("Метаданные выбранного формата некорректны. Обновите превью и выберите формат заново.");
    }
    internal static MediaFormat[] Parse(JsonElement item)=>Parse(item,out _);
    internal static MediaFormat[] Parse(JsonElement item,out bool truncated)
    {
        truncated=false;
        if(item.ValueKind!=JsonValueKind.Object || item.TryGetProperty("entries",out _) ||
            !item.TryGetProperty("formats",out var formats) || formats.ValueKind!=JsonValueKind.Array)return [];
        // Extractors sort from worse to better. Keep a bounded window at the
        // quality end, while continuing to inspect the already byte-bounded JSON.
        var result=new LinkedList<MediaFormat>();
        var retained=new Dictionary<string,LinkedListNode<MediaFormat>>(StringComparer.Ordinal);
        foreach(var value in formats.EnumerateArray())
        {
            if(value.ValueKind!=JsonValueKind.Object || value.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))continue;
            string Text(string key,int max)=>value.TryGetProperty(key,out var field) && field.ValueKind==JsonValueKind.String &&
                SafeText(field.GetString(),max)?field.GetString()!:"";
            double Number(string key)=>value.TryGetProperty(key,out var field) && field.ValueKind==JsonValueKind.Number &&
                field.TryGetDouble(out double number) && double.IsFinite(number)?number:0;
            int Dimension(string key)=>Number(key) is >0 and <=131072 and var pixels?(int)pixels:0;
            long? Size(string key)=>value.TryGetProperty(key,out var field) && field.ValueKind==JsonValueKind.Number &&
                field.TryGetInt64(out long size) && size>0?size:null;
            long? exact=Size("filesize");
            long? approximate=Size("filesize_approx");
            var format=new MediaFormat(Text("format_id",128),Text("ext",24),Dimension("width"),
                Dimension("height"),Number("fps"),Text("vcodec",96),Text("acodec",96),Text("language",48),
                exact??approximate,exact is null && approximate is not null){DynamicRange=Text("dynamic_range",48)};
            try
            {
                Validate(format);
                if(retained.ContainsKey(format.Id))continue;
                if(result.Count==MaxFormats)
                {
                    retained.Remove(result.First!.Value.Id);result.RemoveFirst();truncated=true;
                }
                retained[format.Id]=result.AddLast(format);
            }
            catch(ArgumentException){ }
        }
        return result.ToArray();
    }
    internal static bool IsValid(IReadOnlyList<MediaFormat>? formats)
    {
        if(formats is null || formats.Count>MaxFormats)return false;
        var ids=new HashSet<string>(StringComparer.Ordinal);
        foreach(var format in formats)
        {
            if(format is null || !ids.Add(format.Id))return false;
            try {Validate(format);}catch(ArgumentException){return false;}
        }
        return true;
    }
}
