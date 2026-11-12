using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestQueueSkipResume(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Queue skip/resume WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing real queue skip/resume control: "+name));
        static void InvokeUi(Dispatcher dispatcher,Action action)
        {
            var operation=dispatcher.BeginInvoke(action);
            PumpUntil(()=>operation.Task.IsCompleted,"The actual Dispatcher operation must deliver its queue action.");
            operation.Task.GetAwaiter().GetResult();
        }
        static void Click(Window window,string name)=>InvokeUi(window.Dispatcher,
            ()=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
        static Window LoadWindow()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);return (Window)XamlReader.Load(reader);
        }
        static void Select(DataGrid grid,params DownloadItem[] rows)
        {
            grid.SelectedItems.Clear();
            foreach(var row in rows)grid.SelectedItems.Add(row);
        }
        static bool IsAlive(int pid)
        {
            try{using var process=Process.GetProcessById(pid);return !process.HasExited;}
            catch(ArgumentException){return false;}
        }
        static int ReadPid(string path)
        {
            try{return int.TryParse(File.ReadAllText(path),out int pid)?pid:0;}
            catch(IOException){return 0;}
        }
        static MenuItem MenuAction(DataGrid grid,string prefix)
        {
            var menu=grid.ContextMenu??throw new InvalidOperationException("The actual grid context menu is missing.");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.StartsWith(prefix,StringComparison.Ordinal)==true);
        }
        static object? PrivateField(NativeWindow owner,string name)=>(typeof(NativeWindow).GetField(name,
            BindingFlags.NonPublic|BindingFlags.Instance)??throw new MissingFieldException(typeof(NativeWindow).FullName,name)).GetValue(owner);
        static void AnswerMessage(string title,int answer,Action operation,Action? beforeAnswer=null)
        {
            uint thread=GetCurrentThreadId();bool observed=false;Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>
            {
                bool present=false;
                EnumThreadWindows(thread,(handle,_)=>
                {
                    var caption=new StringBuilder(256);GetWindowTextW(handle,caption,caption.Capacity);
                    if(caption.ToString()!=title)return true;
                    present=true;return false;
                },IntPtr.Zero);
                if(!present)return;
                try{beforeAnswer?.Invoke();beforeAnswer=null;}
                catch(Exception error){failure=error;}
                if(DismissDialog(thread,title,failure is null?answer:7)){observed=true;timer.Stop();}
            };
            using var watchdog=new CancellationTokenSource();
            _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
            {if(!task.IsCanceled)DismissDialog(thread,title,7);},TaskScheduler.Default);
            timer.Start();try{operation();}finally{watchdog.Cancel();timer.Stop();}
            if(failure is not null)throw new InvalidOperationException("Queue modal inspection failed.",failure);
            Require(observed,"The real confirmation was not displayed: "+title);
        }

        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The compiled metadata/download fixture is required.");
        string root=Path.Combine(testRoot,"queue-skip-resume");Directory.CreateDirectory(root);
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        // The real coordinator owns the child and publication. This fixture
        // writes media itself; these files satisfy its ordinary tool preflight.
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"preflight fixture");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"preflight fixture");
        string? oldPath=Environment.GetEnvironmentVariable("PATH"),oldGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE"),
            oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        var oldCulture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+oldPath);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        Window window=null!;NativeWindow subject=null!;
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        string clearTitle=ErrorMessages.Text("Очистить всю очередь","Clear entire queue");
        NativeWindow Create(Window owner)=>new(owner,storage,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
            localPosterFfmpegPath:Path.Combine(root,"no-local-poster-tool.exe"));
        try
        {
            window=LoadWindow();subject=Create(window);window.Show();
            subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",Container="MP4"});
            var queue=Control<DataGrid>(window,"QueueGrid");
            var search=Control<TextBox>(window,"QueueSearchBox");
            var status=Control<ComboBox>(window,"QueueStatusFilter");
            string[] urls=["https://startup-download.invalid/scope-show-first","https://startup-download.invalid/scope-show-second",
                "https://startup-download.invalid/scope-hidden","https://startup-download.invalid/scope-completed"];
            var first=new DownloadItem{Url=urls[0]};var second=new DownloadItem{Url=urls[1]};
            var hidden=new DownloadItem{Url=urls[2]};
            string published=Path.Combine(root,"previously-published.mp4");File.WriteAllText(published,"published media sentinel");
            var completed=new DownloadItem{Url=urls[3],Status="Completed",State="Done",Progress=100,AttemptFinished=true,
                Path=published,OwnedPaths=[published],OutputRoot=root};
            subject.SessionState.AddBatch([first,second,hidden,completed],subject.ReadOptions());subject.Reindex();
            foreach(var row in subject.SessionState.Queue)subject.Preview.RequestRow(row);
            PumpUntil(()=>urls.All(url=>subject.Preview.GetCached(url) is not null),"Seed metadata must finish before isolated action assertions.");

            // Disabled controls alone are insufficient: raised real events must
            // also respect restore, maintenance and failed-checkpoint barriers.
            foreach(string name in new[]{"_checkpointReadOnly","_restoringSession","_profileBackupBusy","_profileBackupRecoveryRequired",
                "_profileBackupReplaying","_appUpdateHandoff","_legacyEngineBlocked","_dependencyOperationRunning","_startingSession"})
            {
                var field=typeof(NativeWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)
                    ??throw new MissingFieldException(typeof(NativeWindow).FullName,name);
                second.Status="Skipped";second.State="Skipped";second.AttemptFinished=true;
                Select(queue,first,second);var previousAction=PrivateField(subject,"_queueItemActionTask");
                var previousRun=subject.SessionState.RunningTask;field.SetValue(subject,true);
                try
                {
                    subject.UpdateState();Click(window,"SkipQueueItemsButton");Click(window,"ResumeQueueItemsButton");
                    Require(first.Status=="Queued" && second.Status=="Skipped" && subject.SessionState.Coordinator is null &&
                        subject.SessionState.StartedAt is null && !File.Exists(storage.HistoryPath) &&
                        ReferenceEquals(previousAction,PrivateField(subject,"_queueItemActionTask")) &&
                        ReferenceEquals(previousRun,subject.SessionState.RunningTask) && PrivateField(subject,"_queueItemActionBusy") is false &&
                        (bool)PrivateField(subject,"_startingSession")! == (name=="_startingSession"),
                        "Raised Skip/Resume events bypassed "+name+".");
                }
                finally{field.SetValue(subject,false);second.Status="Queued";second.State="Queued";subject.UpdateState();}
            }

            Select(queue,first,second,hidden);search.Text="scope-show-";
            PumpUntil(()=>queue.Items.Count==2 && queue.SelectedItems.Count==2,"Filtering must remove the hidden row from the actionable multi-selection.");
            var skipMenu=MenuAction(queue,"Пропустить");
            Require(skipMenu.IsEnabled && Control<Button>(window,"SkipQueueItemsButton").IsEnabled,
                "The queue menu and button must expose the same visible multi-selection.");
            InvokeUi(skipMenu.Dispatcher,()=>skipMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            PumpUntil(()=>first.Status=="Skipped" && second.Status=="Skipped" && first.AttemptFinished && second.AttemptFinished,
                "The actual Skip button must mark both selected rows as Skipped.");
            Require(hidden.Status=="Queued" && completed.Status=="Completed" && subject.SessionState.Coordinator is null &&
                subject.SessionState.StartedAt is null && !File.Exists(storage.HistoryPath) && File.ReadAllText(published)=="published media sentinel" &&
                first.StateLabel.Contains("Пропущ",StringComparison.Ordinal) && second.StateLabel.Contains("Пропущ",StringComparison.Ordinal),
                "Skipping must preserve hidden/completed rows and published files, localize state, and never start a session or record success.");
            status.SelectedValue="Skipped";
            PumpUntil(()=>queue.Items.Count==2,"The real status filter must expose Skipped as a machine state.");
            Require(!Control<Button>(window,"SkipQueueItemsButton").IsEnabled && Control<Button>(window,"ResumeQueueItemsButton").IsEnabled,
                "An all-Skipped selection must offer Resume rather than Skip again.");
            Require(!MenuAction(queue,"Пропустить").IsEnabled && MenuAction(queue,"Продолжить").IsEnabled,
                "The context menu must refresh action eligibility after both selected rows become Skipped.");
            subject.SaveCheckpoint();
            Require(storage.Restore()!.Queue.Count(row=>row.Status=="Skipped")==2,"Skipped states must survive the actual checkpoint decoder.");

            // Reopen the product and answer its genuine recovery prompt. The
            // old UI is disposed before the restored profile receives workers.
            subject.Dispose();window.Close();window=LoadWindow();
            AnswerMessage(ErrorMessages.Text("Восстановить предыдущую сессию","Restore previous session"),6,
                ()=>subject=Create(window));
            window.Show();queue=Control<DataGrid>(window,"QueueGrid");search=Control<TextBox>(window,"QueueSearchBox");
            status=Control<ComboBox>(window,"QueueStatusFilter");
            first=subject.SessionState.Queue.Single(row=>row.Url==urls[0]);second=subject.SessionState.Queue.Single(row=>row.Url==urls[1]);
            hidden=subject.SessionState.Queue.Single(row=>row.Url==urls[2]);completed=subject.SessionState.Queue.Single(row=>row.Url==urls[3]);
            Require(first.Status=="Skipped" && second.Status=="Skipped" && completed.Status=="Completed" &&
                subject.SessionState.Coordinator is null && subject.SessionState.StartedAt is null && !File.Exists(storage.HistoryPath),
                "The real restore window must retain Skipped/Completed without automatic downloading or success records.");
            Click(window,"StartButton");
            PumpUntil(()=>hidden.Status=="Completed" && subject.SessionState.Coordinator is null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Ordinary Start must download the eligible row and finish normally.");
            Require(first.Status=="Skipped" && second.Status=="Skipped" && storage.ReadHistory().Keys.SequenceEqual(new[]{hidden.Url}) &&
                completed.OwnedPaths.SequenceEqual(new[]{published}) && File.ReadAllText(published)=="published media sentinel",
                "Ordinary Start must exclude skipped and previously completed rows without changing their ownership.");
            string hiddenOutput=hidden.Path;

            var lateSkip=new DownloadItem{Url="https://startup-download.invalid/skip-during-clear"};
            subject.SessionState.TryAdd(lateSkip);subject.Reindex();search.Text="no visible matches";
            PumpUntil(()=>queue.Items.Count==0,"Clear must be tested with all affected rows hidden.");
            AnswerMessage(clearTitle,7,()=>Click(window,"ClearQueueButton"));
            Require(subject.SessionState.Queue.Count==5,"Declining Clear must preserve every skipped and non-skipped row.");
            string lateUrl="https://startup-download.invalid/late-clear-arrival";
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearQueueButton"),()=>subject.ReceiveArguments([lateUrl]));
            PumpUntil(()=>subject.SessionState.Queue.Count==4,"Completed-first Clear must remove only its captured finished rows.");
            Require(subject.SessionState.Queue.Contains(first) && subject.SessionState.Queue.Contains(second) && subject.SessionState.Queue.Contains(lateSkip) &&
                lateSkip.Status=="Queued" &&
                subject.SessionState.Queue.Any(row=>row.Url==lateUrl) && !subject.SessionState.Queue.Contains(hidden) && !subject.SessionState.Queue.Contains(completed) &&
                File.ReadAllText(published)=="published media sentinel" && File.Exists(hiddenOutput),
                "Completed-first Clear must retain queued/skipped rows, late IPC arrivals and published files.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearQueueButton"),()=>
            {lateSkip.Status="Skipped";lateSkip.State="Skipped";lateSkip.AttemptFinished=true;});
            PumpUntil(()=>subject.SessionState.Queue.Count==3,"The next mixed Clear must remove only the captured late queued arrival.");
            Require(subject.SessionState.Queue.All(row=>row.Status=="Skipped"),
                "Whole Clear must recheck late Skipped statuses after the modal and retain original skipped rows.");
            AnswerMessage(clearTitle,7,()=>Click(window,"ClearQueueButton"));
            Require(subject.SessionState.Queue.Count==3,"Declining an all-Skipped Clear must keep skipped entries.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0,"Clear must empty an entirely skipped queue.");

            // Actual WPF -> coordinator -> owned native child. One worker is
            // held in the fixture, so Skip must release it to the next URL.
            // The keeper keeps the SAME engine alive while Resume is clicked.
            Click(window,"ResetQueueFiltersButton");
            string gate=Path.Combine(root,"release-active");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            var slow=new DownloadItem{Url="https://startup-download.invalid/skip-first-timer-slow"};
            var next=new DownloadItem{Url="https://startup-download.invalid/skip-next"};
            var keeper=new DownloadItem{Url="https://startup-download.invalid/skip-keeper-timer-slow"};
            var idleSkipped=new DownloadItem{Url="https://startup-download.invalid/skip-idle",Status="Skipped",State="Skipped",AttemptFinished=true};
            var idleSecond=new DownloadItem{Url="https://startup-download.invalid/skip-idle-second",Status="Skipped",State="Skipped",AttemptFinished=true};
            subject.SessionState.AddBatch([slow,next,keeper,idleSkipped,idleSecond],subject.ReadOptions());subject.Reindex();
            Click(window,"StartButton");
            PumpUntil(()=>slow.Status=="Running" && ReadPid(gate+".started")>0,"The first real worker must hold its native child before Skip.");
            int firstPid=ReadPid(gate+".started");var active=subject.SessionState.Coordinator;
            Require(active is not null && next.Status=="Queued" && keeper.Status=="Queued" && idleSkipped.Status=="Skipped" && idleSecond.Status=="Skipped",
                "With one worker, later eligible rows must wait while a pre-skipped row stays outside admission.");
            Select(queue,slow);Click(window,"SkipQueueItemsButton");
            PumpUntil(()=>slow.Status=="Skipped" && slow.AttemptFinished && next.Status=="Completed" && keeper.Status=="Running" &&
                ReadPid(gate+".started")>0 && ReadPid(gate+".started")!=firstPid,
                "Skip must join its child, publish the next waiting URL and let the keeper occupy the same worker.");
            string retained=slow.StageDir;
            Require(!IsAlive(firstPid) && retained.Length>0 && Directory.Exists(retained) && !storage.ReadHistory().ContainsKey(slow.Url) &&
                ReferenceEquals(subject.SessionState.Coordinator,active) && idleSkipped.Status=="Skipped",
                "Skip must retain its owned stage, avoid success, finish its own native child and preserve the other worker/session.");
            PumpUntil(()=>Control<Button>(window,"ResumeQueueItemsButton").IsEnabled,
                "The joined Skip operation must release its controls before Resume is requested.");
            var downloads=Control<DataGrid>(window,"DownloadGrid");
            PumpUntil(()=>downloads.Items.Contains(slow) && downloads.Items.Contains(next),"The actual Downloads view must receive both native attempt states.");
            Select(downloads,slow,next);string nextOutput=next.Path;
            var resumeMenu=MenuAction(downloads,"Продолжить");
            Require(resumeMenu.IsEnabled,"Downloads must offer Resume for a skipped row in a mixed selection.");
            InvokeUi(resumeMenu.Dispatcher,()=>resumeMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            PumpUntil(()=>slow.Status=="Queued" && active!.IsPending(slow),"The real Resume button must readmit the skipped URL to the live session.");
            Require(ReferenceEquals(subject.SessionState.Coordinator,active) && slow.StageDir==retained && keeper.Status=="Running" &&
                next.Status=="Completed" && next.Path==nextOutput,
                "Live Resume must preserve its retained stage and enqueue behind the current worker without replacing the engine.");
            File.WriteAllText(gate,"release");
            PumpUntil(()=>slow.Status=="Completed" && keeper.Status=="Completed" && subject.SessionState.Coordinator is null &&
                subject.SessionState.RunningTask?.IsCompleted==true,"The same live engine must complete the resumed URL after its keeper releases.");
            Require(!Directory.Exists(retained) && new[]{slow,next,keeper}.All(row=>File.Exists(row.Path) && storage.ReadHistory().ContainsKey(row.Url)) &&
                idleSkipped.Status=="Skipped" && idleSecond.Status=="Skipped" && !storage.ReadHistory().ContainsKey(idleSkipped.Url) &&
                !storage.ReadHistory().ContainsKey(idleSecond.Url),
                "Completed resume must release retained staging, publish exact ownership/history and leave unrequested skipped work untouched.");
            var existingOutputs=new[]{slow,next,keeper}.SelectMany(row=>row.OwnedPaths).ToArray();
            subject.UpdateState();
            Require(!Control<Button>(window,"StartButton").IsEnabled,"Only skipped and completed rows must disable ordinary Start.");
            var completedRun=subject.SessionState.RunningTask;
            Click(window,"StartButton");
            Require(idleSkipped.Status=="Skipped" && idleSecond.Status=="Skipped" && subject.SessionState.Coordinator is null &&
                ReferenceEquals(completedRun,subject.SessionState.RunningTask) && PrivateField(subject,"_startingSession") is false,
                "A raised ordinary Start event must not download an all-ineligible queue.");
            Select(queue,idleSkipped,idleSecond);Click(window,"ResumeQueueItemsButton");
            PumpUntil(()=>idleSkipped.Status=="Completed" && idleSecond.Status=="Completed" && subject.SessionState.Coordinator is null &&
                subject.SessionState.RunningTask?.IsCompleted==true,"Explicit Resume must start both selected skipped rows when no coordinator remains.");
            Require(existingOutputs.All(File.Exists) && storage.ReadHistory().ContainsKey(idleSkipped.Url) && storage.ReadHistory().ContainsKey(idleSecond.Url),
                "Idle multi-resume must preserve other published attempts and record only the two genuine new successes.");

            // Clearing a queue containing only a retained skipped job must
            // remove its own partial stage rather than leak resumable data.
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0,"Completed fixture rows must clear before retained-stage cleanup.");
            gate=Path.Combine(root,"release-clear-only");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            var clearSkipped=new DownloadItem{Url="https://startup-download.invalid/clear-only-timer-slow"};
            subject.SessionState.TryAdd(clearSkipped);subject.Reindex();Click(window,"StartButton");
            PumpUntil(()=>clearSkipped.Status=="Running" && File.Exists(gate+".started"),"The retained-clear fixture must own a real native attempt.");
            Select(queue,clearSkipped);Click(window,"SkipQueueItemsButton");
            PumpUntil(()=>clearSkipped.Status=="Skipped" && clearSkipped.AttemptFinished && subject.SessionState.Coordinator is null,
                "The only skipped native attempt must finish while retaining its partial context.");
            retained=clearSkipped.StageDir;Require(retained.Length>0 && Directory.Exists(retained),"The skipped job must have an owned stage to clear.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0 && !Directory.Exists(retained),
                "An all-skipped Clear must remove the row only after its owned retained stage has been deleted.");
            Require(!storage.ReadHistory().ContainsKey(clearSkipped.Url) && existingOutputs.All(File.Exists) && File.Exists(published),
                "Clearing skipped work must never count it successful or delete earlier published media.");
            Console.WriteLine("PASS: real filtered multi-select Skip/Resume, guarded events, Skipped recovery, ordinary Start exclusion, mixed/all-skipped Clear and modal races, native worker handoff/live+idle resume and retained-stage cleanup");
        }
        finally
        {
            if(subject?.SessionState.Coordinator is {} active)
            {active.Stop();PumpUntil(()=>subject.SessionState.Coordinator is null,"Queue skip/resume fixture cancellation must join its worker.");}
            subject?.Dispose();window?.Close();
            Environment.SetEnvironmentVariable("PATH",oldPath);Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",oldGate);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);CultureInfo.CurrentUICulture=oldCulture;
        }
    }
}
