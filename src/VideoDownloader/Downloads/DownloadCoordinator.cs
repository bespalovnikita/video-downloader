using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace VideoDownloader.Native;

/// <summary>Native C# yt-dlp session. No pwsh, ThreadJob, live text files, or polling IPC.</summary>
public sealed class DownloadCoordinator : IAsyncDisposable
{
    private readonly string _ytDlp;
    private readonly AppPersistence _data;
    private readonly Channel<DownloadItem> _queue=Channel.CreateUnbounded<DownloadItem>(
        new UnboundedChannelOptions { SingleReader=false,SingleWriter=false });
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _itemCancels=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> _queued=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,DownloadItem> _items=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,string> _outputRoots=new(StringComparer.Ordinal);
    // Tombstones outlive a worker's CTS registration and prevent cancellation races.
    private readonly ConcurrentDictionary<string,byte> _cancelled=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop=new();
    private readonly DownloadOptions _options;
    private readonly Action<DownloadItem> _changed;
    private readonly Action<string> _log;
    private readonly Action<JournalRecord>? _trace;
    private readonly ConcurrentDictionary<string,string> _traceJobIds=new(StringComparer.Ordinal);
    private readonly Stopwatch _traceClock=Stopwatch.StartNew();
    private long _traceSequence;
    public string TraceSessionId { get; }=Guid.NewGuid().ToString("N")[..8];
    private readonly string _stageRoot=Path.Combine(Path.GetTempPath(),"vd-native-"+Guid.NewGuid().ToString("N"));
    private readonly WindowsDownloadPause _pause=new();
    private readonly YtDlpArguments _arguments;
    private readonly object _completionGate=new();

    private int _outstanding;
    private int _completed;
    private Task? _runTask;
    private readonly object _resultGate=new();
    private readonly string _runStamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
    public int Outstanding=>Volatile.Read(ref _outstanding);
    public int Completed=>Volatile.Read(ref _completed);
    public bool IsPaused=>_pause.IsPaused;
    public bool IsRunning {get;private set;}=true;

