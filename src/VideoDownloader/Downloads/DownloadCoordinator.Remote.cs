using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace VideoDownloader.Native;

public sealed partial class DownloadCoordinator
{
    private const int RemoteParallel=4;
    private readonly IRemoteDownloadClient? _remote;
    private readonly Channel<DownloadAttempt>? _remoteQueue;
    private List<DownloadAttempt>? _unroutedRemote;
    private volatile bool _remoteReady;

    // Both queues carry the same attempt. The common claim under
    // _completionGate makes a second token inert once either side starts it.
    private bool EnqueueAttempt(DownloadAttempt attempt,DownloadOptions options)
    {
        if(!_queue.Writer.TryWrite(attempt))return false;
        if(_unroutedRemote is {} pending)pending.Add(attempt);
        else if(_remoteReady &&
            (_remote!.OwnsAssignment(attempt.Row) || _options.AllowFreshRemoteDownloads && _remote.Supports(attempt.Row,options)))
            EnqueueRemote(attempt);
        return true;
    }

    private bool EnqueueRemote(DownloadAttempt attempt)
    {
        if(!attempt.RemoteQueued && _remoteQueue?.Writer.TryWrite(attempt)==true)
            attempt.RemoteQueued=true;
        return attempt.RemoteQueued;
    }

    // Assignments restored from disk must use their original server result.
    // The initial Admit can run before the client has loaded its journal.
    private bool RedirectOwnedAssignment(DownloadAttempt attempt)
    {
        if(!_remoteReady)return false;
        lock(_completionGate)
        {
            return _acceptingWork && !attempt.Started && !attempt.LocalOnly && !attempt.Finished.Task.IsCompleted &&
                _remote!.OwnsAssignment(attempt.Row) && EnqueueRemote(attempt);
        }
    }

    private bool TryReturnToLocal(DownloadAttempt attempt)
    {
        var row=attempt.Row;
        lock(_completionGate)
        {
            if(!_acceptingWork || attempt.Stop.IsCancellationRequested || row.PendingRemoval || row.CleanupError.Length>0 ||
                row.StageDir.Length>0 || !_queue.Writer.TryWrite(attempt))return false;
            // The server confirmed it cannot handle this source. Requeue the
            // same generation without creating another local worker or briefly
            // releasing the outstanding admission count.
            _itemCancels.TryRemove(row.Url,out _);
            attempt.LocalOnly=true;
            attempt.Finalizing=false;
            attempt.Started=false;
            row.Status="Queued";row.State="Queued";row.Progress=0;row.ProgressDetail="";row.Slot="";
            Trace(row,JournalLevel.Information,"server_declined_source","waiting_for_local_slot=true",worker:"");
            return true;
        }
    }

    private async Task PrepareRemoteAsync()
    {
        if(_remote is null)return;
        bool prepared=false;
        try
        {
            await Task.Run(()=>_remote.PrepareAsync(_stop.Token)).ConfigureAwait(false);
            prepared=true;
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested) { }
        catch(Exception error)
        {
            _log(ErrorMessages.Text("Серверные задания недоступны: ","Server downloads are unavailable: ")+error.Message);
        }
        lock(_completionGate)
        {
            var pending=_unroutedRemote!;
            _unroutedRemote=null;
            if(!prepared || !_acceptingWork)return;
            _remoteReady=true;
            // Reclaim existing server results before submitting fresh work:
            // those results can occupy all available space on the server.
            foreach(var attempt in pending)
                if(!attempt.Finished.Task.IsCompleted && _remote.OwnsAssignment(attempt.Row))EnqueueRemote(attempt);
            if(_options.AllowFreshRemoteDownloads && _remote.IsAvailable)
                foreach(var attempt in pending)
                    if(!attempt.RemoteQueued && !attempt.Finished.Task.IsCompleted && _remote.Supports(attempt.Row,_options))
                        EnqueueRemote(attempt);
        }
    }

