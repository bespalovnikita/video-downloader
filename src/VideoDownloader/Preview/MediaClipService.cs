using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;

namespace VideoDownloader.Native;

/// <summary>An owned, short local clip. Detach the player before disposing it.</summary>
internal sealed class MediaClipPreview(string path,double start,double end,bool hasVideo,bool hasAudio,Action? cleanup=null):IDisposable
{
    private readonly object _useGate=new();
    private Action? _cleanup=cleanup;
    private bool _released;
    private int _users;
    internal string Path {get;}=path;
    internal double StartSeconds {get;}=start;
    internal double EndSeconds {get;}=end;
    internal double DurationSeconds=>EndSeconds-StartSeconds;
    internal bool HasVideo {get;}=hasVideo;
    internal bool HasAudio {get;}=hasAudio;
    // A decoder can retain this same cache slot while its process and pipes
    // finish. Disposing the service revokes ownership without deleting an
    // input file that an in-flight consumer still uses.
    internal IDisposable Retain()
    {
        lock(_useGate)
        {
            ObjectDisposedException.ThrowIf(_released,this);
            _users++;return new Usage(this);
        }
    }
    private sealed class Usage(MediaClipPreview owner):IDisposable
    {
        private MediaClipPreview? _owner=owner;
        public void Dispose()=>Interlocked.Exchange(ref _owner,null)?.ReleaseUse();
    }
    private void ReleaseUse()
    {
        Action? cleanup=null;
        lock(_useGate)
        {
            _users--;
            if(_released && _users==0){cleanup=_cleanup;_cleanup=null;}
        }
        cleanup?.Invoke();
    }
    public void Dispose()
    {
        Action? cleanup=null;
        lock(_useGate)
        {
            if(_released)return;_released=true;
            if(_users==0){cleanup=_cleanup;_cleanup=null;}
        }
        cleanup?.Invoke();
    }
}

