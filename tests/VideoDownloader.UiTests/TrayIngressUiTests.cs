using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using VideoDownloader.Native;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    // Actual StateChanged/Click handlers and the production named-pipe pair.
    // All metadata is cached locally so this test never contacts a public site.
    internal void TestTrayAndProtocolIngress(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Tray/ingress WPF test failed: "+error);
        }
        static void PumpUntil(Func<bool> condition,string error)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            var deadline=DateTime.UtcNow.AddSeconds(10);
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            Require(condition(),error);
        }
        string profile=Path.GetDirectoryName(_storage.SessionPath)!;
        Require(Path.GetFullPath(profile).StartsWith(Path.GetFullPath(testRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase),"Tests require an isolated user profile");
        var originalQueue=SessionState.Queue.ToArray();
        var originalDownloads=SessionState.Downloads.ToArray();
        var originalSelection=Q.SelectedItem;
        var originalSession=SessionState.Coordinator;
        var originalTask=SessionState.RunningTask;
        string originalPreview=_preview.SelectedUrl;
        string originalPreviewTitle=T("PreviewTitle").Text;
        var originalState=_window.WindowState;
        bool originalVisibility=_window.IsVisible;
        string root=Path.Combine(testRoot,"tray-ingress-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string pipeName="VideoDownloader.Native.Test."+Guid.NewGuid().ToString("N");
        string prefix="https://example.invalid/tray-ingress/"+Guid.NewGuid().ToString("N")+"/";
        string[] urls=[prefix+"Video?Token=A%2FB&name=Upper+Case#part",prefix+"video?Token=A%2FB&name=Upper+Case#part",
            prefix+"pipe?Token=Exact",prefix+"file?Token=Exact",prefix+"barrier?Token=Exact"];
        foreach(string url in urls)
        {
            var metadata=new PreviewData(url,"Локальное название","","",0,"",0,0,"","","",DateTime.UtcNow);
            string cache=Path.Combine(_storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(cache,JsonSerializer.Serialize(new {
                CacheVersion=1,PreviewFetchVersion=3,metadata.Url,metadata.Title,metadata.SavedAtUtc,metadata.Uploader,metadata.PlaylistTitle,metadata.Duration,
                metadata.Extractor,metadata.MaxHeight,metadata.MaxFps,metadata.Codecs,metadata.DynamicRange,metadata.ThumbnailBase64
            },AppPersistence.Json));
        }
        static string Protocol(string url)=>"VIDEODOWNLOADER://"+Uri.EscapeDataString(url);
        async Task Deliver(params string[] arguments)
        {
            string? executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE");
            Require(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable),
                "VD_NATIVE_TITLE_FIXTURE must identify the compiled native test host");
            var start=new ProcessStartInfo(executable!) {
                UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,
                StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
            };
            start.ArgumentList.Add("--native-ipc-forward");
            start.ArgumentList.Add(pipeName);
            foreach(string argument in arguments)start.ArgumentList.Add(argument);
            using var child=ManagedChildProcess.Start(start);
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            using var kill=timeout.Token.Register(()=>{try{if(!child.Process.HasExited)child.Process.Kill(true);}catch{}});
            var stdout=child.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr=child.StandardError.ReadToEndAsync(timeout.Token);
            await Task.WhenAll(stdout,stderr,child.Process.WaitForExitAsync(timeout.Token)).ConfigureAwait(false);
            Require(child.Process.ExitCode==0 && child.Process.Id!=Environment.ProcessId &&
                stdout.Result.Trim()=="FORWARDED:"+child.Process.Id && stderr.Result.Length==0,
                "A separate native process must send the complete argument array through the production IPC client");
        }
        void RequireIdle()=>Require(ReferenceEquals(SessionState.Coordinator,originalSession) && ReferenceEquals(SessionState.RunningTask,originalTask) &&
            SessionState.Downloads.SequenceEqual(originalDownloads) && ReferenceEquals(Q.SelectedItem,originalSelection) &&
            _preview.SelectedUrl==originalPreview && T("PreviewTitle").Text==originalPreviewTitle,
            "Visibility and ingress must preserve selection, preview and the current download session");
        try
        {
            _window.WindowState=WindowState.Normal;
            _window.Show();
            _window.WindowState=WindowState.Minimized;
            PumpUntil(()=>!_window.IsVisible,"Minimizing the real window must hide it through StateChanged");
            Require(_lifecycle.TrayIcon.Visible,"Minimizing must retain the tray icon");
            RequireIdle();
            _lifecycle.TrayIcon.ContextMenuStrip!.Items[0].PerformClick();
            PumpUntil(()=>_window.IsVisible && _window.WindowState==WindowState.Normal,
                "The actual tray Open action must show and restore the window");
            RequireIdle();

            F<Button>("TrayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(!_window.IsVisible && _lifecycle.TrayIcon.Visible,"The Tray button must hide the window while keeping the icon");
            ReceiveArguments([Protocol(urls[0]),Protocol(urls[1])]);
            Require(_window.IsVisible && _window.WindowState==WindowState.Normal,
                "Protocol arguments must reveal the existing window");
            Require(SessionState.Queue.Skip(originalQueue.Length).Select(row=>row.Url).SequenceEqual(urls.Take(2)),
                "Protocol decoding must preserve escaped query, fragment and case-distinct URLs");
            ReceiveArguments([Protocol(urls[0])]);
            Require(SessionState.Queue.Count==originalQueue.Length+2,"Repeated protocol delivery must not duplicate its exact URL");
            RequireIdle();

            string list=Path.Combine(root,"ссылки [pipe input].txt");
            File.WriteAllText(list,urls[3]+Environment.NewLine,Encoding.UTF8);
            using var cancel=new CancellationTokenSource();
            var listener=VideoDownloader.Native.Program.ListenForSecondaryInstancesAsync(Subject,cancel.Token,pipeName);
            try
            {
                _window.WindowState=WindowState.Minimized;
                PumpUntil(()=>!_window.IsVisible,"IPC test must begin with the existing window hidden");
                var delivery=Deliver(Protocol(urls[2]),list);
                PumpUntil(()=>delivery.IsCompleted && (delivery.IsFaulted || delivery.IsCanceled || SessionState.Queue.Any(row=>row.Url==urls[3])),
                    "The production pipe client/server must forward protocol and spaced Unicode file arguments to the dispatcher");
                delivery.GetAwaiter().GetResult();
                Require(_window.IsVisible && _window.WindowState==WindowState.Normal,
                    "A secondary-instance message must restore the minimized existing window");
                Require(SessionState.Queue.Skip(originalQueue.Length).Select(row=>row.Url).SequenceEqual(urls.Take(4)),
                    "IPC must deliver the complete argument array once in order, preserving exact URL identities");
                var repeated=Deliver(Protocol(urls[2]),list,Protocol(urls[4]));
                PumpUntil(()=>repeated.IsCompleted && (repeated.IsFaulted || repeated.IsCanceled || SessionState.Queue.Any(row=>row.Url==urls[4])),
                    "The listener must accept a subsequent connection without dropping its new final argument");
                repeated.GetAwaiter().GetResult();
                // The new final URL above proves the repeated message has been
                // dispatched before hiding again and testing empty activation.
                F<Button>("TrayButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                var barrier=Deliver();
                PumpUntil(()=>barrier.IsCompleted && (barrier.IsFaulted || barrier.IsCanceled || _window.IsVisible),
                    "The listener must complete repeated and empty deliveries");
                barrier.GetAwaiter().GetResult();
                Require(SessionState.Queue.Count==originalQueue.Length+urls.Length,"Repeated and empty IPC deliveries must not add duplicates");
                RequireIdle();
                SaveCheckpoint();
                Require(_storage.Restore()!.Queue.Skip(originalQueue.Length).Select(row=>row.Url).SequenceEqual(urls),
                    "Protocol/IPC URLs must survive checkpoint persistence without rewriting case or escapes");
            }
            finally
            {
                cancel.Cancel();
                listener.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
            }
            TestWatchClipboard(SessionState.Queue.Single(row=>row.Url==urls[0]));
            Console.WriteLine("PASS: native minimize/hide/tray Open/restore, protocol decoding and production named-pipe ingress");
        }
        finally
        {
            SessionState.Queue.Clear();foreach(var row in originalQueue)SessionState.Queue.Add(row);
            Q.SelectedItem=originalSelection;
            _window.WindowState=originalState;
            if(originalVisibility)_window.Show();else _window.Hide();
            Reindex();
        }
    }

    private void TestWatchClipboard(DownloadItem selected)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Watch clipboard WPF test failed: "+error);
        }
        var originalData=System.Windows.Clipboard.GetDataObject();
        bool? originalEnabled=K("WatchClipboardCheck").IsChecked;
        string originalLast=_ingress.LastClipboard;
        var originalQueue=SessionState.Queue.ToArray();
        var originalDownloads=SessionState.Downloads.ToArray();
        var originalSelection=Q.SelectedItem;
        var originalSession=SessionState.Coordinator;
        var originalTask=SessionState.RunningTask;
        string originalPreview=_preview.SelectedUrl;
        string originalTitle=T("PreviewTitle").Text;
        string originalMeta=T("PreviewMeta").Text;
        string originalFormats=T("PreviewFormats").Text;
        var originalImage=F<Image>("PreviewImage").Source;
        string url="https://example.invalid/watch-clipboard/"+Guid.NewGuid().ToString("N")+"/Video?Token=A%2FB";
        string cache=Path.Combine(_storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        AppPersistence.AtomicWrite(cache,JsonSerializer.Serialize(new {
            CacheVersion=1,PreviewFetchVersion=3,Url=url,Title="Название из буфера",SavedAtUtc=DateTime.UtcNow,Uploader="",PlaylistTitle="",Duration=0,
            Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",ThumbnailBase64=""
        },AppPersistence.Json));
        int selectionChanges=0;
        System.Windows.Controls.SelectionChangedEventHandler observer=(_,_)=>selectionChanges++;
        try
        {
            K("WatchClipboardCheck").IsChecked=false;
            Q.SelectedItem=selected;
            string selectedTitle=T("PreviewTitle").Text;
            string selectedMeta=T("PreviewMeta").Text;
            Q.SelectionChanged+=observer;
            System.Windows.Clipboard.SetText(url);
            K("WatchClipboardCheck").IsChecked=true;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(SessionState.Queue.Any(row=>row.Url==url && row.Title=="Название из буфера") || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            var imported=SessionState.Queue.SingleOrDefault(row=>row.Url==url);
            Require(imported is {Title:"Название из буфера",Status:"Queued"} && imported.Path.Length==0 &&
                SessionState.Queue.Count==originalQueue.Length+1 && _ingress.LastClipboard==url,
                "The actual Watch clipboard timer must read the Windows clipboard and queue its exact URL with cached metadata");
            _ingress.PollClipboard();_ingress.PollClipboard();
            Require(SessionState.Queue.Count==originalQueue.Length+1 && selectionChanges==0 && ReferenceEquals(Q.SelectedItem,selected) &&
                _preview.SelectedUrl==selected.Url && T("PreviewTitle").Text==selectedTitle && T("PreviewMeta").Text==selectedMeta,
                "Repeated polling must deduplicate URLs and preserve the user's selected preview");
            Require(ReferenceEquals(SessionState.Coordinator,originalSession) && ReferenceEquals(SessionState.RunningTask,originalTask) &&
                SessionState.Downloads.SequenceEqual(originalDownloads),"Watch clipboard must not start downloads");
            SaveCheckpoint();
            var saved=_storage.Restore()!;
            Require(saved.Settings.WatchClipboard && saved.Queue.Single(row=>row.Url==url).Title=="Название из буфера",
                "Clipboard URLs and the enabled setting must survive checkpoint persistence");
            Console.WriteLine("PASS: actual Windows clipboard watch timer, exact URL, cached title, dedup and unchanged selected preview");
        }
        finally
        {
            Q.SelectionChanged-=observer;
            K("WatchClipboardCheck").IsChecked=false;
            try
            {
                if(originalData!=null)System.Windows.Clipboard.SetDataObject(originalData,true);
                else System.Windows.Clipboard.Clear();
            }
            finally
            {
                _ingress.LastClipboard=originalLast;
                K("WatchClipboardCheck").IsChecked=originalEnabled;
                SessionState.Queue.Clear();foreach(var row in originalQueue)SessionState.Queue.Add(row);
                Q.SelectedItem=originalSelection;
                _preview.SelectedUrl=originalPreview;
                T("PreviewTitle").Text=originalTitle;
                T("PreviewMeta").Text=originalMeta;
                T("PreviewFormats").Text=originalFormats;
                F<Image>("PreviewImage").Source=originalImage;
                Reindex();
            }
        }
    }
}
