using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;

namespace VideoDownloader.Native;

public sealed partial class PreviewService
{
    internal static readonly TimeSpan FrameSourceLifetime=TimeSpan.FromMinutes(2);
    private const int MaxFrameSourceCount=128;
    private const int MaxFrameSourceCharacters=256*1024;
    private readonly Dictionary<string,FrameSourceSet> _frameSources=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<FrameSourceWaiter>> _frameRequests=new(StringComparer.Ordinal);
    private sealed class FrameSourceSet(Dictionary<string,MediaFrameSource> sources,MediaFrameSource? automatic,string context)
    {
        internal readonly Dictionary<string,MediaFrameSource> Sources=sources;
        internal readonly HashSet<string> Unavailable=new(StringComparer.Ordinal);
        internal readonly MediaFrameSource? Automatic=automatic;
        internal readonly string CookieContext=context;
        internal readonly DateTime CreatedUtc=DateTime.UtcNow;
    }
    private sealed record FrameSourceWaiter(string? FormatId,string CookieContext,TaskCompletionSource<MediaFrameSource> Completion);

    // Called by the frame decoder off the UI thread. Its cancellation only
    // cancels this waiter, never a metadata job still needed by other previews.
    internal async Task<MediaFrameSource> GetFrameSourceAsync(string url,string? videoFormatId,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!AppPersistence.ValidUrl(url) || videoFormatId is not null &&
            !Regex.IsMatch(videoFormatId,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z",RegexOptions.CultureInvariant))
            throw new MediaFrameUnavailableException("Некорректный URL или формат для кадра.");
        string cookie=CookieContext(_cookiePath());
        var completion=new TaskCompletionSource<MediaFrameSource>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_gate)
        {
            if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
            if(_frameSources.TryGetValue(url,out var cached) && cached.CookieContext==cookie && DateTime.UtcNow-cached.CreatedUtc<FrameSourceLifetime)
            {
                if(videoFormatId is null)
                    return cached.Automatic??throw UnsupportedFrameSource();
                if(cached.Sources.TryGetValue(videoFormatId,out var found))return found;
                if(cached.Unavailable.Contains(videoFormatId))throw UnsupportedFrameSource();
            }
            if(!_frameRequests.TryGetValue(url,out var pending))_frameRequests[url]=pending=[];
            pending.RemoveAll(waiter=>waiter.Completion.Task.IsCompleted);
            if(pending.Count>=32)throw new MediaFrameUnavailableException("Слишком много запросов кадра. Повторите попытку.");
            pending.Add(new(videoFormatId,cookie,completion));
        }
        using var cancel=token.Register(()=>completion.TrySetCanceled(token));
        Request(url,true);
        return await completion.Task.ConfigureAwait(false);
    }
    private static MediaFrameUnavailableException UnsupportedFrameSource()=>new(
        "Кадр недоступен: нужен прямой HTTP(S) видеопоток MP4/WebM/MKV. Коллекции, аудио, DRM, live и манифесты не поддерживаются.");

