namespace VideoDownloader.Native;

public sealed partial class PreviewService
{
    private sealed class MetadataIteration(string url,long generation,CancellationToken stop):IDisposable
    {
        internal readonly string Url=url;
        internal readonly long Generation=generation;
        internal readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(stop);
        internal readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()=>Stop.Dispose();
    }
    private readonly Dictionary<string,long> _generations=new(StringComparer.Ordinal);
    private readonly HashSet<string> _resetting=new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _resetGate=new(1,1);
    private MetadataIteration? _activeMetadata;
    private LocalPosterWork? _activeLocalPoster;

    // The version travels with a notification, rather than being read when a
    // queued Dispatcher callback eventually runs.
    internal event Action<PreviewData,bool,long>? ResultAvailable;
    internal event Action<string,string,long>? FailureAvailable;
    internal long Generation(string url)
    {lock(_gate)return _generations.GetValueOrDefault(url);}
    internal bool IsCurrent(string url,long generation)
    {lock(_gate)return Volatile.Read(ref _disposed)==0 && !_resetting.Contains(url) && _generations.GetValueOrDefault(url)==generation;}
    private bool IsCurrent(MetadataIteration iteration)=>IsCurrent(iteration.Url,iteration.Generation);
    private void Publish(MetadataIteration iteration,PreviewData data,bool final)
    {
        if(!IsCurrent(iteration))return;
        if(final)_ready(data);else _updated?.Invoke(data);
        ResultAvailable?.Invoke(data,final,iteration.Generation);
    }
    private void PublishFailure(MetadataIteration iteration,string error)
    {
        if(!IsCurrent(iteration))return;
        _failed?.Invoke(iteration.Url,error);
        FailureAvailable?.Invoke(iteration.Url,error,iteration.Generation);
    }

    // Explicit reset has no implicit Request: the caller decides when its row
    // and download attempt are ready for a fresh metadata request.
    internal async Task ResetAsync(IEnumerable<string> urls)
    {
        var selected=urls.Distinct(StringComparer.Ordinal).ToArray();
        if(selected.Any(url=>!AppPersistence.ValidUrl(url)))throw new ArgumentException("Некорректный URL для сброса метаданных.",nameof(urls));
        if(selected.Length==0)return;
        await _resetGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MetadataIteration? active;
            LocalPosterWork? local;
            lock(_gate)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed)!=0,this);
                foreach(string url in selected)
                {
                    _generations[url]=checked(_generations.GetValueOrDefault(url)+1);
                    _resetting.Add(url);_pending.Remove(url);_known.Remove(url);_formatsRequested.Remove(url);
                    ClearPrivateSources(url);
                    foreach(var queued in _localPosters.Where(work=>work.Input.Url==url).ToArray())
                    {
                        _localPosters.Remove(queued);queued.ResetStop.Cancel();
                        queued.Completion.TrySetCanceled(queued.ResetStop.Token);
                    }
                }
                active=_activeMetadata is {} metadata && selected.Contains(metadata.Url,StringComparer.Ordinal)?metadata:null;
                local=_activeLocalPoster is {} poster && selected.Contains(poster.Input.Url,StringComparer.Ordinal)?poster:null;
            }
            try{active?.Stop.Cancel();}catch(ObjectDisposedException){ }
            try{local?.ResetStop.Cancel();}catch(ObjectDisposedException){ }
            if(active is not null)await active.Completion.Task.ConfigureAwait(false);
            if(local is not null)
                try{await local.Completion.Task.ConfigureAwait(false);}catch(OperationCanceledException){ }
            await Task.Run(()=>
            {
                // Preflight every cache path before the first deletion. The
                // SHA entry is ours; a link, directory or protected file is not
                // permission to erase its target or bypass filesystem policy.
                var paths=selected.Select(CacheFile).ToArray();
                foreach(string path in paths)
                {
                    OwnedFiles.NoReparseAncestors(path);OwnedFiles.RegularOrAbsent(path);
                    try
                    {
                        if((File.GetAttributes(path)&FileAttributes.ReadOnly)!=0)
                            throw new UnauthorizedAccessException("Файл кеша метаданных доступен только для чтения.");
                    }
                    catch(FileNotFoundException){ }
                    catch(DirectoryNotFoundException){ }
                }
                foreach(string path in paths)File.Delete(path);
                lock(_gate)
                {
                    foreach(string url in selected)
                    {
                        lock(_memoryGate)_cache.TryRemove(url,out _);
                        _lastUse.TryRemove(url,out _);_fetchVersions.TryRemove(url,out _);_thumbnailErrors.TryRemove(url,out _);
                        ClearPrivateSources(url);
                    }
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            lock(_gate)foreach(string url in selected)_resetting.Remove(url);
            _resetGate.Release();
        }
    }

    private void ClearPrivateSources(string url)
    {
        _frameSources.Remove(url);_clipSources.Remove(url);
        if(_frameRequests.Remove(url,out var frames))foreach(var waiter in frames)waiter.Completion.TrySetCanceled();
        if(_clipRequests.Remove(url,out var clips))foreach(var waiter in clips)waiter.Completion.TrySetCanceled();
    }
}
