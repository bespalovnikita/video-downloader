using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

namespace VideoDownloader.Native;

public sealed record MediaFrameRequest(string Url,double Seconds,string? VideoFormatId=null,
    double? DurationSeconds=null,bool IsEnd=false);

public sealed record MediaFrameResult(string Url,double RequestedSeconds,double? SampledSeconds,byte[] PngBytes,string Error)
{
    public bool Success=>PngBytes.Length>0;
}

internal sealed record MediaFrameSource(Uri Address,string FormatId,IReadOnlyDictionary<string,string> Headers,
    string Cookies,string CookieFile,string CookieContext,double Fps,double? Duration,int Width,int Height);

internal sealed class MediaFrameUnavailableException(string message):IOException(message);

/// <summary>One bounded frame decoder; it never owns queue results, published files or history.</summary>
internal sealed class MediaFrameService:IDisposable
{
    internal const int MaxFrameBytes=1024*1024;
    internal const int MaxErrorBytes=128*1024;
    internal const long MaxMediaBytes=16L*1024*1024;
    internal static readonly TimeSpan RequestTimeout=TimeSpan.FromSeconds(30);
    private readonly PreviewService _previews;
    private readonly string _ffmpeg;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _worker=new(1,1);
    private readonly CancellationTokenSource _stop=new();
    private int _disposed;

    internal MediaFrameService(PreviewService previews,string? ffmpegPath=null,TimeSpan? requestTimeout=null)
    {
        _previews=previews;
        _ffmpeg=ffmpegPath??DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        _timeout=requestTimeout??RequestTimeout;
        if(_timeout<=TimeSpan.Zero)throw new ArgumentOutOfRangeException(nameof(requestTimeout));
    }
    internal Task<MediaFrameResult> GetFrameAsync(MediaFrameRequest request,CancellationToken cancellationToken)=>
        Task.Run(()=>GetCoreAsync(request,cancellationToken),CancellationToken.None);

    private async Task<MediaFrameResult> GetCoreAsync(MediaFrameRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken,_stop.Token);
        deadline.CancelAfter(_timeout);
        bool entered=false;
        MediaFrameResult Failure(string error)=>new(request.Url,request.Seconds,null,[],error);
        try
        {
            if(!AppPersistence.ValidUrl(request.Url) || !double.IsFinite(request.Seconds) || request.Seconds<0 ||
                request.Seconds>=TimeSpan.MaxValue.TotalSeconds || request.DurationSeconds is {} duration &&
                (!double.IsFinite(duration) || duration<=0 || request.Seconds>duration))
                return Failure("Для кадра укажите корректное время внутри материала.");
            if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
            if(!File.Exists(_ffmpeg))return Failure("Для кадров нужен FFmpeg. Установите его в разделе зависимостей.");
            await _worker.WaitAsync(deadline.Token).ConfigureAwait(false);entered=true;
            var source=await _previews.GetFrameSourceAsync(request.Url,request.VideoFormatId,deadline.Token).ConfigureAwait(false);
            double? known=request.DurationSeconds??source.Duration;
            if(known is {} maximum && request.Seconds>maximum)
                return Failure("Выбранное время выходит за длительность материала.");
            double seek=request.IsEnd?Math.Max(0,request.Seconds-(source.Fps>0?1/source.Fps:.05)):request.Seconds;
            var cookies=await MediaFrameCookies.CreateAsync(source,deadline.Token).ConfigureAwait(false);
            await using var proxy=new BoundedMediaProxy(source.Address,source.Headers,cookies,deadline.Token,MaxMediaBytes);
            using var decode=CancellationTokenSource.CreateLinkedTokenSource(deadline.Token,proxy.FailureToken);
            var start=new ProcessStartInfo(_ffmpeg){UseShellExecute=false,CreateNoWindow=true,
                RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"-hide_banner","-loglevel","error","-nostdin","-max_alloc","67108864",
                "-threads","2","-filter_threads","1","-protocol_whitelist","http,tcp","-format_whitelist","mov,matroska,webm",
                "-rw_timeout","5000000","-probesize","2097152","-analyzeduration","3000000","-max_pixels","16777216",
                "-ss",seek.ToString("0.######",CultureInfo.InvariantCulture),"-i",proxy.Address.AbsoluteUri,
                "-map","0:v:0","-frames:v","1","-an","-sn","-dn","-vf",
                "scale='min(640,iw)':'min(360,ih)':force_original_aspect_ratio=decrease","-c:v","png","-threads","1","-f","image2pipe","pipe:1"})
                start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);
            CapturedProcessOutput output;
            try {output=await BoundedProcessOutput.ReadAsync(child,MaxFrameBytes,MaxErrorBytes,decode.Token).ConfigureAwait(false);}
            catch(OperationCanceledException) when(proxy.Failure is not null)
            {throw new MediaFrameUnavailableException(proxy.Failure.Message);}
            if(proxy.Failure is {} proxyError)throw new MediaFrameUnavailableException(proxyError.Message);
            if(child.Process.ExitCode!=0 || !IsBoundedPng(output.StandardOutput))
                return Failure("Не удалось получить кадр в выбранной точке. Поток может не поддерживать быстрый поиск.");
            return new(request.Url,request.Seconds,seek,output.StandardOutput,"");
        }
        catch(OperationCanceledException) when(cancellationToken.IsCancellationRequested){throw;}
        catch(OperationCanceledException) when(_stop.IsCancellationRequested){throw;}
        catch(OperationCanceledException){return Failure("Время получения кадра истекло. Можно повторить попытку.");}
        catch(MediaFrameUnavailableException error){return Failure(SafeError(error.Message));}
        catch(Exception){return Failure("Не удалось получить кадр. Проверьте доступность материала и FFmpeg.");}
        finally {if(entered)_worker.Release();}
    }
    internal static bool IsBoundedPng(byte[] bytes)
    {
        if(bytes.Length is <24 or >MaxFrameBytes || !bytes.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}))return false;
        uint width=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16,4));
        uint height=System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20,4));
        return width is >0 and <=640 && height is >0 and <=360;
    }
    private static string SafeError(string text)
    {
        string safe=JournalSanitizer.Redact(text,multiline:false);
        return safe.Length>512?safe[..512]+"…":safe;
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref _disposed,1)!=0)return;
        // Active decoders/proxies observe cancellation and join their owners.
        // Do not dispose a semaphore or token underneath their finally blocks.
        _stop.Cancel();
    }
}

