using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoDownloader.Native;

internal sealed partial class FragmentMergeService
{
    private sealed record EncodingPlan(string Muxer,string Video,string Audio,string ExpectedVideo,string ExpectedAudio);
    private static EncodingPlan EncodingFor(string extension)=>extension switch
    {
        ".mp4" or ".m4v" or ".m4a"=>new("mp4","libx264","aac","h264","aac"),
        ".mov"=>new("mov","libx264","aac","h264","aac"),
        ".mkv" or ".mka"=>new("matroska","libx264","aac","h264","aac"),
        ".webm"=>new("webm","libvpx-vp9","libopus","vp9","opus"),
        ".mp3"=>new("mp3","","libmp3lame","","mp3"),
        ".wav"=>new("wav","","pcm_s16le","","pcm_s16le"),
        ".flac"=>new("flac","","flac","","flac"),
        ".opus"=>new("opus","","libopus","","opus"),
        ".ogg"=>new("ogg","","libvorbis","","vorbis"),
        ".aac"=>new("adts","","aac","","aac"),
        ".ts"=>new("mpegts","libx264","aac","h264","aac"),
        ".avi"=>new("avi","libx264","libmp3lame","h264","mp3"),
        ".flv"=>new("flv","libx264","aac","h264","aac"),
        ".mpeg" or ".mpg"=>new("mpeg","mpeg2video","mp2","mpeg2video","mp2"),
        ".3gp"=>new("3gp","libx264","aac","h264","aac"),
        _=>throw FragmentMediaProbe.Invalid("Unsupported merged output extension. Choose MP4, MKV or WebM.")
    };
    private static void LocalInput(ProcessStartInfo start,FragmentMediaInfo info)
    {
        OwnedFiles.RegularOrAbsent(info.Path);
        Add(start,"-threads","2","-max_alloc","268435456","-protocol_whitelist","file","-format_whitelist",info.Demuxer,
            "-probesize","2097152","-analyzeduration","3000000","-max_pixels","67108864","-f",info.Demuxer);
        if(info.Demuxer=="mov")Add(start,"-enable_drefs","0","-use_absolute_path","0");
        Add(start,"-i",info.Path);
    }
    private async Task NormalizeAsync(FragmentMediaInfo input,FragmentMediaInfo first,string target,EncodingPlan encoding,CancellationToken token)
    {
        OwnedFiles.RegularOrAbsent(target);
        var start=Start(_ffmpeg);
        Add(start,"-hide_banner","-loglevel","error","-nostdin","-n","-filter_threads","1","-filter_complex_threads","1");
        LocalInput(start,input);
        if(input.Video is {} video)
        {
            var chosen=first.Video!;
            int width=Math.Max(2,(chosen.Width+1)/2*2),height=Math.Max(2,(chosen.Height+1)/2*2);
            Add(start,"-map","0:"+video.Index,"-vf",$"scale={width}:{height}:force_original_aspect_ratio=decrease,pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1,fps={Number(chosen.Fps)}",
                "-c:v",encoding.Video,"-pix_fmt","yuv420p","-threads:v","2","-bf","0");
            if(encoding.Video=="libx264")Add(start,"-preset","medium","-crf","18");
            else if(encoding.Video=="libvpx-vp9")Add(start,"-crf","24","-b:v","0","-deadline","good","-cpu-used","4","-row-mt","1");
            else Add(start,"-q:v","3");
        }
        else Add(start,"-vn");
        for(int index=0;index<input.Audio.Length;index++)
        {
            var stream=input.Audio[index];var chosen=first.Audio[index];
            int rate=encoding.Audio is "libopus" or "libvorbis" or "libmp3lame" or "mp2"?48000:
                encoding.Audio=="aac"?Math.Min(96000,chosen.SampleRate):chosen.SampleRate;
            Add(start,"-map","0:"+stream.Index,"-c:a:"+index,encoding.Audio,"-ar:a:"+index,rate.ToString(CultureInfo.InvariantCulture),
                "-ac:a:"+index,chosen.Channels.ToString(CultureInfo.InvariantCulture));
            if(encoding.Audio is "aac" or "libopus" or "libvorbis" or "libmp3lame" or "mp2")
                Add(start,"-b:a:"+index,(Math.Clamp(chosen.Channels,1,8)*96).ToString(CultureInfo.InvariantCulture)+"k");
            if(chosen.Language.Length>0)Add(start,"-metadata:s:a:"+index,"language="+chosen.Language);
        }
        if(input.Audio.Length==0)Add(start,"-an");
        Add(start,"-sn","-dn","-map_metadata","-1","-map_chapters","-1","-t",Number(input.Duration),"-f",encoding.Muxer);
        if(encoding.Muxer is "mp4" or "mov")Add(start,"-movflags","+faststart");
        Add(start,target);
        await RunEncoderAsync(start,token).ConfigureAwait(false);
    }

