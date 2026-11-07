using System.Globalization;
using System.Text.Json;

namespace VideoDownloader.Native;

internal sealed record FragmentChapter(double Start,double End,string Title);
internal sealed record FragmentMediaStream(int Index,string Type,string Codec,string Language,string Signature,
    int Width,int Height,double Fps,double PixelAspect,string PixelFormat,string Transfer,int SampleRate,int Channels,string ChannelLayout,bool Cover);
internal sealed record FragmentMediaInfo(string Path,string Demuxer,double Duration,FragmentMediaStream[] Streams,FragmentChapter[] Chapters)
{
    internal FragmentMediaStream[] Tracks=>Streams.Where(stream=>!stream.Cover && stream.Type is "video" or "audio").ToArray();
    internal FragmentMediaStream? Video=>Tracks.FirstOrDefault(stream=>stream.Type=="video");
    internal FragmentMediaStream[] Audio=>Tracks.Where(stream=>stream.Type=="audio").ToArray();
    internal FragmentMediaStream? Cover=>Streams.FirstOrDefault(stream=>stream.Cover);
}

/// <summary>Only bounded media structure is retained, never arbitrary tags or source URLs.</summary>
internal static class FragmentMediaProbe
{
    internal const string AllowedDemuxers="mov,matroska,webm,mp3,aac,ogg,flac,wav,mpegts,avi,flv,mpeg";
    private static readonly string[] SignatureFields=["codec_type","codec_name","codec_tag_string","profile","level",
        "width","height","pix_fmt","sample_aspect_ratio","r_frame_rate","avg_frame_rate","time_base","sample_fmt",
        "sample_rate","channels","channel_layout","color_range","color_space","color_transfer","color_primaries","extradata_hash"];
    internal static FragmentMediaInfo Parse(string path,byte[] bytes)
    {
        using var json=JsonDocument.Parse(bytes,new JsonDocumentOptions{MaxDepth=20});
        var root=json.RootElement;
        var format=root.GetProperty("format");
        double duration=Number(format,"duration");
        if(!double.IsFinite(duration) || duration<=0 || duration>=TimeSpan.MaxValue.TotalSeconds)throw Invalid("Unknown or invalid fragment duration.");
        string demuxer=Text(format,"format_name").Split(',')[0];
        if(!AllowedDemuxers.Split(',').Contains(demuxer,StringComparer.Ordinal))throw Invalid("Unsupported media container.");
        var raw=root.GetProperty("streams");
        if(raw.ValueKind!=JsonValueKind.Array || raw.GetArrayLength() is <1 or >16)throw Invalid("Unsupported stream count.");
        var streams=new List<FragmentMediaStream>();
        foreach(var value in raw.EnumerateArray())
        {
            string type=Text(value,"codec_type"),codec=Text(value,"codec_name");
            int index=Integer(value,"index");
            if(index<0 || index>63 || streams.Any(stream=>stream.Index==index))throw Invalid("Invalid stream index.");
            var tags=value.TryGetProperty("tags",out var foundTags)?foundTags:default;
            string mime=Text(tags,"mimetype");
            bool picture=value.TryGetProperty("disposition",out var disposition) && Integer(disposition,"attached_pic")==1;
            bool cover=type=="video" && picture || type=="attachment" && mime is "image/jpeg" or "image/png";
            // FFmpeg represents MP4 chapters as a bin_data text track. Chapters
            // are separately bounded and rebased below, never blindly copied.
            bool chapterTrack=type=="data" && codec=="bin_data" && Text(tags,"handler_name")=="SubtitleHandler";
            if(!cover && type is not ("audio" or "video") && !chapterTrack)throw Invalid("Subtitle, data or unsupported attachment tracks require separate files.");
            string language=Text(tags,"language",48).ToLowerInvariant();
            if(language=="und")language="";
            if(language.Any(character=>!char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')))throw Invalid("Invalid audio language tag.");
            var signature=SignatureFields.Select(name=>value.TryGetProperty(name,out var field)?field.GetRawText():"").Append(language).ToArray();
            streams.Add(new(index,type,codec,language,JsonSerializer.Serialize(signature),Integer(value,"width"),Integer(value,"height"),
                Rate(Text(value,"r_frame_rate")),Aspect(Text(value,"sample_aspect_ratio")),Text(value,"pix_fmt"),Text(value,"color_transfer"),Integer(value,"sample_rate"),
                Integer(value,"channels"),Text(value,"channel_layout"),cover));
        }
        if(streams.Count(stream=>stream.Type=="video" && !stream.Cover)>1 || streams.Count(stream=>stream.Type=="audio")>8 || streams.Count(stream=>stream.Cover)>1 ||
            streams.All(stream=>stream.Cover || stream.Type is not ("video" or "audio")))throw Invalid("Unsupported video, audio or cover track layout.");
        var chapters=new List<FragmentChapter>();
        if(root.TryGetProperty("chapters",out var chapterArray))
        {
            if(chapterArray.ValueKind!=JsonValueKind.Array || chapterArray.GetArrayLength()>2048)throw Invalid("Too many fragment chapters.");
            foreach(var chapter in chapterArray.EnumerateArray())
            {
                double start=Number(chapter,"start_time"),end=Number(chapter,"end_time");
                if(!double.IsFinite(start) || !double.IsFinite(end) || end<=start)throw Invalid("Invalid fragment chapter.");
                start=Math.Clamp(start,0,duration);end=Math.Clamp(end,0,duration);
                if(end>start)chapters.Add(new(start,end,chapter.TryGetProperty("tags",out var chapterTags)?Text(chapterTags,"title",512):""));
            }
        }
        return new(path,demuxer,duration,streams.ToArray(),chapters.ToArray());
    }
    private static string Text(JsonElement root,string name,int maximum=256)
    {
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty(name,out var value) || value.ValueKind!=JsonValueKind.String)return "";
        string text=value.GetString()??"";
        if(text.Length>maximum || text.Contains('\0'))throw Invalid("Oversized fragment metadata.");
        return text;
    }
    private static double Number(JsonElement root,string name)
    {
        if(root.ValueKind!=JsonValueKind.Object || !root.TryGetProperty(name,out var value))return double.NaN;
        if(value.ValueKind==JsonValueKind.Number && value.TryGetDouble(out double number))return number;
        return value.ValueKind==JsonValueKind.String && double.TryParse(value.GetString(),NumberStyles.Float,CultureInfo.InvariantCulture,out number)?number:double.NaN;
    }
    private static int Integer(JsonElement root,string name)
    {double number=Number(root,name);return double.IsFinite(number) && number>=0 && number<=int.MaxValue && number==Math.Truncate(number)?(int)number:0;}
    private static double Aspect(string text)
    {
        if(text is "" or "N/A" or "0:1")return 1;
        var pair=text.Split(':');return pair.Length==2 && double.TryParse(pair[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double numerator) &&
            double.TryParse(pair[1],NumberStyles.Float,CultureInfo.InvariantCulture,out double denominator) &&
            double.IsFinite(numerator) && double.IsFinite(denominator) && numerator>0 && denominator>0?numerator/denominator:0;
    }
    private static double Rate(string text)
    {
        var pair=text.Split('/');
        return pair.Length==2 && double.TryParse(pair[0],NumberStyles.Float,CultureInfo.InvariantCulture,out double numerator) &&
            double.TryParse(pair[1],NumberStyles.Float,CultureInfo.InvariantCulture,out double denominator) && numerator>0 && denominator>0 && numerator/denominator<=240?numerator/denominator:0;
    }
    internal static InvalidDataException Invalid(string detail)=>new("Fragment merge failed: "+detail);
}
