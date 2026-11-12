using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestAppUpdateControls(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("App-update WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing real update control: "+name));
        static void Click(Window window,string name)=>window.Dispatcher.BeginInvoke(new Action(()=>
            Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent))));

        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(testRoot,"app-update-ui"));
        var service=new UiUpdateService();
        using var subject=new NativeWindow(window,storage,"must-not-launch",updateService:service,automaticUpdates:false);
        try
        {
            window.Show();window.UpdateLayout();
            var check=Control<Button>(window,"CheckAppUpdateButton");
            var install=Control<Button>(window,"InstallAppUpdateButton");
            var status=Control<TextBlock>(window,"AppUpdateStatusText");
            var installed=Control<TextBlock>(window,"CurrentAppVersionText");
            var appVersion=typeof(NativeWindow).Assembly.GetName().Version??new Version(2,0,0);
            string versionText=new Version(appVersion.Major,appVersion.Minor,Math.Max(0,appVersion.Build)).ToString();
            string installedText=installed.Text;
            Require(check.IsEnabled && !install.IsEnabled && service.CheckCalls==0 && status.Text.Length>0,
                "The update controls must render without background network in an isolated profile.");
            Require(installed.IsVisible && installed.Text.EndsWith(versionText,StringComparison.Ordinal),
                "The installed application assembly version must already be visible before any update request.");
            Require(install.Opacity<check.Opacity,"A disabled install action must remain visibly different in the dark theme.");

            Click(window,"CheckAppUpdateButton");
            PumpUntil(()=>service.CheckCalls==1,"Manual check did not reach the injected service.");
            Require(service.CurrentVersion==appVersion && installed.Text==installedText,
                "The version sent to update checking must match the displayed installed application, while checking keeps it visible.");
            Require(!check.IsEnabled && !install.IsEnabled,"An in-flight check must disable duplicate check/install actions.");
            var release=new AppUpdateRelease("999.0.0",
                new Uri("https://github.com/bespalovnikita/video-downloader/releases/download/v999.0.0/VideoDownloader-Windows-x64.zip"),
                new Uri("https://github.com/bespalovnikita/video-downloader/releases/download/v999.0.0/SHA256SUMS.txt"),100);
            service.CheckCompletion!.SetResult(release);
            PumpUntil(()=>check.IsEnabled && install.IsEnabled,"A newer verified release must enable installation when idle.");
            Require(status.Text.Contains("999.0.0",StringComparison.Ordinal),"The available version must be visible in the actual status control.");
            Require(installed.IsVisible && installed.Text==installedText && !installed.Text.Contains(release.Version,StringComparison.Ordinal),
                "An available release must not replace the separately displayed installed version.");

            var cleanup=new DownloadItem{Url="https://update-ui.invalid/cleanup"};
            subject.SessionState.Queue.Add(cleanup);
            Control<DataGrid>(window,"QueueGrid").SelectedItem=cleanup;
            subject.UpdateState();Click(window,"MediaSelectionButton");
            PumpUntil(()=>subject.MediaSelectionEditor?.IsVisible==true,"The row editor did not open.");
            Require(!install.IsEnabled,"An open editor with unapplied inputs must block application restart.");
            subject.MediaSelectionEditor!.Close();
            Require(install.IsEnabled,"Closing the editor must restore the idle installation action.");
            cleanup.PendingRemoval=true;subject.UpdateState();
            Require(!install.IsEnabled,"Pending file cleanup must block installation.");
            Click(window,"InstallAppUpdateButton");
            // A directly raised event is also guarded, even though normal users
            // cannot click a disabled control.
            window.Dispatcher.Invoke(()=>{},DispatcherPriority.ApplicationIdle);
            Require(service.PrepareCalls==0,"Disabled-control events cannot bypass pending cleanup.");
            cleanup.PendingRemoval=false;subject.UpdateState();
            Require(install.IsEnabled,"Installation becomes available once cleanup is complete.");

            int handoffs=0;
            subject.AppUpdateLaunchOverride=prepared=>
            {
                Require(prepared.Version=="999.0.0" && prepared.TargetDirectory==AppContext.BaseDirectory,
                    "The installer handoff must retain the selected version and current package boundary.");
                handoffs++;
            };
            Click(window,"InstallAppUpdateButton");
            PumpUntil(()=>service.PrepareCalls==1,"Install did not start background package preparation.");
            Require(!check.IsEnabled && !install.IsEnabled,"Preparation must keep both update actions disabled.");
            cleanup.PendingRemoval=true;subject.UpdateState();
            service.PrepareCompletion!.SetResult(UiPreparedUpdate(testRoot));
            PumpUntil(()=>check.IsEnabled,"A completed preparation did not release its busy state.");
            Require(handoffs==0 && window.IsVisible && !install.IsEnabled,
                "New file work during preparation must prevent handoff and preserve the running window.");
            Require(!status.Text.Contains("Загрузка и проверка",StringComparison.OrdinalIgnoreCase) &&
                !status.Text.Contains("Downloading and verifying",StringComparison.OrdinalIgnoreCase),
                "A postponed installation must not keep showing an in-progress download after preparation ends.");
            cleanup.PendingRemoval=false;subject.UpdateState();
            Click(window,"InstallAppUpdateButton");
            PumpUntil(()=>service.PrepareCalls==2,"The idle retry did not prepare a fresh package.");
            service.PrepareCompletion!.SetResult(UiPreparedUpdate(testRoot));
            PumpUntil(()=>handoffs==1 && check.IsEnabled,"An idle prepared update must reach the observed handoff exactly once.");
            Require(window.IsVisible && subject.SessionState.Coordinator==null && !File.Exists(storage.HistoryPath),
                "The isolated handoff must not close a user's window, launch a real updater or create download history.");

            Click(window,"CheckAppUpdateButton");PumpUntil(()=>service.CheckCalls==2,"Second check did not start.");
            service.CheckCompletion!.SetResult(null);
            PumpUntil(()=>check.IsEnabled && !install.IsEnabled,"An up-to-date result must clear the old available update.");
            Require(installed.IsVisible && installed.Text==installedText,"An up-to-date result must retain the installed version.");
            Click(window,"CheckAppUpdateButton");PumpUntil(()=>service.CheckCalls==3,"Failure check did not start.");
            service.CheckCompletion!.SetException(new IOException("offline update fixture"));
            PumpUntil(()=>check.IsEnabled && status.Text.Contains("offline update fixture",StringComparison.Ordinal),
                "A manual network error must be visible and leave the check retryable.");
            Require(!install.IsEnabled && window.IsVisible,"A failed check must not offer or launch an unverified package.");
            Require(installed.IsVisible && installed.Text==installedText,"A network error must retain the installed version.");

            Click(window,"CheckAppUpdateButton");PumpUntil(()=>service.CheckCalls==4,"Cancellation check did not start.");
            subject.Dispose();
            Require(service.Disposed && service.CheckCancelled,"Closing the UI owner must cancel its outstanding update request.");
            Console.WriteLine("PASS: real updater controls, asynchronous check/preparation, cleanup race guards, isolated handoff, retry and cancellation");
        }
        finally{subject.Dispose();if(window.IsVisible)window.Close();}
    }

    private static PreparedAppUpdate UiPreparedUpdate(string testRoot)
    {
        string root=Path.Combine(testRoot,"uncreated-update-"+Guid.NewGuid().ToString("N"));
        return new PreparedAppUpdate(root,Path.Combine(root,"stage"),Path.Combine(root,"backup"),AppContext.BaseDirectory,"999.0.0");
    }

    private sealed class UiUpdateService : IAppUpdateService
    {
        private readonly List<CancellationTokenRegistration> _registrations=[];
        internal int CheckCalls {get;private set;}
        internal int PrepareCalls {get;private set;}
        internal bool CheckCancelled {get;private set;}
        internal bool Disposed {get;private set;}
        internal Version? CurrentVersion {get;private set;}
        internal TaskCompletionSource<AppUpdateRelease?>? CheckCompletion {get;private set;}
        internal TaskCompletionSource<PreparedAppUpdate>? PrepareCompletion {get;private set;}
        public Task<AppUpdateRelease?> CheckAsync(Version currentVersion,CancellationToken cancel)
        {
            CurrentVersion=currentVersion;
            CheckCalls++;
            var completion=new TaskCompletionSource<AppUpdateRelease?>(TaskCreationOptions.RunContinuationsAsynchronously);
            CheckCompletion=completion;
            _registrations.Add(cancel.Register(()=>{CheckCancelled=true;completion.TrySetCanceled(cancel);}));
            return completion.Task;
        }
        public Task<PreparedAppUpdate> PrepareAsync(AppUpdateRelease release,string currentDirectory,CancellationToken cancel)
        {
            PrepareCalls++;
            var completion=new TaskCompletionSource<PreparedAppUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
            PrepareCompletion=completion;
            _registrations.Add(cancel.Register(()=>completion.TrySetCanceled(cancel)));
            return completion.Task;
        }
        public void Dispose()
        {
            Disposed=true;
            foreach(var registration in _registrations)registration.Dispose();
            _registrations.Clear();
        }
    }
}