internal static class MediaFrameCookies
{
    private const int MaxCookieBytes=4*1024*1024;
    internal static async Task<CookieContainer> CreateAsync(MediaFrameSource source,CancellationToken token)
    {
        var jar=new CookieContainer(512,128,8192);
        // yt-dlp saves its updated jar on exit. Read only cookies scoped to this
        // actual media URL; never turn the site's complete jar into a raw header.
        if(source.CookieFile.Length>0 && File.Exists(source.CookieFile))
        {
            using var input=new FileStream(source.CookieFile,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete,
                8192,FileOptions.Asynchronous|FileOptions.SequentialScan);
            if(input.Length>MaxCookieBytes)throw new MediaFrameUnavailableException("Файл cookies слишком большой для превью кадра.");
            using var bytes=new MemoryStream();
            var buffer=new byte[8192];
            while(true)
            {
                int count=await input.ReadAsync(buffer,token).ConfigureAwait(false);
                if(count==0)break;
                if(bytes.Length+count>MaxCookieBytes)throw new MediaFrameUnavailableException("Файл cookies слишком большой для превью кадра.");
                bytes.Write(buffer,0,count);
            }
            foreach(string raw in Encoding.UTF8.GetString(bytes.ToArray()).Split('\n'))
            {
                token.ThrowIfCancellationRequested();
                string line=raw.TrimEnd('\r');
                if(line.StartsWith("#HttpOnly_",StringComparison.Ordinal))line=line[10..];
                else if(line.StartsWith('#') || line.Length==0)continue;
                string[] fields=line.Split('\t');
                if(fields.Length!=7)continue;
                if(fields[4].Length==0)fields[4]="0";
                if(!long.TryParse(fields[4],NumberStyles.Integer,CultureInfo.InvariantCulture,out long expires))continue;
                if(expires!=0 && expires<=DateTimeOffset.UtcNow.ToUnixTimeSeconds())continue;
                Add(jar,source.Address,fields[5],fields[6],fields[0],fields[2],fields[1].Equals("TRUE",StringComparison.OrdinalIgnoreCase),
                    fields[3].Equals("TRUE",StringComparison.OrdinalIgnoreCase));
            }
        }
        // Cookies set while extracting can also be supplied in yt-dlp's scoped
        // metadata, including when the user has no imported Netscape file.
        AddMetadata(jar,source.Address,source.Cookies);
        return jar;
    }
    private static void AddMetadata(CookieContainer jar,Uri address,string text)
    {
        string? name=null,value=null,domain=null,path=null;bool secure=false;
        void Flush()
        {
            if(name is not null && value is not null && domain is not null)
                Add(jar,address,name,value,domain,path??"/",domain.StartsWith('.'),secure);
        }
        foreach(string part in Split(text))
        {
            int separator=part.IndexOf('=');
            string key=(separator<0?part:part[..separator]).Trim();
            string data=separator<0?"":part[(separator+1)..].Trim();
            if(key.Equals("Domain",StringComparison.OrdinalIgnoreCase))domain=data;
            else if(key.Equals("Path",StringComparison.OrdinalIgnoreCase))path=data;
            else if(key.Equals("Secure",StringComparison.OrdinalIgnoreCase))secure=true;
            else if(key.Equals("Expires",StringComparison.OrdinalIgnoreCase) || key.Equals("Version",StringComparison.OrdinalIgnoreCase))continue;
            else if(separator>0)
            {
                Flush();name=key;value=data;domain=null;path=null;secure=false;
            }
        }
        Flush();
    }
    private static IEnumerable<string> Split(string text)
    {
        if(text.Length>64*1024)yield break;
        bool quoted=false,escaped=false;int start=0;
        for(int index=0;index<text.Length;index++)
        {
            char current=text[index];
            if(escaped){escaped=false;continue;}
            if(current=='\\' && quoted){escaped=true;continue;}
            if(current=='"')quoted=!quoted;
            if(current==';' && !quoted){yield return text[start..index].Trim();start=index+1;}
        }
        if(start<text.Length)yield return text[start..].Trim();
    }
    private static void Add(CookieContainer jar,Uri address,string name,string value,string domain,string path,bool subdomains,bool secure)
    {
        string host=domain.TrimStart('.');
        if(host.Length==0 || !(address.Host.Equals(host,StringComparison.OrdinalIgnoreCase) || subdomains &&
            address.Host.EndsWith("."+host,StringComparison.OrdinalIgnoreCase)) || secure && address.Scheme!="https" ||
            name.Length>256 || value.Length>8192 || domain.Length>256 || path.Length>2048 ||
            new[]{name,value,domain,path}.Any(field=>field.Any(char.IsControl)))return;
        try
        {
            var cookie=new Cookie(name,value,path.Length>0?path:"/"){Secure=secure};
            if(subdomains)cookie.Domain=domain.StartsWith('.')?domain:"."+domain;
            jar.Add(address,cookie);
        }
        catch(CookieException){ }
    }
}
