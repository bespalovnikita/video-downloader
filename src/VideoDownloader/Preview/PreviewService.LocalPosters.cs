using System.Text.Json;
using System.Text.Json.Nodes;

namespace VideoDownloader.Native;

public sealed partial class PreviewService
{
    internal const int MaxPendingLocalPosters=64;
    private readonly List<LocalPosterWork> _localPosters=new();
    private bool _preferLocalPoster;
    private sealed class LocalPosterWork(LocalPosterInput input,Func<Action,CancellationToken,Task<bool>> publish,CancellationToken token)
    {
        internal LocalPosterInput Input{get;}=input;
        internal Func<Action,CancellationToken,Task<bool>> Publish{get;}=publish;
        internal CancellationToken Token{get;}=token;
        internal TaskCompletionSource<bool> Completion{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal CancellationTokenRegistration Registration;
        internal bool Started;
    }
    private static bool MetadataFresh(PreviewData data)=>DateTime.UtcNow-data.SavedAtUtc<TimeSpan.FromHours(72);
    private static bool LocalImageFresh(PreviewData data)=>data.ThumbnailBase64.Length>0 &&
        data.LocalThumbnailFingerprint is {Length:64} && data.LocalThumbnailSavedAtUtc is {} saved &&
        saved<=DateTime.UtcNow.AddMinutes(5) && DateTime.UtcNow-saved<TimeSpan.FromHours(72);
    private static bool CanKeepPoster(PreviewData data,string fingerprint)=>data.ThumbnailBase64.Length>0 &&
        (string.IsNullOrEmpty(data.LocalThumbnailFingerprint) ||
            data.LocalThumbnailFingerprint==fingerprint && LocalImageFresh(data));