    private bool FrameSourceNeeded(string url)
    {
        lock(_gate)return _frameRequests.TryGetValue(url,out var requests) && requests.Any(request=>!request.Completion.Task.IsCompleted);
    }
    private static string CookieContext(string path)
    {
        if(string.IsNullOrEmpty(path))return "";
        try
        {
            var info=new FileInfo(path);
            if(!info.Exists)return info.FullName+"|missing";
            using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite|FileShare.Delete);
            using var bytes=new MemoryStream();
            byte[] buffer=new byte[8192];
            while(true)
            {
                int count=input.Read(buffer);
                if(count==0)break;
                if(bytes.Length+count>4*1024*1024)return info.FullName+"|oversize";
                bytes.Write(buffer,0,count);
            }
            // Ignore jar comments/order and the equivalent session-expiry
            // spellings, which yt-dlp can rewrite without changing credentials.
            var rows=new List<string>();
            foreach(string raw in Encoding.UTF8.GetString(bytes.ToArray()).Split('\n'))
            {
                string line=raw.TrimEnd('\r');
                if(line.StartsWith("#HttpOnly_",StringComparison.Ordinal))line=line[10..];
                else if(line.StartsWith('#') || line.Length==0)continue;
                var fields=line.Split('\t');
                if(fields.Length==7)
                {
                    fields[1]=fields[1].ToUpperInvariant();fields[3]=fields[3].ToUpperInvariant();
                    if(fields[4].Length==0)fields[4]="0";
                    line=string.Join('\t',fields);
                }
                rows.Add(line);
            }
            rows.Sort(StringComparer.Ordinal);
            return info.FullName+"|"+Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n',rows))));
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException){return path+"|unavailable";}
    }
    private void CaptureFrameSources(string url,JsonElement item,bool collection,string cookieFile,string extractionCookieContext)
    {
        string cookieContext=extractionCookieContext;
        var preferred=new HashSet<string>(StringComparer.Ordinal);
        lock(_gate)
            if(_frameRequests.TryGetValue(url,out var requests))
                foreach(var request in requests.Where(request=>!request.Completion.Task.IsCompleted && request.FormatId is not null))preferred.Add(request.FormatId!);
        var retained=new Dictionary<string,MediaFrameSource>(StringComparer.Ordinal);
        var rejected=new HashSet<string>(StringComparer.Ordinal);
        var order=new LinkedList<string>();
        MediaFrameSource? automatic=null;
        long characters=0;
        long Size(MediaFrameSource source)=>source.Address.AbsoluteUri.Length+source.Cookies.Length+source.CookieFile.Length+
            source.CookieContext.Length+source.FormatId.Length+source.Headers.Sum(header=>(long)header.Key.Length+header.Value.Length)+256;
        void Keep(MediaFrameSource source)
        {
            if(retained.ContainsKey(source.FormatId))return;
            retained.Add(source.FormatId,source);order.AddLast(source.FormatId);characters+=Size(source);
        }
        if(!collection && item.ValueKind==JsonValueKind.Object &&
            !(item.TryGetProperty("is_live",out var live) && live.ValueKind==JsonValueKind.True) &&
            !(item.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)))
        {
            IEnumerable<JsonElement> formats=item.TryGetProperty("formats",out var array) && array.ValueKind==JsonValueKind.Array?array.EnumerateArray():new[]{item};
            foreach(var format in formats)
            {
                var source=ReadFrameSource(format,item,cookieFile,cookieContext);
                if(source is null)
                {
                    string id=format.ValueKind==JsonValueKind.Object?Str(format,"format_id"):"";
                    if(rejected.Count<128 && Regex.IsMatch(id,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z",RegexOptions.CultureInvariant))rejected.Add(id);
                    continue;
                }
                if(automatic is null || PreviewRank(source)<PreviewRank(automatic))automatic=source;
                Keep(source);
                while(retained.Count>MaxFrameSourceCount || characters>MaxFrameSourceCharacters)
                {
                    var remove=order.First;
                    while(remove is not null && preferred.Contains(remove.Value))remove=remove.Next;
                    remove??=order.First;
                    if(remove is null)break;
                    characters-=Size(retained[remove.Value]);retained.Remove(remove.Value);order.Remove(remove);
                }
            }
            if(automatic is not null)
            {
                string automaticId=automatic.FormatId;
                Keep(automatic);
                while(retained.Count>MaxFrameSourceCount || characters>MaxFrameSourceCharacters)
                {
                    var remove=order.First;
                    while(remove is not null && (preferred.Contains(remove.Value) || remove.Value==automaticId))remove=remove.Next;
                    if(remove is null)
                    {
                        automatic=null;
                        if(retained.Remove(automaticId,out var removed))characters-=Size(removed);
                        var autoNode=order.Find(automaticId);if(autoNode is not null)order.Remove(autoNode);
                        break;
                    }
                    characters-=Size(retained[remove.Value]);retained.Remove(remove.Value);order.Remove(remove);
                }
            }
        }
        var set=new FrameSourceSet(retained,automatic,cookieContext);
        set.Unavailable.UnionWith(rejected);
        lock(_gate)
        {
            if(Volatile.Read(ref _disposed)!=0)return;
            foreach(string expired in _frameSources.Where(entry=>DateTime.UtcNow-entry.Value.CreatedUtc>=FrameSourceLifetime).Select(entry=>entry.Key).ToArray())
                _frameSources.Remove(expired);
            _frameSources[url]=set;
            while(_frameSources.Count>4)
                _frameSources.Remove(_frameSources.Where(entry=>entry.Key!=url).MinBy(entry=>entry.Value.CreatedUtc).Key);
            if(_frameRequests.TryGetValue(url,out var requests))
            {
                foreach(var request in requests)
                {
                    if(request.Completion.Task.IsCompleted || request.CookieContext!=extractionCookieContext)continue;
                    var source=request.FormatId is null?automatic:retained.GetValueOrDefault(request.FormatId);
                    if(source is not null)request.Completion.TrySetResult(source);
                    else
                    {
                        if(request.FormatId is not null && set.Unavailable.Count<128)set.Unavailable.Add(request.FormatId);
                        request.Completion.TrySetException(UnsupportedFrameSource());
                    }
                }
                requests.RemoveAll(request=>request.Completion.Task.IsCompleted);
                if(requests.Count==0)_frameRequests.Remove(url);
            }
        }
    }
    private static long PreviewRank(MediaFrameSource source)
    {
        // A modest frame source avoids decoding a 4K stream merely to render a
        // small timeline image. An explicit video ID is resolved independently.
        int height=source.Height;
        return (height is >=240 and <=720?0L:1_000_000)+Math.Abs((height>0?height:10000)-360)*1000L+(long)source.Fps;
    }
    private static MediaFrameSource? ReadFrameSource(JsonElement format,JsonElement root,string cookieFile,string context)
    {
        if(format.ValueKind!=JsonValueKind.Object)return null;
        string id=Str(format,"format_id"),address=Str(format,"url"),extension=Str(format,"ext"),protocol=Str(format,"protocol");
        string codec=Str(format,"vcodec");
        if(!Regex.IsMatch(id,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z",RegexOptions.CultureInvariant) || codec=="none" ||
            address.Length>8192 || !Uri.TryCreate(address,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length>0 || protocol is not ("" or "http" or "https") ||
            extension.ToLowerInvariant() is not ("mp4" or "m4v" or "mov" or "webm" or "mkv") ||
            Path.GetExtension(uri.AbsolutePath).ToLowerInvariant() is ".m3u8" or ".mpd" ||
            format.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))return null;
        double width=Number(format,"width"),height=Number(format,"height"),fps=Number(format,"fps"),duration=Number(root,"duration");
        if(!double.IsFinite(width) || !double.IsFinite(height) || width<0 || height<0 || width>131072 || height>131072 ||
            width*height>16_777_216 || !double.IsFinite(fps) || fps<0 || fps>1000)return null;
        var headers=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        bool Merge(JsonElement from)
        {
            if(!from.TryGetProperty("http_headers",out var values) || values.ValueKind!=JsonValueKind.Object)return true;
            foreach(var pair in values.EnumerateObject())
            {
                if(pair.Name.Equals("Cookie",StringComparison.OrdinalIgnoreCase))continue;
                if(pair.Name.Length>128 || !Regex.IsMatch(pair.Name,@"\A[!#$%&'*+.^_`|~0-9A-Za-z-]+\z",RegexOptions.CultureInvariant) ||
                    pair.Value.ValueKind!=JsonValueKind.String)return false;
                string value=pair.Value.GetString()!;
                if(value.Length>8192 || value.Any(char.IsControl))return false;
                headers[pair.Name]=value;
                if(headers.Count>32 || headers.Sum(header=>header.Key.Length+header.Value.Length)>32768)return false;
            }
            return true;
        }
        if(!Merge(root) || !Merge(format))return null;
        string cookies=Str(format,"cookies");
        if(cookies.Length==0)cookies=Str(root,"cookies");
        if(cookies.Length>64*1024 || cookies.Any(char.IsControl))return null;
        return new(uri,id,headers,cookies,cookieFile,context,fps,double.IsFinite(duration) && duration>0?duration:null,(int)width,(int)height);
    }
    private void FailFrameSourceRequests(string url,Exception error,string? cookieContext)
    {
        lock(_gate)
            if(_frameRequests.TryGetValue(url,out var requests))
            {
                foreach(var request in requests.Where(request=>cookieContext is null || request.CookieContext==cookieContext))
                    request.Completion.TrySetException(error);
                requests.RemoveAll(request=>request.Completion.Task.IsCompleted);
                if(requests.Count==0)_frameRequests.Remove(url);
            }
    }
    private void FinishFrameSourceIteration(string url)
    {
        lock(_gate)
        {
            _known.Remove(url);_formatsRequested.Remove(url);
            if(_frameRequests.TryGetValue(url,out var requests))
            {
                requests.RemoveAll(request=>request.Completion.Task.IsCompleted);
                if(requests.Count==0)_frameRequests.Remove(url);
                else if(Volatile.Read(ref _disposed)==0)
                {
                    _known.Add(url);_pending.Insert(0,url);_signal.Release();
                }
            }
        }
    }
    private void CancelFrameSources()
    {
        lock(_gate)
        {
            foreach(var requests in _frameRequests.Values)
                foreach(var request in requests)request.Completion.TrySetCanceled();
            _frameRequests.Clear();_frameSources.Clear();
        }
    }
}
