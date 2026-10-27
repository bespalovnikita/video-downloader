using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

public sealed class PreviewService : IDisposable
{
    private readonly string _exe;
    private readonly string _cacheDir;
    private readonly Func<string> _cookiePath;
    private readonly Action<PreviewData> _ready;
    private readonly Action<string> _log;
    private readonly Action<string,string>? _failed;
    private readonly Func<byte[],bool> _thumbnailValidator;
    private readonly List<string> _pending=new();
    private readonly HashSet<string> _known=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,PreviewData> _cache=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,string> _thumbnailErrors=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal=new(0);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _gate=new();
    private readonly HttpClient _http=new(){Timeout=Timeout.InfiniteTimeSpan};
    private readonly TimeSpan _requestTimeout;
    private readonly TimeSpan _thumbnailTimeout;
    private readonly string? _ffmpegOverride;
    private readonly Task _worker;
    public PreviewService(string exe,AppPersistence data,Func<string> cookiePath,Action<PreviewData> ready,Action<string> log,
        TimeSpan? requestTimeout=null,TimeSpan? thumbnailTimeout=null,string? ffmpegPath=null,
        Action<string,string>? failed=null,Func<byte[],bool>? thumbnailValidator=null)
    {
        _requestTimeout=requestTimeout??TimeSpan.FromSeconds(30);
        _thumbnailTimeout=thumbnailTimeout??TimeSpan.FromSeconds(12);
        if (_requestTimeout<=TimeSpan.Zero || _thumbnailTimeout<=TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(requestTimeout));
        _exe=exe;_cacheDir=data.PreviewDir;_cookiePath=cookiePath;_ready=ready;_log=log;
        _ffmpegOverride=ffmpegPath;
        _failed=failed;_thumbnailValidator=thumbnailValidator??PreviewImage.IsValid;
        Directory.CreateDirectory(_cacheDir);
        _worker=Task.Run(Worker);
    }
    private string CacheFile(string url) => Path.Combine(_cacheDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
    public PreviewData? Get(string url)
    {
        if (_cache.TryGetValue(url,out var value) && DateTime.UtcNow-value.SavedAtUtc<TimeSpan.FromHours(72))
        {
            if(value.ThumbnailBase64.Length>0 || !_thumbnailErrors.TryGetValue(url,out string? previousError) || !ConverterNowAvailable(previousError)) return value;
            _cache.TryRemove(url,out _);_thumbnailErrors.TryRemove(url,out _);
        }
        string path=CacheFile(url);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path,Encoding.UTF8));
            var root=doc.RootElement;
            if (root.GetProperty("CacheVersion").GetInt32()!=1 ||
                root.GetProperty("Url").GetString()!=url) throw new InvalidDataException("Invalid preview cache identity");
            if (Str(root,"ThumbnailBase64").Length==0 &&
                ((Str(root,"ThumbnailError")=="no JPEG/PNG thumbnail was provided by yt-dlp" &&
                (!root.TryGetProperty("PreviewFetchVersion",out var fetchVersion) || !fetchVersion.TryGetInt32(out int version) || version<2)) ||
                ConverterNowAvailable(Str(root,"ThumbnailError"))))
                throw new InvalidDataException("Retry a legacy thumbnail result rejected only by its URL suffix.");
            var entry=JsonSerializer.Deserialize<PreviewData>(root.GetRawText(),AppPersistence.Json);
            if (entry!=null)
                entry=entry with { SavedAtUtc=entry.SavedAtUtc.Kind==DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(entry.SavedAtUtc,DateTimeKind.Utc)
                    : entry.SavedAtUtc.ToUniversalTime() };
            if (entry is { Title:not null, Uploader:not null, PlaylistTitle:not null,
                Extractor:not null, Codecs:not null, DynamicRange:not null, ThumbnailBase64:not null } &&
                entry.Duration>=0 && entry.Duration<TimeSpan.MaxValue.TotalSeconds &&
                DateTime.UtcNow-entry.SavedAtUtc<TimeSpan.FromHours(72) &&
                (entry.ThumbnailBase64.Length==0 || _thumbnailValidator(Convert.FromBase64String(entry.ThumbnailBase64)))) {
                _cache[url]=entry;_thumbnailErrors[url]=Str(root,"ThumbnailError");return entry;
            }
        }
        catch(Exception) { }
        try { File.Delete(path); } catch(IOException){} catch(UnauthorizedAccessException){}
        return null;
    }
    public void Request(string url,bool priority=false)
    {
        if (!AppPersistence.ValidUrl(url)) return;
        if (Get(url)!=null) return;
        lock(_gate)
        {
            if (_known.Contains(url))
            {
                if (priority && _pending.Remove(url)) _pending.Insert(0,url);
                return;
            }
            _known.Add(url);
            if (priority) _pending.Insert(0,url);
            else _pending.Add(url);
            _signal.Release();
        }
    }
    private async Task Worker()
    {
        while (!_stop.IsCancellationRequested)
        {
            try { await _signal.WaitAsync(_stop.Token).ConfigureAwait(false); }
            catch(OperationCanceledException) { break; }
            string? url;
            lock(_gate)
            {
                if (_pending.Count==0) continue;
                url=_pending[0];_pending.RemoveAt(0);
            }
            try
            {
                using var deadline=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                deadline.CancelAfter(_requestTimeout);
                var fetched=await FetchAsync(url,deadline.Token).ConfigureAwait(false);
                var data=fetched.Data;
                if (data!=null)
                {
                    if (_stop.IsCancellationRequested) break;
                    _cache[url]=data;
                    _thumbnailErrors[url]=fetched.ThumbnailError;
                    var contents=JsonSerializer.Serialize(new {
                        CacheVersion=1, PreviewFetchVersion=2, data.SavedAtUtc, data.Url, Success=true, fetched.ThumbnailError,
                        data.Title,data.PlaylistTitle,data.Uploader,data.Duration,
                        data.ThumbnailBase64,data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange
                    },AppPersistence.Json);
                    try { AppPersistence.AtomicWrite(CacheFile(url),contents); }
                    catch(Exception error) when(error is IOException or UnauthorizedAccessException)
                    { _log("Preview cache write failed: "+error.Message); }
                    _ready(data);
                }
            }
            catch(OperationCanceledException) when (_stop.IsCancellationRequested){break;}
            catch(OperationCanceledException)
            {
                _log("Preview timed out for "+url);
                _failed?.Invoke(url,"Время получения метаданных истекло.");
            }
            catch(Exception e)
            {
                _log("Preview unavailable for "+url+": "+e.Message);
                if(!_stop.IsCancellationRequested)_failed?.Invoke(url,e.Message);
            }
            finally { lock(_gate) { _known.Remove(url); } }
        }
    }
    private static string Str(JsonElement json,string name)
    {
        return json.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.String ? v.GetString()??"" : "";
    }
    private static double Number(JsonElement json,string name)
    {
        if (json.TryGetProperty(name,out var v) && v.ValueKind==JsonValueKind.Number && v.TryGetDouble(out var n)) return n;
        return 0;
    }
    private async Task<(PreviewData Data,string ThumbnailError)> FetchAsync(string url,CancellationToken token)
    {
        var psi=new ProcessStartInfo(_exe) {
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        foreach(string arg in new[]{"--ignore-config","--dump-single-json","--skip-download","--no-warnings","--playlist-items","1","--encoding","utf-8","--impersonate","chrome"})
            psi.ArgumentList.Add(arg);
        string cookie=_cookiePath();
        if (File.Exists(cookie)) { psi.ArgumentList.Add("--cookies");psi.ArgumentList.Add(cookie); }
        psi.ArgumentList.Add(url);
        using var child=ManagedChildProcess.Start(psi);
        var process=child.Process;
        using var kill=token.Register(() => { try { if (!process.HasExited) process.Kill(true); }catch{} });
        var stdout=child.StandardOutput.ReadToEndAsync(token);
        var stderr=child.StandardError.ReadToEndAsync(token);
        await Task.WhenAll(stdout,stderr,process.WaitForExitAsync(token)).ConfigureAwait(false);
        if (process.ExitCode!=0) throw new IOException(stderr.Result.Trim() is {Length:>0} e ? e : "yt-dlp metadata request failed.");
        using var doc=JsonDocument.Parse(stdout.Result);
        var j=doc.RootElement;
        string playlist="";
        if (j.TryGetProperty("entries",out var entries) && entries.ValueKind==JsonValueKind.Array &&
            entries.GetArrayLength()>0 && !j.TryGetProperty("formats",out _))
        { playlist=Str(j,"title");j=entries[0]; }
        int maxHeight=0;double maxFps=0;
        var codecs=new HashSet<string>(StringComparer.Ordinal);
        var ranges=new HashSet<string>(StringComparer.Ordinal);
        if (j.TryGetProperty("formats",out var fmts) && fmts.ValueKind==JsonValueKind.Array)
        foreach(var fmt in fmts.EnumerateArray())
        {
            string vc=Str(fmt,"vcodec");
            if(vc=="" || vc=="none") continue;
            maxHeight=Math.Max(maxHeight,(int)Number(fmt,"height"));
            maxFps=Math.Max(maxFps,Number(fmt,"fps"));
            codecs.Add(vc.StartsWith("av01")?"AV1":vc.StartsWith("vp9")?"VP9":vc.StartsWith("avc1")||vc.StartsWith("h264")?"H264":vc.Split('.')[0]);
            string dr=Str(fmt,"dynamic_range");
            if(dr!="" && dr!="SDR" && dr!="None") ranges.Add(dr);
        }
        var candidates=new List<(string Url,double Area)>();
        if(j.TryGetProperty("thumbnails",out var thumbs) && thumbs.ValueKind==JsonValueKind.Array)
            foreach(var thumbnail in thumbs.EnumerateArray())
                if(thumbnail.ValueKind==JsonValueKind.Object)
                    candidates.Add((Str(thumbnail,"url"),Number(thumbnail,"width")*Number(thumbnail,"height")));
        candidates.Add((Str(j,"thumbnail"),0));
        var seen=new HashSet<string>(StringComparer.Ordinal);
        var images=candidates.Where(candidate=>
                Uri.TryCreate(candidate.Url,UriKind.Absolute,out var address) && address.Scheme is "https" or "http" &&
                !string.IsNullOrWhiteSpace(address.Host) && seen.Add(candidate.Url))
            .OrderByDescending(candidate=>
                Path.GetExtension(new Uri(candidate.Url).AbsolutePath).ToLowerInvariant() is ".jpg" or ".jpeg" or ".png")
            .ThenByDescending(candidate=>candidate.Area).Take(16).ToArray();
        string base64="",thumbnailError="";
        if (images.Length>0)
        {
            using var imageDeadline=CancellationTokenSource.CreateLinkedTokenSource(token);
            imageDeadline.CancelAfter(_thumbnailTimeout);
            long received=0;
            foreach(var image in images)
            {
                try
                {
                    var imageToken=imageDeadline.Token;
                    imageToken.ThrowIfCancellationRequested();
                    using var request=new HttpRequestMessage(HttpMethod.Get,image.Url);
                    request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) VideoDownloader/1.0");
                    request.Headers.TryAddWithoutValidation("Accept","image/jpeg,image/png,image/gif,image/bmp,image/tiff,*/*;q=0.1");
                    if(Uri.TryCreate(url,UriKind.Absolute,out var referer) && referer.Scheme is "http" or "https")
                        request.Headers.Referrer=new UriBuilder(referer){UserName="",Password="",Path="/",Query="",Fragment=""}.Uri;
                    using var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,imageToken).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (response.Content.Headers.ContentLength>2_000_000-received) throw new IOException("Thumbnail exceeds 2MB");
                    using var stream=await response.Content.ReadAsStreamAsync(imageToken).ConfigureAwait(false);
                    using var buffer=new MemoryStream();
                    var chunk=new byte[16384];
                    int read;
                    while((read=await stream.ReadAsync(chunk.AsMemory(0,(int)Math.Min(chunk.Length,2_000_001-received)),imageToken).ConfigureAwait(false))>0)
                    {
                        received+=read;
                        if(received>2_000_000)throw new IOException("Thumbnail exceeds 2MB");
                        buffer.Write(chunk,0,read);
                    }
                    byte[] bytes=buffer.ToArray();
                    if(WebPImage(bytes) || AvifImage(bytes)) bytes=await ConvertWebPAsync(bytes,imageToken).ConfigureAwait(false);
                    if(!SupportedImage(bytes)) throw new IOException("Thumbnail response is not a supported JPEG, PNG, GIF, BMP or TIFF image.");
                    if(!_thumbnailValidator(bytes))throw new IOException("Thumbnail image is damaged or exceeds the preview dimensions limit.");
                    imageToken.ThrowIfCancellationRequested();
                    base64=Convert.ToBase64String(bytes);
                    thumbnailError="";
                    break;
                }
                catch(HttpRequestException e){thumbnailError=e.Message;_log("Thumbnail request failed: "+e.Message);}
                catch(IOException e){thumbnailError=e.Message;_log("Thumbnail unavailable: "+e.Message);}
                catch(OperationCanceledException) when (!_stop.IsCancellationRequested)
                {
                    // Once metadata is parsed, an image or total request timeout
                    // must not throw away the title, author or format summary.
                    _log("Thumbnail timed out for "+url);
                    thumbnailError="Thumbnail timed out";
                    break;
                }
                if(received>=2_000_000) break;
            }
        }
        return (new PreviewData(url,Str(j,"title"),Str(j,"uploader"),playlist,Number(j,"duration"),
            Str(j,"extractor"),maxHeight,maxFps,string.Join(", ",codecs),string.Join(", ",ranges),base64,DateTime.UtcNow),thumbnailError);
    }
    private static bool SupportedImage(ReadOnlySpan<byte> bytes) =>
        bytes.StartsWith(new byte[]{0xff,0xd8,0xff}) ||
        bytes.StartsWith(new byte[]{0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a}) ||
        bytes.StartsWith("GIF87a"u8) || bytes.StartsWith("GIF89a"u8) || bytes.StartsWith("BM"u8) ||
        bytes.StartsWith(new byte[]{0x49,0x49,0x2a,0x00}) || bytes.StartsWith(new byte[]{0x4d,0x4d,0x00,0x2a}) ||
        bytes.StartsWith(new byte[]{0x00,0x00,0x01,0x00});
    private static bool WebPImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length>=12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8,4).SequenceEqual("WEBP"u8);
    private static bool AvifImage(ReadOnlySpan<byte> bytes) =>
        bytes.Length>=12 && bytes.Slice(4,4).SequenceEqual("ftyp"u8) &&
        (bytes.Slice(8,4).SequenceEqual("avif"u8) || bytes.Slice(8,4).SequenceEqual("avis"u8));

    private string FfmpegExecutable => _ffmpegOverride??DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
    private bool ConverterNowAvailable(string error) =>
        (error=="WebP/AVIF thumbnail needs FFmpeg; text metadata is available." ||
         error=="WebP/AVIF thumbnail requires FFmpeg; text metadata is still available.") && File.Exists(FfmpegExecutable);

    private async Task<byte[]> ConvertWebPAsync(byte[] bytes,CancellationToken token)
    {
        string ffmpeg=FfmpegExecutable;
        if(!File.Exists(ffmpeg)) throw new IOException("WebP/AVIF thumbnail needs FFmpeg; text metadata is available.");
        token.ThrowIfCancellationRequested();
        string scratch=Path.Combine(Path.GetTempPath(),"vd-preview-image-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string input=Path.Combine(scratch,AvifImage(bytes)?"thumbnail.avif":"thumbnail.webp");
            await File.WriteAllBytesAsync(input,bytes,token).ConfigureAwait(false);
            var start=new ProcessStartInfo(ffmpeg){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            foreach(string argument in new[]{"-hide_banner","-v","error","-nostdin","-max_alloc","67108864","-max_pixels","16777216","-protocol_whitelist","file,pipe","-f",AvifImage(bytes)?"mov":"webp_pipe","-i",input,
                "-frames:v","1","-threads","1","-vf","scale=w='min(720,iw)':h='min(720,ih)':force_original_aspect_ratio=decrease","-f","image2pipe","-c:v","png","pipe:1"})
                start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);
            var process=child.Process;
            using var stop=CancellationTokenSource.CreateLinkedTokenSource(token);
            using var kill=stop.Token.Register(()=>{try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){} });
            async Task<byte[]> ReadOutput()
            {
                using var buffer=new MemoryStream();
                byte[] chunk=new byte[16384];int count;
                try
                {
                    while((count=await child.StandardOutput.BaseStream.ReadAsync(chunk,stop.Token).ConfigureAwait(false))>0)
                    {
                        if(buffer.Length+count>2_000_000)throw new IOException("Converted thumbnail exceeds 2MB");
                        buffer.Write(chunk,0,count);
                    }
                    return buffer.ToArray();
                }
                catch{stop.Cancel();throw;}
            }
            async Task DrainErrors()
            {
                byte[] chunk=new byte[4096];
                while(await child.StandardError.BaseStream.ReadAsync(chunk,stop.Token).ConfigureAwait(false)>0) { }
            }
            var output=ReadOutput();var errors=DrainErrors();
            try
            {
                await Task.WhenAll(output,errors,process.WaitForExitAsync(stop.Token)).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if(process.ExitCode!=0 || !SupportedImage(output.Result))throw new IOException("FFmpeg could not convert the WebP thumbnail; text metadata is available.");
                return output.Result;
            }
            finally
            {
                try{if(!process.HasExited)process.Kill(true);}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try{await Task.WhenAll(output,errors).ConfigureAwait(false);}catch { }
            }
        }
        catch(System.ComponentModel.Win32Exception error)
        { throw new IOException("FFmpeg could not start for thumbnail conversion; text metadata is available.",error); }
        finally { try{Directory.Delete(scratch,true);}catch(IOException){}catch(UnauthorizedAccessException){} }
    }
    public void Dispose()
    {
        _stop.Cancel();
        // The worker is still allowed to observe cancellation. Do not dispose
        // the semaphore/HTTP client underneath an in-flight prefetch.
        _signal.Release();
        _= _worker.ContinueWith(_=>{
            _http.Dispose();
            _signal.Dispose();
            _stop.Dispose();
        },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
    }
}
