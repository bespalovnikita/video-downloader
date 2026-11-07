using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

internal sealed record FragmentMergeToolOutput(int ExitCode,byte[] StandardOutput,byte[] StandardError);
internal sealed record FragmentMergeResult(string PublishStage,string MediaPath,bool Reencoded,double DurationSeconds);

/// <summary>Joins verified local staging files; never accepts a source URL or publishes by itself.</summary>
internal sealed partial class FragmentMergeService
{
    private readonly string _ffmpeg,_ffprobe;
    private readonly Func<ProcessStartInfo,int,int,CancellationToken,Task<FragmentMergeToolOutput>> _run;
    private readonly Func<bool> _paused;
    private readonly TimeSpan _timeout;
    internal FragmentMergeService(string ffmpeg,string ffprobe,
        Func<ProcessStartInfo,int,int,CancellationToken,Task<FragmentMergeToolOutput>>? run=null,
        Func<bool>? paused=null,TimeSpan? timeout=null)
    {_ffmpeg=ffmpeg;_ffprobe=ffprobe;_run=run??RunOwnedAsync;_paused=paused??(()=>false);_timeout=timeout??TimeSpan.FromHours(6);}

    internal async Task<FragmentMergeResult> MergeAsync(string stage,IReadOnlyList<string> paths,bool allowReencode,
        bool embedMetadata,bool embedChapters,CancellationToken token,Action<string,bool>? progress=null)
    {
        token.ThrowIfCancellationRequested();
        if(paths.Count is <2 or >DownloadSectionSet.MaximumCount)throw FragmentMediaProbe.Invalid("Select between two and 32 fragments.");
        if(!File.Exists(_ffmpeg) || !File.Exists(_ffprobe))throw FragmentMediaProbe.Invalid("FFmpeg and FFprobe are required.");
        stage=Path.GetFullPath(stage);OwnedFiles.NoReparseAncestors(Path.Combine(stage,"check"));
        var unique=new HashSet<string>(OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal);
        foreach(string path in paths)
        {
            string full=Path.GetFullPath(path);
            if(!OwnedFiles.Inside(stage,full) || !unique.Add(full) || full.Any(char.IsControl))throw FragmentMediaProbe.Invalid("Invalid or duplicate owned fragment path.");
            OwnedFiles.RegularOrAbsent(full);
            if(!File.Exists(full) || new FileInfo(full).Length==0)throw FragmentMediaProbe.Invalid("A fragment is missing or empty.");
        }
        await using var budget=new ActiveBudget(token,_timeout,_paused);
        try
        {
            var original=new List<FragmentMediaInfo>();
            foreach(string path in paths)
            {
                progress?.Invoke($"Проверка фрагмента {original.Count+1}/{paths.Count}",false);
                var info=await ProbeAsync(Path.GetFullPath(path),budget.Token).ConfigureAwait(false);ValidateMedia(info);
                original.Add(info);
                if(original.Sum(item=>item.Chapters.Length)>4096)throw FragmentMediaProbe.Invalid("Too many chapters in the complete fragment plan.");
            }
            EnsureTrackShape(original);
            bool compatible=CopyCompatible(original);
            if(!compatible && !allowReencode)throw FragmentMediaProbe.Invalid("Fragment stream parameters differ. Enable re-encoding or save separate files.");
            string extension=Path.GetExtension(paths[0]).ToLowerInvariant();
            var encoding=EncodingFor(extension);
            if(original[0].Cover is not null && encoding.Muxer is not ("mp3" or "mp4" or "mov" or "flac"))
                throw FragmentMediaProbe.Invalid("Embedded covers cannot be preserved when joining this container. Disable thumbnail embedding or save separate files.");
            if(original[0].Video is not null && encoding.Video.Length==0)throw FragmentMediaProbe.Invalid("The chosen container cannot hold video.");
            string work=Path.Combine(stage,"merge-"+Guid.NewGuid().ToString("N"));
            if(Directory.Exists(work) || File.Exists(work))throw FragmentMediaProbe.Invalid("A merge staging directory already exists.");
            string publish=Path.Combine(work,"publish");Directory.CreateDirectory(publish);
            var inputs=original.ToArray();
            if(!compatible)
            {
                EnsureCanReencode(original);
                string normalized=Path.Combine(work,"normalized");Directory.CreateDirectory(normalized);
                for(int index=0;index<inputs.Length;index++)
                {
                    progress?.Invoke($"Перекодирование фрагмента {index+1}/{inputs.Length}",true);
                    string target=Path.Combine(normalized,index.ToString("D2",CultureInfo.InvariantCulture)+extension);
                    await NormalizeAsync(original[index],original[0],target,encoding,budget.Token).ConfigureAwait(false);
                    inputs[index]=await ProbeAsync(target,budget.Token).ConfigureAwait(false);ValidateMedia(inputs[index]);
                    if(!SameTrackShape(original[0],inputs[index]) || !SameAudioChannels(original[0],inputs[index]) || Math.Abs(inputs[index].Duration-original[index].Duration)>.35)
                        throw FragmentMediaProbe.Invalid("Re-encoding did not preserve the complete fragment and all of its tracks.");
                }
                if(!CopyCompatible(inputs))throw FragmentMediaProbe.Invalid("Normalized fragments still have incompatible stream parameters.");
            }
            budget.Token.ThrowIfCancellationRequested();
            string relative=Path.GetRelativePath(stage,paths[0]);
            string name=System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(relative),@"\Afragment-\d+-\d+ms ","");
            string destination=Path.Combine(publish,Path.GetDirectoryName(relative)??"",$"merged-{paths.Count}-fragments "+name);
            if(!OwnedFiles.Inside(publish,destination))throw FragmentMediaProbe.Invalid("Merged output escaped its publication stage.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);OwnedFiles.RegularOrAbsent(destination);
            progress?.Invoke("Склейка проверенных фрагментов в один файл",false);
            await JoinAsync(inputs,original.ToArray(),work,destination,encoding,embedMetadata,embedChapters,budget.Token).ConfigureAwait(false);
            OwnedFiles.RegularOrAbsent(destination);
            if(!File.Exists(destination) || new FileInfo(destination).Length==0)throw FragmentMediaProbe.Invalid("FFmpeg did not create a merged media file.");
            var result=await ProbeAsync(destination,budget.Token).ConfigureAwait(false);ValidateMedia(result);
            double expected=inputs.Sum(item=>item.Duration);
            string expectedDemuxer=encoding.Muxer switch{"mp4" or "mov" or "3gp"=>"mov","webm"=>"matroska","opus"=>"ogg","adts"=>"aac",_=>encoding.Muxer};
            if(result.Demuxer!=expectedDemuxer || !SameTrackShape(original[0],result) || !SameAudioChannels(original[0],result) || original[0].Cover is not null && result.Cover is null ||
                Math.Abs(result.Duration-expected)>Math.Max(.35,inputs.Length*.03))
                throw FragmentMediaProbe.Invalid($"Merged media did not preserve all tracks and the full ordered duration (container {result.Demuxer}/{expectedDemuxer}; audio languages {string.Join(",",result.Audio.Select(track=>track.Language))}/{string.Join(",",original[0].Audio.Select(track=>track.Language))}; duration {Number(result.Duration)}/{Number(expected)}).");
            if(!compatible && (result.Video is {} video && video.Codec!=encoding.ExpectedVideo ||
                result.Audio.Any(audio=>audio.Codec!=encoding.ExpectedAudio)))
                throw FragmentMediaProbe.Invalid("The encoded output has unexpected codecs.");
            budget.Token.ThrowIfCancellationRequested();
            return new(publish,destination,!compatible,result.Duration);
        }
        catch(OperationCanceledException) when(budget.Expired && !token.IsCancellationRequested)
        {throw new TimeoutException("Fragment merge failed: Processing exceeded its active-time deadline.");}
    }