    // This is local-only work on the existing sole preview worker. It never
    // calls yt-dlp, starts a download or changes queue/history ownership.
    internal Task<bool> RequestLocalPosterAsync(LocalPosterInput input,
        Func<Action,CancellationToken,Task<bool>> publish,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var work=new LocalPosterWork(input,publish,token);
        lock(_gate)
        {
            if(Volatile.Read(ref _disposed)!=0)return Task.FromResult(false);
            if(_localPosters.Count>=MaxPendingLocalPosters)return Task.FromResult(false);
            _localPosters.Add(work);
            work.Registration=token.Register(()=>
            {
                lock(_gate)
                    if(!work.Started && _localPosters.Remove(work))work.Completion.TrySetCanceled(token);
            });
            _signal.Release();
        }
        return AwaitLocalPoster(work);
    }
    private static async Task<bool> AwaitLocalPoster(LocalPosterWork work)
    {
        try{return await work.Completion.Task.ConfigureAwait(false);}
        finally{work.Registration.Dispose();}
    }
    private bool UsableImage(PreviewData data)
    {
        try{return data.ThumbnailBase64.Length>0 && ValidGallery(data);}
        catch(Exception error) when(error is FormatException or ArgumentException){return false;}
    }
    private PreviewData RepairCachedGallery(PreviewData data)
    {
        var images=new List<string>();int bytes=0;
        foreach(string? image in new[]{data.ThumbnailBase64}.Concat(data.ThumbnailsBase64??[]).Take(MaxImages+1).Distinct(StringComparer.Ordinal))
        {
            if(string.IsNullOrEmpty(image) || image.Length>2_666_668)continue;
            try
            {
                byte[] decoded=Convert.FromBase64String(image);
                if(decoded.Length>MaxImageBytes || bytes+decoded.Length>MaxGalleryBytes || !_thumbnailValidator(decoded))continue;
                bytes+=decoded.Length;images.Add(image);
                if(images.Count==MaxImages)break;
            }
            catch(FormatException){ }
        }
        string first=images.FirstOrDefault()??"";
        bool same=first.Length>0 && first==data.ThumbnailBase64;
        return data with{ThumbnailBase64=first,ThumbnailsBase64=images.ToArray(),
            LocalThumbnailFingerprint=same?data.LocalThumbnailFingerprint:"",LocalThumbnailSavedAtUtc=same?data.LocalThumbnailSavedAtUtc:null};
    }
    private PreviewData LocalPosterBase(LocalPosterInput input)
    {
        if(_cache.TryGetValue(input.Url,out var memory))return memory;
        // Recover metadata from a damaged/missing image without turning an
        // optional local frame into a new metadata fetch or erasing its TTL.
        try
        {
            var root=ReadLocalEnvelope(input.Url);
            if(root is not null && root.Deserialize<PreviewData>(AppPersistence.Json) is {} entry &&
                entry.Title is not null && entry.Uploader is not null && entry.PlaylistTitle is not null &&
                entry.Extractor is not null && entry.Codecs is not null && entry.DynamicRange is not null &&
                entry.ThumbnailBase64 is not null && entry.Duration>=0 && entry.Duration<TimeSpan.MaxValue.TotalSeconds &&
                MediaFormatCatalog.IsValid(entry.Formats) && (!entry.IsCollection || entry.Formats.Length==0))
            {
                if(!UsableImage(entry))entry=RepairCachedGallery(entry);
                _fetchVersions[input.Url]=root["PreviewFetchVersion"]?.GetValue<int>()??0;
                _thumbnailErrors[input.Url]=root["ThumbnailError"]?.GetValue<string>()??"";
                CacheInMemory(entry);
                return entry;
            }
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or FormatException or ArgumentException)
        {Log("Local preview cache unavailable: "+ErrorDetails(error));}
        return new(input.Url,input.Title,"","",0,"",0,0,"","","",DateTime.MinValue);
    }
    private JsonObject? ReadLocalEnvelope(string url)
    {
        string path=CacheFile(url);
        OwnedFiles.RegularOrAbsent(path);
        if(!File.Exists(path))return null;
        using var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,8192,FileOptions.SequentialScan);
        if(stream.Length>MaxCacheBytes)throw new InvalidDataException("Preview cache exceeds the gallery limit.");
        byte[] bytes=new byte[checked((int)stream.Length)];stream.ReadExactly(bytes);
        if(stream.ReadByte()!=-1)throw new InvalidDataException("Preview cache grew beyond its bounded snapshot.");
        var root=JsonNode.Parse(bytes) as JsonObject;
        return root?["CacheVersion"]?.GetValue<int>()==1 && root["Url"]?.GetValue<string>()==url?root:null;
    }
    private async Task ProcessLocalPoster(LocalPosterWork work)
    {
        // Completion is deliberately outside the core's using/finally scopes:
        // an awaiting Remove must observe all native readers and file leases
        // released, rather than race a TrySetResult before Dispose.
        try{work.Completion.TrySetResult(await ProcessLocalPosterCore(work).ConfigureAwait(false));}
        catch(OperationCanceledException error){work.Completion.TrySetCanceled(error.CancellationToken);}
        catch(Exception error)
        {
            Log("Local video preview unavailable for "+DiagnosticField(work.Input.Url)+": "+ErrorDetails(error));
            work.Completion.TrySetResult(false);
        }
    }
    private async Task<bool> ProcessLocalPosterCore(LocalPosterWork work)
    {
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token,work.Token);
        CancellationToken token=stop.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            var prior=LocalPosterBase(work.Input);
            string fingerprint=LocalPosterDecoder.Fingerprint(work.Input);
            // LocalPosterBase validates disk images once; memory was already
            // decoded by its producer. A matching local file still needs a
            // fresh image timestamp, independently of metadata freshness.
            if(CanKeepPoster(prior,fingerprint))
                return false;
            string ffmpeg=FfmpegExecutable;
            string ffprobe=_ffmpegOverride is null?DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"):
                Path.Combine(Path.GetDirectoryName(ffmpeg)!,OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe");
            if(!File.Exists(ffmpeg) || !File.Exists(ffprobe))
            {
                Log("Local video preview unavailable for "+DiagnosticField(work.Input.Url)+": FFmpeg and ffprobe are required.");return false;
            }
            using var frame=await new LocalPosterDecoder(ffmpeg,ffprobe).DecodeAsync(work.Input,token).ConfigureAwait(false);
            if(frame is null || !_thumbnailValidator(frame.Bytes))
            {
                Log("Local video preview unavailable for "+DiagnosticField(work.Input.Url)+": no decodable video frame in the supported published file.");return false;
            }
            token.ThrowIfCancellationRequested();
            if(LocalPosterDecoder.Fingerprint(work.Input)!=frame.Fingerprint)return false;
            string image=Convert.ToBase64String(frame.Bytes);
            PreviewData? merged=null;
            bool committed=false;
            bool accepted=await work.Publish(()=>
            {
                if(token.IsCancellationRequested || Volatile.Read(ref _disposed)!=0)return;
                // Recheck at the actual dispatcher admission point. A remote
                // image published while local work was queued always wins.
                var current=_cache.TryGetValue(work.Input.Url,out var latest)?latest:prior;
                // Memory entries were already decoded on the worker. Avoid
                // decoding a complete gallery again on the UI dispatcher.
                if(CanKeepPoster(current,frame.Fingerprint))return;
                merged=current with {ThumbnailBase64=image,ThumbnailsBase64=[image],
                    LocalThumbnailFingerprint=frame.Fingerprint,LocalThumbnailSavedAtUtc=DateTime.UtcNow};
                CacheInMemory(merged);committed=true;
            },token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if(!accepted || !committed || merged is null)return false;
            try
            {
                // Preserve every existing envelope field, including metadata
                // success/failure, fetch version, timestamps and future fields.
                var root=ReadLocalEnvelope(work.Input.Url)??JsonSerializer.SerializeToNode(merged,AppPersistence.Json)!.AsObject();
                if(!root.ContainsKey("CacheVersion"))
                {
                    root["CacheVersion"]=1;root["PreviewFetchVersion"]=_fetchVersions.GetValueOrDefault(work.Input.Url);
                    root["Success"]=false;root["ThumbnailError"]=_thumbnailErrors.GetValueOrDefault(work.Input.Url)??"";
                }
                root["ThumbnailBase64"]=merged.ThumbnailBase64;
                root["ThumbnailsBase64"]=JsonSerializer.SerializeToNode(merged.ThumbnailsBase64);
                root["LocalThumbnailFingerprint"]=merged.LocalThumbnailFingerprint;
                root["LocalThumbnailSavedAtUtc"]=merged.LocalThumbnailSavedAtUtc;
                token.ThrowIfCancellationRequested();
                AppPersistence.AtomicWrite(CacheFile(work.Input.Url),root.ToJsonString(AppPersistence.Json));
                PruneDiskCache(CacheFile(work.Input.Url));
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
            {Log("Local preview cache write failed: "+ErrorDetails(error));}
            return true;
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
        catch(Exception error)
        {
            Log("Local video preview unavailable for "+DiagnosticField(work.Input.Url)+": "+ErrorDetails(error));
            return false;
        }
    }
    private void CancelLocalPosters()
    {
        foreach(var work in _localPosters)work.Completion.TrySetCanceled(_stop.Token);
        _localPosters.Clear();
    }
}