    public DownloadCoordinator(string ytDlp,AppPersistence data,DownloadOptions options,
        Action<DownloadItem> changed,Action<string> log,Action<JournalRecord>? trace=null)
    {
        _ytDlp=ytDlp;_data=data;_options=options;_changed=changed;_log=log;_trace=trace;
        _arguments=new YtDlpArguments(options);
        _options.PinOutputDirectory();
        OwnedFiles.NoReparseAncestors(Path.Combine(_options.OutputDirectory,"__check__"));
        Directory.CreateDirectory(_stageRoot);
    }
    public bool Add(DownloadItem item)
    {
        if (!IsRunning || _stop.IsCancellationRequested) return false;
        string outputRoot=SubscriptionNames.ResolveOutputDirectory(_options.OutputDirectory,item.SubscriptionName);
        OwnedFiles.NoReparseAncestors(Path.Combine(outputRoot,"__check__"));
        if (!IsRunning || _stop.IsCancellationRequested || !_queued.TryAdd(item.Url,0)) return false;
        Interlocked.Increment(ref _outstanding);
        item.AttemptFinished=false;
        item.OutputRoot=outputRoot;
        _outputRoots[item.Url]=outputRoot;
        _items[item.Url]=item;
        Trace(item,JournalLevel.Information,"job_queued",$"url={DownloadProgressTrace.Quoted(item.Url)} outstanding={Outstanding}",worker:"");
        if (!_queue.Writer.TryWrite(item))
        {
            Trace(item,JournalLevel.Warning,"queue_rejected","reason=ingress_closed",worker:"");
            _queued.TryRemove(item.Url,out _);
            _items.TryRemove(item.Url,out _);
            _outputRoots.TryRemove(item.Url,out _);
            item.AttemptFinished=true;
            Interlocked.Decrement(ref _outstanding);
            return false;
        }
        return true;
    }
    public bool IsPending(DownloadItem item) => _items.TryGetValue(item.Url,out var owned) &&
        ReferenceEquals(item,owned) && !item.AttemptFinished;
    public void Cancel(DownloadItem item)
    {
        Trace(item,JournalLevel.Information,"cancel_requested",$"status={item.Status}");
        // Serializes cancellation with the final success/ownership transition.
        // If success wins, the GUI sees Completed and deletes OwnedPaths.
        // If cancellation wins, the worker must not mark it Completed.
        lock (_completionGate)
        {
            _cancelled[item.Url]=0;
            if (_itemCancels.TryGetValue(item.Url,out var cts))
            {
                try { cts.Cancel(); } catch (ObjectDisposedException) { }
            }
            else if (item.Status=="Queued")
            {
                item.Status="Interrupted"; item.State="Cancelled"; _changed(item);
            }
        }
    }
    public void Pause(bool pause)
    {
        bool changed=_pause.IsPaused!=pause;
        _pause.SetPaused(pause);
        if(changed)Trace(null,JournalLevel.Information,pause?"session_paused":"session_resumed",$"outstanding={Outstanding}");
    }
    public void Stop()
    {
        if(IsRunning && !_stop.IsCancellationRequested)Trace(null,JournalLevel.Information,"stop_requested",$"outstanding={Outstanding}");
        if (_pause.IsPaused) Pause(false);
        _stop.Cancel();
        foreach(var src in _itemCancels.Values) { try { src.Cancel(); } catch(ObjectDisposedException){} }
        _queue.Writer.TryComplete();
    }
    public Task RunAsync() => _runTask ??= RunCoreAsync();
    private async Task RunCoreAsync()
    {
        Trace(null,JournalLevel.Information,"session_start",$"parallel={_options.Parallel} fragments={_options.FragmentCount} quality={DownloadProgressTrace.Quoted(_options.Quality)}");
        var workers=Enumerable.Range(1,_options.Parallel)
            .Select(i=>Task.Run(()=>WorkerAsync(i),CancellationToken.None)).ToArray();
        try
        {
            // Allow live additions while jobs run, but end after a short idle grace
            // period. A new addition after that stays Queued for a later Start.
            var idleSince=DateTime.UtcNow;
            while (!_stop.IsCancellationRequested)
            {
                if (Outstanding==0)
                {
                    if (DateTime.UtcNow-idleSince>TimeSpan.FromMilliseconds(1500)) break;
                }
                else idleSince=DateTime.UtcNow;
                await Task.Delay(150).ConfigureAwait(false);
            }
        }
        finally
        {
            _queue.Writer.TryComplete();
            await Task.WhenAll(workers).ConfigureAwait(false);
            IsRunning=false;
            Trace(null,JournalLevel.Information,"session_complete",$"completed={Completed} outstanding={Outstanding} stopped={_stop.IsCancellationRequested}");
            // A failed cleanup remains on disk, with its paths in the checkpoint.
            // Never recursively erase another row's retained staging context.
            try { if (!Directory.EnumerateFileSystemEntries(_stageRoot).Any()) Directory.Delete(_stageRoot); }
            catch(IOException){} catch(UnauthorizedAccessException){}
        }
    }
    private async Task WorkerAsync(int slot)
    {
        await foreach(var item in _queue.Reader.ReadAllAsync())
        {
            using var linked=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _itemCancels[item.Url]=linked;
            try
            {
                if (_cancelled.ContainsKey(item.Url) || linked.IsCancellationRequested)
                {
                    item.Status="Interrupted";item.State="Cancelled";continue;
                }
                item.Slot="T"+slot;item.Status="Running";item.State="Starting";_changed(item);
                Trace(item,JournalLevel.Information,"job_start",$"url={DownloadProgressTrace.Quoted(item.Url)}");
                // Cancellation can arrive between the previous check and CTS registration.
                if (_cancelled.ContainsKey(item.Url)) linked.Cancel();
                var result=await DownloadAsync(item,linked.Token).ConfigureAwait(false);
                bool cancelled=result.Cancelled || linked.IsCancellationRequested || _cancelled.ContainsKey(item.Url);
                lock (_completionGate)
                {
                    // A cancellation issued after Publish but before this lock
                    // must still clean the published files.
                    cancelled |= _cancelled.ContainsKey(item.Url) || linked.IsCancellationRequested;
                    if (cancelled)
                    {
                        if (item.CleanupError.Length==0)
                        {
                            DownloadCleanup.Retry(item);
                            item.Status="Interrupted";item.State="Cancelled";
                        }
                    }
                    else if (result.Success && item.CleanupError.Length==0 && !item.PendingRemoval)
                    {
                        item.Path=result.Path;item.OwnedPaths=result.OwnedPaths.ToList();
                        item.CleanupPaths=[];
                        item.OutputRoot=_outputRoots[item.Url];
                        item.Status="Completed";item.State="Done";
                        item.Progress=100;item.Speed="";item.ETA="";
                        try { _data.AppendSuccess(item.Url); }
                        catch(Exception e) { _log($"WARNING: Could not append successful URL to history: {e.Message}"); }
                        Interlocked.Increment(ref _completed);
                        WriteResult("success",$"{item.Url} | {item.Path}");
                    }
                    else if (item.CleanupError.Length==0)
                    {
                        item.Status="Failed";item.State="Failed";
                        item.RawError=result.Error;item.Speed="";item.ETA="";
                        WriteResult("error",$"{item.Url} | {result.Error}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                item.Status="Interrupted";item.State="Cancelled";
            }
            catch(Exception ex)
            {
                if (item.CleanupPaths.Count>0 || item.CleanupError.Length>0)
                    DownloadCleanup.Failed(item,ex.Message);
                else {item.Status="Failed";item.State="Failed";item.RawError=ex.Message;}
                WriteResult("error",$"{item.Url} | {ex.Message}");
                _log($"ERROR {item.Url}: {ex.Message}");
            }
            finally
            {
                if (item.CleanupError.Length>0) DownloadCleanup.Failed(item,item.CleanupError);
                item.AttemptFinished=true;
                Trace(item,item.Status=="Failed" || item.CleanupError.Length>0 ? JournalLevel.Error : JournalLevel.Information,
                    "job_finished",$"status={item.Status} state={DownloadProgressTrace.Quoted(item.State)} shown_pct={DownloadProgressTrace.Number(item.Progress)} transferred={item.TransferredBytes}"+
                    (item.RawError.Length>0 ? " error="+DownloadProgressTrace.Quoted(item.RawError) : "")+
                    (item.CleanupError.Length>0 ? " cleanup_error="+DownloadProgressTrace.Quoted(item.CleanupError) : ""));
                _changed(item);
                _itemCancels.TryRemove(item.Url,out _);
                Interlocked.Decrement(ref _outstanding);
            }
        }
    }

    private void WriteResult(string kind,string line)
    {
        try
        {
            lock(_resultGate)
            {
                Directory.CreateDirectory(_options.OutputDirectory);
                var path=Path.Combine(_options.OutputDirectory,$"VideoDownloader-{_runStamp}-{kind}.txt");
                File.AppendAllText(path,line+Environment.NewLine,new System.Text.UTF8Encoding(false));
            }
        }
        catch(Exception e) { _log("Cannot write download result log: "+e.Message); }
    }
    public static double ParseSize(string raw)=>YtDlpOutputParser.ParseSize(raw);

    private void Trace(DownloadItem? item,JournalLevel level,string eventName,string details="",string category="download",string? worker=null)
    {
        if(_trace==null)return;
        try
        {
            long sequence=Interlocked.Increment(ref _traceSequence);
            var record=new JournalRecord(level,category,
                $"event={eventName} "+(item==null?"":DownloadTraceIdentity.VideoLabel(item)+" ")+
                $"seq={sequence} elapsed_ms={_traceClock.ElapsedMilliseconds}"+(details.Length>0?" "+details:""),
                TraceSessionId,item==null?"":_traceJobIds.GetOrAdd(item.Url,DownloadTraceIdentity.JobId),worker??item?.Slot??"");
            _trace(record);
        }
        catch(Exception) { /* Diagnostics must not change a download's outcome. */ }
    }

    private async Task<DownloadResult> DownloadAsync(DownloadItem item,CancellationToken ct)
    {
        string outputRoot=_outputRoots[item.Url];
        OwnedFiles.NoReparseAncestors(Path.Combine(outputRoot,"__check__"));
        string stage=Path.Combine(_stageRoot,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        item.RunDir=_stageRoot;item.StageDir=stage;item.OutputRoot=outputRoot;
        _changed(item);
        string mediaPath="";
        var streamed=new Dictionary<string,long>(StringComparer.Ordinal);
        long transferred=0;
        var progressTrace=new DownloadProgressTrace();
        var progressPlan=new DownloadProgressPlan();
        int launch=0;
        try
        {
            OwnedFiles.ValidateTemplate(_options.FilenameTemplate);
            foreach(var q in _arguments.QualityLevels())
            {
                for (int attempt=0;attempt<=3;attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    launch++;
                    double previousShown=item.Progress;
                    if(launch>1)
                    {
                        item.Progress=0;item.Speed="";item.ETA="";item.State="Starting";
                        item.ProgressDetail="";_changed(item);
                    }
                    // A failed earlier invocation must never lend its final
                    // media marker to a later zero-exit invocation.
                    mediaPath="";
                    progressPlan.Reset();
                    bool postprocessing=false;
                    Trace(item,JournalLevel.Information,"attempt_start",$"attempt={attempt+1} invocation={launch} quality={DownloadProgressTrace.Quoted(q)} previous_shown_pct={DownloadProgressTrace.Number(previousShown)} transferred={transferred}");
                    var errors=new ConcurrentQueue<string>();
                    void Line(string line,string source)
                    {
                        var parsed=YtDlpOutputParser.Parse(line);
                        if(parsed is not YtDlpOutputEvent.Progress)
                            Trace(item,JournalLevel.Verbose,"output",$"attempt={attempt+1} quality={DownloadProgressTrace.Quoted(q)} stream={source} text={DownloadProgressTrace.Quoted(line)}",category:"yt-dlp");
                        switch(parsed)
                        {
                            case YtDlpOutputEvent.Title title:
                                item.Title=title.Value;_changed(item);
                                Trace(item,JournalLevel.Information,"title",$"attempt={attempt+1} stream={source} title={DownloadProgressTrace.Quoted(title.Value)}");break;
                            case YtDlpOutputEvent.MediaPath media:
                                mediaPath=media.Value;
                                Trace(item,JournalLevel.Information,"media_reported",$"attempt={attempt+1} stream={source}");break;
                            case YtDlpOutputEvent.Plan plan:
                                lock(streamed)progressPlan.Begin(plan);
                                Trace(item,JournalLevel.Information,"download_plan",$"attempt={attempt+1} components={plan.FormatIds.Count} media_index={plan.MediaIndex} media_count={plan.MediaCount}");
                                break;
                            case YtDlpOutputEvent.Progress progress:
                                // stdout and stderr are consumed by independent async
                                // tasks. Progress samples must not race each other.
                                lock(streamed)
                                {
                                    streamed.TryGetValue(progress.Component,out long previous);
                                    long bytes=Math.Max(0,progress.DownloadedBytes);
                                    // A retry can reset a component counter. Keep
                                    // its new baseline and count the retried bytes;
                                    // identical final samples still contribute zero.
                                    transferred+=bytes<previous ? bytes : bytes-previous;
                                    streamed[progress.Component]=bytes;
                                    var display=progressPlan.Observe(progress,item.Progress);
                                    item.Progress=display.Percent;
                                    item.ProgressDetail=display.Detail;
                                    item.Speed=progress.Speed;
                                    item.Downloaded=DownloadFormat.Bytes(bytes)+(progress.TotalBytes>0 ? " / "+DownloadFormat.Bytes(progress.TotalBytes) : "");
                                    item.ETA=progress.Eta;item.TransferredBytes=transferred;
                                    item.State="Downloading";
                                    string? traceDetails=_trace==null ? null : progressTrace.Observe(progress,item.Progress,transferred,attempt+1,q,source);
                                    // Record the immutable sample before the other child pipe
                                    // can update this same URL. Sequence then reflects the
                                    // actual percentage/counter transition, not callback races.
                                    if(traceDetails!=null)Trace(item,JournalLevel.Progress,"progress",traceDetails);
                                }
                                _changed(item);break;
                            case YtDlpOutputEvent.Diagnostic diagnostic:
                                if(diagnostic.Error is {} error) errors.Enqueue(error);
                                if(diagnostic.Error!=null)
                                    Trace(item,line.StartsWith("ERROR:",StringComparison.OrdinalIgnoreCase)?JournalLevel.Error:JournalLevel.Warning,
                                        "diagnostic",$"attempt={attempt+1} stream={source} text={DownloadProgressTrace.Quoted(diagnostic.Error)}",category:"yt-dlp");
                                if(diagnostic.Merging)
                                {
                                    item.State="Merging";_changed(item);
                                    Trace(item,JournalLevel.Information,"merging",$"attempt={attempt+1} stream={source}");
                                }
                                else if(diagnostic.Postprocessing)
                                {
                                    item.State="Processing";_changed(item);
                                    Trace(item,JournalLevel.Information,"processing",$"attempt={attempt+1} stream={source}");
                                }
                                if(diagnostic.Postprocessing)postprocessing=true;
                                break;
                        }
                    }
                    int exitCode=await RunProcessAsync(item,q,attempt+1,_arguments.Build(item.Url,stage,q),Line,ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (exitCode==0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(mediaPath))
                            return new DownloadResult(false,false,"",[],
                                "yt-dlp finished without reporting a downloaded media file.",transferred);
                        if(!_options.AudioOnly && _options.Container!="Auto" &&
                            !Path.GetExtension(mediaPath).Equals("."+_options.Container,StringComparison.OrdinalIgnoreCase))
                            return new DownloadResult(false,false,"",[],
                                "yt-dlp did not produce the requested container: "+_options.Container,transferred);
                        // Publication updates the row's manifest after each move,
                        // so even a failed rollback retains exact cleanup paths.
                        item.State="Publishing";item.Speed="";item.ETA="";_changed(item);
                        Trace(item,JournalLevel.Information,"publishing",$"attempt={attempt+1} quality={DownloadProgressTrace.Quoted(q)}");
                        var published=OwnedFiles.Publish(stage,outputRoot,mediaPath,
                            paths=>item.CleanupPaths=paths.ToList(),renameCollisions:true);
                        Trace(item,JournalLevel.Information,"published",$"attempt={attempt+1} files={published.OwnedPaths.Count}");
                        if (ct.IsCancellationRequested || _cancelled.ContainsKey(item.Url))
                        {
                            return new DownloadResult(false,true,published.Path,published.OwnedPaths,"",transferred);
                        }
                        return new DownloadResult(true,false,published.Path,published.OwnedPaths,"",transferred);
                    }
                    string err=string.Join(" | ",errors.TakeLast(12));
                    if (err.Length==0) err="yt-dlp exit code "+exitCode;
                    // FFmpeg/container/metadata failures are final processing
                    // failures. Restarting yt-dlp would redownload completed
                    // media and cannot repair a broken postprocessor.
                    if(postprocessing)
                    {
                        Trace(item,JournalLevel.Error,"processing_failed",$"attempt={attempt+1} error={DownloadProgressTrace.Quoted(err)}");
                        return new DownloadResult(false,false,"",[],err,transferred);
                    }
                    if (YtDlpOutputParser.FormatUnavailable(err) && q != _arguments.QualityLevels().Last())
                    {
                        Trace(item,JournalLevel.Warning,"quality_fallback",$"attempt={attempt+1} quality={DownloadProgressTrace.Quoted(q)} error={DownloadProgressTrace.Quoted(err)}");
                        _log($"Format unavailable at {q}, trying lower quality."); break;
                    }
                    if (YtDlpOutputParser.IsTransient(err) && attempt<3)
                    {
                        int delay=Math.Min(60,5*(1<<attempt));
                        Trace(item,JournalLevel.Warning,"retry_scheduled",$"attempt={attempt+1} next_attempt={attempt+2} quality={DownloadProgressTrace.Quoted(q)} delay_s={delay} error={DownloadProgressTrace.Quoted(err)}");
                        item.State="Retrying";item.Speed="";item.ETA="";
                        item.ProgressDetail=ErrorMessages.Text($"Попытка {attempt+2}/4 через {delay} с",$"Attempt {attempt+2}/4 in {delay}s");_changed(item);
                        _log($"Transient error on {item.Url}, retry {attempt+1}/3 in {delay}s: {err}");
                        await Task.Delay(TimeSpan.FromSeconds(delay),ct).ConfigureAwait(false);
                        continue;
                    }
                    return new DownloadResult(false,false,"",[] ,err,transferred);
                }
            }
            return new DownloadResult(false,false,"",[],"No suitable quality available",transferred);
        }
        catch(OperationCanceledException) { return new DownloadResult(false,true,"",[],"",transferred); }
        finally
        {
            // Staged partial files never touch the destination folder.
            try { OwnedFiles.DeleteStage(stage,_stageRoot);item.StageDir=""; }
            catch(Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DownloadCleanup.Failed(item,"Could not remove partial staging files: "+e.Message);
                _log(item.CleanupError);
            }
        }
    }

    private async Task<int> RunProcessAsync(DownloadItem item,string quality,int attempt,IEnumerable<string> args,Action<string,string> onLine,CancellationToken token)
    {
        var psi=new ProcessStartInfo(_ytDlp)
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
            StandardOutputEncoding=System.Text.Encoding.UTF8,StandardErrorEncoding=System.Text.Encoding.UTF8,
            WorkingDirectory=AppContext.BaseDirectory
        };
        foreach(string arg in args) psi.ArgumentList.Add(arg);
        await _pause.WaitForResumeAsync(token).ConfigureAwait(false);
        using var child=ManagedChildProcess.Start(psi);
        var process=child.Process;
        _pause.Register(item.Url,process);
        Trace(item,JournalLevel.Information,"process_start",$"attempt={attempt} quality={DownloadProgressTrace.Quoted(quality)} pid={process.Id}");
        using var reg=token.Register(()=>
        {
            try { if (!process.HasExited) { _pause.ResumeForCancellation(process); process.Kill(true); } }
            catch (InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
        });
        async Task Pump(StreamReader stream,string source)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line,source);
        }
        try
        {
            await Task.WhenAll(Pump(child.StandardOutput,"stdout"),Pump(child.StandardError,"stderr"),process.WaitForExitAsync())
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally
        {
            try
            {
                if(process.HasExited)Trace(item,JournalLevel.Information,"process_exit",
                    $"attempt={attempt} quality={DownloadProgressTrace.Quoted(quality)} pid={process.Id} exit_code={process.ExitCode} cancelled={token.IsCancellationRequested}");
            }
            catch(Exception) { /* Reading diagnostic process metadata must not change the outcome. */ }
            _pause.Unregister(item.Url);
        }
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        try
        {
            if (_runTask!=null) await _runTask.ConfigureAwait(false);
        }
        finally { _stop.Dispose(); }
    }
}
