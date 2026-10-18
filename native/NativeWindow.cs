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
    private readonly ObservableCollection<DownloadItem> _queue=new();
    private readonly ObservableCollection<DownloadItem> _downloads=new();
    private readonly AppPersistence _storage;
    private readonly PreviewService _previews;
    private readonly DispatcherTimer _tick=new(){Interval=TimeSpan.FromMilliseconds(400)};
    private readonly DispatcherTimer _clipboardTick=new(){Interval=TimeSpan.FromMilliseconds(950)};
    private readonly Forms.NotifyIcon _tray=new();
    private readonly string _ytDlp=DependencyCenter.Executable("yt-dlp.exe");
    private readonly Dictionary<string,string> _finalStates=new(StringComparer.Ordinal);
    private readonly HashSet<string> _sessionUrls=new(StringComparer.Ordinal);
    private readonly Dictionary<string,long> _transferred=new(StringComparer.Ordinal);
    private DownloadCoordinator? _session;
    private Task? _sessionTask;
    private DownloadOptions _sessionOptions=new();
    private DateTime? _sessionStart;
    private bool _stopping;
    private bool _exitFromTray;
    private bool _disposed;
    private string _previewUrl="";
    private string _lastClipboard="";
    private string _lastSavedSnapshot="";
    private int _ok;
    private int _failed;
    private int _lastActive;
    private DownloadItem? _dragged;
    private System.Windows.Point _dragPoint;
    public Dispatcher Dispatcher=>_window.Dispatcher;
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
    {
        _window=window;
        _storage=persistence??new AppPersistence();
        Q.ItemsSource=_queue;
        D.ItemsSource=_downloads;
        foreach(var (control,values) in new (string,string[])[]{
            ("QualityCombo",["Best","2160","1440","1080","720"]),
            ("CodecCombo",["Auto","H264","VP9","AV1"]),
            ("ContainerCombo",["Auto","MP4","MKV","WebM"]),
            ("RateLimitCombo",["Unlimited","1M","5M","10M","25M","50M"]),
            ("FragmentsCombo",["Auto","1","2","3","4","6","8"]),
            ("ProfileCombo",["Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive"])})
        {
            foreach(var v in values) C(control).Items.Add(v);
            C(control).SelectedIndex=0;
        }
        B("OutputBox").Text=new DownloadOptions().Output;
        _previews=new PreviewService(_ytDlp,_storage,()=>B("CookiesBox").Text,
            data=>_window.Dispatcher.BeginInvoke(()=>OnPreviewReady(data)),
            message=>_window.Dispatcher.BeginInvoke(()=>Log(message)));
        SetupTray();
        BindActions();
        _tick.Tick+=(_,_)=>Tick();
        _clipboardTick.Tick+=(_,_)=>PollClipboard();
        _tick.Start();_clipboardTick.Start();
        _window.Closing+=OnClosing;
        _window.Drop+=WindowDrop;
        _window.DragOver+=(_,e)=>e.Effects=DragDropEffects.Copy;
        Q.SelectionChanged+=(_,_)=>{ if(Q.SelectedItem is DownloadItem item)ShowPreview(item.Url); };
        D.SelectionChanged+=(_,_)=>SetDownloadActions();
        Q.PreviewMouseLeftButtonDown+=(_,e)=>
        {
            _dragPoint=e.GetPosition(Q);
            _dragged=Q.SelectedItem as DownloadItem;
        };
        Q.MouseMove+=(_,e)=>
        {
            if (_session?.IsRunning==true || _dragged==null || e.LeftButton!=MouseButtonState.Pressed) return;
            if ((e.GetPosition(Q)-_dragPoint).Length>8)
            {
                try { DragDrop.DoDragDrop(Q,_dragged,DragDropEffects.Move); }
                finally { _dragged=null; }
            }
        };
        Q.Drop+=(_,e)=>
        {
            if (_session?.IsRunning==true) return;
            if (e.Data.GetDataPresent(typeof(DownloadItem)) && e.Data.GetData(typeof(DownloadItem)) is DownloadItem item)
            {
                var target=Q.SelectedItem as DownloadItem;
                if (target!=null && item!=target) MoveItem(item,_queue.IndexOf(target));
                e.Handled=true;
            }
        };
        TryRestore();
        _sessionOptions=ReadOptions();
        Reindex();
        RefreshDependencyLabel();
        UpdateState();
    }
    private void Tick()
    {
        UpdateState();
        if ((DateTime.UtcNow.Second%2)==0) SaveCheckpoint();
    }
    private void UpdateState()
    {
        if (_disposed) return;
        // Do not count successful downloads from a previous session again.
        // Live additions join this set; removed URLs leave it.
        var sessionRows=_sessionStart.HasValue
            ? _queue.Where(x=>_sessionUrls.Contains(x.Url)).ToArray()
            : _queue.ToArray();
        int active=sessionRows.Count(x=>x.Status=="Running");
        int left=sessionRows.Count(x=>x.Status is not ("Completed" or "Failed"));
        _ok=sessionRows.Count(x=>x.Status=="Completed");
        _failed=sessionRows.Count(x=>x.Status=="Failed");
        double speed=sessionRows.Where(x=>x.Status=="Running").Sum(x=>DownloadCoordinator.ParseSize(x.Speed));
        long downloaded=sessionRows.Sum(x=>x.TransferredBytes);
        int total=sessionRows.Length;
        double progress=total==0 ? 0 : sessionRows.Sum(x=>x.Status=="Completed"||x.Status=="Failed"?100:x.Progress)/(double)total;
        bool running=_session?.IsRunning==true;
        bool paused=running && _session!.IsPaused;
        T("HeaderStats").Text=$"{active} active · {DownloadFormat.Speed(speed)} · {_ok}/{total}";
        F<ProgressBar>("OverallProgress").Value=progress;
        T("SessionStats").Text=_sessionStart.HasValue
            ? $"Session: {_ok} OK · {left} left · {_failed} errors · {DownloadFormat.Bytes(downloaded)} · {DateTime.Now-_sessionStart.Value:hh\\:mm\\:ss}"
            : $"Session: idle · {total} URLs";
        string title=_sessionStart.HasValue
            ? DownloadFormat.Tray(_ok,left,_failed,speed,running,paused) : "Video Downloader | Ready";
        if (_tray.Text!=title)_tray.Text=title;
        F<Button>("StartButton").IsEnabled=!running && _queue.Any(x=>x.Status!="Completed");
        F<Button>("StopButton").IsEnabled=running;
        F<Button>("PauseButton").IsEnabled=running;
        F<Button>("PauseButton").Content=paused?"Resume":"Pause";
        F<Button>("RetryFailedButton").IsEnabled=!running && _queue.Any(x=>x.Status=="Failed");
        foreach(string name in new[]{"MoveUpButton","MoveDownButton","ClearQueueButton"})
            F<Button>(name).IsEnabled=!running;
        Q.AllowDrop=!running;
        if (running)
        {
            T("HeaderStatus").Text=paused?"● Paused":"● Downloading";
            T("HeaderStatus").Foreground=Brushes.LightSkyBlue;
        }
        else if (_sessionStart.HasValue)
        {
            T("HeaderStatus").Text=_stopping?"● Stopped":_failed==0?"● Completed":"● Completed with errors";
            T("HeaderStatus").Foreground=_stopping?Brushes.Orange:_failed==0?Brushes.LightGreen:Brushes.Tomato;
        }
        else
        {
            T("HeaderStatus").Text="● Ready";
            T("HeaderStatus").Foreground=Brushes.LightGray;
        }
        if(active!=_lastActive) { _lastActive=active; Q.Items.Refresh(); D.Items.Refresh(); }
    }
    private void OnRowChanged(DownloadItem item)
    {
        _=Dispatcher.BeginInvoke(()=>
        {
            if (_disposed || !_queue.Contains(item)) return;
            if (!_downloads.Contains(item))_downloads.Add(item);
            if (item.Status is "Completed" or "Failed" or "Interrupted")
            {
                if (!_finalStates.TryGetValue(item.Url,out var before) || before!=item.Status)
                {
                    _finalStates[item.Url]=item.Status;
                    Log($"{item.Status}: {item.Url}"+(item.RawError.Length>0?" | "+item.RawError:""));
                    SaveCheckpoint();
                }
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
        Profile=Pick(C("ProfileCombo")),Quality=Pick(C("QualityCombo")),Codec=Pick(C("CodecCombo")),
        Container=Pick(C("ContainerCombo")),RateLimit=C("RateLimitCombo").Text,
        Threads=B("ThreadsBox").Text,Fragments=Pick(C("FragmentsCombo")),
        FilenameTemplate=B("FilenameTemplateBox").Text,Output=B("OutputBox").Text,
        Cookies=B("CookiesBox").Text,SubtitleLangs=B("SubtitleLangsBox").Text,
        WriteSubs=Check(K("WriteSubsCheck")),AutoSubs=Check(K("AutoSubsCheck")),EmbedSubs=Check(K("EmbedSubsCheck")),
        EmbedThumbnail=Check(K("EmbedThumbnailCheck")),EmbedMetadata=Check(K("EmbedMetadataCheck")),
        EmbedChapters=Check(K("EmbedChaptersCheck")),Archive=Check(K("ArchiveCheck")),
        Sponsor=Check(K("SponsorCheck")),AudioOnly=Check(K("AudioOnlyCheck")),
        WatchClipboard=Check(K("WatchClipboardCheck"))
    };
    private void ApplyOptions(DownloadOptions o)
    {
        foreach(var (name,value) in new[]{
            ("ProfileCombo",o.Profile),("QualityCombo",o.Quality),("CodecCombo",o.Codec),
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
            ("SponsorCheck",o.Sponsor),("AudioOnlyCheck",o.AudioOnly),("WatchClipboardCheck",o.WatchClipboard)})
            K(name).IsChecked=value;
    }
    private void TryRestore()
    {
        try
        {
            var previous=_storage.Restore();
            if (previous==null || previous.Queue.Count==0) return;
            if (MessageBox.Show(_window,$"Restore previous queue ({previous.Queue.Count} URLs)?\nCompleted downloads will not restart automatically.",
                    "Restore previous session",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes) return;
            ApplyOptions(previous.Settings);
            foreach(var item in previous.Queue)
            {
                item.Order=_queue.Count+1;_queue.Add(item);
                _previews.Request(item.Url);
            }
            Log($"Restored {_queue.Count} URLs. Downloads will not restart automatically.");
        }
        catch(Exception e)
        {
            MessageBox.Show(_window,"Cannot read previous session: "+e.Message+
               "\nThe existing checkpoint has not been overwritten.","Session recovery error",MessageBoxButton.OK,MessageBoxImage.Warning);
            _checkpointReadOnly=true;
        }
    }
    private bool _checkpointReadOnly;
    private void SaveCheckpoint()
    {
        if (_checkpointReadOnly || _disposed) return;
        try
        {
            // Save on meaningful changes, not every progress tick.
            var fingerprint=string.Join("|",_queue.Select(i=>$"{i.Url}:{i.Status}:{i.Path}:{i.Order}"))+
                ":"+System.Text.Json.JsonSerializer.Serialize(ReadOptions(),AppPersistence.Json)+":"+_queue.Count;
            if (fingerprint==_lastSavedSnapshot) return;
            _storage.Save(_queue,ReadOptions());_lastSavedSnapshot=fingerprint;
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
            foreach(var item in rejected) _queue.Remove(item);
            Reindex();
        }
        return approved;
    }
    private void Reindex()
    {
        int n=0;foreach(var item in _queue)item.Order=++n;
        T("QueueCount").Text=n+" URLs";
        UpdateState();SaveCheckpoint();
    }
    private static IReadOnlyList<string> ParseUrls(string text)
    {
        var found=new HashSet<string>(StringComparer.Ordinal);
        foreach(var token in text.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries))
        {
            var value=token.Trim();
            if(AppPersistence.ValidUrl(value))found.Add(value);
        }
        return found.ToList();
    }
    private void AddUrls(IEnumerable<string> urls,bool clipboard=false,bool selectNew=false)
    {
        var rows=urls.Where(AppPersistence.ValidUrl).Distinct(StringComparer.Ordinal)
            .Where(url=>!_queue.Any(i=>string.Equals(i.Url,url,StringComparison.Ordinal)))
            .Select(url=>new DownloadItem{Url=url}).ToList();
        if(rows.Count==0)return;
        bool active=_session?.IsRunning==true;
        if(active)
        {
            var checkedRows=ConfirmDuplicates(rows,false);
            if(checkedRows==null)return;
            rows=checkedRows;
        }
        foreach(var row in rows)
        {
            _queue.Add(row);
            _previews.Request(row.Url,clipboard&&active);
            if(active)
            {
                if(_session!.Add(row)) _sessionUrls.Add(row.Url);
                else Log("Session finished; URL stays queued for next Start: "+row.Url);
            }
        }
        if(selectNew && rows.Count>0)
        {
            Q.SelectedItem=rows[0];
            ShowPreview(rows[0].Url);
        }
        Reindex();
        T("ClipboardHint").Text=rows.Count+" URL(s) added.";
    }
    private void ShowPreview(string url)
    {
        if(!AppPersistence.ValidUrl(url))return;
        _previewUrl=url;
        var data=_previews.Get(url);
        if(data!=null){OnPreviewReady(data);return;}
        T("PreviewTitle").Text="Preparing preview...";
        T("PreviewMeta").Text=url;
        T("PreviewFormats").Text="";
        F<Image>("PreviewImage").Source=null;
        _previews.Request(url,true);
    }
    private void OnPreviewReady(PreviewData data)
    {
        if(_disposed || !string.Equals(_previewUrl,data.Url,StringComparison.Ordinal))return;
        T("PreviewTitle").Text=data.Title.Length>0?data.Title:"Untitled";
        var summary=new List<string>();
        if(data.PlaylistTitle.Length>0)summary.Add("Playlist: "+data.PlaylistTitle);
        if(data.Uploader.Length>0)summary.Add(data.Uploader);
        if(data.Duration>0)summary.Add(TimeSpan.FromSeconds(data.Duration).ToString(@"hh\:mm\:ss"));
        if(data.Extractor.Length>0)summary.Add(data.Extractor);
        T("PreviewMeta").Text=string.Join(" · ",summary);
        T("PreviewFormats").Text=$"{(data.MaxHeight>0?data.MaxHeight+"p ":"")}{data.MaxFps:0} FPS · {data.Codecs} · {data.DynamicRange}";
        if(data.ThumbnailBase64.Length==0){F<Image>("PreviewImage").Source=null;return;}
        try
        {
            var bytes=Convert.FromBase64String(data.ThumbnailBase64);
            using var memory=new MemoryStream(bytes);
            var bitmap=new BitmapImage();
            bitmap.BeginInit();bitmap.CacheOption=BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth=720;bitmap.StreamSource=memory;bitmap.EndInit();bitmap.Freeze();
            F<Image>("PreviewImage").Source=bitmap;
        }
        catch(Exception e){Log("Thumbnail decode failed: "+e.Message);F<Image>("PreviewImage").Source=null;}
    }
    private void PollClipboard()
    {
        if(!Check(K("WatchClipboardCheck")))return;
        try
        {
            if(!System.Windows.Clipboard.ContainsText())return;
            var raw=System.Windows.Clipboard.GetText();
            if(raw==_lastClipboard)return;
            _lastClipboard=raw;
            AddUrls(ParseUrls(raw),clipboard:true);
        }
        catch(System.Runtime.InteropServices.COMException){}
    }
    private async Task StartAsync(IEnumerable<DownloadItem>? custom=null)
    {
        if(_session?.IsRunning==true) return;
        var pending=(custom??_queue.Where(i=>i.Status!="Completed")).Distinct().ToList();
        if(pending.Count==0)return;
        var approved=ConfirmDuplicates(pending,true);
        if(approved==null||approved.Count==0)return;
        var options=ReadOptions();
        try
        {
            if(!File.Exists(_ytDlp))throw new FileNotFoundException("yt-dlp.exe is missing.",_ytDlp);
            if(!string.IsNullOrWhiteSpace(options.Cookies)&&!File.Exists(options.Cookies))
                throw new FileNotFoundException("Cookie file is missing.",options.Cookies);
            if(string.IsNullOrWhiteSpace(options.Output))throw new ArgumentException("Choose an output directory.");
            OwnedFiles.ValidateTemplate(options.FilenameTemplate);
            options.PinOutputDirectory();
            OwnedFiles.NoReparseAncestors(Path.Combine(options.OutputDirectory,"__validation__"));
            Directory.CreateDirectory(options.OutputDirectory);
            _sessionOptions=options;
            _stopping=false;_sessionStart=DateTime.Now;_finalStates.Clear();_downloads.Clear();
            _sessionUrls.Clear();
            foreach(var row in approved) _sessionUrls.Add(row.Url);
            foreach(var row in approved)
            {
                row.Status="Queued";row.State="Queued";row.RawError="";row.Progress=0;
                row.Speed="";row.Downloaded="";row.ETA="";
            }
            var session=new DownloadCoordinator(_ytDlp,_storage,options,OnRowChanged,
                line=>Dispatcher.BeginInvoke(()=>Log(line)));
            _session=session;
            foreach(var row in approved)session.Add(row);
            UpdateState();SaveCheckpoint();
            Log($"Started {approved.Count} URL(s), parallel={options.Parallel}, quality={options.Quality}.");
            _sessionTask=session.RunAsync();
            try { await _sessionTask; }
            finally
            {
                await session.DisposeAsync();
                if(ReferenceEquals(_session,session)) _session=null;
                // Results are already on row objects; do not touch other session rows.
                UpdateState();SaveCheckpoint();
                Log($"Session complete: {_queue.Count(x=>x.Status=="Completed")} OK, {_queue.Count(x=>x.Status=="Failed")} failed.");
                if(!_stopping && !_disposed)
                {
                    var done=_queue.Count(x=>x.Status=="Completed");
                    var bad=_queue.Count(x=>x.Status=="Failed");
                    var remaining=_queue.Count(x=>x.Status=="Queued"||x.Status=="Interrupted");
                    try
                    {
                        (bad>0?System.Media.SystemSounds.Hand:System.Media.SystemSounds.Asterisk).Play();
                        _tray.ShowBalloonTip(4000,"Video Downloader",
                            $"OK {done} · Left {remaining} · Err {bad}",
                            bad>0?Forms.ToolTipIcon.Warning:Forms.ToolTipIcon.Info);
                    }
                    catch(InvalidOperationException){}
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
        if (_session?.IsRunning==true && (row.Status=="Queued" || row.Status=="Running"))_session.Cancel(row);
    }
    private void RemoveRows(IEnumerable<DownloadItem> rows)
    {
        var selected=rows.Where(_queue.Contains).Distinct().ToArray();
        if(selected.Length==0)return;
        if(selected.Any(x=>x.Status!="Queued") &&
            MessageBox.Show(_window,$"Remove {selected.Length} selected URL(s)?\nActive jobs will be stopped; finished files belonging to them will be permanently deleted.\nOther downloads continue.",
                "Delete downloads",MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        foreach(var row in selected)
        {
            try
            {
                CancelRow(row);
                if(row.Status=="Completed")
                {
                    // Never delete an old checkpoint path without the owned-file manifest.
                    if(row.OwnedPaths.Count==0 && row.Path.Length>0)
                        throw new IOException("This restored download has no ownership manifest. Remove the file manually; the application will not risk deleting an unrelated file.");
                    if(row.OwnedPaths.Count>0)
                        OwnedFiles.DeleteOwned(_sessionOptions.OutputDirectory,row.OwnedPaths);
                }
                _queue.Remove(row);
                _downloads.Remove(row);
                _sessionUrls.Remove(row.Url);
            }
            catch(Exception e)
            {
                Log("Removal failed for "+row.Url+": "+e.Message);
                MessageBox.Show(_window,e.Message,"Removal blocked",MessageBoxButton.OK,MessageBoxImage.Warning);
            }
        }
        Reindex();
    }
    private void MoveItem(DownloadItem item,int index)
    {
        if(_session?.IsRunning==true)return;
        int old=_queue.IndexOf(item);
        if(old<0||index<0||index>=_queue.Count||old==index)return;
        _queue.Move(old,index);Reindex();
    }
    public void ReceiveArguments(IEnumerable<string> args)
    {
        var urls=new List<string>();
        foreach(var arg in args)
        {
            if(arg.StartsWith("videodownloader://",StringComparison.OrdinalIgnoreCase))
            {
                string payload=Uri.UnescapeDataString(arg["videodownloader://".Length..].TrimStart('/'));
                urls.AddRange(ParseUrls(payload));
            }
            else if (File.Exists(arg))urls.AddRange(ReadInputFile(arg));
            else urls.AddRange(ParseUrls(arg));
        }
        if(urls.Count>0)AddUrls(urls);
        if(!_window.IsVisible)_window.Show();
        if(_window.WindowState==WindowState.Minimized)_window.WindowState=WindowState.Normal;
        _window.Activate();
    }
    private static IReadOnlyList<string> ReadInputFile(string file)
    {
        if(Path.GetExtension(file).Equals(".url",StringComparison.OrdinalIgnoreCase))
            return ParseUrls(File.ReadAllText(file));
        if(Path.GetExtension(file).Equals(".txt",StringComparison.OrdinalIgnoreCase))
            return ParseUrls(File.ReadAllText(file));
        return [];
    }
    private void WindowDrop(object sender,DragEventArgs e)
    {
        if(e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files=(string[])(e.Data.GetData(DataFormats.FileDrop)??Array.Empty<string>());
            AddUrls(files.SelectMany(ReadInputFile));e.Handled=true;
        }
        else if(e.Data.GetDataPresent(DataFormats.Text))
        {
            AddUrls(ParseUrls(e.Data.GetData(DataFormats.Text)?.ToString()??""));e.Handled=true;
        }
    }
}
