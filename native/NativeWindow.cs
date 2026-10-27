using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Forms=System.Windows.Forms;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow : IDisposable
{
    private readonly Window _window;
    private readonly QueueSessionController _queueSession=new();
    private readonly AppPersistence _storage;
    private readonly PreviewController _preview;
    private readonly DispatcherTimer _tick=new(){Interval=TimeSpan.FromMilliseconds(400)};
    private readonly WindowLifecycleController _lifecycle;
    private readonly InputIngressController _ingress;
    private readonly string _ytDlp;
    private bool _disposed;
    private bool _closing;
    private bool _closeReady;
    private string _lastSavedSnapshot="";
    private DownloadItem? _dragged;
    private System.Windows.Point _dragPoint;
    public Dispatcher Dispatcher=>_window.Dispatcher;
    internal PreviewController Preview=>_preview;
    internal QueueSessionController SessionState=>_queueSession;
    private T F<T>(string name) where T:class => (T)(_window.FindName(name)
        ?? throw new InvalidOperationException($"Missing WPF control: {name}"));
    private TextBox B(string name)=>F<TextBox>(name);
    private ComboBox C(string name)=>F<ComboBox>(name);
    private CheckBox K(string name)=>F<CheckBox>(name);
    private TextBlock T(string name)=>F<TextBlock>(name);
    private DataGrid Q=>F<DataGrid>("QueueGrid");
    private DataGrid D=>F<DataGrid>("DownloadGrid");
    private static bool Check(CheckBox c)=>c.IsChecked==true;
    private static string Pick(ComboBox c)=>c.SelectedItem?.ToString()??c.Text;

    public NativeWindow(Window window,AppPersistence? persistence=null)
        : this(window,persistence,DependencyCenter.Executable("yt-dlp.exe")) { }

    // The isolated UI test uses a metadata fixture process through the same
    // production worker. Normal startup always resolves the packaged yt-dlp.
    internal NativeWindow(Window window,AppPersistence? persistence,string ytDlpExecutable)
    {
        _window=window;
        _storage=persistence??new AppPersistence();
        _ytDlp=ytDlpExecutable;
        Q.ItemsSource=SessionState.Queue;
        D.ItemsSource=SessionState.Downloads;
        foreach(var (control,values) in new (string,string[])[]{
            ("QualityCombo",["Best","2160","1440","1080","720"]),
            ("CodecCombo",["Auto","H264","VP9","AV1"]),
            ("ContainerCombo",["Auto","MP4","MKV","WebM"]),
            ("RateLimitCombo",["Unlimited","1M","5M","10M","25M","50M"]),
            ("FragmentsCombo",["Auto","1","2","3","4","6","8"]),
            ("ProfileCombo",ProfileCatalog.DisplayNames)})
        {
            foreach(var v in values) C(control).Items.Add(v);
            C(control).SelectedIndex=0;
        }
        B("OutputBox").Text=new DownloadOptions().Output;
        _preview=new PreviewController(_window,_storage,_ytDlp,
            ()=>SessionState.Queue.Concat(SessionState.Downloads),SaveCheckpoint,Log);
        _lifecycle=new WindowLifecycleController(_window,()=>OpenFolder(B("OutputBox").Text));
        _ingress=new InputIngressController(_window,AddUrls,()=>Check(K("WatchClipboardCheck")),_lifecycle.Activate,Log);
        BindActions();
        K("ShowVideoTitlesCheck").Checked+=(_,_)=>SaveCheckpoint();
        K("ShowVideoTitlesCheck").Unchecked+=(_,_)=>SaveCheckpoint();
        _tick.Tick+=(_,_)=>Tick();
        _window.Closing+=OnClosing;
        Q.SelectionChanged+=(_,_)=>{ if(Q.SelectedItem is DownloadItem item)ShowPreview(item.Url); };
        D.SelectionChanged+=(_,_)=>SetDownloadActions();
        Q.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            _dragPoint=e.GetPosition(Q);
            _dragged=Q.SelectedItem as DownloadItem;
        };
        Q.MouseMove+=(_,e)=>
        {
            if (SessionState.Coordinator?.IsRunning==true || _dragged==null || e.LeftButton!=MouseButtonState.Pressed) return;
            if ((e.GetPosition(Q)-_dragPoint).Length>8)
            {
                try { DragDrop.DoDragDrop(Q,_dragged,DragDropEffects.Move); }
                finally { _dragged=null; }
            }
        };
        Q.Drop+=(_,e)=>
        {
            if (SessionState.Coordinator?.IsRunning==true) return;
            if (e.Data.GetDataPresent(typeof(DownloadItem)) && e.Data.GetData(typeof(DownloadItem)) is DownloadItem item)
            {
                // Selection normally remains on the dragged item. Resolve the
                // actual row under the drop pointer, not Q.SelectedItem.
                System.Windows.DependencyObject? visual=e.OriginalSource as System.Windows.DependencyObject;
                while(visual!=null && visual is not DataGridRow)
                    visual=System.Windows.Media.VisualTreeHelper.GetParent(visual);
                var target=(visual as DataGridRow)?.Item as DownloadItem;
                if(target!=null && item!=target)MoveItem(item,SessionState.Queue.IndexOf(target));
                e.Handled=true;
            }
        };
        TryRestore();
        SessionState.SetOptions(ReadOptions());
        Reindex();
        RefreshDependencyLabel();
        UpdateState();
        _tick.Start();_ingress.Start();
    }
    private void Tick()
    {
        UpdateState();
        if ((DateTime.UtcNow.Second%2)==0) SaveCheckpoint();
    }
    private DownloadItem[] CurrentSessionRows()=>SessionState.CurrentRows();
    private void UpdateState()
    {
        if (_disposed) return;
        // Do not count successful downloads from a previous session again.
        // Live additions join this set; removed URLs leave it.
        var metrics=SessionState.ReadMetrics();
        bool running=metrics.Running,paused=metrics.Paused;
        T("HeaderStats").Text=$"{metrics.Active} active · {DownloadFormat.Speed(metrics.Speed)} · {metrics.Successful}/{metrics.Total}";
        F<ProgressBar>("OverallProgress").Value=metrics.Progress;
        T("SessionStats").Text=SessionState.StartedAt.HasValue
            ? $"Session: {metrics.Successful} OK · {metrics.Left} left · {metrics.Failed} errors · {DownloadFormat.Bytes(metrics.Transferred)} · {DateTime.Now-SessionState.StartedAt.Value:hh\\:mm\\:ss}"
            : $"Session: idle · {metrics.Total} URLs";
        string title=SessionState.StartedAt.HasValue
            ? DownloadFormat.Tray(metrics.Successful,metrics.Left,metrics.Failed,metrics.Speed,running,paused) : "Video Downloader | Ready";
        _lifecycle.UpdateStatus(title);
        F<Button>("StartButton").IsEnabled=!running && SessionState.Queue.Any(x=>x.Status!="Completed" && x.CanResume);
        F<Button>("StopButton").IsEnabled=running;
        F<Button>("PauseButton").IsEnabled=running;
        F<Button>("PauseButton").Content=paused?"Resume":"Pause";
        F<Button>("RetryFailedButton").IsEnabled=!running && SessionState.Queue.Any(x=>x.Status=="Failed");
        foreach(string name in new[]{"MoveUpButton","MoveDownButton","ClearQueueButton"})
            F<Button>(name).IsEnabled=!running;
        Q.AllowDrop=!running;
        if (running)
        {
            T("HeaderStatus").Text=paused?"● Paused":"● Downloading";
            T("HeaderStatus").Foreground=Brushes.LightSkyBlue;
        }
        else if (SessionState.StartedAt.HasValue)
        {
            T("HeaderStatus").Text=SessionState.Stopping?"● Stopped":metrics.Failed==0?"● Completed":"● Completed with errors";
            T("HeaderStatus").Foreground=SessionState.Stopping?Brushes.Orange:metrics.Failed==0?Brushes.LightGreen:Brushes.Tomato;
        }
        else
        {
            T("HeaderStatus").Text="● Ready";
            T("HeaderStatus").Foreground=Brushes.LightGray;
        }
        if(metrics.ActiveChanged) { Q.Items.Refresh(); D.Items.Refresh(); }
    }
    private void OnRowChanged(DownloadItem item)
    {
        _=Dispatcher.BeginInvoke(()=>
        {
            if (_disposed) return;
            if (item.Status=="Completed" && item.AttemptFinished && item.CleanupError.Length==0)
                RecordSubscriptionSuccess(item.Url);
            if (!SessionState.Queue.Contains(item)) return;
            if (item.PendingRemoval && item.AttemptFinished && item.CleanupError.Length==0)
            {
                try { DownloadCleanup.Retry(item);ForgetRow(item);Reindex();return; }
                catch(Exception e) { Log("Removal failed for "+item.Url+": "+e.Message); }
            }
            if (SessionState.RecordRowChanged(item))
            {
                Log($"{item.Status}: {item.Url}"+(item.RawError.Length>0?" | "+item.RawError:""));
                SaveCheckpoint();
                if (item.CleanupError.Length>0)
                    MessageBox.Show(_window,"Cleanup failed. The row and paths were kept; use Remove to retry.\n"+
                        item.CleanupError,"Removal blocked",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            UpdateState();
        });
    }
    private void Log(string message)
    {
        if (_disposed) return;
        var box=B("LogBox");
        box.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        // Limit the log to avoid excessive WPF memory usage.
        if (box.LineCount>3000) box.Text=string.Join(Environment.NewLine,box.Text.Split('\n').TakeLast(1500));
        box.ScrollToEnd();
    }
    private DownloadOptions ReadOptions() => new()
    {
        Profile=ProfileCatalog.ToId(Pick(C("ProfileCombo"))),Quality=Pick(C("QualityCombo")),Codec=Pick(C("CodecCombo")),
        Container=Pick(C("ContainerCombo")),RateLimit=C("RateLimitCombo").Text,
        Threads=B("ThreadsBox").Text,Fragments=Pick(C("FragmentsCombo")),
        FilenameTemplate=B("FilenameTemplateBox").Text,Output=B("OutputBox").Text,
        Cookies=B("CookiesBox").Text,SubtitleLangs=B("SubtitleLangsBox").Text,
        WriteSubs=Check(K("WriteSubsCheck")),AutoSubs=Check(K("AutoSubsCheck")),EmbedSubs=Check(K("EmbedSubsCheck")),
        EmbedThumbnail=Check(K("EmbedThumbnailCheck")),EmbedMetadata=Check(K("EmbedMetadataCheck")),
        EmbedChapters=Check(K("EmbedChaptersCheck")),Archive=Check(K("ArchiveCheck")),
        Sponsor=Check(K("SponsorCheck")),AudioOnly=Check(K("AudioOnlyCheck")),
        WatchClipboard=Check(K("WatchClipboardCheck")),ShowVideoTitles=Check(K("ShowVideoTitlesCheck"))
    };
    private void ApplyOptions(DownloadOptions o)
    {
        foreach(var (name,value) in new[]{
            ("ProfileCombo",ProfileCatalog.Display(o.Profile)),("QualityCombo",o.Quality),("CodecCombo",o.Codec),
            ("ContainerCombo",o.Container),("FragmentsCombo",o.Fragments)})
            if (C(name).Items.Contains(value)) C(name).SelectedItem=value;
        C("RateLimitCombo").Text=o.RateLimit;
        foreach(var(name,value)in new[]{
            ("ThreadsBox",o.Threads),("FilenameTemplateBox",o.FilenameTemplate),
            ("OutputBox",o.Output),("CookiesBox",o.Cookies),("SubtitleLangsBox",o.SubtitleLangs)})
            B(name).Text=value;
        foreach(var(name,value)in new[]{
            ("WriteSubsCheck",o.WriteSubs),("AutoSubsCheck",o.AutoSubs),("EmbedSubsCheck",o.EmbedSubs),
            ("EmbedThumbnailCheck",o.EmbedThumbnail),("EmbedMetadataCheck",o.EmbedMetadata),
            ("EmbedChaptersCheck",o.EmbedChapters),("ArchiveCheck",o.Archive),
            ("SponsorCheck",o.Sponsor),("AudioOnlyCheck",o.AudioOnly),("WatchClipboardCheck",o.WatchClipboard),
            ("ShowVideoTitlesCheck",o.ShowVideoTitles)})
            K(name).IsChecked=value;
    }
    private void TryRestore()
    {
        _restoringSession=true;
        try
        {
            var previous=_storage.Restore();
            if (previous==null) return;
            if (previous.LegacyEngineWasActive || AppPersistence.LegacyEngineIsAlive(previous.Engine))
            {
                _checkpointReadOnly=true;
                _legacyEngineBlocked=true;
                MessageBox.Show(_window,
                    "The previous PowerShell download engine is still running.\n"+
                    "Stop the old downloader first, then restart the C# version to restore its latest results.\n"+
                    "The existing checkpoint has been left unchanged.",
                    "Old download process still active",MessageBoxButton.OK,MessageBoxImage.Warning);
                return;
            }
            // Settings belong to the application even when the queue is empty
            // or the user chooses not to restore its rows.
            ApplyOptions(previous.Settings);
            if (previous.Queue.Count==0) return;
            if (MessageBox.Show(_window,$"Restore previous queue ({previous.Queue.Count} URLs)?\nCompleted downloads will not restart automatically.",
                    "Restore previous session",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
            RestoreSubscriptionQueueTitles(previous.Queue);
            foreach(var item in previous.Queue)
            {
                SessionState.TryAdd(item);
                RequestRowPreview(item);
            }
            Log($"Restored {SessionState.Queue.Count} URLs. Downloads will not restart automatically.");
        }
        catch(Exception e)
        {
            // A modal error dialog pumps dispatcher messages too.
            _checkpointReadOnly=true;
            MessageBox.Show(_window,"Cannot read previous session: "+e.Message+
               "\nThe existing checkpoint has not been overwritten.","Session recovery error",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally { _restoringSession=false; }
    }
    private bool _restoringSession;
    private bool _checkpointReadOnly;
    private bool _legacyEngineBlocked;
    private void SaveCheckpoint()
    {
        if (_restoringSession || _checkpointReadOnly || _disposed) return;
        try
        {
            // Save on meaningful changes, not every progress tick.
            var fingerprint=System.Text.Json.JsonSerializer.Serialize(SessionState.Queue.Select(i=>new{
                i.Url,i.Title,i.Status,i.Path,i.Order,i.OwnedPaths,i.OutputRoot,i.RunDir,i.StageDir,
                i.CleanupPaths,i.CleanupError,i.PendingRemoval,i.AttemptFinished,i.DeferredToNextStart,
                i.SubscriptionId,i.SubscriptionName
            }),AppPersistence.Json)+
                ":"+System.Text.Json.JsonSerializer.Serialize(ReadOptions(),AppPersistence.Json)+":"+SessionState.Queue.Count;
            if (fingerprint==_lastSavedSnapshot) return;
            _storage.Save(SessionState.Queue,ReadOptions());_lastSavedSnapshot=fingerprint;
        }
        catch(Exception e)
        {
            _checkpointReadOnly=true;Log("Cannot save session checkpoint: "+e.Message);
        }
    }
    private enum DuplicateChoice{Yes,No,YesAll,NoAll,Cancel}
    private DuplicateChoice AskDuplicate(string url,DateTimeOffset? completed,int i,int total)
    {
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","DuplicateDownloadDialog.xaml"));
        using var reader=XmlReader.Create(stream);
        var dialog=(Window)XamlReader.Load(reader);
        dialog.Owner=_window;
        return DisplayDuplicate(dialog,url,completed,i,total);
    }
    private static DuplicateChoice DisplayDuplicate(Window dialog,string url,DateTimeOffset? completed,int index,int count)
    {
        ((TextBlock)dialog.FindName("CounterLabel")!).Text=$"Previously downloaded URL ({index}/{count})";
        ((TextBlock)dialog.FindName("CompletedAtLabel")!).Text=completed.HasValue
            ? "Last downloaded: "+completed.Value.LocalDateTime.ToString("g",CultureInfo.CurrentCulture)
            : "Last download date unknown";
        ((TextBox)dialog.FindName("DuplicateUrl")!).Text=url;
        var choice=DuplicateChoice.Cancel;
        foreach(var(name,value) in new[]{
            ("YesButton",DuplicateChoice.Yes),("NoButton",DuplicateChoice.No),
            ("YesAllButton",DuplicateChoice.YesAll),("NoAllButton",DuplicateChoice.NoAll),
            ("CancelButton",DuplicateChoice.Cancel)})
            ((Button)dialog.FindName(name)!).Click+=(_,_)=>{choice=value;dialog.Close();};
        dialog.ShowDialog();
        return choice;
    }
    private List<DownloadItem>? ConfirmDuplicates(List<DownloadItem> candidate,bool removeRejected)
    {
        Dictionary<string,DateTimeOffset?> history;
        try { history=_storage.ReadHistory(); }
        catch(Exception e)
        {
            MessageBox.Show(_window,"History cannot be read; start was blocked to protect the records:\n"+e.Message,
                "History error",MessageBoxButton.OK,MessageBoxImage.Warning);
            return null;
        }
        int total=candidate.Count(x=>history.ContainsKey(x.Url)),index=0;
        var approved=new List<DownloadItem>();
        var rejected=new List<DownloadItem>();
        DuplicateChoice? all=null;
        foreach(var item in candidate)
        {
            if(!history.TryGetValue(item.Url,out var when)){approved.Add(item);continue;}
            index++;
            var choice=all??AskDuplicate(item.Url,when,index,total);
            if (choice==DuplicateChoice.Cancel) return null;
            if (choice==DuplicateChoice.YesAll) {all=DuplicateChoice.Yes;choice=DuplicateChoice.Yes;}
            if (choice==DuplicateChoice.NoAll) {all=DuplicateChoice.No;choice=DuplicateChoice.No;}
            if (choice==DuplicateChoice.Yes)approved.Add(item);
            else rejected.Add(item);
        }
        if(removeRejected)
        {
            foreach(var item in rejected)SessionState.RejectQueueEntry(item);
            Reindex();
        }
        return approved;
    }
    private void Reindex()
    {
        int n=SessionState.Reindex();
        T("QueueCount").Text=n+" URLs";
        UpdateState();SaveCheckpoint();
    }
    private static IReadOnlyList<string> ParseUrls(string text)=>InputIngressController.ParseUrls(text);
    private void AddUrls(IEnumerable<string> urls,bool clipboard=false,bool selectNew=false)
    {
        if (_closing || _disposed) return;
        var rows=urls.Where(AppPersistence.ValidUrl).Distinct(StringComparer.Ordinal)
            .Where(url=>!SessionState.Queue.Any(i=>string.Equals(i.Url,url,StringComparison.Ordinal)))
            .Select(url=>new DownloadItem{Url=url}).ToList();
        if(rows.Count==0)return;
        var session=SessionState.Coordinator;
        bool active=session?.IsRunning==true;
        if(active)
        {
            var checkedRows=ConfirmDuplicates(rows,false);
            if(checkedRows==null)return;
            rows=checkedRows;
        }
        // A modal duplicate dialog pumps the dispatcher: IPC/clipboard may
        // have added these URLs, completed the session, or closed the window.
        if (_closing || _disposed) return;
        var added=new List<DownloadItem>();
        foreach(var row in rows)
        {
            if (!SessionState.TryAdd(row))continue;
            added.Add(row);
            RequestRowPreview(row,clipboard&&active);
            if(active && !SessionState.TryJoinActive(session,row))
            {
                Log("URL stays queued for next Start: "+row.Url);
            }
        }
        if(selectNew && added.Count>0)
        {
            Q.SelectedItem=added[0];
            ShowPreview(added[0].Url);
        }
        Reindex();
        T("ClipboardHint").Text=added.Count+" URL(s) added.";
    }
    private void RequestRowPreview(DownloadItem row,bool priority=false)=>_preview.RequestRow(row,priority);
    private void ShowPreview(string url)=>_preview.Show(url);
    private void PollClipboard()=>_ingress.PollClipboard();
    private async Task StartAsync(IEnumerable<DownloadItem>? custom=null)
    {
        if (_closing || _disposed) return;
        if (_legacyEngineBlocked)
        {
            MessageBox.Show(_window,
                "The previous PowerShell downloader is still active. Stop it and restart this app before starting a new session.",
                "Native migration blocked",MessageBoxButton.OK,MessageBoxImage.Warning);
            return;
        }
        if(SessionState.Coordinator?.IsRunning==true) return;
        var pending=(custom??SessionState.Queue.Where(i=>i.Status!="Completed")).Where(i=>i.CanResume).Distinct().ToList();
        if(pending.Count==0)return;
        var options=ReadOptions();
        try
        {
            DependencyCenter.ValidateDownloadTools(_ytDlp);
            if(!string.IsNullOrWhiteSpace(options.Cookies)&&!File.Exists(options.Cookies))
                throw new FileNotFoundException("Cookie file is missing.",options.Cookies);
            if(string.IsNullOrWhiteSpace(options.Output))throw new ArgumentException("Choose an output directory.");
            OwnedFiles.ValidateTemplate(options.FilenameTemplate);
            var approved=ConfirmDuplicates(pending,true);
            if(approved==null||approved.Count==0)return;
            options.PinOutputDirectory();
            OwnedFiles.NoReparseAncestors(Path.Combine(options.OutputDirectory,"__validation__"));
            var outputRoots=approved.ToDictionary(row=>row,row=>
            {
                string output=SubscriptionNames.ResolveOutputDirectory(options.OutputDirectory,row.SubscriptionName);
                OwnedFiles.NoReparseAncestors(Path.Combine(output,"__validation__"));
                return output;
            });
            Directory.CreateDirectory(options.OutputDirectory);
            SessionState.SetOptions(options);
            // Validate retained staging before resetting any attempt or ownership.
            foreach(var row in approved.Where(row=>row.StageDir.Length>0))
            {
                try { OwnedFiles.DeleteStage(row.StageDir,row.RunDir);row.StageDir=""; }
                catch(Exception e) {DownloadCleanup.Failed(row,e.Message);SaveCheckpoint();throw;}
            }
            foreach(var row in approved)
            {
                row.Status="Queued";row.State="Queued";row.RawError="";row.Progress=0;
                row.Speed="";row.Downloaded="";row.ETA="";row.TransferredBytes=0;
                // An explicit retry is a new attempt; previous successful files
                // remain on disk and must never become this attempt's ownership.
                row.Path="";row.OwnedPaths.Clear();row.CleanupPaths.Clear();
                row.OutputRoot=outputRoots[row];row.RunDir="";
                row.AttemptFinished=false;row.DeferredToNextStart=false;
            }
            var session=new DownloadCoordinator(_ytDlp,_storage,options,OnRowChanged,
                line=>Dispatcher.BeginInvoke(()=>Log(line)));
            SessionState.Begin(session,options,approved);
            UpdateState();SaveCheckpoint();
            Log($"Started {approved.Count} URL(s), parallel={options.Parallel}, quality={options.Quality}.");
            var running=session.RunAsync();
            SessionState.TrackTask(running);
            try { await running; }
            finally
            {
                await session.DisposeAsync();
                SessionState.End(session);
                // Results are already on row objects; do not touch other session rows.
                UpdateState();SaveCheckpoint();
                var finishedRows=CurrentSessionRows();
                var done=finishedRows.Count(x=>x.Status=="Completed");
                var bad=finishedRows.Count(x=>x.Status=="Failed");
                var remaining=finishedRows.Count(x=>x.Status is not ("Completed" or "Failed"));
                Log($"Session complete: {done} OK, {bad} failed.");
                if(!SessionState.Stopping && !_disposed)
                {
                    _lifecycle.NotifyCompletion(done,remaining,bad);
                }
            }
        }
        catch(Exception e)
        {
            Log("Cannot start download: "+e.Message);
            MessageBox.Show(_window,e.Message,"Start failed",MessageBoxButton.OK,MessageBoxImage.Error);
        }
    }
    private void CancelRow(DownloadItem row)
    {
        if (SessionState.Coordinator?.IsRunning==true && (row.Status=="Queued" || row.Status=="Running"))SessionState.Coordinator.Cancel(row);
    }
    private void RemoveRows(IEnumerable<DownloadItem> rows)
    {
        var selected=rows.Where(SessionState.Queue.Contains).Distinct().ToArray();
        if(selected.Length==0)return;
        if(selected.Any(x=>x.Status!="Queued") &&
            MessageBox.Show(_window,$"Remove {selected.Length} selected URL(s)?\nActive jobs will be stopped; finished files belonging to them will be permanently deleted.\nOther downloads continue.",
                "Delete downloads",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        foreach(var row in selected)
        {
            try
            {
                if (SessionState.Coordinator?.IsRunning==true && SessionState.Coordinator.IsPending(row))
                {
                    row.PendingRemoval=true;row.State="Cancelling — waiting for cleanup";
                    SessionState.Coordinator.Cancel(row);
                    continue;
                }
                DownloadCleanup.Retry(row);
                ForgetRow(row);
            }
            catch(Exception e)
            {
                Log("Removal failed for "+row.Url+": "+e.Message);
                MessageBox.Show(_window,e.Message,"Removal blocked",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }
        Reindex();
    }
    private void ForgetRow(DownloadItem row)
    {
        SessionState.Forget(row);
    }
    private void ClearRows()
    {
        foreach(var row in SessionState.Queue.ToArray())
            if (row.CanResume && row.StageDir.Length==0 && row.CleanupPaths.Count==0) ForgetRow(row);
        if (SessionState.Queue.Count>0)
            MessageBox.Show(_window,"Downloads awaiting cleanup were kept. Use Remove to retry.",
                "Cleanup pending",MessageBoxButton.OK,MessageBoxImage.Warning);
        Reindex();
    }
    private void MoveItem(DownloadItem item,int index)
    {
        if(SessionState.Move(item,index))Reindex();
    }
    public void ReceiveArguments(IEnumerable<string> args)=>_ingress.ReceiveArguments(args);
    private static IReadOnlyList<string> ReadInputFile(string file)=>InputIngressController.ReadInputFile(file);
}
