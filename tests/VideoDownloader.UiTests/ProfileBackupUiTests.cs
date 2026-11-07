using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestProfileBackup(string testRoot)
    {
        static void Require(bool condition,string reason)
        {if(!condition)throw new InvalidOperationException("Profile backup WPF: "+reason);}
        static void PumpUntil(Func<bool> condition,string reason)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}Require(condition(),reason);
        }
        static Window View()
        {
            using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(stream);
            return (Window)XamlReader.Load(reader);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)??throw new InvalidOperationException("Missing backup control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static string Status(Window window)=>Control<TextBlock>(window,"ProfileBackupStatus").Text;
        static Dictionary<string,byte[]?> Files(AppPersistence storage)=>new(StringComparer.Ordinal)
        {
            [storage.SessionPath]=File.Exists(storage.SessionPath)?File.ReadAllBytes(storage.SessionPath):null,
            [storage.HistoryPath]=File.Exists(storage.HistoryPath)?File.ReadAllBytes(storage.HistoryPath):null,
            [Path.Combine(Path.GetDirectoryName(storage.SessionPath)!,"subscriptions.json")]=File.Exists(Path.Combine(Path.GetDirectoryName(storage.SessionPath)!,"subscriptions.json"))
                ?File.ReadAllBytes(Path.Combine(Path.GetDirectoryName(storage.SessionPath)!,"subscriptions.json")):null
        };
        static bool EqualFiles(Dictionary<string,byte[]?> before)=>before.All(pair=>pair.Value is null?!File.Exists(pair.Key):File.Exists(pair.Key) && pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key)));
        static ProfileBackupWindow Open(NativeWindow subject,Window owner)
        {Click(owner,"OpenProfileBackupButton");return subject.ProfileBackupDialog??throw new InvalidOperationException("The actual backup button did not open its owned window.");}
        static void Load(ProfileBackupWindow backup)
        {
            Click(backup,"OpenProfileBackupButton");PumpUntil(()=>!backup.IsBusy,"Archive load/preview did not join.");
            Require(Control<Button>(backup,"RestoreProfileBackupButton").IsEnabled,"A validated real archive must enable the explicit confirmation step: "+Status(backup));
        }
        static void Confirm(ProfileBackupWindow backup)
        {
            Click(backup,"RestoreProfileBackupButton");
            Require(Control<StackPanel>(backup,"ProfileBackupConfirmation").Visibility==Visibility.Visible,
                "Restore must first show a distinct confirmation step.");
            Click(backup,"ConfirmProfileRestoreButton");
        }

        string root=Path.Combine(testRoot,"profile-backup-ui");Directory.CreateDirectory(root);
        string export=Path.Combine(root,"actual.vdbackup"),sourceRoot=Path.Combine(root,"source"),targetRoot=Path.Combine(root,"target");
        var sourceStorage=new AppPersistence(sourceRoot);var sourceOptions=new DownloadOptions{Quality="1080",Codec="H264",
            Output=Path.Combine(root,"source-output-private"),Cookies=Path.Combine(root,"source-cookies-private.txt"),WatchClipboard=true,ShowVideoTitles=true};
        File.WriteAllText(sourceOptions.Cookies,"private cookie sentinel");sourceStorage.Save([],sourceOptions);
        var sourceStore=new SubscriptionStore(sourceRoot);
        var publicSource=sourceStore.Add("https://backup-ui.invalid/channel",true,shortName:"Канал");
        sourceStore.ApplyScan(publicSource.Id,new("Канал Ёж",[new("https://backup-ui.invalid/Video?id=AbC","Материал Ёж")],"",""),[]);
        sourceStore.MarkQueued(publicSource.Id,["https://backup-ui.invalid/Video?id=AbC"]);
        sourceStore.SetSchedule(publicSource.Id,true,11);
        sourceStore.Add("https://backup-ui.invalid/private?token=source-secret",false);
        sourceStorage.AppendSuccess("https://backup-ui.invalid/success");sourceStorage.AppendSuccess("https://backup-ui.invalid/private-success?token=history-secret");
        var sourceView=View();using(var sourceSubject=new NativeWindow(sourceView,sourceStorage,"must-not-launch",automaticUpdates:false))
        {
            try
            {
                bool pickerMakesSessionBusy=false;var modalTask=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                string modalExport=Path.Combine(root,"must-not-export-while-active.vdbackup");
                sourceSubject.ProfileBackupPickers=new(_=>null,_=>
                {if(pickerMakesSessionBusy){sourceSubject.SessionState.TrackTask(modalTask.Task);return modalExport;}return export;});
                sourceView.Show();var backup=Open(sourceSubject,sourceView);
                Require(!Control<Button>(backup,"RestoreProfileBackupButton").IsEnabled,"A new backup window must have no implicit restore plan.");
                Click(backup,"CreateProfileBackupButton");PumpUntil(()=>!backup.IsBusy,"Real archive export did not join.");
                Require(File.Exists(export) && Status(backup).Contains("сохранена",StringComparison.Ordinal),"Explicit export must save a real archive: "+Status(backup));
                Require(Control<TextBlock>(backup,"ProfileBackupSummary").Text.Contains("подписки 1",StringComparison.Ordinal) &&
                    Control<TextBlock>(backup,"ProfileBackupSummary").Text.Contains("история 1",StringComparison.Ordinal),
                    "The actual view must disclose skipped private subscription/history addresses.");
                byte[] first=File.ReadAllBytes(export);Click(backup,"CreateProfileBackupButton");PumpUntil(()=>!backup.IsBusy,"Duplicate destination export did not finish.");
                Require(Status(backup).Contains("не выполнена",StringComparison.Ordinal) && first.SequenceEqual(File.ReadAllBytes(export)),
                    "Exporting to an existing name must report refusal and preserve its bytes.");
                pickerMakesSessionBusy=true;Click(backup,"CreateProfileBackupButton");PumpUntil(()=>!backup.IsBusy,"Post-picker active guard did not return.");
                Require(!File.Exists(modalExport) && Status(backup).Contains("не выполнена",StringComparison.Ordinal),
                    "An active task discovered after the file picker must block writes before acquiring the profile snapshot.");
                modalTask.SetResult();
                using var zip=ZipFile.OpenRead(export);string payload=string.Join("\n",zip.Entries.Select(entry=>
                {using var reader=new StreamReader(entry.Open(),Encoding.UTF8);return reader.ReadToEnd();}));
                Require(zip.Entries.Count==4 && !payload.Contains("source-output-private",StringComparison.Ordinal) &&
                    !payload.Contains("source-secret",StringComparison.Ordinal) && !payload.Contains("history-secret",StringComparison.Ordinal) &&
                    !payload.Contains("private cookie sentinel",StringComparison.Ordinal) && !payload.Contains("source-cookies-private.txt",StringComparison.Ordinal),
                    "The UI export must use the real four-entry portable archive without cookies, paths or private URL parameters.");
                backup.Close();
            }
            finally{sourceSubject.Dispose();sourceView.Close();}
        }

        var storage=new AppPersistence(targetRoot);string localOutput=Path.Combine(root,"local-output"),localCookies=Path.Combine(root,"local-cookies.txt");
        Directory.CreateDirectory(localOutput);File.WriteAllText(localCookies,"local auth sentinel");
        string media=Path.Combine(localOutput,"owned.mp4");File.WriteAllText(media,"owned media must survive every backup operation");
        storage.Save([],new DownloadOptions{Quality="720",Output=localOutput,Cookies=localCookies,WatchClipboard=true});
        var localStore=new SubscriptionStore(targetRoot);localStore.Add("https://backup-ui.invalid/local-channel",false,shortName:"Местный");
        storage.AppendSuccess(new DownloadItem{Url="https://backup-ui.invalid/local-success",Status="Completed",Path=media,OwnedPaths=[media]});
        byte[] originalHistory=File.ReadAllBytes(storage.HistoryPath);
        using var held=new BackupUiCheckpoint();var service=new WindowsProfileBackup(storage,held.Visit);
        var owner=View();using var subject=new NativeWindow(owner,storage,"must-not-launch",automaticUpdates:false,profileBackup:service);
        string chosenArchive=export;string? chosenExport=null;subject.ProfileBackupPickers=new(_=>chosenArchive,_=>chosenExport);ProfileBackupWindow? current=null;
        var completed=new DownloadItem{Url="https://backup-ui.invalid/current-completed",Title="Current completed",Status="Completed",State="Done",
            Path=media,OutputRoot=localOutput,OwnedPaths=[media],AttemptFinished=true,CompletedSelection=new("https://backup-ui.invalid/current-completed",new(1,2))};
        var queued=new DownloadItem{Url="https://backup-ui.invalid/current-queued",Title="Current queued",DeferredToNextStart=true};
        try
        {
            owner.Show();subject.SessionState.TryAdd(completed);subject.SessionState.TryAdd(queued);subject.Reindex();
            string queueBefore=JsonSerializer.Serialize(subject.SessionState.Queue,AppPersistence.Json);var before=Files(storage);
            current=Open(subject,owner);chosenArchive=Path.Combine(root,"broken.vdbackup");File.WriteAllText(chosenArchive,"invalid ZIP fixture");
            Click(current,"OpenProfileBackupButton");PumpUntil(()=>!current.IsBusy,"Damaged archive validation did not join.");
            Require(Status(current).Contains("не выполнена",StringComparison.Ordinal) && EqualFiles(before) &&
                !Control<Button>(current,"RestoreProfileBackupButton").IsEnabled,"A damaged archive must fail before any profile write or restore confirmation.");
            chosenArchive=export;byte[] validSubscriptions=File.ReadAllBytes(localStore.Path);File.WriteAllText(localStore.Path,"damaged subscriptions must not be replaced");
            var damaged=Files(storage);Click(current,"OpenProfileBackupButton");PumpUntil(()=>!current.IsBusy,"Damaged local profile validation did not join.");
            Require(Status(current).Contains("не выполнена",StringComparison.Ordinal) && EqualFiles(damaged) &&
                !Control<Button>(current,"RestoreProfileBackupButton").IsEnabled,"Damaged current subscriptions must fail closed instead of being overwritten by a backup.");
            File.WriteAllBytes(localStore.Path,validSubscriptions);Load(current);
            Require(Control<TextBlock>(current,"ProfileBackupSummary").Text.Contains("Подписок: 1",StringComparison.Ordinal) &&
                Control<TextBlock>(current,"ProfileBackupChanges").Text.Contains("подписок 1",StringComparison.Ordinal) && EqualFiles(before),
                "Loading and previewing the actual ZIP must show its counts without changing any profile file.");
            chosenExport=Path.Combine(root,"target-export.vdbackup");Click(current,"CreateProfileBackupButton");PumpUntil(()=>!current.IsBusy,"Export after a reviewed plan did not join.");
            Require(File.Exists(chosenExport) && !Control<Button>(current,"RestoreProfileBackupButton").IsEnabled,
                "Showing a newly exported archive must revoke the prior archive's restore plan.");
            chosenExport=null;Load(current);
            Click(current,"RestoreProfileBackupButton");Click(current,"CancelProfileRestoreConfirmationButton");
            Require(EqualFiles(before) && Control<StackPanel>(current,"ProfileBackupConfirmation").Visibility==Visibility.Collapsed,
                "Cancelling the distinct restore confirmation must preserve the current profile.");
            Control<ComboBox>(current,"ProfileBackupPolicyCombo").SelectedIndex=1;PumpUntil(()=>!current.IsBusy,"Replacement preview did not finish.");
            Require(Control<TextBlock>(current,"ProfileBackupChanges").Text.Contains("Заменяем текущих подписок: 1",StringComparison.Ordinal) && EqualFiles(before),
                "Replace must explicitly describe only the existing subscription list before any write.");
            var changed=subject.ReadOptions();changed.Codec="VP9";subject.ApplyOptions(changed);Confirm(current);
            PumpUntil(()=>!current.IsBusy,"A stale restore plan did not finish rejecting changed settings.");
            Require(Status(current).Contains("не выполнена",StringComparison.Ordinal) && localStore.Read().Subscriptions.Single().Url.EndsWith("local-channel",StringComparison.Ordinal) &&
                subject.ReadOptions().Codec=="VP9" && originalHistory.SequenceEqual(File.ReadAllBytes(storage.HistoryPath)),
                "Settings changed after preview must invalidate its plan before restoring subscriptions/history.");
            Load(current);held.Arm("staged");Confirm(current);PumpUntil(()=>held.Entered,"Actual restore did not reach its staged filesystem transaction.");
            Require(subject.ProfileBackupBusy && current.IsBusy && !Control<Button>(owner,"StartButton").IsEnabled &&
                !Control<Button>(owner,"RetryFailedButton").IsEnabled,"An actual restore transaction must freeze main-window writers.");
            var watch=Stopwatch.StartNew();subject.SaveCheckpoint();watch.Stop();
            Require(watch.Elapsed<TimeSpan.FromMilliseconds(250),"SaveCheckpoint must skip the held profile mutex instead of blocking the WPF dispatcher.");
            const string deferredFirst="https://backup-ui.invalid/late-ipc",deferredCase="https://backup-ui.invalid/Case?v=AbC";
            string deferredFile=Path.Combine(root,"deferred-input.txt");File.WriteAllText(deferredFile,"https://backup-ui.invalid/deferred-file");
            subject.ReceiveArguments([deferredFirst,deferredCase,"https://backup-ui.invalid/case?v=AbC"]);
            subject.ReceiveArguments([deferredFirst,deferredFile,PortableQueueCodec.Encode([new("https://backup-ui.invalid/deferred-portable","Deferred portable")])]);
            subject.AddUrls(["https://backup-ui.invalid/late-add"]);
            subject.Preview.ReceiveReady(new(queued.Url,"late metadata must not rewrite row","","",1,"",0,0,"","","",DateTime.UtcNow));
            subject.Input.PollClipboard();subject.ImportPortableTextAsync(PortableQueueCodec.Encode([new("https://backup-ui.invalid/late-portable","")])).GetAwaiter().GetResult();
            Require(subject.QueueSubscriptionUrls(["https://backup-ui.invalid/late-subscription"]).Count==0,"Late subscription offers must be rejected while restoring.");
            foreach(string action in new[]{"StartButton","RetryFailedButton","ClearQueueButton","RemoveQueueButton","SubscriptionsButton","UpdateYtDlpButton","InstallFfmpegButton","CheckAppUpdateButton"})Click(owner,action);
            Require(subject.SessionState.Queue.Count==2 && queued.Title=="Current queued" &&
                JsonSerializer.Serialize(subject.SessionState.Queue,AppPersistence.Json)==queueBefore && subject.SessionState.Coordinator is null,
                "Late IPC, explicit actions, portable import and metadata callbacks must preserve queue state/ownership and never start work.");
            held.Release();PumpUntil(()=>!current.IsBusy,"The real restore did not complete after its checkpoint was released.");
            Require(Status(current).Contains("восстановлен",StringComparison.Ordinal) && !subject.ProfileBackupBusy,"Successful restore must re-enable the main window.");
            Require(subject.SessionState.Queue.Select(row=>row.Url).SequenceEqual(new[]{completed.Url,queued.Url,deferredFirst,deferredCase,"https://backup-ui.invalid/case?v=AbC",
                "https://backup-ui.invalid/deferred-file","https://backup-ui.invalid/deferred-portable"}) &&
                storage.Restore()!.Queue.Select(row=>row.Url).SequenceEqual(subject.SessionState.Queue.Select(row=>row.Url)) && subject.SessionState.Coordinator is null,
                "Deferred original IPC groups must replay once, in order with exact URL dedup, ordinary file/portable ingress and a saved checkpoint after restore.");
            queueBefore=JsonSerializer.Serialize(subject.SessionState.Queue,AppPersistence.Json);
            var restored=subject.ReadOptions();var local=localStore.Read();
            Require(restored.Quality=="1080" && restored.Codec=="H264" && restored.Output==localOutput && restored.Cookies==localCookies && restored.WatchClipboard &&
                local.Subscriptions.Count==1 && local.Subscriptions.Single().Url==publicSource.Url && local.Items.Single().State=="New" &&
                !local.Subscriptions.Single().ScheduleEnabled && local.Subscriptions.Single().ScheduleIntervalMinutes==11 &&
                File.ReadAllBytes(storage.HistoryPath).Take(originalHistory.Length).SequenceEqual(originalHistory) && storage.ReadHistory().Count==2 &&
                JsonSerializer.Serialize(subject.SessionState.Queue,AppPersistence.Json)==queueBefore && File.ReadAllText(media).StartsWith("owned media",StringComparison.Ordinal) &&
                subject.SessionState.Coordinator is null && subject.SessionState.StartedAt is null,
                "Replace must preserve queue, media and local paths; restore settings/subscriptions, append history and require explicit Start.");

            subject.SaveCheckpoint();Load(current);before=Files(storage);held.Arm("pointer");Confirm(current);
            PumpUntil(()=>held.Entered,"Cancellation test did not reach the actual durable restore pointer.");
            subject.ReceiveArguments(["https://backup-ui.invalid/cancelled-restore-ipc"]);
            subject.ReceiveArguments([new string('x',PortableQueueCodec.ClipboardTextLimit+1),"https://backup-ui.invalid/overflow-must-not-add"]);
            Click(current,"CancelProfileBackupOperationButton");held.Release();PumpUntil(()=>!current.IsBusy,"Cancelled restore did not join rollback.");
            Require(before.Where(pair=>pair.Key!=storage.SessionPath).All(pair=>pair.Value is null?!File.Exists(pair.Key):pair.Value.SequenceEqual(File.ReadAllBytes(pair.Key))) &&
                !service.RecoveryRequired && Status(current).Contains("отменена",StringComparison.Ordinal) && Status(current).Contains("Лимит",StringComparison.Ordinal) &&
                subject.SessionState.Queue.Last().Url=="https://backup-ui.invalid/cancelled-restore-ipc" &&
                subject.SessionState.Queue.All(row=>row.Url!="https://backup-ui.invalid/overflow-must-not-add") &&
                storage.Restore()!.Queue.Last().Url=="https://backup-ui.invalid/cancelled-restore-ipc" && subject.SessionState.Coordinator is null,
                "Verified cancellation must replay accepted IPC once after rollback, keep history/subscriptions, and visibly reject an entire oversized group.");

            Load(current);held.Arm("pointer");Confirm(current);PumpUntil(()=>held.Entered,"Replay cursor test did not enter the real profile transaction.");
            string firstPortable=Path.Combine(root,"accepted-prefix.vdqueue"),laterText=Path.Combine(root,"retry-suffix.txt");
            File.WriteAllText(firstPortable,PortableQueueCodec.Encode([new("https://backup-ui.invalid/replay-prefix","")]));
            File.WriteAllText(laterText,"https://backup-ui.invalid/replay-suffix");subject.ReceiveArguments([firstPortable,laterText]);
            using(var locked=new FileStream(laterText,FileMode.Open,FileAccess.ReadWrite,FileShare.None))
            {
                Click(current,"CancelProfileBackupOperationButton");held.Release();PumpUntil(()=>!current.IsBusy,"A locked later argument did not release the replay barrier.");
                Require(Status(current).Contains("Отложенные аргументы",StringComparison.Ordinal) &&
                    subject.SessionState.Queue.Count(row=>row.Url=="https://backup-ui.invalid/replay-prefix")==1 &&
                    subject.SessionState.Queue.All(row=>row.Url!="https://backup-ui.invalid/replay-suffix") &&
                    storage.Restore()!.Queue.Any(row=>row.Url=="https://backup-ui.invalid/replay-prefix"),
                    "A failed later input must keep its suffix pending and checkpoint the already accepted prefix.");
            }
            File.WriteAllText(firstPortable,PortableQueueCodec.Encode([new("https://backup-ui.invalid/prefix-must-not-reopen","")]));
            Load(current);
            Require(subject.SessionState.Queue.Count(row=>row.Url=="https://backup-ui.invalid/replay-prefix")==1 &&
                subject.SessionState.Queue.Last().Url=="https://backup-ui.invalid/replay-suffix" &&
                subject.SessionState.Queue.All(row=>row.Url!="https://backup-ui.invalid/prefix-must-not-reopen") && subject.SessionState.Coordinator is null,
                "Retry must resume at the first unprocessed argument, never reopen a successfully handled portable file, and never start a session.");
            Load(current);before=Files(storage);held.Arm("staged");Confirm(current);PumpUntil(()=>held.Entered,"Close test did not enter actual restore.");
            var closing=current;closing.Close();Require(closing.IsVisible && closing.IsBusy,"Closing must wait for its in-flight profile operation to join.");
            held.Release();PumpUntil(()=>!closing.IsVisible && !subject.ProfileBackupBusy,"Close did not join cancellation before releasing the profile gate.");current=null;
            Require(EqualFiles(before),"Closing the restore window must preserve all pre-operation profile bytes.");

            queued.Status="Running";subject.UpdateState();Click(owner,"OpenProfileBackupButton");
            Require(subject.ProfileBackupDialog is null && !Control<Button>(owner,"OpenProfileBackupButton").IsEnabled,"A running row must block even a directly raised backup action.");
            queued.Status="Queued";queued.CleanupPaths=[Path.Combine(root,"cleanup")];subject.UpdateState();Click(owner,"OpenProfileBackupButton");
            Require(subject.ProfileBackupDialog is null,"Pending ownership cleanup must block backup operations.");queued.CleanupPaths=[];
            var active=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);subject.SessionState.TrackTask(active.Task);subject.UpdateState();
            Click(owner,"OpenProfileBackupButton");Require(subject.ProfileBackupDialog is null,"An outstanding session task must block profile maintenance.");
            active.SetResult();subject.UpdateState();subject.SaveCheckpoint();
            var attributes=File.GetAttributes(storage.SessionPath);File.SetAttributes(storage.SessionPath,attributes|FileAttributes.ReadOnly);
            try
            {
                queued.Title="unsaved readonly title";subject.SaveCheckpoint();subject.UpdateState();Click(owner,"OpenProfileBackupButton");
                Require(subject.CheckpointReadOnly && subject.ProfileBackupDialog is null,"A genuine checkpoint write failure must block backup/restore before a profile is overwritten.");
            }
            finally{File.SetAttributes(storage.SessionPath,attributes);}
        }
        finally{held.Release();current?.RequestCancellation();current?.Close();subject.Dispose();owner.Close();}
        TestProfileBackupMainCloseUi(root,export);
        TestProfileBackupSchedulingUi(root,export);
        TestProfileBackupRecoveryUi(root,export);
        Console.WriteLine("PASS: real WPF backup export/load/preview/explicit confirmation, filesystem restore/rollback/cancel/close, local paths, append-only history and late mutation guards");
    }

    private sealed class BackupUiCheckpoint:IDisposable
    {
        private readonly ManualResetEventSlim _release=new(true);
        private string _stage="";private int _entered;
        internal bool Entered=>Volatile.Read(ref _entered)!=0;
        internal void Arm(string stage){_stage=stage;Volatile.Write(ref _entered,0);_release.Reset();}
        internal void Visit(string stage)
        {
            if(stage!=_stage)return;_stage="";Volatile.Write(ref _entered,1);
            if(!_release.Wait(TimeSpan.FromSeconds(15)))throw new TimeoutException("The WPF test did not release its real backup checkpoint.");
        }
        internal void Release()=>_release.Set();
        public void Dispose(){_release.Set();_release.Dispose();}
    }
    private static void TestProfileBackupRecoveryUi(string root,string archive)
    {
        string directory=Path.Combine(root,"recovery-required");var storage=new AppPersistence(directory);storage.Save([],new DownloadOptions());
        Action? deferredArguments=null;
        var service=new WindowsProfileBackup(storage,stage=>
        {
            if(stage!="published:session.json")return;
            File.WriteAllText(storage.SessionPath,"foreign change during restore must be preserved");
            deferredArguments?.Invoke();
            throw new IOException("injected transaction failure after a foreign profile change");
        });
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(stream);
        var owner=(Window)XamlReader.Load(reader);using var subject=new NativeWindow(owner,storage,"must-not-launch",automaticUpdates:false,profileBackup:service);
        deferredArguments=()=>subject.Dispatcher.Invoke(()=>subject.ReceiveArguments(["https://backup-ui.invalid/recovery-must-not-replay"]));
        subject.ProfileBackupPickers=new(_=>archive,_=>null);
        try
        {
            owner.Show();((Button)owner.FindName("OpenProfileBackupButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var backup=subject.ProfileBackupDialog!;
            void Click(string name)=>((Button)backup.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            void Join()
            {
                var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};var deadline=Stopwatch.StartNew();
                timer.Tick+=(_,_)=>{if(!backup.IsBusy || deadline.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};timer.Start();
                try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
                if(backup.IsBusy)throw new InvalidOperationException("Recovery-required UI operation did not join.");
            }
            Click("OpenProfileBackupButton");Join();Click("RestoreProfileBackupButton");Click("ConfirmProfileRestoreButton");Join();subject.UpdateState();
            if(!subject.CheckpointReadOnly || !service.RecoveryRequired || !((TextBlock)backup.FindName("ProfileBackupStatus")!).Text.Contains("Перезапустите",StringComparison.Ordinal) ||
                ((Button)backup.FindName("CreateProfileBackupButton")!).IsEnabled || ((Button)owner.FindName("StartButton")!).IsEnabled ||
                !((TextBlock)backup.FindName("ProfileBackupStatus")!).Text.Contains("Отложенные аргументы",StringComparison.Ordinal) ||
                subject.SessionState.Queue.Count!=0 || subject.SessionState.Coordinator is not null ||
                File.ReadAllText(storage.SessionPath)!="foreign change during restore must be preserved")
                throw new InvalidOperationException("A real incomplete rollback must block writers and explain restart without overwriting the foreign change.");
            subject.SaveCheckpoint();if(File.ReadAllText(storage.SessionPath)!="foreign change during restore must be preserved")
                throw new InvalidOperationException("Read-only recovery must suppress the final checkpoint too.");
            backup.Close();
        }
        finally{subject.Dispose();owner.Close();}
    }

    private static void TestProfileBackupMainCloseUi(string root,string archive)
    {
        var storage=new AppPersistence(Path.Combine(root,"main-close"));storage.Save([],new DownloadOptions());
        using var held=new BackupUiCheckpoint();
        var service=new WindowsProfileBackup(storage,held.Visit);
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(stream);
        var owner=(Window)XamlReader.Load(reader);using var subject=new NativeWindow(owner,storage,"must-not-launch",automaticUpdates:false,
            profileBackup:service);subject.ProfileBackupPickers=new(_=>archive,_=>null);
        void Pump(Func<bool> condition)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};var watch=Stopwatch.StartNew();
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};timer.Start();
            try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(!condition())throw new InvalidOperationException("Main close did not join the real profile restore/rollback.");
        }
        try
        {
            owner.Show();((Button)owner.FindName("OpenProfileBackupButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));var backup=subject.ProfileBackupDialog!;
            void Click(string name)=>((Button)backup.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Click("OpenProfileBackupButton");Pump(()=>!backup.IsBusy);
            byte[] before=File.ReadAllBytes(storage.SessionPath);held.Arm("pointer");Click("RestoreProfileBackupButton");Click("ConfirmProfileRestoreButton");Pump(()=>held.Entered);
            subject.Lifecycle.RequestExit();
            if(!owner.IsVisible || !subject.ProfileBackupBusy)throw new InvalidOperationException("Main Close must defer while its profile transaction owns the mutex.");
            held.Release();Pump(()=>!owner.IsVisible && !subject.ProfileBackupBusy);
            if(!before.SequenceEqual(File.ReadAllBytes(storage.SessionPath)) || service.RecoveryRequired)
                throw new InvalidOperationException("Shutdown must cancel/join rollback before the final checkpoint is saved.");
        }
        finally{held.Release();subject.Dispose();owner.Close();}
    }
}
