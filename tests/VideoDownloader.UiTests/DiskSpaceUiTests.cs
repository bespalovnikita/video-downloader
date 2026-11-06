using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestDiskSpaceControls(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Disk-space WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || clock.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing disk-space control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Stop(NativeWindow subject,Window window)
        {
            Click(window,"StopButton");
            PumpUntil(()=>subject.SessionState.Coordinator is null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Stopping the owned fixture must join the actual session and its child");
        }
        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"The actual native fixture host is required");
        string root=Path.Combine(testRoot,"disk-space-ui");Directory.CreateDirectory(root);
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"fixture preflight");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"fixture preflight");
        string? oldPath=Environment.GetEnvironmentVariable("PATH"),oldGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE"),
            oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        CultureInfo oldCulture=CultureInfo.CurrentUICulture;
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+oldPath);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        NativeWindow? subject=null;Window? window=null;
        var provider=new UiDiskSpaceProvider();
        int dialogs=0;Action<Window> respond=dialog=>Click(dialog,"CancelDiskSpaceStartButton");
        var responder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
        var observed=new HashSet<Window>();
        responder.Tick+=(_,_)=>
        {
            var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate=>candidate.Name=="DiskSpaceWarningDialog" && candidate.IsVisible && observed.Add(candidate));
            if(dialog is null)return;
            dialogs++;
            Require(Control<TextBlock>(dialog,"DiskSpaceWarningDetails").Text.Length>0,"The real modal warning must explain measured space and estimates");
            respond(dialog);
        };
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);window=(Window)XamlReader.Load(reader);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            subject=new NativeWindow(window,storage,executable,automaticUpdates:false,diskSpaceProvider:provider);
            window.Show();window.UpdateLayout();
            var options=new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",EmbedMetadata=false,EmbedChapters=false};
            subject.ApplyOptions(options);
            var row=new DownloadItem{Url="https://startup-download.invalid/timer-slow",Title="Disk-space source",Order=1};
            var format=new MediaFormat("18","mp4",160,90,10,"avc1","mp4a","en",2L*1024*1024*1024,false);
            row.Selection=new(row.Url,Format:format);
            subject.SessionState.Queue.Add(row);subject.Reindex();
            var refresh=subject.RefreshDiskSpaceAsync();
            PumpUntil(()=>refresh.IsCompleted,"The real destination summary did not finish its background query");
            Require(Control<TextBlock>(window,"DiskSpaceSummaryText").Text.Contains("резерв",StringComparison.Ordinal) &&
                provider.Calls>0 && row.Status=="Queued" && subject.SessionState.Coordinator is null,
                "The main window must render measured free space and a processing allowance without starting work");

            string kept=Path.Combine(root,"kept-result.mp4");File.WriteAllText(kept,"previous result sentinel");
            row.Path=kept;row.OwnedPaths=[kept];row.OutputRoot=root;row.AttemptFinished=true;
            provider.Free=64L*1024*1024;
            responder.Start();
            Click(window,"StartButton");
            PumpUntil(()=>dialogs==1 && Control<Button>(window,"StartButton").IsEnabled,
                "Low measured space must show an actual Cancel/Continue warning before admission");
            Require(subject.SessionState.Coordinator is null && row.Status=="Queued" && row.Path==kept &&
                row.OwnedPaths.SequenceEqual([kept]) && File.ReadAllText(kept)=="previous result sentinel" &&
                !File.Exists(storage.HistoryPath),"Cancel must keep the queue, prior ownership and history unchanged");

            // The modal dispatcher can change destination settings before a
            // nominal Continue. That consent must not apply to another plan.
            respond=dialog=>
            {
                Control<TextBox>(window,"OutputBox").Text=Path.Combine(root,"changed-output");
                Click(dialog,"ContinueDiskSpaceStartButton");
            };
            Click(window,"StartButton");
            PumpUntil(()=>dialogs==2 && Control<TextBlock>(window,"DiskSpaceWarningText").Text.Contains("изменились",StringComparison.Ordinal),
                "A changed destination during the real warning must postpone Start");
            Require(subject.SessionState.Coordinator is null && row.Path==kept && row.Status=="Queued",
                "Changing options during consent must not reset an attempt or launch a child");

            // A normal explicit Continue must still reach the actual existing
            // coordinator. Its fixture waits until this scenario stops it.
            string gate=Path.Combine(root,"continue-gate");
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            respond=dialog=>Click(dialog,"ContinueDiskSpaceStartButton");
            Click(window,"StartButton");
            PumpUntil(()=>dialogs==3 && subject.SessionState.Coordinator?.IsRunning==true && File.Exists(gate+".started"),
                "Explicit Continue must admit the unchanged plan and start the actual owned fixture");
            Require(row.Status=="Running" && File.Exists(kept) && Control<Button>(window,"StopButton").IsEnabled,
                "Continuing a low-space warning must preserve earlier media and leave Stop usable");
            Stop(subject,window);
            Require(!File.Exists(storage.HistoryPath),"Stopping before a verified result must not record a successful URL");

            // A fresh session starts with room. A later low measurement warns
            // once without stopping or restarting that already running session.
            provider.Free=100L*1024*1024*1024;
            gate=Path.Combine(root,"live-gate");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            int before=dialogs;
            Click(window,"StartButton");
            PumpUntil(()=>subject.SessionState.Coordinator?.IsRunning==true && File.Exists(gate+".started"),
                "The healthy-space plan did not start its actual slow fixture");
            var running=subject.SessionState.Coordinator;
            Require(dialogs==before,"Enough space must not add a modal confirmation");
            provider.Free=32L*1024*1024;
            refresh=subject.RefreshDiskSpaceAsync();
            PumpUntil(()=>refresh.IsCompleted && Control<TextBlock>(window,"DiskSpaceWarningText").Visibility==Visibility.Visible,
                "A low-space live measurement must show a visible nonmodal warning");
            string journal=subject.Journal.Snapshot();
            int warnings=journal.Split("Мало места на диске ",StringSplitOptions.None).Length-1;
            Require(warnings==1 && ReferenceEquals(running,subject.SessionState.Coordinator) && running!.IsRunning &&
                Control<Button>(window,"StopButton").IsEnabled && row.Status=="Running",
                "A low-space warning must not stop, replace or restart the active session");
            refresh=subject.RefreshDiskSpaceAsync();PumpUntil(()=>refresh.IsCompleted,"The repeated live check did not finish");
            Require(subject.Journal.Snapshot().Split("Мало места на диске ",StringSplitOptions.None).Length-1==warnings,
                "The same low volume must not spam another warning on every poll");
            Stop(subject,window);

            // A queue change while a slow probe is pending must invalidate the
            // preflight before either the disk dialog or admission begins.
            provider.Free=100L*1024*1024*1024;
            refresh=subject.RefreshDiskSpaceAsync();PumpUntil(()=>refresh.IsCompleted,"The idle measurement did not settle before the guarded preflight");
            provider.HoldNext=true;
            Click(window,"StartButton");
            PumpUntil(()=>provider.Pending is not null,"The controlled asynchronous disk query did not start");
            var added=new DownloadItem{Url="https://startup-download.invalid/added-during-check"};
            subject.SessionState.Queue.Add(added);
            provider.Release();
            PumpUntil(()=>Control<TextBlock>(window,"DiskSpaceWarningText").Text.Contains("изменились",StringComparison.Ordinal),
                "A changed queue while the disk probe waits must invalidate that plan");
            Require(subject.SessionState.Coordinator is null && row.Status!="Running" && added.Status=="Queued" && dialogs==before,
                "No late free-space result may start a changed queue or automatically admit the newly added URL");
            Console.WriteLine("PASS: real WPF disk summary, Cancel/Continue, changed-plan guards and live low-space warnings without autostart");
        }
        finally
        {
            responder.Stop();provider.Release();
            if(subject?.SessionState.Coordinator?.IsRunning==true && window is not null)Stop(subject,window);
            subject?.Dispose();if(window?.IsVisible==true)window.Close();
            CultureInfo.CurrentUICulture=oldCulture;
            Environment.SetEnvironmentVariable("PATH",oldPath);
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",oldGate);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
        }
    }
    private sealed class UiDiskSpaceProvider:IDiskSpaceProvider
    {
        internal long Free=100L*1024*1024*1024;
        internal int Calls;
        internal bool HoldNext;
        internal TaskCompletionSource<DiskSpaceSnapshot>? Pending;
        private string _destination="";
        public Task<DiskSpaceSnapshot> ReadAsync(string destination,CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();Interlocked.Increment(ref Calls);
            if(HoldNext)
            {
                HoldNext=false;_destination=destination;
                var pending=new TaskCompletionSource<DiskSpaceSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
                Pending=pending;return pending.Task.WaitAsync(cancellationToken);
            }
            return Task.FromResult(new DiskSpaceSnapshot(destination,"fixture-volume",Free,DiskSpaceAvailability.Available,""));
        }
        internal void Release()
        {
            var pending=Pending;Pending=null;
            pending?.TrySetResult(new(_destination,"fixture-volume",Free,DiskSpaceAvailability.Available,""));
        }
    }
}
