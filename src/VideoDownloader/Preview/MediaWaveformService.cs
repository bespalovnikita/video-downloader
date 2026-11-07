using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;

namespace VideoDownloader.Native;

/// <summary>Explicit waveform requests reuse the existing bounded clip owner.</summary>
internal sealed class MediaWaveformService:IDisposable
{
    internal const int SampleRate=48000;
    internal const int Channels=2;
    private const int BytesPerFrame=Channels*2;
    internal const int MaximumPcmBytes=(int)(MediaClipService.MaxPreviewSeconds*SampleRate*BytesPerFrame)+BytesPerFrame;
    internal static readonly TimeSpan RequestTimeout=TimeSpan.FromSeconds(60);
    private readonly Func<MediaClipRequest,CancellationToken,Task<MediaClipResult>> _prepare;
    private readonly string _ffmpeg;
    private readonly TimeSpan _timeout;
    private readonly CancellationTokenSource _stop=new();
    private readonly SemaphoreSlim _worker=new(1,1);
    private int _disposed;

    internal MediaWaveformService(MediaClipService clips,string? ffmpeg=null,TimeSpan? timeout=null)
        :this(clips.GetClipAsync,ffmpeg??clips.FfmpegExecutable,timeout) { }
    internal MediaWaveformService(Func<MediaClipRequest,CancellationToken,Task<MediaClipResult>> prepare,
        string? ffmpeg=null,TimeSpan? timeout=null)
    {
        _prepare=prepare;_ffmpeg=ffmpeg??DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        _timeout=timeout??RequestTimeout;
        if(_timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(timeout));
    }
    internal Task<MediaWaveformResult> GetWaveformAsync(MediaWaveformRequest request,CancellationToken token)=>
        Task.Run(()=>GetCoreAsync(request,token),CancellationToken.None);

