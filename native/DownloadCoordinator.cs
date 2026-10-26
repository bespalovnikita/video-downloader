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
        Action<DownloadItem> changed,Action<string> log)
    {
        _ytDlp=ytDlp;_data=data;_options=options;_changed=changed;_log=log;
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
        if (!_queue.Writer.TryWrite(item))
        {
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
    public void Pause(bool pause)=>_pause.SetPaused(pause);
    public void Stop()
    {
        if (_pause.IsPaused) Pause(false);
        _stop.Cancel();
        foreach(var src in _itemCancels.Values) { try { src.Cancel(); } catch(ObjectDisposedException){} }
        _queue.Writer.TryComplete();
    }
    public Task RunAsync() => _runTask ??= RunCoreAsync();
    private async Task RunCoreAsync()
    {
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
                        item.Progress=100;item.Speed="";item.ETA="";
                        item.Status="Completed";item.State="Done";
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
        try
        {
            OwnedFiles.ValidateTemplate(_options.FilenameTemplate);
            foreach(var q in _arguments.QualityLevels())
            {
                for (int attempt=0;attempt<=3;attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var errors=new ConcurrentQueue<string>();
                    void Line(string line)
                    {
                        switch(YtDlpOutputParser.Parse(line))
                        {
                            case YtDlpOutputEvent.Title title:
                                item.Title=title.Value;_changed(item);break;
                            case YtDlpOutputEvent.MediaPath media:
                                mediaPath=media.Value;break;
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
                                    item.Progress=Math.Clamp(progress.Percent,0,100);item.Speed=progress.Speed;
                                    item.Downloaded=DownloadFormat.Bytes(bytes)+(progress.TotalBytes>0 ? " / "+DownloadFormat.Bytes(progress.TotalBytes) : "");
                                    item.ETA=progress.Eta;item.TransferredBytes=transferred;
                                    if (item.Progress>0) item.State=$"{item.Progress:0.0}%";
                                }
                                _changed(item);break;
                            case YtDlpOutputEvent.Diagnostic diagnostic:
                                if(diagnostic.Error is {} error) errors.Enqueue(error);
                                if(diagnostic.Merging) { item.State="Merging";_changed(item); }
                                break;
                        }
                    }
                    int exitCode=await RunProcessAsync(item.Url,_arguments.Build(item.Url,stage,q),Line,ct).ConfigureAwait(false);
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
                        var published=OwnedFiles.Publish(stage,outputRoot,mediaPath,
                            paths=>item.CleanupPaths=paths.ToList(),renameCollisions:true);
                        if (ct.IsCancellationRequested || _cancelled.ContainsKey(item.Url))
                        {
                            return new DownloadResult(false,true,published.Path,published.OwnedPaths,"",transferred);
                        }
                        return new DownloadResult(true,false,published.Path,published.OwnedPaths,"",transferred);
                    }
                    string err=string.Join(" | ",errors.TakeLast(12));
                    if (err.Length==0) err="yt-dlp exit code "+exitCode;
                    if (YtDlpOutputParser.FormatUnavailable(err) && q != _arguments.QualityLevels().Last())
                    {
                        _log($"Format unavailable at {q}, trying lower quality."); break;
                    }
                    if (YtDlpOutputParser.IsTransient(err) && attempt<3)
                    {
                        int delay=Math.Min(60,5*(1<<attempt));
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

    private async Task<int> RunProcessAsync(string url,IEnumerable<string> args,Action<string> onLine,CancellationToken token)
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
        _pause.Register(url,process);
        using var reg=token.Register(()=>
        {
            try { if (!process.HasExited) { _pause.ResumeForCancellation(process); process.Kill(true); } }
            catch (InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
        });
        async Task Pump(StreamReader stream)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
        }
        try
        {
            await Task.WhenAll(Pump(child.StandardOutput),Pump(child.StandardError),process.WaitForExitAsync())
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally { _pause.Unregister(url); }
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
