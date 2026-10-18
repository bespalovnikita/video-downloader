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
    private readonly List<string> _pending=new();
    private readonly HashSet<string> _known=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,PreviewData> _cache=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _signal=new(0);
    private readonly CancellationTokenSource _stop=new();
    private readonly object _gate=new();
    private readonly HttpClient _http=new(){Timeout=TimeSpan.FromSeconds(15)};
    private readonly Task _worker;
    public PreviewService(string exe,AppPersistence data,Func<string> cookiePath,Action<PreviewData> ready,Action<string> log)
    {
        _exe=exe;_cacheDir=data.PreviewDir;_cookiePath=cookiePath;_ready=ready;_log=log;
        Directory.CreateDirectory(_cacheDir);
        _worker=Task.Run(Worker);
    }
    private string CacheFile(string url) => Path.Combine(_cacheDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
    public PreviewData? Get(string url)
    {
        if (_cache.TryGetValue(url,out var value) && DateTime.UtcNow-value.SavedAtUtc<TimeSpan.FromHours(72)) return value;
        string path=CacheFile(url);
        if (!File.Exists(path)) return null;
        try
        {
            using var doc=JsonDocument.Parse(File.ReadAllText(path,Encoding.UTF8));
            var root=doc.RootElement;
            if (root.GetProperty("CacheVersion").GetInt32()!=1 ||
                root.GetProperty("Url").GetString()!=url) return null;
            var entry=JsonSerializer.Deserialize<PreviewData>(root.GetRawText(),AppPersistence.Json);
            if (entry!=null && DateTime.UtcNow-entry.SavedAtUtc<TimeSpan.FromHours(72)) {
                _cache[url]=entry;return entry;
            }
        }
        catch(Exception) { }
        try { File.Delete(path); } catch(IOException){}
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
                var data=await FetchAsync(url,_stop.Token).ConfigureAwait(false);
                if (data!=null)
                {
                    _cache[url]=data;
                    var contents=JsonSerializer.Serialize(new {
                        CacheVersion=1, data.SavedAtUtc, data.Url, Success=true,
                        data.Title,data.PlaylistTitle,data.Uploader,data.Duration,
                        data.ThumbnailBase64,data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange
                    },AppPersistence.Json);
                    AppPersistence.AtomicWrite(CacheFile(url),contents);
                    _ready(data);
                }
            }
            catch(OperationCanceledException) when (_stop.IsCancellationRequested){break;}
            catch(Exception e){_log("Preview unavailable for "+url+": "+e.Message);}
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
    private async Task<PreviewData?> FetchAsync(string url,CancellationToken token)
    {
        var psi=new ProcessStartInfo(_exe) {
            UseShellExecute=false,CreateNoWindow=true,
            RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
        };
        foreach(string arg in new[]{"--dump-single-json","--skip-download","--no-warnings","--playlist-items","1","--encoding","utf-8","--impersonate","chrome"})
            psi.ArgumentList.Add(arg);
        string cookie=_cookiePath();
        if (File.Exists(cookie)) { psi.ArgumentList.Add("--cookies");psi.ArgumentList.Add(cookie); }
        psi.ArgumentList.Add(url);
        using var process=new Process{StartInfo=psi};
        process.Start();
        using var kill=token.Register(() => { try { if (!process.HasExited) process.Kill(true); }catch{} });
        var stdout=process.StandardOutput.ReadToEndAsync(token);
        var stderr=process.StandardError.ReadToEndAsync(token);
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
        string thumb=Str(j,"thumbnail");
        if(thumb=="" && j.TryGetProperty("thumbnails",out var thumbs) && thumbs.ValueKind==JsonValueKind.Array)
        {
            foreach(var t in thumbs.EnumerateArray())
            {
                var candidate=Str(t,"url");
                if(candidate.StartsWith("https://",StringComparison.OrdinalIgnoreCase))thumb=candidate;
            }
        }
        string base64="";
        if (Uri.TryCreate(thumb,UriKind.Absolute,out var uri) && (uri.Scheme=="https"||uri.Scheme=="http"))
        {
            try
            {
                using var response=await _http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is > 2_000_000) throw new IOException("Thumbnail exceeds 2MB");
                using var stream=await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                using var buffer=new MemoryStream();
                var chunk=new byte[16384];
                int read;
                while((read=await stream.ReadAsync(chunk,token).ConfigureAwait(false))>0)
                {
                    if(buffer.Length+read>2_000_000)throw new IOException("Thumbnail exceeds 2MB");
                    buffer.Write(chunk,0,read);
                }
                base64=Convert.ToBase64String(buffer.ToArray());
            }
            catch(HttpRequestException e){_log("Thumbnail request failed: "+e.Message);}
            catch(IOException e){_log("Thumbnail unavailable: "+e.Message);}
        }
        return new PreviewData(url,Str(j,"title"),Str(j,"uploader"),playlist,Number(j,"duration"),
            Str(j,"extractor"),maxHeight,maxFps,string.Join(", ",codecs),string.Join(", ",ranges),base64,DateTime.UtcNow);
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
