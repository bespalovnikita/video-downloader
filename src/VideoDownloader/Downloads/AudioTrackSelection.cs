using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

/// <summary>Strict audio selection. Every video/codec fallback retains the requested tracks.</summary>
public static class AudioTrackSelection
{
    private static readonly (string Id,string Caption)[] Modes =
    [
        ("Default","По умолчанию"),
        ("Original","Оригинальная"),
        ("Languages","По языкам")
    ];
    public static string[] DisplayNames => Modes.Select(mode=>mode.Caption).ToArray();
    public static string Display(string? id) => Modes.FirstOrDefault(mode=>mode.Id==id).Caption??Modes[0].Caption;
    public static string ToId(string? caption) => Modes.FirstOrDefault(mode=>mode.Caption==caption || mode.Id==caption).Id??"Default";

    private static string[] Languages(DownloadOptions options)
    {
        string value=options.AudioLanguages??"";
        if(value.Length>128 || value.Any(char.IsControl))throw new ArgumentException("Укажите не более восьми кодов языков аудио без управляющих символов, например ru,en.");
        var languages=value.Split(',',StringSplitOptions.TrimEntries);
        if(languages.Length is <1 or >8 || languages.Any(language=>
            !Regex.IsMatch(language,@"\A[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*\z",RegexOptions.CultureInvariant)))
            throw new ArgumentException("Укажите коды языков аудио через запятую, например ru, en или pt-BR.");
        return languages.Select(language=>language.ToLowerInvariant()).Distinct(StringComparer.Ordinal).ToArray();
    }

    public static void Validate(DownloadOptions options)
    {
        if(!Modes.Any(mode=>mode.Id==options.AudioTrack))throw new ArgumentException("Неизвестный режим выбора аудиодорожки.");
        if(options.AudioTrack!="Languages")return;
        if(Languages(options).Length<=1)return;
        if(options.AudioOnly)throw new ArgumentException("MP3 поддерживает одну аудиодорожку. Выберите один язык или отключите «MP3 only».");
        if(options.Container is not ("Auto" or "MKV"))
            throw new ArgumentException("Для нескольких аудиодорожек выберите контейнер Auto или MKV.");
    }

    internal static bool Multiple(DownloadOptions options) => options.AudioTrack=="Languages" && Languages(options).Length>1;
    internal static string Container(DownloadOptions options) => Multiple(options)?"MKV":options.Container;

    internal static string? FormatSelector(DownloadOptions options,string quality)
    {
        Validate(options);
        string height=quality=="best"?"":$"[height<={quality}]";
        string codec=options.Codec switch {"H264"=>"avc1","VP9"=>"vp9","AV1"=>"av01",_=>""};
        if(options.AudioTrack=="Default")
        {
            if(options.AudioOnly)return null;
            string generic=$"bv*{height}+ba/b{height}";
            return codec.Length==0?generic:$"bv*{height}[vcodec^={codec}]+ba/b{height}[vcodec^={codec}]/{generic}";
        }
        // An unmarked/default track is not evidence of the original language.
        string[] filters=options.AudioTrack=="Original"
            ? [@"[format_note~='(?i)(?:^original$|\(original\))']"]
            : Languages(options).Select(language=>$"[language~='(?i)^{language}(?:-|$)']").ToArray();
        string audio=string.Join("+",filters.Select(filter=>"ba"+filter));
        if(options.AudioOnly)return audio+"/b"+filters[0];
        string Video(string videoCodec)
        {
            string bound=height+videoCodec;
            // bv excludes embedded audio, which could displace the chosen track.
            string result=$"bv{bound}+{audio}";
            return filters.Length==1?result+$"/b{bound}{filters[0]}":result;
        }
        return codec.Length==0?Video(""):Video($"[vcodec^={codec}]")+"/"+Video("");
    }
}
