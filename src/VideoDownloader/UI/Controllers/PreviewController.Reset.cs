namespace VideoDownloader.Native;

internal sealed partial class PreviewController
{
    private sealed class PreviewOperation(string url,CancellationToken token):IDisposable
    {
        internal readonly string Url=url;
        internal readonly CancellationTokenSource Stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        internal readonly TaskCompletionSource Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Dispose()=>Stop.Dispose();
    }
    private readonly object _operationGate=new();
    private readonly HashSet<string> _resetUrls=new(StringComparer.Ordinal);
    private readonly HashSet<PreviewOperation> _previewOperations=[];
    private bool IsResetting(string url){lock(_operationGate)return _resetUrls.Contains(url);}
    private void DispatchCurrent(string url,long generation,Action receive)=>Dispatch(()=>
    {
        if(!IsResetting(url) && _service.IsCurrent(url,generation))receive();
    });
    private void ServiceResultAvailable(PreviewData data,bool final,long generation)=>
        DispatchCurrent(data.Url,generation,()=>ReceiveReady(data,final));
    private void ServiceFailureAvailable(string url,string error,long generation)=>
        DispatchCurrent(url,generation,()=>ReceiveFailure(url,error));
    private async Task<T> RunPreviewOperationAsync<T>(string url,CancellationToken token,
        Func<CancellationToken,Task<T>> run,Action<T>? discard=null)
    {
        PreviewOperation operation;
        long generation=_service.Generation(url);
        lock(_operationGate)
        {
            if(_disposed || _resetUrls.Contains(url))throw new OperationCanceledException("Предпросмотр материала сбрасывается.");
            operation=new PreviewOperation(url,token);_previewOperations.Add(operation);
        }
        try
        {
            T result=await run(operation.Stop.Token).ConfigureAwait(false);
            if(operation.Stop.IsCancellationRequested || !_service.IsCurrent(url,generation))
            {
                discard?.Invoke(result);
                throw new OperationCanceledException(operation.Stop.Token);
            }
            return result;
        }
        finally
        {
            lock(_operationGate)_previewOperations.Remove(operation);
            operation.Completion.TrySetResult();operation.Dispose();
        }
    }
    private void CancelPreviewOperations()
    {
        PreviewOperation[] operations;
        lock(_operationGate)operations=_previewOperations.ToArray();
        foreach(var operation in operations)
            try{operation.Stop.Cancel();}catch(ObjectDisposedException){ }
    }
    private Task JoinPreviewOperationsAsync()
    {
        lock(_operationGate)return Task.WhenAll(_previewOperations.Select(operation=>operation.Completion.Task).ToArray());
    }

    // The caller captures and guards its rows and releases any editor/player
    // that already owns a returned clip. This operation owns pending work and
    // cache invalidation, and deliberately does not schedule a new fetch.
    internal async Task ResetAsync(IEnumerable<DownloadItem> rows)
    {
        var selected=rows.Distinct().ToArray();
        var urls=selected.Select(row=>row.Url).Distinct(StringComparer.Ordinal).ToArray();
        if(urls.Length==0)return;
        PreviewOperation[] operations;
        lock(_operationGate)
        {
            if(_disposed)throw new ObjectDisposedException(nameof(PreviewController));
            if(urls.Any(_resetUrls.Contains))throw new InvalidOperationException("Сброс метаданных выбранного материала уже выполняется.");
            foreach(string url in urls)_resetUrls.Add(url);
            operations=_previewOperations.Where(operation=>urls.Contains(operation.Url,StringComparer.Ordinal)).ToArray();
        }
        try
        {
            foreach(var operation in operations)
                try{operation.Stop.Cancel();}catch(ObjectDisposedException){ }
            await CancelLocalPostersAsync(selected);
            await Task.WhenAll(operations.Select(operation=>operation.Completion.Task));
            await _service.ResetAsync(urls);
            if(_disposed)return;
            if(urls.Contains(_lastReceivedUrl,StringComparer.Ordinal)){_lastReceivedUrl="";_lastReceivedTitle="";}
            if(urls.Contains(SelectedUrl,StringComparer.Ordinal))
            {
                _selectedKnownTitle="";ClearGallery();
                _title.Text=ErrorMessages.Text("Метаданные сброшены","Metadata reset");
                _metadata.Text=SelectedUrl;_metadata.ToolTip=null;_formats.Text="";
            }
        }
        finally{lock(_operationGate)foreach(string url in urls)_resetUrls.Remove(url);}
    }
}