    private async Task<MediaWaveformResult> GetCoreAsync(MediaWaveformRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        double boundary=request.IsEnd?request.ClipRequest.EndSeconds:request.ClipRequest.StartSeconds;
        MediaWaveformResult Failure(string message)=>new(request.ClipRequest.Url,boundary,null,message);
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token,_stop.Token);
        deadline.CancelAfter(_timeout);
        bool entered=false;MediaClipPreview? owned=null;IDisposable? decoding=null;
        try
        {
            if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
            if(!TryContext(request,out var context))return Failure("Укажите корректные начало и конец фрагмента для звуковой волны.");
            if(!File.Exists(_ffmpeg))return Failure("Для звуковой волны нужен FFmpeg. Установите его в разделе зависимостей.");
            await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);entered=true;
            var prepared=await _prepare(context!,deadline.Token).ConfigureAwait(false);
            owned=prepared.Clip;
            if(prepared.Url!=context!.Url || prepared.RequestedStart!=context.StartSeconds || prepared.RequestedEnd!=context.EndSeconds)
                return Failure("Источник вернул другое окно звуковой волны.");
            if(!prepared.Success)return Failure(prepared.Error.Length>0?prepared.Error:"Звуковая волна недоступна.");
            decoding=owned!.Retain();
            if(!owned!.HasAudio)return Failure("В выбранном источнике нет аудиодорожки.");
            if(!double.IsFinite(owned.StartSeconds) || !double.IsFinite(owned.EndSeconds) ||
                Math.Abs(owned.StartSeconds-context.StartSeconds)>.001 || Math.Abs(owned.EndSeconds-context.EndSeconds)>.001 ||
                owned.DurationSeconds<=0 || owned.DurationSeconds>MediaClipService.MaxPreviewSeconds+.001)
                return Failure("Источник не подтвердил выбранное окно звуковой волны.");
            OwnedFiles.NoReparseAncestors(owned.Path);OwnedFiles.RegularOrAbsent(owned.Path);
            if(!File.Exists(owned.Path) || new FileInfo(owned.Path).Length is <=0 or >MediaClipService.MaxClipBytes)
                return Failure("Подготовленный фрагмент недоступен для анализа звука.");
            var waveform=await DecodeAsync(owned,deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            return new(request.ClipRequest.Url,boundary,waveform,"");
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested || _stop.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return Failure("Время подготовки звуковой волны истекло. Можно повторить запрос.");}
        catch(Exception){return Failure("Не удалось получить звуковую волну. Проверьте выбранную дорожку и FFmpeg.");}
        finally
        {
            // The decoder and both pipes have finished before its local lease
            // is released; this file is never shared with the playback graph.
            decoding?.Dispose();owned?.Dispose();if(entered)_worker.Release();
        }
    }

    internal static bool TryContext(MediaWaveformRequest request,out MediaClipRequest? context)
    {
        context=null;var clip=request.ClipRequest;
        if(!AppPersistence.ValidUrl(clip.Url) || !double.IsFinite(clip.StartSeconds) || !double.IsFinite(clip.EndSeconds) ||
            clip.StartSeconds<0 || clip.EndSeconds<=clip.StartSeconds || clip.EndSeconds>=TimeSpan.MaxValue.TotalSeconds ||
            clip.DurationSeconds is {} total && (!double.IsFinite(total) || total<=0 || clip.EndSeconds>total))
            return false;
        double boundary=request.IsEnd?clip.EndSeconds:clip.StartSeconds;
        double start=Math.Max(0,boundary-MediaClipService.MaxPreviewSeconds/2);
        double end;
        if(clip.DurationSeconds is {} known)
        {
            end=Math.Min(known,start+MediaClipService.MaxPreviewSeconds);
            start=Math.Max(0,end-MediaClipService.MaxPreviewSeconds);
        }
        else if(request.IsEnd)
        {
            // Without source duration, seeking beyond the selected end would
            // invent coverage. The earlier side of this boundary is known.
            end=boundary;start=Math.Max(0,end-MediaClipService.MaxPreviewSeconds);
        }
        else end=Math.Min(clip.EndSeconds,start+MediaClipService.MaxPreviewSeconds);
        if(end<=start || boundary<start || boundary>end)return false;
        context=clip with{StartSeconds=start,EndSeconds=end,FromEnd=false};
        return true;
    }

    private async Task<AudioWaveform> DecodeAsync(MediaClipPreview clip,CancellationToken token)
    {
        var start=new ProcessStartInfo(_ffmpeg)
            {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        string[] arguments=["-hide_banner","-loglevel","error","-nostdin","-max_alloc","16777216","-threads","1",
            "-protocol_whitelist","file","-format_whitelist","mov","-f","mov","-enable_drefs","0","-use_absolute_path","0",
            "-i",clip.Path,"-map","0:a:0","-vn","-sn","-dn","-t",
            clip.DurationSeconds.ToString("0.######",CultureInfo.InvariantCulture),
            "-ac",Channels.ToString(CultureInfo.InvariantCulture),"-ar",SampleRate.ToString(CultureInfo.InvariantCulture),"-c:a","pcm_s16le","-f","s16le","pipe:1"];
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var bounded=CancellationTokenSource.CreateLinkedTokenSource(token);
        bounded.CancelAfter(TimeSpan.FromSeconds(10));
        using var child=ManagedChildProcess.Start(start);
        var output=await BoundedProcessOutput.ReadAsync(child,MaximumPcmBytes,MediaFrameService.MaxErrorBytes,bounded.Token).ConfigureAwait(false);
        if(child.Process.ExitCode!=0)throw new IOException("Audio waveform decoder failed.");
        return FromPcm(clip.StartSeconds,clip.EndSeconds,output.StandardOutput);
    }

    internal static AudioWaveform FromPcm(double start,double end,ReadOnlySpan<byte> pcm)
    {
        if(!double.IsFinite(start) || !double.IsFinite(end) || start<0 || end<=start ||
            end-start>MediaClipService.MaxPreviewSeconds+.001 || pcm.Length==0 || pcm.Length%BytesPerFrame!=0 || pcm.Length>MaximumPcmBytes)
            throw new InvalidDataException("Invalid waveform PCM.");
        int expected=checked((int)Math.Ceiling((end-start)*SampleRate));
        int actual=pcm.Length/BytesPerFrame;
        if(actual>expected+1)throw new InvalidDataException("Decoded audio exceeds its waveform window.");
        actual=Math.Min(actual,expected);
        int count=Math.Min(AudioWaveform.MaximumBins,expected);
        var bins=new WaveformBin[count];
        for(int index=0;index<count;index++)
        {
            int first=(int)((long)index*expected/count),last=(int)((long)(index+1)*expected/count);
            double peak=0,squares=0;
            for(int sample=first;sample<Math.Min(last,actual);sample++)
            {
                for(int channel=0;channel<Channels;channel++)
                {
                    double value=BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(sample*BytesPerFrame+channel*2,2))/32768d;
                    peak=Math.Max(peak,Math.Abs(value));squares+=value*value;
                }
            }
            // A shorter audio track has no audible samples in the remaining
            // media interval. Keep that gap in place instead of stretching it.
            // Measure both channels independently: a mono downmix would erase
            // audible opposite-phase stereo; 48 kHz retains the audible band.
            bins[index]=new(peak,Math.Sqrt(squares/((last-first)*Channels)));
        }
        return new(start,end,bins,Math.Min(end,start+actual/(double)SampleRate));
    }

    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        _stop.Cancel();
    }
}
