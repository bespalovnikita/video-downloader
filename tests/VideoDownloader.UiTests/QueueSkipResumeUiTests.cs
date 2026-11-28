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
        string clearTitle=ErrorMessages.Text("Очистить очередь","Clear queue");
        string completedClearTitle=ErrorMessages.Text("Убрать завершённые записи","Remove completed entries");
        NativeWindow Create(Window owner)=>new(owner,storage,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
            localPosterFfmpegPath:Path.Combine(root,"no-local-poster-tool.exe"));
        void ResumeWithAnswer(DataGrid grid,DownloadItem row,string answer,Action? beforeAnswer=null)
        {
            bool observed=false;Exception? failure=null;
            var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            observer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate=>
                    candidate.FindName("DuplicateUrl") is TextBox text && text.Text==row.Url);
                if(dialog is null)return;
                observer.Stop();observed=true;
                try{beforeAnswer?.Invoke();}
                catch(Exception error){failure=error;}
                Control<Button>(dialog,failure is null?answer:"CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            observer.Start();
            try
            {
                Select(grid,row);Click(window,"ResumeQueueItemsButton");
                PumpUntil(()=>observed && PrivateField(subject,"_queueItemActionBusy") is false,
                    "The actual live Resume must display and finish its history decision for "+row.Url);
            }
            finally{observer.Stop();}
            if(failure is not null)throw new InvalidOperationException("Live Resume modal inspection failed.",failure);
        }
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

            Select(queue,first);
            first.Status="Failed";first.AttemptFinished=true;first.Progress=90;
            Require(!MenuAction(queue,"Докачать после ошибки").IsEnabled,
                "An error and percentage alone must not enable resuming without retained data.");
            first.ResumeState=new DownloadResumeState{RetainedAfterFailure=true};
            first.Progress=10;
            Require(!MenuAction(queue,"Докачать после ошибки").IsEnabled,
                "The context action requires strictly more than ten percent.");
            first.Progress=90;
            Require(MenuAction(queue,"Докачать после ошибки").IsEnabled,
                "The actual context menu must offer a separate action for retained failed downloads.");
            first.ResumeState=null;first.Progress=0;first.AttemptFinished=false;first.Status="Queued";
            queue.SelectedItems.Clear();

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
            AnswerMessage(completedClearTitle,7,()=>Click(window,"ClearQueueButton"));
            Require(subject.SessionState.Queue.Count==5,"Declining Clear must preserve every skipped and non-skipped row.");
            string lateUrl="https://startup-download.invalid/late-clear-arrival";
            AnswerMessage(completedClearTitle,6,()=>Click(window,"ClearQueueButton"),()=>subject.ReceiveArguments([lateUrl]));
            PumpUntil(()=>subject.SessionState.Queue.Count==4,"Completed-first Clear must remove only its captured finished rows.");
            Require(subject.SessionState.Queue.Contains(first) && subject.SessionState.Queue.Contains(second) && subject.SessionState.Queue.Contains(lateSkip) &&
                lateSkip.Status=="Queued" &&
                subject.SessionState.Queue.Any(row=>row.Url==lateUrl) && !subject.SessionState.Queue.Contains(hidden) && !subject.SessionState.Queue.Contains(completed) &&
                File.ReadAllText(published)=="published media sentinel" && File.Exists(hiddenOutput),
                "Completed-first Clear must retain queued/skipped rows, late IPC arrivals and published files.");
            Click(window,"ClearQueueButton");
            Require(subject.SessionState.Queue.Count==4,
                "Completed-only clear must not fall through to unfinished or skipped records when no completed rows remain.");
            AnswerMessage(clearTitle,7,()=>Click(window,"ClearAllQueueButton"));
            Require(subject.SessionState.Queue.Count==4,"Declining explicit full clear must keep skipped entries.");
            string afterFullClear="https://startup-download.invalid/after-full-clear-question";
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearAllQueueButton"),()=>
            {
                lateSkip.Status="Skipped";lateSkip.State="Skipped";lateSkip.AttemptFinished=true;
                subject.ReceiveArguments([afterFullClear]);
            });
            PumpUntil(()=>subject.SessionState.Queue.Count==1,"Explicit full clear must remove its captured skipped and queued entries.");
            Require(subject.SessionState.Queue.Single().Url==afterFullClear && !subject.SessionState.Queue.Contains(first) &&
                !subject.SessionState.Queue.Contains(second) && !subject.SessionState.Queue.Contains(lateSkip),
                "Full clear must honor its explicit scope including skipped rows while preserving a late arrival outside its snapshot.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearAllQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0,"A new full-clear confirmation must remove the late arrival.");

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
            // The original admission already obtained consent. Even if history
            // gains this URL meanwhile, this exact retained generation keeps it.
            storage.AppendSuccess(slow.Url);bool repeatedConsent=false;
            var consentObserver=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            consentObserver.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate=>
                    candidate.FindName("DuplicateUrl") is TextBox text && text.Text==slow.Url);
                if(dialog is null)return;
                repeatedConsent=true;consentObserver.Stop();
                Control<Button>(dialog,"CancelButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            consentObserver.Start();
            try
            {
                InvokeUi(resumeMenu.Dispatcher,()=>resumeMenu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
                PumpUntil(()=>repeatedConsent || slow.Status=="Queued" && active!.IsPending(slow),
                    "The real Resume button must readmit the skipped URL to the live session.");
            }
            finally{consentObserver.Stop();}
            Require(!repeatedConsent,"An exact joined Skip generation must retain its original session consent.");
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
            AnswerMessage(completedClearTitle,6,()=>Click(window,"ClearQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0,"Completed fixture rows must clear before retained-stage cleanup.");
            gate=Path.Combine(root,"release-clear-only");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            var clearSkipped=new DownloadItem{Url="https://startup-download.invalid/clear-only-timer-slow"};
            subject.SessionState.TryAdd(clearSkipped);subject.Reindex();Click(window,"StartButton");
            PumpUntil(()=>clearSkipped.Status=="Running" && File.Exists(gate+".started"),"The retained-clear fixture must own a real native attempt.");
            Select(queue,clearSkipped);Click(window,"SkipQueueItemsButton");
            PumpUntil(()=>clearSkipped.Status=="Skipped" && clearSkipped.AttemptFinished && subject.SessionState.Coordinator is null,
                "The only skipped native attempt must finish while retaining its partial context.");
            retained=clearSkipped.StageDir;Require(retained.Length>0 && Directory.Exists(retained),"The skipped job must have an owned stage to clear.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearAllQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0 && !Directory.Exists(retained),
                "An all-skipped Clear must remove the row only after its owned retained stage has been deleted.");
            Require(!storage.ReadHistory().ContainsKey(clearSkipped.Url) && existingOutputs.All(File.Exists) && File.Exists(published),
                "Clearing skipped work must never count it successful or delete earlier published media.");

            // Fresh imported rows have never passed this coordinator's history
            // consent. Skipping them must not create that consent implicitly.
            gate=Path.Combine(root,"release-history");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            var historyKeeper=new DownloadItem{Url="https://startup-download.invalid/resume-history-keeper-timer-slow"};
            subject.SessionState.TryAdd(historyKeeper);subject.Reindex();Click(window,"StartButton");
            PumpUntil(()=>historyKeeper.Status=="Running" && ReadPid(gate+".started")>0,
                "History consent must run alongside an actual held native keeper.");
            active=subject.SessionState.Coordinator;
            var imported=subject.SessionState.ImportPortable(new[]{"cancel","yes","corrupt","stopped","changed"}
                .Select(name=>new PortableQueueEntry("https://startup-download.invalid/resume-history-"+name,name)).ToArray(),subject.ReadOptions()).ToArray();
            subject.Reindex();foreach(var row in imported)storage.AppendSuccess(row.Url);
            Select(queue,imported);Click(window,"SkipQueueItemsButton");
            PumpUntil(()=>imported.All(row=>row.Status=="Skipped" && row.AttemptFinished) &&
                PrivateField(subject,"_queueItemActionBusy") is false,"The real Skip action must leave imported rows unadmitted.");
            Require(imported.All(row=>row.DeferredToNextStart && !active!.IsPending(row)),
                "A deferred Skip must not silently register work with the active coordinator.");
            byte[] beforeCancel=File.ReadAllBytes(storage.HistoryPath);int beforeCount=subject.SessionState.Queue.Count;
            ResumeWithAnswer(queue,imported[0],"CancelButton");
            Require(subject.SessionState.Queue.Count==beforeCount && subject.SessionState.Queue.Contains(imported[0]) &&
                imported[0].Status=="Skipped" && imported[0].DeferredToNextStart && !active!.IsPending(imported[0]) &&
                !subject.SessionState.Urls.Contains(imported[0].Url) && File.ReadAllBytes(storage.HistoryPath).SequenceEqual(beforeCancel),
                "Cancelling fresh live Resume must keep the row, deferred flag and history without admission.");
            ResumeWithAnswer(queue,imported[0],"NoButton");
            Require(!subject.SessionState.Queue.Contains(imported[0]) && !active!.IsPending(imported[0]) &&
                storage.ReadHistory().ContainsKey(imported[0].Url) && existingOutputs.All(File.Exists),
                "Declining fresh live Resume must remove only the selected entry and preserve successful history/files.");
            ResumeWithAnswer(queue,imported[4],"YesButton",()=>imported[4].Selection=new DownloadSelection(imported[4].Url,new DownloadSection(0,1)));
            Require(imported[4].Status=="Skipped" && imported[4].DeferredToNextStart && !active!.IsPending(imported[4]) &&
                imported[4].Selection?.IsPartial==true,"A changed row plan must invalidate the captured history decision.");
            imported[4].Selection=null;ResumeWithAnswer(queue,imported[4],"NoButton");

            byte[] validHistory=File.ReadAllBytes(storage.HistoryPath);
            File.AppendAllText(storage.HistoryPath,"{broken history\n");
            byte[] corruptHistory=File.ReadAllBytes(storage.HistoryPath);
            try
            {
                AnswerMessage(ErrorMessages.Text("Ошибка истории","History error"),1,()=>
                {
                    Select(queue,imported[2]);Click(window,"ResumeQueueItemsButton");
                    PumpUntil(()=>PrivateField(subject,"_queueItemActionBusy") is false,"An unreadable history must retire the live Resume action.");
                });
                Require(imported[2].Status=="Skipped" && imported[2].DeferredToNextStart && !active!.IsPending(imported[2]) &&
                    File.ReadAllBytes(storage.HistoryPath).SequenceEqual(corruptHistory),
                    "Corrupt history must block fresh Resume without changing the file or admitting a child.");
            }
            finally{File.WriteAllBytes(storage.HistoryPath,validHistory);}
            ResumeWithAnswer(queue,imported[2],"NoButton");
            ResumeWithAnswer(queue,imported[1],"YesButton");
            Require(ReferenceEquals(active,subject.SessionState.Coordinator) && active!.IsPending(imported[1]) &&
                imported[1].Status=="Queued" && !imported[1].DeferredToNextStart && subject.SessionState.Urls.Contains(imported[1].Url) &&
                historyKeeper.Status=="Running","Yes must admit exactly the captured fresh row into the same held session.");
            File.WriteAllText(gate,"release");
            PumpUntil(()=>imported[1].Status=="Completed" && historyKeeper.Status=="Completed" && subject.SessionState.Coordinator is null,
                "The approved fresh Resume must actually run and publish after the keeper releases.");
            Require(File.Exists(imported[1].Path) && imported[1].OwnedPaths.Contains(imported[1].Path) &&
                File.ReadLines(storage.HistoryPath).Count(line=>line.Contains(imported[1].Url,StringComparison.Ordinal))==2,
                "The seeded success plus one actual approved publication must produce exactly two history records.");

            gate=Path.Combine(root,"release-stale-resume");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            var stoppedKeeper=new DownloadItem{Url="https://startup-download.invalid/resume-stop-keeper-timer-slow"};
            subject.SessionState.TryAdd(stoppedKeeper);subject.Reindex();Click(window,"StartButton");
            PumpUntil(()=>stoppedKeeper.Status=="Running" && ReadPid(gate+".started")>0,
                "The stale consent case must own a second actual native keeper.");
            active=subject.SessionState.Coordinator;long beforeStop=subject.SessionState.StopVersion;
            ResumeWithAnswer(queue,imported[3],"YesButton",()=>subject.SessionState.Stop());
            PumpUntil(()=>subject.SessionState.Coordinator is null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Stop inside fresh Resume history consent must join its original native keeper.");
            Require(subject.SessionState.StopVersion>beforeStop && imported[3].Status=="Skipped" && imported[3].DeferredToNextStart &&
                !active!.IsPending(imported[3]) && imported[3].StageDir.Length==0 && stoppedKeeper.Status=="Interrupted" &&
                File.Exists(imported[1].Path) && existingOutputs.All(File.Exists),
                "Late Yes after Stop must leave the deferred row untouched and never start a replacement session.");
            AnswerMessage(clearTitle,6,()=>Click(window,"ClearAllQueueButton"));
            PumpUntil(()=>subject.SessionState.Queue.Count==0,"The history fixture must retire only its queue entries.");
            Console.WriteLine("PASS: real filtered multi-select Skip/Resume, recovery and Clear, native handoff/live+idle resume, retained cleanup, fresh history Cancel/No/Yes/corruption and stale plan/Stop decisions");
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