    private async Task<FragmentMediaInfo> ProbeAsync(string path,CancellationToken token)
    {
        OwnedFiles.RegularOrAbsent(path);
        var start=Start(_ffprobe);
        Add(start,"-v","error","-max_alloc","268435456","-protocol_whitelist","file","-format_whitelist",FragmentMediaProbe.AllowedDemuxers,
            "-probesize","2097152","-analyzeduration","3000000","-max_pixels","67108864","-show_data_hash","sha256",
            "-show_entries","format=duration,format_name:stream=index,codec_type,codec_name,codec_tag_string,profile,level,width,height,pix_fmt,sample_aspect_ratio,r_frame_rate,avg_frame_rate,time_base,sample_fmt,sample_rate,channels,channel_layout,color_range,color_space,color_transfer,color_primaries,extradata_hash:stream_disposition=attached_pic:stream_tags=language,mimetype,handler_name:chapter=start_time,end_time:chapter_tags=title",
            "-of","json",path);
        await using var budget=new ActiveBudget(token,TimeSpan.FromSeconds(30),_paused);
        try
        {
            var result=await _run(start,1024*1024,128*1024,budget.Token).ConfigureAwait(false);
            if(result.ExitCode!=0)throw FragmentMediaProbe.Invalid("FFprobe could not inspect a complete local fragment. "+ToolDiagnostic(result));
            try{return FragmentMediaProbe.Parse(path,result.StandardOutput);}
            catch(Exception error) when(error is JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {throw new InvalidDataException("Fragment merge failed: FFprobe returned an invalid media structure.",error);}
        }
        catch(OperationCanceledException) when(budget.Expired && !token.IsCancellationRequested)
        {throw new TimeoutException("Fragment merge failed: Media inspection exceeded its active-time deadline.");}
    }
    private static void ValidateMedia(FragmentMediaInfo info)
    {
        if(info.Video is {} video && (video.Width<=0 || video.Height<=0 || video.Width>16384 || video.Height>16384 ||
            (long)video.Width*video.Height>67108864))throw FragmentMediaProbe.Invalid("Video dimensions exceed the processing limit.");
        if(info.Audio.Any(audio=>audio.SampleRate is <8000 or >384000 || audio.Channels is <1 or >8))
            throw FragmentMediaProbe.Invalid("Unsupported audio sample rate or channel count.");
        if(info.Tracks.Any(stream=>stream.Codec.Length==0))throw FragmentMediaProbe.Invalid("A media track has no known codec.");
    }
    private static bool SameTrackShape(FragmentMediaInfo first,FragmentMediaInfo next)=>
        (first.Video is not null)==(next.Video is not null) && first.Audio.Length==next.Audio.Length &&
        first.Audio.Select(stream=>stream.Language).SequenceEqual(next.Audio.Select(stream=>stream.Language),StringComparer.OrdinalIgnoreCase);
    private static bool SameAudioChannels(FragmentMediaInfo first,FragmentMediaInfo next)=>
        first.Audio.Length==next.Audio.Length && first.Audio.Zip(next.Audio).All(pair=>pair.First.Channels==pair.Second.Channels &&
            (pair.First.ChannelLayout.Length==0 || pair.First.ChannelLayout==pair.Second.ChannelLayout));
    private static void EnsureTrackShape(IReadOnlyList<FragmentMediaInfo> files)
    {
        if(files.Skip(1).Any(file=>!SameTrackShape(files[0],file)))
            throw FragmentMediaProbe.Invalid("Fragments have different video/audio track counts or languages. Save separate files to preserve them.");
    }
    internal static bool CopyCompatible(IReadOnlyList<FragmentMediaInfo> files)
    {
        var first=files[0];
        return files.All(file=>file.Demuxer==first.Demuxer && file.Tracks.Select(track=>(track.Index,track.Signature))
            .SequenceEqual(first.Tracks.Select(track=>(track.Index,track.Signature))));
    }
    private static readonly HashSet<string> ReencodePixelFormats=new(StringComparer.Ordinal)
        {"yuv420p","yuvj420p","yuv422p","yuvj422p","yuv444p","yuvj444p","yuv440p","yuvj440p","yuv411p","yuv410p",
            "nv12","nv21","yuyv422","uyvy422","rgb24","bgr24","rgb0","bgr0","0rgb","0bgr","gbrp","gray"};
    private static void EnsureCanReencode(IReadOnlyList<FragmentMediaInfo> files)
    {
        foreach(var file in files)
        {
            if(file.Video is {} timing && (timing.Fps<=0 || Math.Abs(timing.PixelAspect-1)>1e-9))
                throw FragmentMediaProbe.Invalid("Re-encoding requires a known frame rate up to 240 FPS and square pixels. Save separate files.");
            if(file.Video is {} video && (video.Transfer is "smpte2084" or "arib-std-b67" ||
                !ReencodePixelFormats.Contains(video.PixelFormat)))
                throw FragmentMediaProbe.Invalid("HDR, high-bit-depth or unsupported pixel-format re-encoding is unsupported. Use compatible stream copy or separate files.");
            if(file.Audio.Any(stream=>stream.Channels>2 && stream.ChannelLayout.Length==0))
                throw FragmentMediaProbe.Invalid("Multichannel audio requires a known channel layout for re-encoding. Save separate files.");
            if(file.Audio.Select(stream=>(stream.Channels,stream.ChannelLayout)).SequenceEqual(files[0].Audio.Select(stream=>(stream.Channels,stream.ChannelLayout)))==false)
                throw FragmentMediaProbe.Invalid("Audio channel layouts differ. Save separate files to preserve them.");
        }
    }
    private static string ToolDiagnostic(FragmentMergeToolOutput result)=>
        Encoding.UTF8.GetString(result.StandardError.AsSpan(0,Math.Min(4096,result.StandardError.Length))).Trim();
    private static ProcessStartInfo Start(string executable)=>new(executable){UseShellExecute=false,CreateNoWindow=true,
        RedirectStandardOutput=true,RedirectStandardError=true};
    private static void Add(ProcessStartInfo start,params string[] arguments)
    {foreach(string argument in arguments)start.ArgumentList.Add(argument);}
    private static string Number(double value)=>value.ToString("0.#########",CultureInfo.InvariantCulture);
    private static async Task<FragmentMergeToolOutput> RunOwnedAsync(ProcessStartInfo start,int stdout,int stderr,CancellationToken token)
    {
        using var child=ManagedChildProcess.Start(start);
        var result=await BoundedProcessOutput.ReadAsync(child,stdout,stderr,token).ConfigureAwait(false);
        return new(child.Process.ExitCode,result.StandardOutput,result.StandardError);
    }
    private sealed class ActiveBudget:IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop;
        private readonly Task _watch;
        internal bool Expired {get;private set;}
        internal CancellationToken Token=>_stop.Token;
        internal ActiveBudget(CancellationToken token,TimeSpan timeout,Func<bool> paused)
        {
            if(timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
            _stop=CancellationTokenSource.CreateLinkedTokenSource(token);
            _watch=Watch();
            async Task Watch()
            {
                double active=0;long last=Stopwatch.GetTimestamp();
                try
                {
                    while(true)
                    {
                        await Task.Delay(100,_stop.Token).ConfigureAwait(false);
                        long now=Stopwatch.GetTimestamp();if(!paused())active+=Stopwatch.GetElapsedTime(last,now).TotalMilliseconds;last=now;
                        if(active>=timeout.TotalMilliseconds){Expired=true;_stop.Cancel();return;}
                    }
                }
                catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
            }
        }
        public async ValueTask DisposeAsync()
        {_stop.Cancel();await _watch.ConfigureAwait(false);_stop.Dispose();}
    }
}
