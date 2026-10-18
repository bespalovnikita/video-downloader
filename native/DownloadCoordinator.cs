using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
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
    private readonly ConcurrentDictionary<string,Process> _processes=new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string,byte> _queued=new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stop=new();
    private readonly DownloadOptions _options;
    private readonly Action<DownloadItem> _changed;
    private readonly Action<string> _log;
    private readonly string _stageRoot=Path.Combine(Path.GetTempPath(),"vd-native-"+Guid.NewGuid().ToString("N"));
    private volatile bool _paused;
    private readonly object _pauseGuard=new();
    private readonly Dictionary<int,DateTime> _suspended=new();

    private int _outstanding;
    private int _completed;
    private readonly object _resultGate=new();
    private readonly string _runStamp=DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture);
    public int Outstanding=>Volatile.Read(ref _outstanding);
    public int Completed=>Volatile.Read(ref _completed);
    public bool IsPaused=>_paused;
    public bool IsRunning {get;private set;}=true;

    [DllImport("ntdll.dll")] private static extern int NtSuspendProcess(IntPtr handle);
    [DllImport("ntdll.dll")] private static extern int NtResumeProcess(IntPtr handle);
    private const uint SnapshotProcess=0x00000002;
    [DllImport("kernel32.dll",SetLastError=true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr snapshot,ref ProcessEntry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size,Usage,ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId,Threads,ParentId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=260)] public string ExeFile;
    }
    private static IReadOnlyList<int> ProcessTree(int root)
    {
        var parents=new Dictionary<int,int>();
        var snapshot=CreateToolhelp32Snapshot(SnapshotProcess,0);
        if (snapshot!=new IntPtr(-1))
        {
            try
            {
                var item=new ProcessEntry{Size=(uint)Marshal.SizeOf<ProcessEntry>(),ExeFile=""};
                if(Process32FirstW(snapshot,ref item))
                    do { parents[(int)item.ProcessId]=(int)item.ParentId; } while(Process32NextW(snapshot,ref item));
            }
            finally {CloseHandle(snapshot);}
        }
        var discovered=new List<int>{root};
        var seen=new HashSet<int>{root};
        for(int i=0;i<discovered.Count;i++)
        {
            foreach(var (pid,parent) in parents)
                if(parent==discovered[i]&&seen.Add(pid))discovered.Add(pid);
        }
        return discovered;
    }
    private void SuspendProcess(int pid)
    {
        try
        {
            using var process=Process.GetProcessById(pid);
            if(!process.HasExited && NtSuspendProcess(process.Handle)==0)
                _suspended[pid]=process.StartTime.ToUniversalTime();
        }
        catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
    }
    private void ResumeSuspended()
    {
        foreach(var (pid,started) in _suspended)
        {
            try
            {
                using var process=Process.GetProcessById(pid);
                if(!process.HasExited && Math.Abs((process.StartTime.ToUniversalTime()-started).TotalSeconds)<2)
                    NtResumeProcess(process.Handle);
            }
            catch(ArgumentException){}catch(InvalidOperationException){}catch(System.ComponentModel.Win32Exception){}
        }
        _suspended.Clear();
    }
    public DownloadCoordinator(string ytDlp,AppPersistence data,DownloadOptions options,
        Action<DownloadItem> changed,Action<string> log)
    {
        _ytDlp=ytDlp;_data=data;_options=options;_changed=changed;_log=log;
        OwnedFiles.NoReparseAncestors(Path.Combine(_options.OutputDirectory,"__check__"));
        Directory.CreateDirectory(_stageRoot);
    }
    public bool Add(DownloadItem item)
    {
        if (!IsRunning || _stop.IsCancellationRequested || !_queued.TryAdd(item.Url,0)) return false;
        Interlocked.Increment(ref _outstanding);
        if (!_queue.Writer.TryWrite(item))
        {
            _queued.TryRemove(item.Url,out _);
            Interlocked.Decrement(ref _outstanding);
            return false;
        }
        return true;
    }
    public void Cancel(DownloadItem item)
    {
        if (_itemCancels.TryGetValue(item.Url,out var cts)) cts.Cancel();
        else item.Status="Interrupted"; // queued URL will be skipped by worker
    }
    public void Pause(bool pause)
    {
        lock(_pauseGuard)
        {
            if (_paused==pause)return;
            _paused=pause;
            if(!pause){ResumeSuspended();return;}
            foreach(var process in _processes.Values)
            {
                try
                {
                    if(process.HasExited)continue;
                    foreach(int pid in ProcessTree(process.Id))
                        if(!_suspended.ContainsKey(pid)) SuspendProcess(pid);
                }
                catch(InvalidOperationException){}
            }
        }
    }
    public void Stop()
    {
        if (_paused) Pause(false);
        _stop.Cancel();
        foreach(var src in _itemCancels.Values) { try { src.Cancel(); } catch(ObjectDisposedException){} }
        _queue.Writer.TryComplete();
    }
    public async Task RunAsync()
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
            try { Directory.Delete(_stageRoot,true); } catch(IOException){} catch(UnauthorizedAccessException){}
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
                if (item.Status=="Interrupted") continue;
                item.Slot="T"+slot;item.Status="Running";item.State="Starting";_changed(item);
                var result=await DownloadAsync(item,linked.Token).ConfigureAwait(false);
                if (result.Cancelled || linked.IsCancellationRequested)
                {
                    item.Status="Interrupted";item.State="Cancelled";
                }
                else if(result.Success)
                {
                    item.Path=result.Path;item.OwnedPaths=result.OwnedPaths.ToList();
                    item.Progress=100;item.Speed="";item.ETA="";
                    item.Status="Completed";item.State="Done";
                    try { _data.AppendSuccess(item.Url); }
                    catch(Exception e) { _log($"WARNING: Could not append successful URL to history: {e.Message}"); }
                    Interlocked.Increment(ref _completed);
                    WriteResult("success",$"{item.Url} | {item.Path}");
                }
                else
                {
                    item.Status="Failed";item.State="Failed";
                    item.RawError=result.Error;item.Speed="";item.ETA="";
                    WriteResult("error",$"{item.Url} | {result.Error}");
                }
                _changed(item);
            }
            catch (OperationCanceledException)
            {
                item.Status="Interrupted";item.State="Cancelled";_changed(item);
            }
            catch(Exception ex)
            {
                item.Status="Failed";item.State="Failed";item.RawError=ex.Message;_changed(item);
                WriteResult("error",$"{item.Url} | {ex.Message}");
                _log($"ERROR {item.Url}: {ex.Message}");
            }
            finally
            {
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
    private static readonly Regex Transfer = new(@"^\s*(?<value>\d+(?:\.\d+)?)\s*(?<unit>KiB|MiB|GiB|TiB|KB|MB|GB|B)?(?:/s)?\s*$",RegexOptions.IgnoreCase|RegexOptions.Compiled);
    public static double ParseSize(string raw)
    {
        var m=Transfer.Match(raw.Trim());
        if (!m.Success) return 0;
        if (!double.TryParse(m.Groups["value"].Value,NumberStyles.Float,CultureInfo.InvariantCulture,out double v)) return 0;
        string unit=m.Groups["unit"].Value.ToUpperInvariant();
        double factor=unit switch
        {
            "TIB"=>Math.Pow(1024,4),"GIB"=>Math.Pow(1024,3),"MIB"=>1024*1024,"KIB"=>1024,
            "GB"=>1e9,"MB"=>1e6,"KB"=>1000,_=>1
        };
        return v*factor;
    }
    private static bool IsTransient(string s) =>
        Regex.IsMatch(s,@"timeout|timed out|connection reset|connection aborted|429|502|503|504|server error|temporarily unavailable",
            RegexOptions.IgnoreCase|RegexOptions.CultureInvariant);
    private static bool FormatUnavailable(string s) =>
        s.Contains("Requested format is not available",StringComparison.OrdinalIgnoreCase);
    private string FormatSelector(string quality)
    {
        string code=_options.Codec switch {"H264"=>"avc1","VP9"=>"vp9","AV1"=>"av01",_=>""};
        string filter=quality=="best" ? "" : $"[height<={quality}]";
        if (code.Length==0) return $"bv*{filter}+ba/b{filter}";
        return $"bv*{filter}[vcodec^={code}]+ba/b{filter}[vcodec^={code}]/bv*{filter}+ba/b{filter}";
    }
    private IEnumerable<string> QualityLevels() => _options.Quality switch
    {
        "2160"=>["2160","1440","1080","720"],
        "1440"=>["1440","1080","720"],
        "1080"=>["1080","720"],
        "720"=>["720"],
        _=>["best"]
    };
    private IEnumerable<string> YtDlpArgs(string url,string stage,string quality)
    {
        string[] standard=["--encoding","utf-8","--newline","--progress","--no-color",
            "--impersonate","chrome","-N",_options.FragmentCount.ToString(CultureInfo.InvariantCulture),
            "-o",Path.Combine(stage,_options.FilenameTemplate),
            "--progress-template","download:__VD_PROGRESS__|%(progress._percent_str)s|%(progress._speed_str)s|%(progress._eta_str)s|%(progress.downloaded_bytes)s|%(progress.total_bytes,progress.total_bytes_estimate)s|%(info.id)s:%(info.format_id)s",
            "--print","before_dl:__VD_TITLE__:%(title)s",
            "--print","after_move:__VD_FILE__:%(filepath)s"];
        var args=new List<string>(standard);
        // Explicitly enable file:// only for local offline integration fixtures;
        // GUI URL validation still restricts user-entered links to HTTP/HTTPS.
        if (Uri.TryCreate(url,UriKind.Absolute,out var source) && source.Scheme=="file")
            args.Add("--enable-file-urls");
        if (File.Exists(_options.Cookies)) args.AddRange(["--cookies",_options.Cookies]);
        if (_options.RateLimit.Length>0 && _options.RateLimit!="Unlimited") args.AddRange(["--limit-rate",_options.RateLimit]);
        if (_options.Sponsor) args.AddRange(["--sponsorblock-remove","sponsor"]);
        if (_options.AudioOnly) args.AddRange(["-x","--audio-format","mp3"]);
        else
        {
            args.AddRange(["-f",FormatSelector(quality)]);
            if (_options.Container!="Auto") args.AddRange(["--merge-output-format",_options.Container.ToLowerInvariant()]);
        }
        if (_options.WriteSubs || _options.EmbedSubs) args.Add("--write-subs");
        if (_options.WriteSubs || _options.EmbedSubs || _options.AutoSubs)
            args.AddRange(["--sub-langs",_options.SubtitleLangs]);
        if (_options.AutoSubs) args.Add("--write-auto-subs");
        if (_options.EmbedSubs) args.Add("--embed-subs");
        if (_options.EmbedThumbnail) args.AddRange(["--write-thumbnail","--embed-thumbnail"]);
        if (_options.EmbedMetadata) args.Add("--embed-metadata");
        if (_options.EmbedChapters) args.Add("--embed-chapters");
        args.Add(url);
        return args;
    }

    private async Task<DownloadResult> DownloadAsync(DownloadItem item,CancellationToken ct)
    {
        string stage=Path.Combine(_stageRoot,Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(stage);
        string mediaPath="";
        var streamed=new Dictionary<string,long>(StringComparer.Ordinal);
        long transferred=0;
        try
        {
            OwnedFiles.ValidateTemplate(_options.FilenameTemplate);
            foreach(var q in QualityLevels())
            {
                for (int attempt=0;attempt<=3;attempt++)
                {
                    ct.ThrowIfCancellationRequested();
                    var errors=new ConcurrentQueue<string>();
                    void Line(string line)
                    {
                        if (line.StartsWith("__VD_TITLE__:",StringComparison.Ordinal))
                        {
                            item.Title=line[13..].Trim();_changed(item);return;
                        }
                        if (line.StartsWith("__VD_FILE__:",StringComparison.Ordinal))
                        {
                            mediaPath=line[12..].Trim();return;
                        }
                        if (line.StartsWith("__VD_PROGRESS__|",StringComparison.Ordinal))
                        {
                            var segments=line.Split('|',7);
                            if (segments.Length>=7)
                            {
                                var percentText=Regex.Replace(segments[1],@"[\x1B\[][0-9;]*m","").Replace("%","").Trim();
                                if (!double.TryParse(percentText,NumberStyles.Float,CultureInfo.InvariantCulture,out double pct)) pct=0;
                                long.TryParse(segments[4],NumberStyles.Integer,CultureInfo.InvariantCulture,out long bytes);
                                long.TryParse(segments[5],NumberStyles.Integer,CultureInfo.InvariantCulture,out long total);
                                var key=segments[6];
                                streamed.TryGetValue(key,out long previous);
                                if (bytes>previous) { transferred+=bytes-previous;streamed[key]=bytes; }
                                item.Progress=Math.Clamp(pct,0,100);item.Speed=segments[2].Trim();
                                item.Downloaded=DownloadFormat.Bytes(bytes)+(total>0 ? " / "+DownloadFormat.Bytes(total) : "");
                                item.ETA=segments[3].Trim();item.TransferredBytes=transferred;
                                if (item.Progress>0) item.State=$"{item.Progress:0.0}%";
                                _changed(item);
                            }
                            return;
                        }
                        if (line.StartsWith("ERROR:",StringComparison.OrdinalIgnoreCase)
                            || line.StartsWith("WARNING:",StringComparison.OrdinalIgnoreCase)
                            || line.Contains("connection",StringComparison.OrdinalIgnoreCase))
                            errors.Enqueue(line);
                        if (line.Contains("[Merger]",StringComparison.Ordinal)) { item.State="Merging";_changed(item); }
                    }
                    int exitCode=await RunProcessAsync(item.Url,YtDlpArgs(item.Url,stage,q),Line,ct).ConfigureAwait(false);
                    ct.ThrowIfCancellationRequested();
                    if (exitCode==0)
                    {
                        var published=OwnedFiles.Publish(stage,_options.OutputDirectory,
                            mediaPath.Length>0 ? mediaPath : null);
                        if (ct.IsCancellationRequested)
                        {
                            OwnedFiles.DeleteOwned(_options.OutputDirectory,published.OwnedPaths);
                            ct.ThrowIfCancellationRequested();
                        }
                        return new DownloadResult(true,false,published.Path,published.OwnedPaths,"",transferred);
                    }
                    string err=string.Join(" | ",errors.TakeLast(12));
                    if (err.Length==0) err="yt-dlp exit code "+exitCode;
                    if (FormatUnavailable(err) && q != QualityLevels().Last())
                    {
                        _log($"Format unavailable at {q}, trying lower quality."); break;
                    }
                    if (IsTransient(err) && attempt<3)
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
            try { if (Directory.Exists(stage)) Directory.Delete(stage,true); }
            catch(IOException e) { _log("Could not remove partial staging files: "+e.Message); }
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
        while(_paused) await Task.Delay(100,token).ConfigureAwait(false);
        using var process=new Process{StartInfo=psi};
        if (!process.Start()) throw new IOException("Failed to start yt-dlp");
        _processes[url]=process;
        lock(_pauseGuard)
        {
            if(_paused) SuspendProcess(process.Id);
        }
        using var reg=token.Register(()=>
        {
            try { if (!process.HasExited) { if (_paused) NtResumeProcess(process.Handle); process.Kill(true); } }
            catch (InvalidOperationException) { } catch(System.ComponentModel.Win32Exception) { }
        });
        async Task Pump(StreamReader stream)
        {
            while (await stream.ReadLineAsync().ConfigureAwait(false) is { } line) onLine(line);
        }
        try
        {
            await Task.WhenAll(Pump(process.StandardOutput),Pump(process.StandardError),process.WaitForExitAsync())
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return process.ExitCode;
        }
        finally { _processes.TryRemove(url,out _); }
    }
    public async ValueTask DisposeAsync()
    {
        Stop();
        _stop.Dispose();
        await Task.CompletedTask;
    }
}
