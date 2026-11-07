using System.IO;
using System.Globalization;
using System.Text.Json;
using VideoDownloader.Native;

internal static partial class FragmentMergeTests
{
    private static async Task ActualTrackPreservation(string root)
    {
        string stage=Path.Combine(root,"tracks-stage");Directory.CreateDirectory(stage);
        var service=new FragmentMergeService(ToolPath("ffmpeg"),ToolPath("ffprobe"));
        string first=Path.Combine(stage,"first.mkv"),second=Path.Combine(stage,"second.mkv"),wrong=Path.Combine(stage,"wrong.mkv");
        async Task Tracks(string path,int width,string language)=>await Tool(ToolPath("ffmpeg"),new[]{
            "-hide_banner","-loglevel","error","-f","lavfi","-i",$"color=c=blue:s={width}x90:r=24:d=1",
            "-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=1","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=1",
            "-map","0:v","-map","1:a","-map","2:a","-c:v","libx264","-preset","ultrafast","-c:a","aac","-metadata:s:a:0","language=eng","-metadata:s:a:1","language="+language,path});
        await Tracks(first,160,"rus");await Tracks(second,128,"rus");await Tracks(wrong,160,"fra");
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{first,wrong},true,false,false,CancellationToken.None),"Different language tracks cannot be discarded or relabelled by fallback");
        var result=await service.MergeAsync(stage,new[]{first,second},true,false,true,CancellationToken.None);
        using(var json=JsonDocument.Parse(await Tool(ToolPath("ffprobe"),new[]{"-v","error","-show_streams","-of","json",result.MediaPath})))
        {
            string[] languages=json.RootElement.GetProperty("streams").EnumerateArray().Where(stream=>stream.GetProperty("codec_type").GetString()=="audio")
                .Select(stream=>stream.GetProperty("tags").GetProperty("language").GetString()!).ToArray();
            Require(result.Reencoded && languages.SequenceEqual(new[]{"eng","rus"}),"Fallback must keep every actual audio track in its original order and language");
        }
        string audio=Path.Combine(stage,"audio.mp3"),next=Path.Combine(stage,"next.mp3"),cover=Path.Combine(stage,"cover.jpg"),covered=Path.Combine(stage,"covered.mp3");
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=1","-c:a","libmp3lame",audio});
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=1","-c:a","libmp3lame",next});
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","color=c=blue:s=32x32","-frames:v","1","-threads:v","1",cover});
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-i",audio,"-i",cover,"-map","0:a","-map","1:v","-c","copy","-disposition:v","attached_pic",covered});
        var mp3=await service.MergeAsync(stage,new[]{covered,next},false,true,false,CancellationToken.None);
        using(var json=JsonDocument.Parse(await Tool(ToolPath("ffprobe"),new[]{"-v","error","-show_streams","-show_format","-of","json",mp3.MediaPath})))
        {
            var streams=json.RootElement.GetProperty("streams");
            Require(!mp3.Reencoded && streams.EnumerateArray().Any(stream=>stream.GetProperty("codec_type").GetString()=="video" && stream.GetProperty("disposition").GetProperty("attached_pic").GetInt32()==1),"Stream copy of audio-only fragments must retain the first embedded cover");
            Require(json.RootElement.GetProperty("format").GetProperty("format_name").GetString()=="mp3" && Math.Abs(mp3.DurationSeconds-2)<.2,"Actual output must retain the requested audio container and duration");
        }
        string mkvCover=Path.Combine(stage,"covered.mkv");
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-i",first,"-map","0","-c","copy","-attach",cover,
            "-metadata:s:t","mimetype=image/jpeg","-metadata:s:t","filename=cover.jpg",mkvCover});
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{mkvCover,first},false,false,false,CancellationToken.None),
            "Unsupported attachment-cover remux must fail explicitly instead of adding a new real video track or discarding the image");
        string highDepth=Path.Combine(stage,"nine-bit.mkv");
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-f","lavfi","-i","color=c=red:s=128x90:r=24:d=1",
            "-f","lavfi","-i","sine=frequency=440:sample_rate=48000:duration=1","-f","lavfi","-i","sine=frequency=880:sample_rate=48000:duration=1",
            "-map","0:v","-map","1:a","-map","2:a","-vf","format=yuv420p9le","-threads:v","1","-c:v","ffv1","-c:a","aac",
            "-metadata:s:a:0","language=eng","-metadata:s:a:1","language=rus",highDepth});
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{first,highDepth},true,false,false,CancellationToken.None),"Even explicit consent cannot silently normalize nine-bit source pixels to eight bits");
        string anamorphic=Path.Combine(stage,"non-square.mkv");
        await Tool(ToolPath("ffmpeg"),new[]{"-hide_banner","-loglevel","error","-i",first,"-map","0","-vf","setsar=4/3","-c:v","libx264","-preset","ultrafast","-c:a","copy",anamorphic});
        await RefusesAsync(()=>service.MergeAsync(stage,new[]{first,anamorphic},true,false,false,CancellationToken.None),"Normalization cannot silently change display aspect ratio for non-square source pixels");
        string metadata="""
            {"format":{"duration":"1","format_name":"mov"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":160,"height":90,"r_frame_rate":"0/0","sample_aspect_ratio":"2:2","pix_fmt":"yuv420p"}]}
            """;
        var unknown=FragmentMediaProbe.Parse(first,System.Text.Encoding.UTF8.GetBytes(metadata)).Video!;
        Require(unknown.Fps==0 && unknown.PixelAspect==1,"Unknown FPS stays unknown and equivalent square SAR ratios remain equivalent");
        if(!OperatingSystem.IsWindows())
        {
            string control=Path.Combine(stage,"newline\n.mp4");File.Copy(first,control);
            await RefusesAsync(()=>service.MergeAsync(stage,new[]{first,control},true,false,false,CancellationToken.None),"A control character cannot inject an ffconcat manifest directive");
            string link=Path.Combine(stage,"link.mkv");File.CreateSymbolicLink(link,first);
            bool rejected=false;try{await service.MergeAsync(stage,new[]{first,link},true,false,false,CancellationToken.None);}catch(IOException){rejected=true;}
            Require(rejected,"A symlink cannot establish owned local input authority");
        }
    }
}
