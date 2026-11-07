using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

public sealed partial class PreviewService
{
    private sealed record ClipSourceSet(IReadOnlyList<MediaFrameSource> Sources,string Context,DateTime Created)
    {
        internal readonly HashSet<string> Unavailable=new(StringComparer.Ordinal);
        internal readonly HashSet<string> AudioSelections=new(StringComparer.Ordinal);
    }
    private sealed record ClipSourceWaiter(MediaClipRequest Request,string Context,TaskCompletionSource<MediaClipSources> Completion);
    private readonly Dictionary<string,ClipSourceSet> _clipSources=new(StringComparer.Ordinal);
    private readonly Dictionary<string,List<ClipSourceWaiter>> _clipRequests=new(StringComparer.Ordinal);

    internal async Task<MediaClipSources> GetClipSourcesAsync(MediaClipRequest request,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!AppPersistence.ValidUrl(request.Url) || new[]{request.VideoFormatId,request.AudioFormatId}.Any(id=>id is not null &&
            !Regex.IsMatch(id,@"\A[a-zA-Z0-9][a-zA-Z0-9._-]{0,127}\z",RegexOptions.CultureInvariant)))
            throw new MediaFrameUnavailableException("Некорректный источник предпросмотра.");
        string context=CookieContext(_cookiePath());
        var done=new TaskCompletionSource<MediaClipSources>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock(_gate)
        {
            if(Volatile.Read(ref _disposed)!=0)throw new OperationCanceledException(_stop.Token);
            if(_resetting.Contains(request.Url))throw new OperationCanceledException("Метаданные материала сбрасываются.");
            if(_clipSources.TryGetValue(request.Url,out var cached) && cached.Context==context && DateTime.UtcNow-cached.Created<FrameSourceLifetime &&
                (request.AudioTrack=="Default" || cached.AudioSelections.Contains(AudioSelectionKey(request))) &&
                new[]{request.VideoFormatId,request.AudioFormatId}.All(id=>id is null || cached.Sources.Any(source=>source.FormatId==id) || cached.Unavailable.Contains(id)))
                return SelectClipSources(cached,request);
            if(!_clipRequests.TryGetValue(request.Url,out var waiters))_clipRequests[request.Url]=waiters=[];
            waiters.RemoveAll(waiter=>waiter.Completion.Task.IsCompleted);
            if(waiters.Count>=16)throw new MediaFrameUnavailableException("Слишком много запросов воспроизведения.");
            waiters.Add(new(request,context,done));
        }
        using var cancellation=token.Register(()=>done.TrySetCanceled(token));
        Request(request.Url,true);
        return await done.Task.ConfigureAwait(false);
    }
    private bool ClipSourceNeeded(string url)=>_clipRequests.TryGetValue(url,out var waiters) && waiters.Any(waiter=>!waiter.Completion.Task.IsCompleted);
    private void CaptureClipSources(string url,JsonElement item,bool collection,string cookieFile,string context)
    {
        var preferred=new HashSet<string>(StringComparer.Ordinal);
        ClipSourceWaiter[] capturedWaiters;
        lock(_gate)capturedWaiters=_clipRequests.TryGetValue(url,out var captured)?captured.Where(value=>!value.Completion.Task.IsCompleted && value.Context==context).ToArray():[];
        var pending=capturedWaiters.Select(value=>value.Request).ToArray();
        var preferredAudio=new Dictionary<string,MediaFrameSource>(StringComparer.Ordinal);
        foreach(var request in pending)
            foreach(string? id in new[]{request.VideoFormatId,request.AudioFormatId})if(id is not null)preferred.Add(id);
        var values=new Dictionary<string,MediaFrameSource>(StringComparer.Ordinal);
        var order=new LinkedList<string>();long characters=0;
        MediaFrameSource? muxed=null,audio=null;
        long Size(MediaFrameSource value)=>value.Address.AbsoluteUri.Length+value.Cookies.Length+value.CookieFile.Length+value.CookieContext.Length+
            value.FormatId.Length+value.Language.Length+(value.ManifestAddress?.AbsoluteUri.Length??0)+
            (value.Fragments?.Sum(fragment=>(long)fragment.Address.AbsoluteUri.Length+64)??0)+value.Headers.Sum(pair=>(long)pair.Key.Length+pair.Value.Length)+256;
        void Keep(MediaFrameSource value)
        {if(values.TryAdd(value.FormatId,value)){order.AddLast(value.FormatId);characters+=Size(value);}}
        void Trim()
        {
            while(values.Count>128 || characters>256*1024)
            {
                var remove=order.First;
                while(remove is not null && preferred.Contains(remove.Value))remove=remove.Next;
                remove??=order.First;
                if(remove is null)break;
                characters-=Size(values[remove.Value]);values.Remove(remove.Value);order.Remove(remove);
            }
        }
        if(!collection && item.ValueKind==JsonValueKind.Object &&
            !(item.TryGetProperty("is_live",out var live) && live.ValueKind==JsonValueKind.True) &&
            !(item.TryGetProperty("has_drm",out var drm) && drm.ValueKind is not (JsonValueKind.Null or JsonValueKind.False)))
        {
            IEnumerable<JsonElement> formats=item.TryGetProperty("formats",out var array) && array.ValueKind==JsonValueKind.Array?array.EnumerateArray():new[]{item};
            foreach(var format in formats)
            {
                var source=ReadFrameSource(format,item,cookieFile,context,allowAudio:true);
                if(source is null)continue;
                if(source.HasVideo && source.HasAudio && (muxed is null || PreviewRank(source)<PreviewRank(muxed)))muxed=source;
                if(source.HasAudio && !source.HasVideo)audio=source;
                foreach(var request in pending.Where(request=>request.AudioTrack!="Default" && source.HasAudio && ClipLanguageMatches(source,request)))
                {
                    string key=AudioSelectionKey(request);
                    if(!preferredAudio.TryGetValue(key,out var prior) || !source.HasVideo || prior.HasVideo && PreviewRank(source)<PreviewRank(prior))preferredAudio[key]=source;
                }
                Keep(source);Trim();
            }
            foreach(var source in new[]{muxed,audio}.Concat(preferredAudio.Values))
                if(source is not null){preferred.Add(source.FormatId);Keep(source);Trim();}
        }
        var set=new ClipSourceSet(order.Select(id=>values[id]).ToArray(),context,DateTime.UtcNow);
        set.AudioSelections.UnionWith(pending.Select(AudioSelectionKey));
        lock(_gate)
        {
            if(Volatile.Read(ref _disposed)!=0)return;
            foreach(string expired in _clipSources.Where(pair=>DateTime.UtcNow-pair.Value.Created>=FrameSourceLifetime).Select(pair=>pair.Key).ToArray())_clipSources.Remove(expired);
            _clipSources[url]=set;
            while(_clipSources.Count>4)_clipSources.Remove(_clipSources.Where(pair=>pair.Key!=url).MinBy(pair=>pair.Value.Created).Key);
            if(_clipRequests.TryGetValue(url,out var waiters))
            {
                foreach(var waiter in capturedWaiters.Where(waiter=>!waiter.Completion.Task.IsCompleted))
                {
                    try
                    {
                        foreach(string? id in new[]{waiter.Request.VideoFormatId,waiter.Request.AudioFormatId})
                            if(id is not null && !set.Sources.Any(source=>source.FormatId==id))set.Unavailable.Add(id);
                        waiter.Completion.TrySetResult(SelectClipSources(set,waiter.Request));
                    }
                    catch(MediaFrameUnavailableException error){waiter.Completion.TrySetException(error);}
                }
                waiters.RemoveAll(waiter=>waiter.Completion.Task.IsCompleted);if(waiters.Count==0)_clipRequests.Remove(url);
            }
        }
    }
    private static MediaClipSources SelectClipSources(ClipSourceSet set,MediaClipRequest request)
    {
        var all=set.Sources;
        MediaFrameSource? video=null,audio=null;
        bool LanguageMatches(MediaFrameSource value)=>ClipLanguageMatches(value,request);
        if(request.VideoFormatId is {} id)
        {
            var exact=all.FirstOrDefault(value=>value.FormatId==id)??throw new MediaFrameUnavailableException("Выбранный формат недоступен для воспроизведения.");
            if(exact.HasVideo && !request.AudioOnly)video=exact;
            else if(exact.HasAudio)audio=exact;
            else throw new MediaFrameUnavailableException("В выбранном формате нет пригодного медиа.");
        }
        else if(!request.AudioOnly)
        {
            video=all.Where(value=>value.HasVideo && value.HasAudio && LanguageMatches(value)).MinBy(PreviewRank)??
                all.Where(value=>value.HasVideo).MinBy(PreviewRank);
        }
        if(request.AudioFormatId is {} audioId)
            audio=all.FirstOrDefault(value=>value.FormatId==audioId && value.HasAudio && !value.HasVideo)??
                throw new MediaFrameUnavailableException("Выбранная аудиодорожка недоступна для воспроизведения.");
        else if(audio is null && (video is null || !video.HasAudio || !LanguageMatches(video)))
        {
            audio=all.LastOrDefault(value=>value.HasAudio && !value.HasVideo && LanguageMatches(value));
            audio??=video is null?all.LastOrDefault(value=>value.HasAudio && LanguageMatches(value)):null;
            if(audio is null && request.AudioTrack!="Default")throw new MediaFrameUnavailableException("Источник не предоставил выбранную аудиодорожку для предпросмотра.");
        }
        if(video is null && audio is null)throw new MediaFrameUnavailableException("Источник не предоставил медиа для воспроизведения.");
        return new(video,audio);
    }
    private static string AudioSelectionKey(MediaClipRequest request)=>request.AudioTrack+"|"+request.AudioLanguages.Trim().ToLowerInvariant();
    private static bool ClipLanguageMatches(MediaFrameSource value,MediaClipRequest request)
    {
        if(request.AudioTrack=="Original")return value.IsOriginalAudio;
        if(request.AudioTrack!="Languages")return true;
        string first=request.AudioLanguages.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()??"";
        return first.Length>0 && (value.Language.Equals(first,StringComparison.OrdinalIgnoreCase) || value.Language.StartsWith(first+"-",StringComparison.OrdinalIgnoreCase));
    }
    private void FailClipSources(string url,Exception error,string? context)
    {
        lock(_gate)
            if(_clipRequests.TryGetValue(url,out var waiters))
            {
                foreach(var waiter in waiters.Where(waiter=>context is null || waiter.Context==context))waiter.Completion.TrySetException(error);
                waiters.RemoveAll(waiter=>waiter.Completion.Task.IsCompleted);if(waiters.Count==0)_clipRequests.Remove(url);
            }
    }
    private bool PruneClipWaiters(string url)
    {
        if(!_clipRequests.TryGetValue(url,out var waiters))return false;
        waiters.RemoveAll(waiter=>waiter.Completion.Task.IsCompleted);
        if(waiters.Count==0){_clipRequests.Remove(url);return false;}
        return true;
    }
    private void CancelClipSources()
    {
        foreach(var waiters in _clipRequests.Values)foreach(var waiter in waiters)waiter.Completion.TrySetCanceled();
        _clipRequests.Clear();_clipSources.Clear();
    }
}