    private IEnumerable<Task> StartRemoteWorkers()=>_remoteReady && _remoteQueue is {} queue
        ?Enumerable.Range(1,Math.Clamp(_remote!.MaxConcurrent,1,RemoteParallel))
            .Select(slot=>Task.Run(()=>WorkerAsync(queue.Reader,"VPS "+slot,useRemote:true)))
        :Array.Empty<Task>();

    private void CompleteQueues()
    {
        _queue.Writer.TryComplete();
        _remoteQueue?.Writer.TryComplete();
    }

    private async Task<DownloadResult> DownloadRemoteAsync(DownloadItem item,DownloadAttempt owner)
    {
        CancellationToken token=owner.Stop.Token;
        string outputRoot=_outputRoots[item.Url];
        OwnedFiles.NoReparseAncestors(Path.Combine(outputRoot,"__check__"));
        item.ResetCurrentVideo();
        string stage=Path.Combine(_stageRoot,Guid.NewGuid().ToString("N"));
        OwnedFiles.NoReparseAncestors(Path.Combine(stage,"__check__"));
        Directory.CreateDirectory(stage);
        item.RunDir=_stageRoot;
        item.StageDir=stage;
        item.OutputRoot=outputRoot;
        try
        {
            ReportRemoteProgress(item,new(RemoteDownloadStage.Waiting,0,0,0));
            await _pause.WaitForResumeAsync(token).ConfigureAwait(false);
            var artifact=await _remote!.DownloadAsync(item,_options,stage,_pause.WaitForResumeAsync,
                progress=>{if(!token.IsCancellationRequested)ReportRemoteProgress(item,progress);},token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string mediaPath=Path.GetFullPath(artifact.MediaPath);
            if(!OwnedFiles.Inside(stage,mediaPath) || !Directory.EnumerateFileSystemEntries(stage).SequenceEqual([mediaPath],StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException("Серверная загрузка вернула файл вне ожидаемого временного каталога.");
            OwnedFiles.RegularOrAbsent(mediaPath);
            if(artifact.Size<=0 || new FileInfo(mediaPath).Length!=artifact.Size)
                throw new InvalidDataException("Размер полученного файла не совпал с ожидаемым размером.");
            item.TransferredBytes=Math.Max(item.TransferredBytes,artifact.Size);
            if(artifact.Title.Length>0)item.Title=artifact.Title;
            item.State="Processing";item.Speed="";item.ETA="";_changed(item);
            await VerifyRemoteMediaAsync(item,mediaPath,token).ConfigureAwait(false);
            await _pause.WaitForResumeAsync(token).ConfigureAwait(false);
            item.State="Publishing";_changed(item);
            lock(_completionGate)
            {
                token.ThrowIfCancellationRequested();
                owner.Publishing=true;
            }
            var published=OwnedFiles.Publish(stage,outputRoot,mediaPath,
                paths=>item.CleanupPaths=paths.ToList(),renameCollisions:true);
            owner.RemoteArtifact=artifact;
            return new(true,false,published.Path,published.OwnedPaths,"",item.TransferredBytes);
        }
        catch(OperationCanceledException) when(token.IsCancellationRequested)
        {
            return new(false,true,"",[],"",item.TransferredBytes);
        }
        finally
        {
            lock(_completionGate)owner.Finalizing=true;
            try
            {
                OwnedFiles.DeleteStage(stage,item.RunDir);
                item.StageDir="";
            }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException)
            {
                DownloadCleanup.Failed(item,"Could not remove remote download staging files: "+error.Message);
                _log(item.CleanupError);
            }
        }
    }

    private void ReportRemoteProgress(DownloadItem item,RemoteDownloadProgress progress)
    {
        item.State="Downloading";
        item.ProgressDetail=progress.Stage switch
        {
            RemoteDownloadStage.Waiting=>ErrorMessages.Text("Ожидание загрузки на сервере","Waiting for the server"),
            RemoteDownloadStage.Downloading=>ErrorMessages.Text("Загрузка на сервере","Downloading on the server"),
            _=>ErrorMessages.Text("Получение файла с сервера","Receiving the file from the server")
        };
        // Split the progress bar into phases and reserve 100% for verified
        // local publication. Only bytes received by Windows count as traffic.
        double percent=double.IsFinite(progress.Percent)?Math.Clamp(progress.Percent,0,100):0;
        double overall=progress.Stage switch
        {
            RemoteDownloadStage.Waiting=>0,
            RemoteDownloadStage.Downloading=>percent*.8,
            _=>80+percent*.19
        };
        item.Progress=Math.Max(item.Progress,overall);
        if(progress.Stage==RemoteDownloadStage.Receiving)
        {
            item.TransferredBytes=Math.Max(item.TransferredBytes,progress.BytesReceived);
            item.Downloaded=$"{progress.BytesReceived/1048576d:F1} / {progress.TotalBytes/1048576d:F1} MiB";
        }
        _changed(item);
    }

    private async Task VerifyRemoteMediaAsync(DownloadItem item,string mediaPath,CancellationToken token)
    {
        await using var budget=new FragmentMergeService.ActiveBudget(token,TimeSpan.FromSeconds(30),()=>_pause.IsPaused);
        var start=new ProcessStartInfo(DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"))
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(string arg in new[]{"-v","error","-protocol_whitelist","file","-format_whitelist",FragmentMediaProbe.AllowedDemuxers,
            "-read_intervals","%+#32","-show_packets","-show_streams","-show_entries",
            "stream=index,codec_type:stream_disposition=attached_pic:packet=stream_index","-of","json","-i",mediaPath})
            start.ArgumentList.Add(arg);
        FragmentMergeToolOutput probe;
        try{probe=await RunProcessingToolAsync(item,start,64*1024,8*1024,budget.Token).ConfigureAwait(false);}
        catch(OperationCanceledException) when(budget.Expired && !token.IsCancellationRequested)
        {throw new TimeoutException("Проверка файла с сервера не завершилась за 30 секунд.");}
        if(probe.ExitCode==0 && HasPlayablePacket(probe.StandardOutput))return;
        throw new InvalidDataException("Не удалось подтвердить, что полученный с сервера файл содержит воспроизводимое медиа.");
    }

    private static bool HasPlayablePacket(byte[] output)
    {
        using var document=JsonDocument.Parse(output);
        if(!document.RootElement.TryGetProperty("streams",out var streams) || streams.ValueKind!=JsonValueKind.Array ||
            !document.RootElement.TryGetProperty("packets",out var packets) || packets.ValueKind!=JsonValueKind.Array)return false;
        var playable=new HashSet<int>();
        foreach(var stream in streams.EnumerateArray())
        {
            if(!stream.TryGetProperty("codec_type",out var codec) || codec.GetString() is not ("video" or "audio"))continue;
            if(stream.TryGetProperty("disposition",out var disposition) && disposition.TryGetProperty("attached_pic",out var attached) && attached.GetInt32()!=0)continue;
            if(stream.TryGetProperty("index",out var index))playable.Add(index.GetInt32());
        }
        return packets.EnumerateArray().Any(packet=>packet.TryGetProperty("stream_index",out var index) && playable.Contains(index.GetInt32()));
    }

    private async Task AcknowledgeRemoteAsync(DownloadItem item,RemoteDownloadArtifact artifact)
    {
        // Local publication and completion already won. A Stop can interrupt
        // other jobs, but this acknowledgement gets its own bounded lifetime.
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try{await _remote!.AcknowledgeAsync(artifact,item.Path,deadline.Token).ConfigureAwait(false);}
        catch(Exception error)
        {
            _log(ErrorMessages.Text("Не удалось подтвердить получение файла. Серверную копию можно освободить повторным запросом: ",
                "Could not confirm receipt. Retrying can release the server copy: ")+error.Message);
        }
    }
}