/// <summary>Serial, bounded preparation independent of queue, results and history.</summary>
internal sealed class MediaClipService:IDisposable
{
    internal const double MaxPreviewSeconds=15;
    internal const long MaxInputBytes=64L*1024*1024;
    internal const long MaxClipBytes=16L*1024*1024;
    internal const int MaxCachedClips=4;
    internal static readonly TimeSpan RequestTimeout=TimeSpan.FromSeconds(60);
    private readonly Func<MediaClipRequest,CancellationToken,Task<MediaClipSources>> _source;
    private readonly string _ffmpeg,_ffprobe;
    private readonly MediaClipCache _cache;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _gate=new();
    private readonly HashSet<MediaClipPreview> _clips=[];
    private int _disposed;
    internal string FfmpegExecutable=>_ffmpeg;
    internal MediaClipService(PreviewService previews,string root,string? ffmpeg=null,string? ffprobe=null,TimeSpan? timeout=null)
        :this(previews.GetClipSourcesAsync,root,ffmpeg,ffprobe,timeout) { }
    internal MediaClipService(Func<MediaClipRequest,CancellationToken,Task<MediaClipSources>> source,string root,
        string? ffmpeg=null,string? ffprobe=null,TimeSpan? timeout=null)
    {
        _source=source;_cache=new MediaClipCache(System.IO.Path.GetFullPath(System.IO.Path.Combine(root,"playback")));
        _ffmpeg=ffmpeg??DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        _ffprobe=ffprobe??DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
        _timeout=timeout??RequestTimeout;if(_timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    internal Task<MediaClipResult> GetClipAsync(MediaClipRequest request,CancellationToken token)=>
        Task.Run(()=>GetCoreAsync(request,token),CancellationToken.None);
    private async Task<MediaClipResult> GetCoreAsync(MediaClipRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);deadline.CancelAfter(_timeout);
        bool entered=false;MediaClipCache.Lease? lease=null;
        MediaClipResult Failure(string error)=>new(request.Url,request.StartSeconds,request.EndSeconds,null,error);
        try
        {
            if(!AppPersistence.ValidUrl(request.Url) || !double.IsFinite(request.StartSeconds) || !double.IsFinite(request.EndSeconds) ||
                request.StartSeconds<0 || request.EndSeconds<=request.StartSeconds || request.EndSeconds>=TimeSpan.MaxValue.TotalSeconds ||
                request.DurationSeconds is {} duration && (!double.IsFinite(duration) || duration<=0 || request.EndSeconds>duration) ||
                request.AudioTrack is not ("Default" or "Original" or "Languages") || request.AudioLanguages.Length>256 || request.AudioLanguages.Any(char.IsControl))
                return Failure("Для воспроизведения укажите корректное начало и конец фрагмента.");
            if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
            if(!File.Exists(_ffmpeg) || !File.Exists(_ffprobe))return Failure("Для воспроизведения нужны FFmpeg и FFprobe. Установите их в разделе зависимостей.");
            await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);entered=true;
            lock(_gate)if(_clips.Count>=MaxCachedClips)return Failure("Уже открыты четыре предпросмотра. Закройте один из них перед подготовкой нового.");
            var sources=await _source(request,deadline.Token).ConfigureAwait(false);
            var video=sources.Video;var audio=sources.Audio;
            if(video is null && audio is null)return Failure("Источник не предоставил медиа для воспроизведения.");
            double? known=request.DurationSeconds??video?.Duration??audio?.Duration;
            if(known is {} maximum && request.EndSeconds>maximum+.001)return Failure("Фрагмент выходит за длительность материала.");
            double begin=request.FromEnd?Math.Max(request.StartSeconds,request.EndSeconds-MaxPreviewSeconds):request.StartSeconds;
            double end=Math.Min(request.EndSeconds,begin+MaxPreviewSeconds),length=end-begin;
            var inputs=new List<ClipInput>();
            try
            {
                long perInput=video is not null && audio is not null?MaxInputBytes/2:MaxInputBytes;
                if(video is not null)inputs.Add(await PrepareAsync(video,begin,end,perInput,deadline.Token).ConfigureAwait(false));
                if(audio is not null)inputs.Add(await PrepareAsync(audio,begin,end,perInput,deadline.Token).ConfigureAwait(false));
                bool hasVideo=video is not null,hasAudio=audio is not null || video?.HasAudio==true;
                int audioIndex=audio is not null?inputs.Count-1:0;
                if(video is {AudioKnown:false})
                {
                    var actual=await ProbeInputAsync(inputs[0],deadline.Token).ConfigureAwait(false);
                    hasVideo=actual.Video;if(audio is null)hasAudio=actual.Audio;
                }
                if(audio is {AudioKnown:false})hasAudio=(await ProbeInputAsync(inputs[audioIndex],deadline.Token).ConfigureAwait(false)).Audio;
                if(!hasVideo && !hasAudio)return Failure("Источник не предоставил пригодных видео- или аудиодорожек.");
                lease=_cache.Reserve();string path=lease.Path;
                var start=new ProcessStartInfo(_ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
                Add(start,"-hide_banner","-loglevel","error","-nostdin","-y","-max_alloc","67108864","-filter_complex_threads","1");
                foreach(var input in inputs)
                {
                    Add(start,"-threads","2","-protocol_whitelist","http,tcp","-format_whitelist",input.Prepared?.Bytes is not null?input.Prepared.Demuxer:"mov,matroska,webm,mp3,aac,ogg,flac,wav",
                        "-rw_timeout","5000000","-probesize","2097152","-analyzeduration","3000000","-max_pixels","16777216");
                    if(input.Prepared is {Bytes:not null} binary)
                    {
                        Add(start,"-f",binary.Demuxer);
                        if(binary.Demuxer=="mov")Add(start,"-enable_drefs","0","-use_absolute_path","0");
                    }
                    else Add(start,"-ss",Number(input.Seek));
                    Add(start,"-i",input.Proxy.Address.AbsoluteUri);
                }
                var filters=new List<string>();
                if(hasVideo)
                {
                    filters.Add("[0:v:0]trim=start="+Number(inputs[0].FilterSeek)+":duration="+Number(length)+",setpts=PTS-STARTPTS,scale=w='min(640,iw)':h='min(360,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2[v]");
                }
                // Keep the same requested source origin as the video. A late
                // audio track must keep its leading gap instead of moving its
                // first audible sample to the start of the clip. first_pts
                // pads that gap with real silence without stretching its tail.
                if(hasAudio)filters.Add("["+audioIndex+":a:0]atrim=start="+Number(inputs[audioIndex].FilterSeek)+":duration="+Number(length)+
                    ",asetpts=PTS-"+Number(inputs[audioIndex].FilterSeek)+"/TB,aresample=48000:first_pts=0[a]");
                Add(start,"-filter_complex",string.Join(';',filters));
                if(hasVideo)Add(start,"-map","[v]","-c:v","libx264","-preset","veryfast","-crf","28","-pix_fmt","yuv420p","-threads","2");
                else Add(start,"-vn");
                // A single trimmed frame has no following timestamp from which
                // the encoder can infer duration. Retain its nominal duration;
                // longer previews keep the existing source timestamp behavior.
                if(hasVideo && FragmentTimelineTiming.KnownFps(video!.Fps) && length<=1/video.Fps+1e-9)
                    Add(start,"-r",Number(video.Fps));
                if(hasAudio)Add(start,"-map","[a]","-c:a","aac","-b:a","128k","-ar","48000","-ac","2");
                else Add(start,"-an");
                Add(start,"-sn","-dn","-map_metadata","-1","-map_chapters","-1","-t",Number(length),"-fs",MaxClipBytes.ToString(CultureInfo.InvariantCulture),"-movflags","+faststart","-f","mp4",path);
                using var decode=CancellationTokenSource.CreateLinkedTokenSource(new[]{deadline.Token}.Concat(inputs.Select(input=>input.Proxy.FailureToken)).ToArray());
                using(var child=ManagedChildProcess.Start(start))
                {
                    try{await BoundedProcessOutput.ReadAsync(child,4096,MediaFrameService.MaxErrorBytes,decode.Token).ConfigureAwait(false);}
                    catch(OperationCanceledException) when(inputs.Any(input=>input.Proxy.Failure is not null))
                    {throw new MediaFrameUnavailableException("Источник не смог передать фрагмент в пределах ограничений предпросмотра.");}
                    if(child.Process.ExitCode!=0 || inputs.Any(input=>input.Proxy.Failure is not null))return Failure("Не удалось подготовить фрагмент. Источник может не поддерживать поиск или выбранную дорожку.");
                }
                OwnedFiles.RegularOrAbsent(path);
                if(!File.Exists(path) || new FileInfo(path).Length is <=0 or >MaxClipBytes)return Failure("Фрагмент превышает ограничение объёма предпросмотра.");
                var probe=await ProbeAsync(path,deadline.Token).ConfigureAwait(false);
                if(probe.Video!=hasVideo || probe.Audio!=hasAudio || probe.Duration<Math.Max(Math.Min(.02,length),length-.35) || probe.Duration>length+.35)
                    return Failure("Источник не предоставил полный короткий фрагмент с выбранными дорожками.");
                deadline.Token.ThrowIfCancellationRequested();
                var owned=lease;MediaClipPreview? clip=null;
                clip=new(path,begin,end,probe.Video,probe.Audio,()=>{lock(_gate)_clips.Remove(clip!);owned.Dispose();});
                lock(_gate)
                {
                    if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
                    _clips.Add(clip);
                }
                lease=null;return new(request.Url,request.StartSeconds,request.EndSeconds,clip,"");
            }
            finally{foreach(var input in inputs)await input.Proxy.DisposeAsync().ConfigureAwait(false);}
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested || _stop.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return Failure("Время подготовки фрагмента истекло. Можно повторить попытку.");}
        catch(MediaFrameUnavailableException){return Failure("Источник недоступен для ограниченного предпросмотра. Проверьте доступ и выбранный формат.");}
        catch(Exception){return Failure("Не удалось подготовить фрагмент. Проверьте доступность материала и FFmpeg.");}
        finally{lease?.Dispose();if(entered)_worker.Release();}
    }
    private sealed record ClipInput(BoundedMediaProxy Proxy,PreparedManifestMedia? Prepared,double Seek)
    {internal double FilterSeek=>Prepared?.Bytes is not null?Seek:0;}
    private static async Task<ClipInput> PrepareAsync(MediaFrameSource source,double begin,double end,long budget,CancellationToken token)
    {
        var cookies=await MediaFrameCookies.CreateAsync(source,token).ConfigureAwait(false);
        PreparedManifestMedia? prepared=null;
        if(source.IsHls || source.IsDash)prepared=await ManifestFrameMedia.PrepareAsync(source,begin,cookies,token,budget,end).ConfigureAwait(false);
        var proxy=prepared is null?new BoundedMediaProxy(source.Address,source.Headers,cookies,token,budget,allowAudio:true):
            prepared.DirectAddress is {} direct?new BoundedMediaProxy(direct,source.Headers,cookies,token,budget-prepared.DownloadedBytes,source.ManifestAddress??source.Address,allowAudio:true):
            new BoundedMediaProxy(prepared.Bytes!,prepared.ContentType,token,budget);
        return new(proxy,prepared,Math.Max(0,begin-(prepared?.StartSeconds??0)));
    }
    private async Task<(bool Video,bool Audio)> ProbeInputAsync(ClipInput input,CancellationToken token)
    {
        var start=new ProcessStartInfo(_ffprobe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        Add(start,"-v","error","-max_alloc","67108864","-protocol_whitelist","http,tcp","-format_whitelist",
            input.Prepared?.Bytes is not null?input.Prepared.Demuxer:"mov,matroska,webm,mp3,aac,ogg,flac,wav",
            "-rw_timeout","5000000","-probesize","2097152","-analyzeduration","3000000","-max_pixels","16777216");
        if(input.Prepared is {Bytes:not null} binary)
        {
            Add(start,"-f",binary.Demuxer);
            if(binary.Demuxer=="mov")Add(start,"-enable_drefs","0","-use_absolute_path","0");
        }
        Add(start,"-i",input.Proxy.Address.AbsoluteUri,"-show_entries","stream=codec_type","-of","json");
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token,input.Proxy.FailureToken);
        using var child=ManagedChildProcess.Start(start);
        var output=await BoundedProcessOutput.ReadAsync(child,64*1024,MediaFrameService.MaxErrorBytes,bounded.Token).ConfigureAwait(false);
        if(child.Process.ExitCode!=0 || input.Proxy.Failure is not null)throw new MediaFrameUnavailableException("Не удалось определить дорожки предпросмотра.");
        using var json=JsonDocument.Parse(output.StandardOutput,new JsonDocumentOptions{MaxDepth=16});
        var tracks=json.RootElement.GetProperty("streams").EnumerateArray().Select(stream=>stream.GetProperty("codec_type").GetString()).ToArray();
        return(tracks.Contains("video"),tracks.Contains("audio"));
    }
    private async Task<(bool Video,bool Audio,double Duration)> ProbeAsync(string path,CancellationToken token)
    {
        var start=new ProcessStartInfo(_ffprobe){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        Add(start,"-v","error","-protocol_whitelist","file","-show_entries","stream=codec_type:format=duration","-of","json",path);
        using var child=ManagedChildProcess.Start(start);
        var output=await BoundedProcessOutput.ReadAsync(child,64*1024,MediaFrameService.MaxErrorBytes,token).ConfigureAwait(false);
        if(child.Process.ExitCode!=0)throw new IOException("Invalid preview clip.");
        using var json=JsonDocument.Parse(output.StandardOutput,new JsonDocumentOptions{MaxDepth=16});
        var streams=json.RootElement.GetProperty("streams").EnumerateArray().Select(stream=>stream.GetProperty("codec_type").GetString()).ToArray();
        double duration=double.Parse(json.RootElement.GetProperty("format").GetProperty("duration").GetString()!,CultureInfo.InvariantCulture);
        return(streams.Contains("video"),streams.Contains("audio"),duration);
    }
    private static string Number(double value)=>value.ToString("0.#########",CultureInfo.InvariantCulture);
    private static void Add(ProcessStartInfo start,params string[] values){foreach(string value in values)start.ArgumentList.Add(value);}
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel();MediaClipPreview[] clips;lock(_gate)clips=_clips.ToArray();foreach(var clip in clips)clip.Dispose();
    }
}
