using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using System.Runtime.InteropServices;
using System.Text;

namespace VideoDownloader.Native;

// Real WPF dispatcher/control test, isolated from the user's persistent data.
// Invoked only by the internal --ui-self-test CI switch.
internal static class NativeUiSelfTest
{
    public static void Run()
    {
        static void Stage(string status)
        {
            var location=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(location))File.WriteAllText(location,status);
        }
        Stage("creating temp folder");
        string root=Path.Combine(Path.GetTempPath(),"vd-native-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Stage("creating WPF application");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        NativeWindow? controller=null;
        Window? window=null;
        try
        {
            Stage("testing startup recovery dialogs and empty-queue settings");
            VerifyStartupRecovery(root,Stage);
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            Stage("XAML loaded");
            controller=new NativeWindow(window,new AppPersistence(Path.Combine(root,"profile")));
            Stage("controller constructed");
            window.Show();
            Stage("window shown");
            TextBox urlField=(TextBox)window.FindName("UrlInput")!;
            Button add=(Button)window.FindName("AddUrlButton")!;
            Button remove=(Button)window.FindName("RemoveQueueButton")!;
            DataGrid queue=(DataGrid)window.FindName("QueueGrid")!;

            static void Require(bool condition,string error)
            {
                if(!condition) throw new InvalidOperationException("WPF UI parity test failed: "+error);
            }

            urlField.Text="https://example.com/one https://example.com/two";
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Stage("Add clicked");
            Require(queue.Items.Count==2,"Add button must place both pasted URLs in the queue");
            Require(urlField.Text=="","Add button must clear the URL text field");
            Require((queue.Items[0] as DownloadItem)?.Url=="https://example.com/one","First URL must retain its order");
            Require((queue.Items[1] as DownloadItem)?.Url=="https://example.com/two","Second URL must retain its order");
            Require(window.Resources.Contains("QueueStatusTextStyle"),"Queue state colors must be present");
            Require(window.Resources.Contains("DownloadStatusTextStyle"),"Download state colors must be present");

            Stage("testing URL and background video title display modes");
            controller.TestTitleDisplay(root);

            Stage("testing live Add while duplicate dialogs pump the dispatcher");
            controller.TestLiveAddDuringDuplicateDialog(root);

            Stage("testing manual subscriptions window and inbox queue actions");
            controller.TestSubscriptionsUi(root);

            Stage("testing Russian profile labels and legacy ID mapping");
            var profile=(ComboBox)window.FindName("ProfileCombo")!;
            Require(profile.Items.Count==5 &&
                    profile.Items.Cast<string>().SequenceEqual(ProfileCatalog.DisplayNames),
                "All five profiles must show localized Russian captions.");
            foreach(var id in new[]{"Universal","4K archive","MP4 compatibility","Music MP3","Subtitles archive"})
            {
                var caption=ProfileCatalog.Display(id);
                Require(ProfileCatalog.ToId(caption)==id,
                    "Russian profile captions must round-trip back to the saved English ID: "+id);
            }
            var applyOptions=typeof(NativeWindow).GetMethod("ApplyOptions",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            applyOptions.Invoke(controller, new object[]{new DownloadOptions{Profile="Music MP3",AudioOnly=true}});
            Require((string)profile.SelectedItem=="Музыка MP3",
                "Old English checkpoint profile must restore with Russian label.");
            var readOptions=typeof(NativeWindow).GetMethod("ReadOptions",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var options=(DownloadOptions)readOptions.Invoke(controller,null)!;
            Require(options.Profile=="Music MP3" && options.AudioOnly,
                "Russian music profile must preserve the canonical ID and audio-only preset.");

            Stage("testing all profile transitions through real WPF controls");
            foreach(var previous in ProfileCatalog.DisplayNames)
            foreach(var target in ProfileCatalog.DisplayNames)
            {
                profile.SelectedItem=previous;profile.SelectedItem=target;
                var actual=(DownloadOptions)readOptions.Invoke(controller,null)!;
                var expected=new DownloadOptions();ProfileCatalog.Apply(expected,target);
                Require(actual.Quality==expected.Quality && actual.Codec==expected.Codec && actual.Container==expected.Container &&
                    actual.AudioOnly==expected.AudioOnly && actual.Archive==expected.Archive && actual.AutoSubs==expected.AutoSubs &&
                    actual.WriteSubs==expected.WriteSubs && actual.EmbedSubs==expected.EmbedSubs &&
                    actual.EmbedThumbnail==expected.EmbedThumbnail && actual.EmbedMetadata==expected.EmbedMetadata && actual.EmbedChapters==expected.EmbedChapters,
                    "WPF profile must fully replace previous preset: "+previous+" -> "+target);
            }

            Stage("testing configured output directory");
            var outputButton=(Button)window.FindName("OpenOutputDirectoryButton")!;
            Require(outputButton.IsEnabled && (string)outputButton.Content=="📂 Открыть папку загрузок",
                "Output folder button must be visible and enabled without selecting a download.");
            var outputBox=(TextBox)window.FindName("OutputBox")!;
            outputBox.Text=Path.Combine(root,"my-downloads");
            var archive=(CheckBox)window.FindName("ArchiveCheck")!;
            archive.IsChecked=false;
            var outputPath=typeof(NativeWindow).GetMethod("ConfiguredOutputDirectory",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            Require((string)outputPath.Invoke(controller,null)==outputBox.Text,
                "Open folder must resolve the configured output even before first download.");
            archive.IsChecked=true;
            Require((string)outputPath.Invoke(controller,null)==
                    Path.Combine(outputBox.Text,DateTime.Now.ToString("yyyy-MM-dd")),
                "Archive mode must point into the dated output subfolder.");
            archive.IsChecked=false;

            Stage("testing AppData settings directory button");
            var appSettings=(Button)window.FindName("OpenAppSettingsButton")!;
            Require(appSettings.IsEnabled && (string)appSettings.Content=="📁 Открыть папку настроек",
                "The settings button must be visible beside Register protocol.");
            var realSettingsPath=Path.GetDirectoryName(new AppPersistence(Path.Combine(root,"profile")).SessionPath)!;
            Require(realSettingsPath==Path.Combine(root,"profile") && Directory.Exists(realSettingsPath),
                "Native settings action should open the actual persistence folder, not the download output.");

            // Exercise the actual callback on a worker thread: reading a WPF
            // TextBox here used to throw before any metadata request could start.
            Stage("testing background preview cookies");
            var cookies=(TextBox)window.FindName("CookiesBox")!;
            cookies.Text=Path.Combine(root,"cookies.txt");
            var previews=(PreviewService)typeof(NativeWindow).GetField("_previews",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;
            var readCookies=(Func<string>)typeof(PreviewService).GetField("_cookiePath",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(previews)!;
            Require(Task.Run(readCookies).GetAwaiter().GetResult()==cookies.Text,
                "Preview worker must read the current cookies without accessing WPF controls");
            cookies.Text="";
            Require(Task.Run(readCookies).GetAwaiter().GetResult()=="",
                "Preview worker must observe cookies being cleared");

            queue.SelectedItem=queue.Items[0];
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Stage("Remove clicked");
            Require(queue.Items.Count==1,"Remove button must delete only the selected queued URL");
            Require((queue.Items[0] as DownloadItem)?.Url=="https://example.com/two","Remove must preserve other entries");

            Stage("testing pending removal until worker cleanup acknowledgement");
            var pending=new DownloadItem{Url="https://example.com/cancel-before-start"};
            ((System.Collections.ObjectModel.ObservableCollection<DownloadItem>)queue.ItemsSource).Add(pending);
            var notify=(Action<DownloadItem>)typeof(NativeWindow).GetMethod("OnRowChanged",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .CreateDelegate(typeof(Action<DownloadItem>),controller);
            var sessionField=typeof(NativeWindow).GetField("_session",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var cancelSession=new DownloadCoordinator("must-not-launch",new AppPersistence(Path.Combine(root,"cancel-profile")),
                new DownloadOptions{Output=Path.Combine(root,"cancel-output"),Threads="1"},notify,_=>{});
            try
            {
                cancelSession.Add(pending);sessionField.SetValue(controller,cancelSession);
                var queueSubscriptions=typeof(NativeWindow).GetMethod("QueueSubscriptionUrls",
                    System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance,null,
                    new[]{typeof(IEnumerable<string>),typeof(string),typeof(string)},null)!;
                const string deferredUrl="https://example.com/subscription-next-start";
                var accepted=(IReadOnlyList<string>)queueSubscriptions.Invoke(controller,new object[]{new[]{deferredUrl},"subscription-test","123"})!;
                var deferred=((System.Collections.ObjectModel.ObservableCollection<DownloadItem>)queue.ItemsSource)
                    .Single(row=>row.Url==deferredUrl);
                Require(accepted.SequenceEqual(new[]{deferredUrl}) && deferred.DeferredToNextStart &&
                    deferred.Status=="Queued" && deferred.SubscriptionId=="subscription-test" && deferred.SubscriptionName=="123" && cancelSession.Outstanding==1,
                    "Subscription import must stay queued until Start without entering the active coordinator");
                ((System.Collections.ObjectModel.ObservableCollection<DownloadItem>)queue.ItemsSource).Remove(deferred);
                queue.SelectedItem=pending;remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(queue.Items.Contains(pending) && pending.PendingRemoval && !pending.AttemptFinished,
                    "Remove must retain the row while cancellation is unacknowledged");
                var task=cancelSession.RunAsync();
                var cancellationFrame=new System.Windows.Threading.DispatcherFrame();
                var deadline=DateTime.UtcNow.AddSeconds(10);
                var poll=new System.Windows.Threading.DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                poll.Tick+=(_,_)=>{if(task.IsCompleted && !queue.Items.Contains(pending) || DateTime.UtcNow>deadline)cancellationFrame.Continue=false;};
                poll.Start();
                try{System.Windows.Threading.Dispatcher.PushFrame(cancellationFrame);}finally{poll.Stop();}
                Require(task.IsCompletedSuccessfully && pending.AttemptFinished && !queue.Items.Contains(pending),
                    "Worker acknowledgement must finish removal and preserve the other queue row");
                Require(queue.Items.Count==1,"Cancelling one row must not remove another URL");
            }
            finally
            {
                cancelSession.DisposeAsync().AsTask().GetAwaiter().GetResult();
                sessionField.SetValue(controller,null);
            }

            Stage("testing tray");
            var trayField=typeof(NativeWindow).GetField("_tray",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var tray=(System.Windows.Forms.NotifyIcon?)trayField?.GetValue(controller);
            Require(tray!=null && tray.Visible,"Native WPF UI must expose a functioning tray icon");
            Require(tray!.Text.Length<=63,"Windows tray caption must respect the 63-character limit");

            // Checkpoint must be stored in the isolated test profile, never in
            // the user's actual %LOCALAPPDATA% directory.
            string checkpoint=Path.Combine(root,"profile","session.json");
            Require(File.Exists(checkpoint),"Queue actions must write a native v1 checkpoint");
            Stage("testing checkpoint");
            var restored=new AppPersistence(Path.Combine(root,"profile")).Restore();
            Require(restored?.Queue.Count==1,"New session checkpoint must restore the surviving queue row");

            Stage("testing live clipboard preview selection");
            var addUrls=typeof(NativeWindow).GetMethod("AddUrls",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            addUrls.Invoke(controller,new object[]{new[]{"https://example.com/clipboard"},true,true});
            Require(queue.Items.Count==2,"Clipboard addition must enqueue one new URL");
            Require((queue.SelectedItem as DownloadItem)?.Url=="https://example.com/clipboard",
                "Manual clipboard Paste must immediately select its new preview");
            Require(((TextBlock)window.FindName("PreviewTitle")!).Text=="Получаем метаданные…",
                "New clipboard URL must show preview loading state immediately");

            Stage("testing Windows shortcut import");
            string shortcut=Path.Combine(root,"example.url");
            File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL=https://example.com/shortcut\r\n");
            var readInput=typeof(NativeWindow).GetMethod("ReadInputFile",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            var imported=(IReadOnlyList<string>)readInput.Invoke(null,new object[]{shortcut})!;
            Require(imported.SequenceEqual(new[]{"https://example.com/shortcut"}),
                "Windows .url shortcut must import the URL= field");

            Stage("testing native tray field order");
            var startField=typeof(NativeWindow).GetField("_sessionStart",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            startField.SetValue(controller,DateTime.Now);
            var currentField=typeof(NativeWindow).GetField("_sessionUrls",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var sessionUrls=(HashSet<string>)currentField.GetValue(controller)!;
            sessionUrls.Add("https://example.com/clipboard");
            ((DownloadItem)queue.Items[0]).Status="Completed";
            var currentRows=(DownloadItem[])typeof(NativeWindow).GetMethod("CurrentSessionRows",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .Invoke(controller,null)!;
            Require(currentRows.Length==1 && currentRows[0].Url=="https://example.com/clipboard",
                "Completion notifications and live metrics must share the current-session subset, excluding old successes");
            typeof(NativeWindow).GetMethod("UpdateState",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .Invoke(controller,null);
            Require(tray.Text.Contains("OK 0 Left 1 Err 0"),
                "Tray counts must be current-session only, ordered OK Left Err");

            Stage("testing shutdown");
            // Hold the worker completion task open, then exercise real window
            // Close events and dispatcher continuations. The first Close must
            // leave the app alive until the final result has been checkpointed.
            var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(NativeWindow).GetField("_sessionTask",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .SetValue(controller,cleanup.Task);
            typeof(NativeWindow).GetField("_exitFromTray",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .SetValue(controller,true);
            bool closed=false;
            var frame=new System.Windows.Threading.DispatcherFrame();
            window.Closed+=(_,_)=>{closed=true;frame.Continue=false;};
            window.Close();
            Require(!closed && window.IsVisible && !window.IsEnabled,
                "Exit must keep the window alive while worker cleanup is pending");
            var finalRow=(DownloadItem)queue.Items[0];
            finalRow.Status="Interrupted";finalRow.State="Cancelled";
            cleanup.SetResult();
            var timeout=new System.Windows.Threading.DispatcherTimer {
                Interval=TimeSpan.FromSeconds(10)
            };
            timeout.Tick+=(_,_)=>frame.Continue=false;
            timeout.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Require(closed,"Exit must close the window after cleanup completes");
            var finalSnapshot=new AppPersistence(Path.Combine(root,"profile")).Restore()!;
            Require(finalSnapshot.Queue[0].Status=="Interrupted" &&
                finalSnapshot.Queue[0].State=="Cancelled",
                "Exit must checkpoint the final cancelled result before closing");
            // PASS is set after the finally cleanup to catch shutdown crashes.
        }
        finally
        {
            Stage("dispose controller");
            controller?.Dispose();
            Stage("close WPF window");
            window?.Close();
            Stage("shutdown dispatcher");
            app.Shutdown();
            Stage("delete isolated test profile");
            try {Directory.Delete(root,true);}catch(IOException){}
        }
        Stage("PASS");
    }

    private static void VerifyStartupRecovery(string root,Action<string> stage)
    {
        const System.Reflection.BindingFlags methods=System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance;
        static Window LoadWindow()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            return (Window)XamlReader.Load(reader);
        }
        var data=new AppPersistence(Path.Combine(root,"startup-profile"));
        var options=new DownloadOptions {Output=Path.Combine(root,"saved-output"),Cookies=Path.Combine(root,"cookies.txt"),Threads="7",Profile="Music MP3",AudioOnly=true,ShowVideoTitles=true};
        data.Save([],options);
        var window=LoadWindow();
        stage("recovery: restoring empty queue settings");
        using(var controller=new NativeWindow(window,data))
        {
            var restored=(DownloadOptions)typeof(NativeWindow).GetMethod("ReadOptions",methods)!.Invoke(controller,null)!;
            if(restored.Output!=options.Output || restored.Cookies!=options.Cookies || restored.Threads!="7" || !restored.AudioOnly || !restored.ShowVideoTitles)
                throw new InvalidOperationException("Empty queues must restore saved settings without a dialog.");
        }
        window.Close();
        stage("recovery: empty queue settings restored");
        var subscriptions=new SubscriptionStore(Path.GetDirectoryName(data.SessionPath)!);
        var source=subscriptions.Add("https://example.invalid/recovery-source",false);
        const string missingTitleUrl="https://example.invalid/recovery-title";
        const string urlTitleUrl="https://example.invalid/recovery-url-title";
        const string realTitleUrl="https://example.invalid/recovery-real-title";
        const string upperTitleUrl="https://example.invalid/recovery-Video";
        const string lowerTitleUrl="https://example.invalid/recovery-video";
        subscriptions.ApplyScan(source.Id,new SubscriptionScan("Канал",
            [new DiscoveredMedia(missingTitleUrl,"Название из подписки"),new DiscoveredMedia(urlTitleUrl,"Название вместо URL"),
             new DiscoveredMedia(realTitleUrl,"Обнаруженное название"),new DiscoveredMedia(upperTitleUrl,"Другое видео")],"",""),[]);
        byte[] validSubscriptions=File.ReadAllBytes(subscriptions.Path);
        foreach(string url in new[]{missingTitleUrl,urlTitleUrl,realTitleUrl,lowerTitleUrl})
        {
            string cacheFile=Path.Combine(data.PreviewDir,
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(cacheFile,System.Text.Json.JsonSerializer.Serialize(new {
                CacheVersion=1,PreviewFetchVersion=2,SavedAtUtc=DateTime.UtcNow,Url=url,Success=true,
                Title="",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,
                Codecs="",DynamicRange="",ThumbnailBase64=""
            },AppPersistence.Json));
        }
        foreach(string recoveryCase in new[]{"valid","corrupt subscriptions","missing subscriptions","corrupt checkpoint"})
        {
            bool corrupt=recoveryCase=="corrupt checkpoint";
            if(recoveryCase=="missing subscriptions")File.Delete(subscriptions.Path);
            else if(recoveryCase=="corrupt subscriptions")File.WriteAllText(subscriptions.Path,"{invalid subscriptions");
            else File.WriteAllBytes(subscriptions.Path,validSubscriptions);
            var savedRows=new[]{
                new DownloadItem {Url=missingTitleUrl,SubscriptionId=source.Id,SubscriptionName="Legacy folder"},
                new DownloadItem {Url=urlTitleUrl,Title=urlTitleUrl,SubscriptionId=source.Id,SubscriptionName="Legacy folder"},
                new DownloadItem {Url=realTitleUrl,Title="Уже известное название",SubscriptionId=source.Id,SubscriptionName="Legacy folder"},
                new DownloadItem {Url=lowerTitleUrl,SubscriptionId=source.Id,SubscriptionName="Legacy folder"}
            };
            if(corrupt) File.WriteAllText(data.SessionPath,"{invalid checkpoint");
            else data.Save(savedRows,options);
            string before=File.ReadAllText(data.SessionPath);
            string title=corrupt?"Session recovery error":"Restore previous session";
            int button=corrupt?1:6;
            uint thread=GetCurrentThreadId();
            Exception? failure=null;
            bool observed=false;
            var observer=new System.Windows.Threading.DispatcherTimer {Interval=TimeSpan.FromSeconds(2.5)};
            observer.Tick+=(_,_)=>
            {
                observer.Stop();observed=true;
                stage("recovery: observing "+title);
                try
                {
                    if(File.ReadAllText(data.SessionPath)!=before)
                        throw new InvalidOperationException("Recovery dialog must preserve the checkpoint while dispatcher timers are running.");
                }
                catch(Exception error) {failure=error;}
                finally {stage("recovery: dismissing "+title+" found="+DismissDialog(thread,title,button));}
            };
            using var watchdog=new CancellationTokenSource();
            _=Task.Delay(TimeSpan.FromSeconds(12),watchdog.Token).ContinueWith(task=>
            {
                if(!task.IsCanceled) stage("recovery: watchdog dismissal "+title+" found="+DismissDialog(thread,title,button));
            },TaskScheduler.Default);
            window=LoadWindow();NativeWindow? controller=null;
            observer.Start();
            try
            {
                stage("recovery: constructing controller for "+title);
                controller=new NativeWindow(window,data);
                stage("recovery: controller restored after "+title);
                if(!observed) throw new InvalidOperationException("Recovery dialog must keep the dispatcher responsive.");
                if(failure!=null) throw failure;
                if(corrupt && File.ReadAllText(data.SessionPath)!=before)
                    throw new InvalidOperationException("Invalid checkpoint must remain unchanged after closing the error dialog.");
                if(!corrupt)
                {
                    var rows=((DataGrid)window.FindName("QueueGrid")!).Items.Cast<DownloadItem>().ToArray();
                    if(rows.Length!=savedRows.Length)
                        throw new InvalidOperationException("Confirmed recovery must retain the saved queue.");
                    string[] expectedTitles=recoveryCase=="valid"
                        ? ["Название из подписки","Название вместо URL","Уже известное название",""]
                        : ["",urlTitleUrl,"Уже известное название",""];
                    if(!rows.Select(row=>row.Title).SequenceEqual(expectedTitles) ||
                        rows.Any(row=>row.SubscriptionId!=source.Id || row.SubscriptionName!="Legacy folder"))
                        throw new InvalidOperationException("Restored title repair must use exact subscription/URL pairs and preserve real titles and routing.");
                    if((bool)typeof(NativeWindow).GetField("_checkpointReadOnly",methods)!.GetValue(controller)!)
                        throw new InvalidOperationException("An unavailable optional subscription store must not block queue recovery.");
                    if(recoveryCase=="missing subscriptions" && File.Exists(subscriptions.Path) ||
                        recoveryCase=="corrupt subscriptions" && File.ReadAllText(subscriptions.Path)!="{invalid subscriptions")
                        throw new InvalidOperationException("Restoring titles must not recreate or overwrite the optional subscription store.");
                }
            }
            finally {watchdog.Cancel();observer.Stop();controller?.Dispose();window.Close();}
        }
    }

    private static bool DismissDialog(uint thread,string title,int button)
    {
        bool found=false;
        EnumThreadWindows(thread,(handle,_)=>
        {
            var caption=new StringBuilder(256);GetWindowTextW(handle,caption,caption.Capacity);
            if(caption.ToString()==title)
            {
                // OK-only message boxes support WM_CLOSE. The Yes/No question
                // needs an explicit Yes command because its close box is disabled.
                found=PostMessageW(handle,button==1?0x10u:0x111u,button==1?IntPtr.Zero:(IntPtr)button,IntPtr.Zero);
                return false;
            }
            return true;
        },IntPtr.Zero);
        return found;
    }
    private delegate bool EnumWindowsProc(IntPtr window,IntPtr argument);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool EnumThreadWindows(uint thread,EnumWindowsProc callback,IntPtr argument);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
