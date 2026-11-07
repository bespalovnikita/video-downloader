using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal sealed partial class SubtitleTimelineService
{
    private sealed record ContainerPlan(string Demuxer,string Muxer,string Codec);
    private static ContainerPlan Container(string path)=>Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp4" or ".m4v" or ".m4a"=>new("mov","mp4","mov_text"),
        ".mov"=>new("mov","mov","mov_text"),
        ".mkv" or ".mka"=>new("matroska","matroska","webvtt"),
        ".webm"=>new("matroska","webm","webvtt"),
        _=>throw Invalid("This container cannot embed clipped subtitles. Choose MP4, MKV or WebM, or disable subtitle embedding.")
    };
    private static string Language(string code)
    {
        try{return CultureInfo.GetCultureInfo(code.Replace('_','-').Split('-')[0]).ThreeLetterISOLanguageName;}
        catch(CultureNotFoundException){return code;}
    }
    private async Task EmbedAsync(string stage,string media,string target,IReadOnlyList<Prepared> tracks,CancellationToken token)
    {
        var container=Container(media);
        var start=Start(_ffmpeg);Add(start,"-hide_banner","-loglevel","error","-nostdin","-n");
        Input(start,stage,media,container.Demuxer);
        foreach(var track in tracks)Input(start,stage,track.Path,"webvtt");
        Add(start,"-map","0","-map","-0:s?","-c","copy","-c:s",container.Codec,"-map_metadata","0","-map_chapters","0");
        for(int index=0;index<tracks.Count;index++)
            Add(start,"-map",(index+1).ToString(CultureInfo.InvariantCulture)+":s:0","-metadata:s:s:"+index,"language="+Language(tracks[index].Language),
                "-metadata:s:s:"+index,"title="+tracks[index].Language,"-metadata:s:s:"+index,"handler_name="+tracks[index].Language);
        Add(start,"-f",container.Muxer,target);
        await RunAsync(start,4096,token).ConfigureAwait(false);
        LocalFile(stage,target);
        if(new FileInfo(target).Length==0)throw Invalid("Embedding produced no media.");
    }
    private async Task VerifyAsync(string stage,string original,string media,IReadOnlyList<Prepared> tracks,(double Start,double End) bounds,CancellationToken token)
    {
        var container=Container(media);
        var probe=Start(_ffprobe);Add(probe,"-v","error");
        // ffprobe uses the same explicit local demuxer and MOV reference guards.
        LocalFile(stage,media);
        Add(probe,"-max_alloc","268435456","-protocol_whitelist","file","-format_whitelist",container.Demuxer,"-f",container.Demuxer);
        if(container.Demuxer=="mov")Add(probe,"-enable_drefs","0","-use_absolute_path","0");
        Add(probe,"-show_entries","format=duration:stream=codec_type,codec_name:stream_tags=language","-of","json",media);
        using var json=JsonDocument.Parse(await RunAsync(probe,128*1024,token).ConfigureAwait(false));
        probe.ArgumentList[^1]=original;
        using var before=JsonDocument.Parse(await RunAsync(probe,128*1024,token).ConfigureAwait(false));
        static string[] MediaShape(JsonElement root)=>root.GetProperty("streams").EnumerateArray()
            .Where(stream=>stream.GetProperty("codec_type").GetString()!="subtitle")
            .Select(stream=>stream.GetProperty("codec_type").GetString()+":"+stream.GetProperty("codec_name").GetString()).ToArray();
        if(!MediaShape(json.RootElement).SequenceEqual(MediaShape(before.RootElement)))throw Invalid("Embedding changed the media stream layout.");
        var streams=json.RootElement.GetProperty("streams").EnumerateArray().Where(stream=>stream.GetProperty("codec_type").GetString()=="subtitle").ToArray();
        if(streams.Length!=tracks.Count)throw Invalid("Embedding changed the selected subtitle track count.");
        double duration=double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        if(!double.IsFinite(duration) || duration<=0 || duration>bounds.End-bounds.Start+.5)
            throw Invalid("Embedded subtitle output exceeds the selected duration.");
        for(int index=0;index<tracks.Count;index++)
        {
            if(streams[index].GetProperty("codec_name").GetString()!=container.Codec ||
                !streams[index].TryGetProperty("tags",out var tags) || !tags.TryGetProperty("language",out var language) ||
                language.GetString()!=Language(tracks[index].Language))throw Invalid("Embedding changed a subtitle codec or language.");
            var extract=Start(_ffmpeg);Add(extract,"-hide_banner","-loglevel","error","-nostdin");
            Input(extract,stage,media,container.Demuxer);
            Add(extract,"-map","0:s:"+index,"-c:s","srt","-f","srt","pipe:1");
            var actual=ParseSrt(await RunAsync(extract,MaximumBytes,token).ConfigureAwait(false));
            var expected=tracks[index].Cues;
            // Muxers round to their time base; no full-source cue or shifted
            // dialogue is accepted merely because an output track exists.
            if(actual.Length!=expected.Length || actual.Where((cue,cueIndex)=>Math.Abs(cue.Start-expected[cueIndex].Start)>10 ||
                Math.Abs(cue.End-expected[cueIndex].End)>10 || Plain(cue.Text)!=Plain(expected[cueIndex].Text)).Any())
                throw Invalid("Embedded subtitle cues do not match the clipped timeline.");
        }
    }
    private static string Plain(string text)=>Regex.Replace(Regex.Replace(System.Net.WebUtility.HtmlDecode(text),"<[^>]*>",""),@"\s+"," ").Trim();
}
