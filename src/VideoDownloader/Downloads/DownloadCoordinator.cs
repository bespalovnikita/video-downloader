using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Channels;

namespace VideoDownloader.Native;

/// <summary>Native C# yt-dlp session. No pwsh, ThreadJob, live text files, or polling IPC.</summary>
public sealed partial class DownloadCoordinator : IAsyncDisposable
{
    private readonly string _ytDlp;
    private readonly AppPersistence _data;
    private readonly Channel<DownloadAttempt> _queue=Channel.CreateUnbounded<DownloadAttempt>(
        new UnboundedChannelOptions { SingleReader=false,SingleWriter=false });
    private readonly ConcurrentDictionary<string,CancellationTokenSource> _itemCancels=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> _queued=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,DownloadAttempt> _attempts=new(StringComparer.Ordinal);
    private readonly ConcurrentBag<Task> _queuedCleanups=new();
    private readonly ConcurrentDictionary<string,string> _outputRoots=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,DownloadSelection> _selections=new(StringComparer.Ordinal);
    // Tombstones outlive a worker's CTS registration and prevent cancellation races.
    private readonly ConcurrentDictionary<string,byte> _cancelled=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop=new();
    private readonly DownloadOptions _options;
    private readonly Action<DownloadItem> _changed;
    private readonly Action<string> _log;
    private readonly Action<JournalRecord>? _trace;
    private readonly ConcurrentDictionary<string,string> _traceJobIds=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,(int Attempt,int Invocation,string? Quality)> _diagnosticAttempts=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,int> _invocations=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,JobDiagnosticSettings> _diagnosticPlans=new(StringComparer.Ordinal);
    private readonly Stopwatch _traceClock=Stopwatch.StartNew();
    private long _traceSequence;
    public string TraceSessionId { get; }=Guid.NewGuid().ToString("N")[..8];
    private readonly string _stageRoot=Path.Combine(Path.GetTempPath(),"vd-native-"+Guid.NewGuid().ToString("N"));
    internal string StagingDirectory=>_stageRoot;
    private readonly WindowsDownloadPause _pause=new();
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
        AudioTrackSelection.Validate(options);
        _ytDlp=ytDlp;_data=data;_options=options.Snapshot();_changed=changed;_log=log;_trace=trace;
        _options.PinOutputDirectory();
        OwnedFiles.NoReparseAncestors(Path.Combine(_options.OutputDirectory,"__check__"));
        Directory.CreateDirectory(_stageRoot);
    }
    public bool Add(DownloadItem item)=>Admit(item,false);
    private bool Admit(DownloadItem item,bool resume)
    {
        lock(_completionGate)
        {
            if(!IsRunning || _stop.IsCancellationRequested || item.PendingRemoval || item.CleanupError.Length>0)return false;
            DownloadResumeStore.ValidatePersisted(item);
            var options=item.ResumeState?.Plan.ToOptions()??_options;
            var selection=item.ResumeState?.Selection??item.Selection;
            selection?.Validate(item.Url,options);
            string outputRoot=AdmissionOutputRoot(item);
            OwnedFiles.NoReparseAncestors(Path.Combine(outputRoot,"__check__"));
            if(_attempts.TryGetValue(item.Url,out var previous) && !previous.Finished.Task.IsCompleted)return false;
            if(!resume && !_queued.TryAdd(item.Url,0))return false;
            if(resume)
            {
                if(item.Status!="Skipped" || !item.AttemptFinished || _cancelled.ContainsKey(item.Url))return false;
                _queued[item.Url]=0;
            }
            var attempt=new DownloadAttempt(item,_stop.Token);
            _attempts[item.Url]=attempt;
            Interlocked.Increment(ref _outstanding);
            item.AttemptFinished=false;item.OutputRoot=outputRoot;
            item.Status="Queued";item.State="Queued";item.Speed="";item.ETA="";
            _outputRoots[item.Url]=outputRoot;
            if(selection is not null)_selections[item.Url]=selection;else _selections.TryRemove(item.Url,out _);
            if(_trace is not null)
            {
                var diagnostic=JobDiagnosticSettings.Capture(options,selection);
                _diagnosticPlans[item.Url]=diagnostic with
                {
                    Parallel=_options.Parallel,
                    // The minimal retained plan stores resolved parameters,
                    // not an old display/profile preset identifier.
                    Profile=item.ResumeState is null?diagnostic.Profile:"неизвестно"
                };
            }
            // Queued/resumed belongs to the new generation before its first
            // invocation; it must not inherit the previous attempt's key.
            _diagnosticAttempts.TryRemove(item.Url,out _);
            Trace(item,JournalLevel.Information,resume?"job_resumed":"job_queued",$"url={DownloadProgressTrace.Quoted(item.Url)} outstanding={Outstanding}",worker:"");
            if(!_queue.Writer.TryWrite(attempt))
            {
                _attempts.TryRemove(item.Url,out _);_queued.TryRemove(item.Url,out _);
                _outputRoots.TryRemove(item.Url,out _);_selections.TryRemove(item.Url,out _);
                _diagnosticPlans.TryRemove(item.Url,out _);
                item.AttemptFinished=true;Interlocked.Decrement(ref _outstanding);attempt.Finished.TrySetResult(false);attempt.Stop.Dispose();
                return false;
            }
            return true;
        }
    }
    internal string AdmissionOutputRoot(DownloadItem item)=>item.ResumeState?.Plan.OutputRoot??
        SubscriptionNames.ResolveOutputDirectory(_options.OutputDirectory,item.SubscriptionName);
    public bool IsPending(DownloadItem item)=>_attempts.TryGetValue(item.Url,out var owned) &&
        ReferenceEquals(item,owned.Row) && !owned.Finished.Task.IsCompleted;
    public void Cancel(DownloadItem item)
    {
        Trace(item,JournalLevel.Information,"cancel_requested",$"status={item.Status}");
        lock(_completionGate)
        {
            _cancelled[item.Url]=0;
            if(_attempts.TryGetValue(item.Url,out var attempt) && ReferenceEquals(attempt.Row,item) && !attempt.Finished.Task.IsCompleted)
            {
                attempt.Reason=AttemptStopReason.Cancel;
                try{attempt.Stop.Cancel();}catch(ObjectDisposedException){}
                if(!attempt.Started)
                {
                    attempt.Started=true;
                    var cleanup=Task.Run(()=>
                    {
                        try{DownloadCleanup.Retry(item);}
                        catch(Exception error){_log("Queued cancellation cleanup failed: "+error.Message);}
                        finally{lock(_completionGate)FinishQueuedAttempt(attempt,false);}
                    });
                    _queuedCleanups.Add(cleanup);
                }
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
            await Task.WhenAll(_queuedCleanups.ToArray()).ConfigureAwait(false);
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
        await foreach(var attempt in _queue.Reader.ReadAllAsync())
        {
            var item=attempt.Row;
            lock(_completionGate)
            {
                if(attempt.Started || attempt.Finished.Task.IsCompleted || !_attempts.TryGetValue(item.Url,out var current) || !ReferenceEquals(current,attempt))continue;
                attempt.Started=true;
            }
            var linked=attempt.Stop;
            _itemCancels[item.Url]=linked;
            try
            {
                if(_cancelled.ContainsKey(item.Url) || linked.IsCancellationRequested)
                {item.Status="Interrupted";item.State="Cancelled";continue;}
                item.Slot="T"+slot;item.Status="Running";item.State="Starting";_changed(item);
                Trace(item,JournalLevel.Information,"job_start",$"url={DownloadProgressTrace.Quoted(item.Url)}");
                if(_cancelled.ContainsKey(item.Url))linked.Cancel();
                var result=await DownloadAsync(item,attempt).ConfigureAwait(false);
                bool cancelled=result.Cancelled || linked.IsCancellationRequested || _cancelled.ContainsKey(item.Url);
                while(true)
                {
                    bool clean=false;
                    lock(_completionGate)
                    {
                        cancelled|=_cancelled.ContainsKey(item.Url) || linked.IsCancellationRequested;
                        if(attempt.Reason==AttemptStopReason.Skip && item.CleanupError.Length==0 && !item.PendingRemoval)
                        {
                            item.Status="Skipped";item.State="Skipped";item.Speed="";item.ETA="";item.Slot="";
                            break;
                        }
                        if(cancelled && item.CleanupError.Length==0 &&
                            (item.OwnedPaths.Count>0 || item.CleanupPaths.Count>0 || item.StageDir.Length>0 || item.Path.Length>0))
                        {attempt.Finalizing=true;clean=true;}
                        else if(cancelled)
                        {
                            if(item.CleanupError.Length==0){item.Status="Interrupted";item.State="Cancelled";}
                            break;
                        }
                        else if(result.Success && item.CleanupError.Length==0 && !item.PendingRemoval)
                        {
                            item.Path=result.Path;item.OwnedPaths=result.OwnedPaths.ToList();item.CleanupPaths=[];
                            item.OutputRoot=_outputRoots[item.Url];item.CompletedSelection=_selections.GetValueOrDefault(item.Url);
                            item.Status="Completed";item.State="Done";item.Progress=100;item.Speed="";item.ETA="";
                            try{if(!item.IsPartialResult)_data.AppendSuccess(item);}
                            catch(Exception e){_log($"WARNING: Could not append successful URL to history: {e.Message}");}
                            Interlocked.Increment(ref _completed);WriteResult("success",$"{item.Url} | {item.Path}");
                            break;
                        }
                        else
                        {
                            if(item.CleanupError.Length==0)
                            {
                                item.Status="Failed";item.State="Failed";item.RawError=result.Error;item.Speed="";item.ETA="";
                                WriteResult("error",$"{item.Url} | {result.Error}");
                            }
                            break;
                        }
                    }
                    // Filesystem cleanup may cross a slow/remote volume. The
                    // finalizing attempt cannot be resumed and owns this work;
                    // other rows' Skip/Cancel must not wait on a shared FS lock.
                    if(clean)DownloadCleanup.Retry(item);
                }
            }
            catch(OperationCanceledException)
            {
                item.Status=attempt.Reason==AttemptStopReason.Skip?"Skipped":"Interrupted";
                item.State=attempt.Reason==AttemptStopReason.Skip?"Skipped":"Cancelled";
            }
            catch(Exception ex)
            {
                if(item.CleanupPaths.Count>0 || item.CleanupError.Length>0)DownloadCleanup.Failed(item,ex.Message);
                else{item.Status="Failed";item.State="Failed";item.RawError=ex.Message;}
                WriteResult("error",$"{item.Url} | {ex.Message}");_log($"ERROR {item.Url}: {ex.Message}");
            }
            finally
            {
                lock(_completionGate)
                {
                    if(item.CleanupError.Length>0)DownloadCleanup.Failed(item,item.CleanupError);
                    item.AttemptFinished=true;item.Speed="";item.ETA="";
                    Trace(item,item.Status=="Failed" || item.CleanupError.Length>0?JournalLevel.Error:JournalLevel.Information,
                        "job_finished",$"status={item.Status} state={DownloadProgressTrace.Quoted(item.State)} shown_pct={DownloadProgressTrace.Number(item.Progress)} transferred={item.TransferredBytes}"+
                        (item.RawError.Length>0?" error="+DownloadProgressTrace.Quoted(item.RawError):"")+
                        (item.CleanupError.Length>0?" cleanup_error="+DownloadProgressTrace.Quoted(item.CleanupError):""));
                    _itemCancels.TryRemove(item.Url,out _);Interlocked.Decrement(ref _outstanding);
                    // Completion is signalled only after the last mutation and
                    // process/pipes join. A new generation can now safely enter.
                    attempt.Finished.TrySetResult(item.Status=="Skipped");
                    attempt.Stop.Dispose();_changed(item);
                }
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
            var numbers=item is null?default:_diagnosticAttempts.GetValueOrDefault(item.Url);
            var record=new JournalRecord(level,category,
                $"event={eventName} "+(item==null?"":DownloadTraceIdentity.VideoLabel(item)+" ")+
                $"seq={sequence} elapsed_ms={_traceClock.ElapsedMilliseconds}"+(details.Length>0?" "+details:""),
                TraceSessionId,item==null?"":_traceJobIds.GetOrAdd(item.Url,DownloadTraceIdentity.JobId),worker??item?.Slot??"",
                Identity:item is null?"":DownloadTraceIdentity.DiagnosticIdentity(item.Url),Event:eventName,
                Attempt:numbers.Attempt>0?numbers.Attempt:null,Invocation:numbers.Invocation>0?numbers.Invocation:null,
                Settings:item is not null && (eventName is "job_queued" or "job_resumed" or "attempt_start" || level is JournalLevel.Error or JournalLevel.Warning) &&
                    _diagnosticPlans.TryGetValue(item.Url,out var captured)
                    ?captured with{Quality=numbers.Quality??captured.Quality}:null);
            _trace(record);
        }
        catch(Exception) { /* Diagnostics must not change a download's outcome. */ }
    }

    private async Task<DownloadResult> DownloadAsync(DownloadItem item,DownloadAttempt owner)
    {
        CancellationToken ct=owner.Stop.Token;
        string outputRoot=_outputRoots[item.Url];
        OwnedFiles.NoReparseAncestors(Path.Combine(outputRoot,"__check__"));
        bool retained=item.ResumeState is not null;
        DownloadResumeStore.Validated? validated=retained?DownloadResumeStore.Validate(item):null;
        var options=validated?.State.Plan.ToOptions()??_options;
        var arguments=new YtDlpArguments(options);
        string stage=retained?item.StageDir:Path.Combine(_stageRoot,Guid.NewGuid().ToString("N"));
        if(!retained)Directory.CreateDirectory(stage);
        if(!retained){item.RunDir=_stageRoot;item.StageDir=stage;}
        item.OutputRoot=outputRoot;
        _changed(item);
        string mediaPath="";
        var reportedMedia=new ConcurrentQueue<string>();
        var subtitleManifests=new ConcurrentQueue<string>();
        var streamed=new Dictionary<string,long>(StringComparer.Ordinal);
        long transferred=retained?item.TransferredBytes:0;
        var progressTrace=new DownloadProgressTrace();
        var progressPlan=new DownloadProgressPlan();
        var fragmentPlans=new Dictionary<int,DownloadProgressPlan>();
        var fragmentPercent=new Dictionary<int,double>();
        int launch=0;
        var selection=_selections.GetValueOrDefault(item.Url);
        int selectedFragments=selection?.EffectiveSections.Count??0;
        int FragmentIndex(double? start,double? end)
        {
            if(selectedFragments<2 || start is null || end is null)return Math.Min(reportedMedia.Count,Math.Max(0,selectedFragments-1));
            for(int index=0;index<selectedFragments;index++)
            {
                var range=selection!.SectionBounds(selection.EffectiveSections[index]);
                if(Math.Abs(start.Value-range.Start)<.00001 && Math.Abs(end.Value-range.End)<.00001)return index;
            }
            return Math.Min(reportedMedia.Count,selectedFragments-1);
        }
        DownloadProgressPlan FragmentPlan(int index)
        {if(!fragmentPlans.TryGetValue(index,out var plan))fragmentPlans[index]=plan=new DownloadProgressPlan();return plan;}
        string[] qualities=arguments.QualityLevels(selection).ToArray();
        if(validated is not null)
        {
            int from=Array.IndexOf(qualities,validated.Quality);
            if(from>=0)qualities=qualities[from..];
        }
        try
        {
            OwnedFiles.ValidateTemplate(options.FilenameTemplate);
            if(!retained)DownloadResumeStore.Create(item,options,selection,qualities[0]);
            foreach(var q in qualities)
            {
                for (int attempt=0;attempt<=3;attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    DownloadResumeStore.UpdateQuality(item,q);
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
                    reportedMedia.Clear();
                    subtitleManifests.Clear();
                    progressPlan.Reset();
                    fragmentPlans.Clear();
                    fragmentPercent.Clear();
                    if(selectedFragments>1)streamed.Clear();
                    bool postprocessing=false;
                    int subtitleManifestCount=0;
                    int invocation=_invocations.AddOrUpdate(item.Url,1,(_,before)=>before+1);
                    _diagnosticAttempts[item.Url]=(attempt+1,invocation,q);
                    Trace(item,JournalLevel.Information,"attempt_start",$"attempt={attempt+1} invocation={invocation} quality={DownloadProgressTrace.Quoted(q)} previous_shown_pct={DownloadProgressTrace.Number(previousShown)} transferred={transferred}");
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
                                reportedMedia.Enqueue(media.Value);
                                if(selectedFragments>1)
                                {
                                    // A partial batch must not be mixed with a
                                    // new quality or automatic full-transfer retry.
                                    postprocessing=true;
                                    item.Progress=Math.Min(99,reportedMedia.Count*100d/selectedFragments);
                                    _changed(item);
                                }
                                Trace(item,JournalLevel.Information,"media_reported",$"attempt={attempt+1} stream={source}");break;
                            case YtDlpOutputEvent.Subtitles subtitles:
                                if(Interlocked.Increment(ref subtitleManifestCount)<=DownloadSectionSet.MaximumCount+1)
                                    subtitleManifests.Enqueue(subtitles.Value.Length<=65536?subtitles.Value:"");
                                break;
                            case YtDlpOutputEvent.Plan plan:
                                lock(streamed)
                                    if(selectedFragments>1)FragmentPlan(FragmentIndex(plan.SectionStart,plan.SectionEnd)).Begin(plan);
                                    else progressPlan.Begin(plan);
                                Trace(item,JournalLevel.Information,"download_plan",$"attempt={attempt+1} components={plan.FormatIds.Count} media_index={plan.MediaIndex} media_count={plan.MediaCount}");
                                break;
                            case YtDlpOutputEvent.Progress progress:
                                // stdout and stderr are consumed by independent async
                                // tasks. Progress samples must not race each other.
                                lock(streamed)
                                {
                                    int fragment=FragmentIndex(progress.SectionStart,progress.SectionEnd);
                                    string component=selectedFragments>1?fragment.ToString(CultureInfo.InvariantCulture)+":"+progress.Component:progress.Component;
                                    string counterKey=DownloadResumeStore.ComponentIdentity(component);
                                    if(!streamed.TryGetValue(component,out long previous) && retained)
                                        previous=validated!.State.ComponentBytes.GetValueOrDefault(counterKey);
                                    long bytes=Math.Max(0,progress.DownloadedBytes);
                                    // A retry can reset a component counter. Keep
                                    // its new baseline and count the retried bytes;
                                    // identical final samples still contribute zero.
                                    transferred+=bytes<previous ? bytes : bytes-previous;
                                    streamed[component]=bytes;
                                    if(item.ResumeState is {} resume && (resume.ComponentBytes.Count<2048 || resume.ComponentBytes.ContainsKey(counterKey)))
                                    {
                                        var counters=new Dictionary<string,long>(resume.ComponentBytes,StringComparer.Ordinal){[counterKey]=bytes};
                                        item.ResumeState=resume with{ComponentBytes=counters};
                                    }
                                    item.TransferredBytes=transferred;
                                    // Separate pipe readers may deliver a final sample of
                                    // an earlier range after its media marker. Count its
                                    // bytes, but never turn it into the next range's UI.
                                    if(selectedFragments>1 && fragment<reportedMedia.Count)break;
                                    var display=(selectedFragments>1?FragmentPlan(fragment):progressPlan).Observe(progress,
                                        selectedFragments>1?fragmentPercent.GetValueOrDefault(fragment):item.Progress);
                                    if(selectedFragments>1)fragmentPercent[fragment]=display.Percent;
                                    item.Progress=selectedFragments>1?Math.Min(99,(fragment+display.Percent/100)*100/selectedFragments):display.Percent;
                                    item.ProgressDetail=(selectedFragments>1?$"Фрагмент {fragment+1}/{selectedFragments} · ":"")+display.Detail;
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
                    int exitCode=await RunProcessAsync(item,q,attempt+1,arguments.Build(item.Url,stage,q,selection),Line,ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (exitCode==0)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (string.IsNullOrWhiteSpace(mediaPath))
                            return new DownloadResult(false,false,"",[],
                                "yt-dlp finished without reporting a downloaded media file.",transferred);
                        var media=reportedMedia.ToArray();
                        if(selectedFragments>1)
                        {
                            var comparer=OperatingSystem.IsWindows()?StringComparer.OrdinalIgnoreCase:StringComparer.Ordinal;
                            if(media.Length!=selectedFragments || media.Select(Path.GetFullPath).Distinct(comparer).Count()!=selectedFragments)
                                return new DownloadResult(false,false,"",[],"yt-dlp did not produce every selected fragment as a separate media file.",transferred);
                            var reported=new HashSet<string>(media.Select(Path.GetFullPath),comparer);
                            string[] mediaExtensions=[".mp4",".m4v",".mov",".webm",".mkv",".mka",".mp3",".m4a",".aac",".wav",".flac",".opus",".ogg",".avi",".flv",".ts",".mpeg",".mpg",".3gp"];
                            foreach(string directory in Directory.EnumerateDirectories(stage,"*",SearchOption.AllDirectories))
                                if((File.GetAttributes(directory)&FileAttributes.ReparsePoint)!=0)
                                    throw new IOException("Staging contains a symlink directory.");
                            if(Directory.EnumerateFiles(stage,"*",SearchOption.AllDirectories).Any(path=>
                                mediaExtensions.Contains(Path.GetExtension(path),StringComparer.OrdinalIgnoreCase) && !reported.Contains(Path.GetFullPath(path))))
                                return new DownloadResult(false,false,"",[],"Staging contains media outside the complete selected-fragment manifest.",transferred);
                            mediaPath=media[0];
                        }
                        foreach(string path in media)
                        {
                            if(!OwnedFiles.Inside(stage,path))
                                return new DownloadResult(false,false,"",[],"Reported media escaped the owned staging directory.",transferred);
                            OwnedFiles.RegularOrAbsent(path);
                            if(!File.Exists(path) || new FileInfo(path).Length==0)
                                return new DownloadResult(false,false,"",[],"A reported media file is absent or empty.",transferred);
                            if(!options.AudioOnly && options.Container!="Auto" &&
                                !Path.GetExtension(path).Equals("."+options.Container,StringComparison.OrdinalIgnoreCase))
                                return new DownloadResult(false,false,"",[],"yt-dlp did not produce the requested container: "+options.Container,transferred);
                        }
                        string publishStage=stage;
                        if(selection?.IsPartial==true && (options.WriteSubs || options.AutoSubs || options.EmbedSubs))
                        {
                            item.State="Processing";item.ProgressDetail="Субтитры выбранных фрагментов";_changed(item);
                            var subtitles=new SubtitleTimelineService(DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg"),
                                DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"),
                                (start,stdout,stderr,token)=>RunProcessingToolAsync(item,start,stdout,stderr,token),()=>_pause.IsPaused);
                            await subtitles.ProcessAsync(stage,media,selection.EffectiveSections,subtitleManifests.ToArray(),options.EmbedSubs,ct,
                                selection.EffectiveSections.Select(selection.SectionBounds).ToArray()).ConfigureAwait(false);
                            Trace(item,JournalLevel.Information,"subtitle_timeline_ready",$"fragments={media.Length}");
                        }
                        if(selection?.IsMerged==true)
                        {
                            var merger=new FragmentMergeService(DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg"),
                                DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffprobe.exe":"ffprobe"),
                                (start,stdout,stderr,token)=>RunProcessingToolAsync(item,start,stdout,stderr,token),()=>_pause.IsPaused);
                            Trace(item,JournalLevel.Information,"fragment_merge_start",$"fragments={media.Length} allow_reencode={selection.AllowMergeReencode}");
                            var joined=await merger.MergeAsync(stage,media,selection.AllowMergeReencode,options.EmbedMetadata,options.EmbedChapters,ct,
                                (detail,processing)=>{item.State=processing?"Processing":"Merging";item.ProgressDetail=detail;item.Speed="";item.ETA="";_changed(item);}).ConfigureAwait(false);
                            publishStage=joined.PublishStage;mediaPath=joined.MediaPath;
                            Trace(item,JournalLevel.Information,"fragment_merge_ready",$"fragments={media.Length} reencoded={joined.Reencoded} duration={DownloadProgressTrace.Number(joined.DurationSeconds)}");
                        }
                        // Publication updates the row's manifest after each move,
                        // so even a failed rollback retains exact cleanup paths.
                        item.State="Publishing";item.Speed="";item.ETA="";_changed(item);
                        Trace(item,JournalLevel.Information,"publishing",$"attempt={attempt+1} quality={DownloadProgressTrace.Quoted(q)}");
                        (string Path,List<string> OwnedPaths) published;
                        lock(_completionGate)
                        {
                            ct.ThrowIfCancellationRequested();
                            owner.Publishing=true;
                        }
                        published=OwnedFiles.Publish(publishStage,outputRoot,mediaPath,
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
                    if (YtDlpOutputParser.FormatUnavailable(err) && q != qualities[^1])
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
            // Skip retains exactly this attempt's own partial data. Cancel,
            // Stop, failure and success keep their original cleanup semantics.
            bool keep;
            lock(_completionGate)
            {
                owner.Finalizing=true;
                keep=owner.Reason==AttemptStopReason.Skip && item.CleanupError.Length==0 && !item.PendingRemoval && item.ResumeState is not null;
            }
            try
            {
                if(!keep)
                {
                    if(item.ResumeState is not null)DownloadResumeStore.DeleteRetained(item);
                    else{OwnedFiles.DeleteStage(stage,item.RunDir);item.StageDir="";}
                }
            }
            catch(Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                DownloadCleanup.Failed(item,"Could not remove partial staging files: "+e.Message);
                _log(item.CleanupError);
            }
        }
    }

    private async Task<FragmentMergeToolOutput> RunProcessingToolAsync(DownloadItem item,ProcessStartInfo start,
        int stdout,int stderr,CancellationToken token)
    {
        await _pause.WaitForResumeAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        using var child=ManagedChildProcess.Start(start);
        _pause.Register(item.Url,child.Process);
        Trace(item,JournalLevel.Information,"processing_process_start",$"tool={DownloadProgressTrace.Quoted(Path.GetFileName(start.FileName))} pid={child.Process.Id}");
        using var resume=token.Register(()=>
        {
            try{if(!child.Process.HasExited)_pause.ResumeForCancellation(child.Process);}
            catch(InvalidOperationException){ }catch(System.ComponentModel.Win32Exception){ }
        });
        try
        {
            var captured=await BoundedProcessOutput.ReadAsync(child,stdout,stderr,token).ConfigureAwait(false);
            return new(child.Process.ExitCode,captured.StandardOutput,captured.StandardError);
        }
        finally{_pause.Unregister(item.Url);}
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
        token.ThrowIfCancellationRequested();
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
            else
            {
                // Add/Cancel may precede RunAsync (or a start can be aborted).
                // Still retire queued generations and join their actual cleanup
                // tasks, including the final callback, before disposing tokens.
                foreach(var attempt in _attempts.Values)
                    if(!attempt.Finished.Task.IsCompleted)Cancel(attempt.Row);
                await Task.WhenAll(_queuedCleanups.ToArray()).ConfigureAwait(false);
                IsRunning=false;
            }
        }
        finally { _stop.Dispose(); }
    }
}