    private async Task JoinAsync(FragmentMediaInfo[] inputs,FragmentMediaInfo[] originals,string work,string destination,
        EncodingPlan encoding,bool embedMetadata,bool embedChapters,CancellationToken token)
    {
        var metadata=originals[0];
        string manifest=Path.Combine(work,"inputs.ffconcat");
        var text=new StringBuilder("ffconcat version 1.0\n");
        foreach(var input in inputs)
        {
            // Per-file format options prevent a changed local file from being
            // interpreted as another concat/playlist or following external refs.
            text.Append("file '").Append(QuotePath(input.Path)).Append("'\noption protocol_whitelist file\noption format_whitelist ").Append(input.Demuxer).Append('\n');
            if(input.Demuxer=="mov")text.Append("option enable_drefs 0\noption use_absolute_path 0\n");
            text.Append("duration ").Append(Number(input.Duration)).Append('\n');
        }
        await WriteOwnedAsync(manifest,text.ToString(),token).ConfigureAwait(false);
        var start=Start(_ffmpeg);
        Add(start,"-hide_banner","-loglevel","error","-nostdin","-n","-max_alloc","268435456","-threads","2",
            "-protocol_whitelist","file","-format_whitelist","concat,"+FragmentMediaProbe.AllowedDemuxers,
            "-f","concat","-safe","0","-i",manifest);
        // The second local input supplies only optional metadata/one cover;
        // it is never used to duplicate the selected video or audio packets.
        LocalInput(start,metadata);
        if(embedChapters)
        {
            string chapters=Path.Combine(work,"chapters.ffmetadata");
            await WriteOwnedAsync(chapters,BuildChapters(inputs,originals),token).ConfigureAwait(false);
            Add(start,"-protocol_whitelist","file","-format_whitelist","ffmetadata","-f","ffmetadata","-i",chapters);
        }
        foreach(var stream in inputs[0].Tracks)Add(start,"-map","0:"+stream.Index);
        if(metadata.Cover is {} cover)Add(start,"-map","1:"+cover.Index);
        Add(start,"-c","copy","-map_metadata",embedMetadata?"1":"-1","-map_chapters",embedChapters?"2":"-1");
        // Track language is part of the admitted media shape, not optional tags.
        for(int index=0;index<inputs[0].Tracks.Length;index++)
            Add(start,"-metadata:s:"+index,"language="+(inputs[0].Tracks[index].Language is {Length:>0} language?language:"und"));
        if(metadata.Cover is {Type:"video"})Add(start,"-disposition:v:"+(metadata.Video is null?0:1),"attached_pic");
        Add(start,"-f",encoding.Muxer);
        if(encoding.Muxer is "mp4" or "mov")Add(start,"-movflags","+faststart");
        Add(start,destination);
        await RunEncoderAsync(start,token).ConfigureAwait(false);
    }
    private string BuildChapters(FragmentMediaInfo[] inputs,FragmentMediaInfo[] originals)
    {
        // Input chapters are already local to each clipped file. Rebase them in
        // list order; clips without chapters get one named range chapter.
        var builder=new StringBuilder(";FFMETADATA1\n");double offset=0;
        for(int index=0;index<inputs.Length;index++)
        {
            var file=inputs[index];
            var chapters=originals[index].Chapters.Length>0?originals[index].Chapters:[new FragmentChapter(0,file.Duration,"Фрагмент "+(index+1))];
            foreach(var chapter in chapters)
            {
                long start=(long)Math.Round((offset+Math.Min(chapter.Start,file.Duration))*1000),end=(long)Math.Round((offset+Math.Min(chapter.End,file.Duration))*1000);
                if(end<=start)continue;
                builder.Append("[CHAPTER]\nTIMEBASE=1/1000\nSTART=").Append(start.ToString(CultureInfo.InvariantCulture))
                    .Append("\nEND=").Append(end.ToString(CultureInfo.InvariantCulture)).Append("\ntitle=").Append(EscapeMetadata(chapter.Title)).Append('\n');
            }
            offset+=file.Duration;
        }
        return builder.ToString();
    }
    private async Task RunEncoderAsync(ProcessStartInfo start,CancellationToken token)
    {
        var result=await _run(start,4096,128*1024,token).ConfigureAwait(false);
        if(result.ExitCode!=0)throw FragmentMediaProbe.Invalid("FFmpeg could not finish joining the local fragments (exit "+result.ExitCode+"). "+ToolDiagnostic(result));
    }
    private static string QuotePath(string path)
    {
        if(path.Any(char.IsControl))throw FragmentMediaProbe.Invalid("A fragment path contains control characters.");
        return Path.GetFullPath(path).Replace('\\','/').Replace("'","'\\''",StringComparison.Ordinal);
    }
    private static async Task WriteOwnedAsync(string path,string text,CancellationToken token)
    {
        OwnedFiles.RegularOrAbsent(path);
        await using var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None,8192,useAsync:true);
        await file.WriteAsync(new UTF8Encoding(false).GetBytes(text),token).ConfigureAwait(false);
    }
    private static string EscapeMetadata(string value)=>value.Replace("\\","\\\\",StringComparison.Ordinal).Replace("=","\\=",StringComparison.Ordinal)
        .Replace(";","\\;",StringComparison.Ordinal).Replace("#","\\#",StringComparison.Ordinal).Replace("\r","",StringComparison.Ordinal).Replace("\n","\\\n",StringComparison.Ordinal);
}
