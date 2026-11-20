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
    private static void TestExpandedQueueRecovery(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Expanded queue recovery WPF test failed: "+message);
        }
        static void PumpUntil(Func<bool> condition)
        {
            var deadline=DateTime.UtcNow.AddSeconds(8);var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{if(!condition())Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),"The recovery operation did not complete within its deadline");
        }
        foreach(bool legacy in new[]{false,true})
        {
            var fixture=QueueCapacityRecoveryFixture.Create(Path.Combine(testRoot,"expanded-recovery-"+legacy),legacy);
            var data=fixture.Storage;int expected=legacy?6:2;
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            var window=(Window)XamlReader.Load(reader);UiScenario? scenario=null;
            uint thread=GetCurrentThreadId();bool question=false,warning=false;int deletes=0,clears=0;
            Exception? failure=null;
            string restoreTitle=ErrorMessages.Text("Восстановить предыдущую сессию","Restore previous session");
            string warningTitle=ErrorMessages.Text("Восстановленная очередь требует сокращения","Recovered queue requires reduction");
            string deleteTitle=ErrorMessages.Text("Удалить загрузки","Delete downloads");
            string clearTitle=ErrorMessages.Text("Убрать завершённые записи","Remove completed entries");
            string pendingTitle=ErrorMessages.Text("Ожидается очистка","Cleanup pending");
            bool IsDialogOpen(string title)
            {
                bool found=false;
                EnumThreadWindows(thread,(handle,_)=>
                {
                    var caption=new StringBuilder(256);GetWindowTextW(handle,caption,caption.Capacity);
                    if(caption.ToString()!=title)return true;
                    found=true;return false;
                },IntPtr.Zero);
                return found;
            }
            var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            observer.Tick+=(_,_)=>
            {
                try
                {
                    if(!question && IsDialogOpen(restoreTitle))
                    {
                        question=true;
                        try
                        {
                            Clipboard.SetText(PortableQueueCodec.Encode([new PortableQueueEntry("https://capacity.invalid/reentrant-transfer")]));
                            ((Button)window.FindName("PasteClipboardButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            ((Button)window.FindName("StartButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                            // Let the genuine async portable decoder and its
                            // dispatcher continuation run inside the question.
                            var nested=new DispatcherFrame();var release=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
                            release.Tick+=(_,_)=>nested.Continue=false;release.Start();
                            try{Dispatcher.PushFrame(nested);}finally{release.Stop();}
                            Require(((DataGrid)window.FindName("QueueGrid")!).Items.Count==0 &&
                                File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint),
                                "Portable Paste and Start during the restore question cannot admit rows or overwrite the old checkpoint");
                        }
                        finally{DismissDialog(thread,restoreTitle,6);}
                    }
                    if(DismissDialog(thread,warningTitle,1))
                    {
                        warning=true;
                        var grid=(DataGrid)window.FindName("QueueGrid")!;
                        Require(grid.Items.Count==expected,"Expanded rows must be exposed before the recovery warning pumps the dispatcher");
                        ((TextBox)window.FindName("UrlInput")!).Text="https://capacity.invalid/during-warning";
                        ((Button)window.FindName("AddUrlButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        ((Button)window.FindName("StartButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        Require(grid.Items.Count==expected && File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint),
                            "Reentrant Add/Start during the recovery warning cannot mutate or save the original queue");
                    }
                    if(DismissDialog(thread,deleteTitle,6))deletes++;
                    if(DismissDialog(thread,clearTitle,6))clears++;
                    DismissDialog(thread,pendingTitle,1);
                    if(DismissDialog(thread,ErrorMessages.Text("Ошибка восстановления","Session recovery error"),1) ||
                        DismissDialog(thread,"Перенос очереди",1))
                        failure??=new InvalidOperationException("Recovery or export displayed an unexpected error dialog");
                }
                catch(Exception error){failure??=error;}
            };
            using var watchdog=new CancellationTokenSource();
            _=Task.Delay(TimeSpan.FromSeconds(15),watchdog.Token).ContinueWith(task=>
            {
                if(task.IsCanceled)return;
                failure??=new TimeoutException("Expanded recovery WPF scenario timed out");
                DismissDialog(thread,restoreTitle,6);DismissDialog(thread,warningTitle,1);
                DismissDialog(thread,deleteTitle,7);DismissDialog(thread,clearTitle,7);DismissDialog(thread,pendingTitle,1);
            },TaskScheduler.Default);
            observer.Start();
            try
            {
                scenario=new UiScenario(window,data,"must-not-launch");window.Show();
                if(failure!=null)throw failure;
                Require(question && warning && scenario.Subject.CheckpointReadOnly && scenario.SessionState.Queue.Count==expected &&
                    scenario.SessionState.Coordinator==null && scenario.SessionState.RunningTask==null &&
                    scenario.SessionState.Queue.Single(row=>row.Url==fixture.PaddedUrl).OutputRoot==Path.GetFullPath(scenario.ReadOptions().Output),
                    "A valid near-cap old checkpoint must restore its expanded rows into an explicit read-only recovery mode");
                scenario.SaveCheckpoint();scenario.AddUrls(["https://capacity.invalid/blocked"]);
                scenario.ReceiveArguments(["https://capacity.invalid/blocked-ipc"]);
                scenario.F<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(scenario.SessionState.Queue.Count==expected && scenario.SessionState.Coordinator==null &&
                    !scenario.F<Button>("StartButton").IsEnabled && !scenario.F<Button>("AddUrlButton").IsEnabled &&
                    File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint) && !File.Exists(data.HistoryPath),
                    "Read-only recovery blocks ordinary ingress, Start and automatic save while retaining the original bytes and history");

                Clipboard.SetText("export pending");
                var transfer=scenario.F<Button>("PortableQueueButton");transfer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                transfer.ContextMenu!.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Копировать очередь"))
                    .RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                PumpUntil(()=>Clipboard.ContainsText() && PortableQueueCodec.IsPortable(Clipboard.GetText()) || failure!=null);
                if(failure!=null)throw failure;
                Require(PortableQueueCodec.Decode(Clipboard.GetText()).Count==expected &&
                    File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint),"Explicit export remains available without rewriting the protected checkpoint");
                transfer.ContextMenu.IsOpen=false;
                if(legacy)
                {
                    var cleanup=scenario.SessionState.Queue.Single(row=>row.Url=="https://legacy.example/cleanup-event");
                    Require(cleanup.PendingRemoval && cleanup.AttemptFinished && cleanup.CleanupError=="Locked file from Result" &&
                        cleanup.OutputRoot==fixture.OriginalOutput && cleanup.OwnedPaths.SequenceEqual(new[]{fixture.EventMedia}) &&
                        cleanup.CleanupPaths.SequenceEqual(new[]{fixture.EventMedia}),"The legacy Result retains proven ownership and cleanup paths in the actual WPF queue");
                    scenario.Q.SelectedItem=cleanup;
                    Require(scenario.F<Button>("RemoveQueueButton").IsEnabled,"Explicit cleanup remains available during capacity recovery");
                    scenario.F<Button>("RemoveQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(()=>!scenario.SessionState.Queue.Contains(cleanup));
                    Require(deletes>0 && !File.Exists(fixture.EventMedia) && File.Exists(fixture.SavedMedia) &&
                        File.Exists(fixture.FinishedMedia) && File.Exists(fixture.UnownedMedia) &&
                        File.ReadAllBytes(fixture.EventsPath).SequenceEqual(fixture.Events),
                        "Confirmed recovery cleanup deletes only the legacy Result's proven owned file");
                    if(scenario.Subject.CheckpointReadOnly)
                        Require(File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint),"A still-oversized explicit reduction preserves the original checkpoint");
                    var finished=scenario.SessionState.Queue.Single(row=>row.Status=="Completed" && row.StageDir.Length==0);
                    var retainedFinished=scenario.SessionState.Queue.Single(row=>row.Status=="Completed" && row.StageDir.Length>0);
                    scenario.F<Button>("ClearQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(()=>!scenario.SessionState.Queue.Contains(finished));
                    Require(scenario.SessionState.Queue.Contains(retainedFinished) && retainedFinished.StageDir.Length>0 &&
                        scenario.SessionState.Queue.Any(row=>row.Url==fixture.PaddedUrl) && File.Exists(fixture.FinishedMedia) &&
                        File.Exists(fixture.SavedMedia) && File.Exists(fixture.UnownedMedia),
                        "Recovery Clear removes only eligible finished records, preserving unfinished rows, owned-stage records and all media.");
                    scenario.F<Button>("ClearQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(scenario.SessionState.Queue.Any(row=>row.Url==fixture.PaddedUrl) && scenario.SessionState.Queue.Contains(retainedFinished),
                        "A completed record awaiting stage cleanup must not let another Clear fall through to unfinished work.");
                    scenario.Q.SelectedItem=scenario.SessionState.Queue.Single(row=>row.Url==fixture.PaddedUrl);
                    scenario.F<Button>("RemoveQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(()=>scenario.SessionState.Queue.All(row=>row.Url!=fixture.PaddedUrl) && !scenario.Subject.CheckpointReadOnly);
                    Require(clears>=2 && scenario.SessionState.Queue.Any(row=>row.Url=="https://legacy.example/cleanup-saved?token=Exact" &&
                        row.PendingRemoval && row.OwnedPaths.Contains(fixture.SavedMedia)) && File.Exists(fixture.FinishedMedia) && File.Exists(fixture.UnownedMedia),
                        "Explicit reduction keeps pending cleanup and preserves downloaded or unowned files");
                }
                else
                {
                    // A real Windows sharing lock rejects the reduced save.
                    // It must become strict I/O read-only, with no remaining
                    // exception for further recovery removal or Clear.
                    using(var held=new FileStream(data.SessionPath,FileMode.Open,FileAccess.Read,FileShare.Read))
                    {
                        scenario.Q.SelectedItem=scenario.SessionState.Queue.Single(row=>row.Url==fixture.PaddedUrl);
                        scenario.F<Button>("RemoveQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        PumpUntil(()=>scenario.SessionState.Queue.Count==1 && scenario.Subject.CheckpointReadOnly);
                    }
                    var keeper=scenario.SessionState.Queue.Single();
                    scenario.Q.SelectedItem=keeper;
                    scenario.F<Button>("RemoveQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    scenario.F<Button>("ClearQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    scenario.AddUrls(["https://capacity.invalid/after-save-failure"]);
                    scenario.F<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Require(scenario.Subject.CheckpointReadOnly && scenario.SessionState.Queue.SequenceEqual(new[]{keeper}) &&
                        scenario.SessionState.Coordinator==null && data.Restore()!.Queue.Count==expected &&
                        File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint),
                        "Failed reduced save preserves the original bytes and blocks subsequent Remove/Clear/Add/Start even after the lock is released");
                }
                if(legacy)Require(!scenario.Subject.CheckpointReadOnly && data.Restore()!.Queue.Count==scenario.SessionState.Queue.Count &&
                    !File.ReadAllBytes(data.SessionPath).SequenceEqual(fixture.Checkpoint) && !File.Exists(data.HistoryPath),
                    "Only the explicit reduced snapshot is saved before ordinary operation becomes available again");
                if(failure!=null)throw failure;
            }
            finally
            {
                watchdog.Cancel();observer.Stop();scenario?.Dispose();window.Close();
            }
        }
        TestPreviewCapacityRecovery(testRoot);
        Console.WriteLine("PASS: real WPF near-cap queued/legacy Result and preview-title recovery preserves rows, protected checkpoints, explicit reduction, owned media and strict read-only after I/O failures");
    }

    private static void TestPreviewCapacityRecovery(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Preview capacity recovery WPF test failed: "+message);}
        string root=Path.Combine(testRoot,"preview-capacity-recovery");Directory.CreateDirectory(root);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        using var scenario=new UiScenario(window,storage,"must-not-launch");
        var options=new DownloadOptions{Output=root,WatchClipboard=false};
        string media=Path.Combine(root,"owned.mp4");File.WriteAllText(media,"published sentinel");
        var completed=new DownloadItem{Url="https://capacity.invalid/preview-padding",Status="Completed",State="Done",Progress=100,
            Path=media,OwnedPaths=[media],OutputRoot=root,AttemptFinished=true};
        var pending=new DownloadItem{Url="https://capacity.invalid/preview-title"};
        try
        {
            window.Show();scenario.ApplyOptions(options);
            scenario.SessionState.AddBatch([completed,pending],scenario.ReadOptions());
            int baseline=JsonSerializer.SerializeToUtf8Bytes(new SessionSnapshot{Queue=scenario.SessionState.Queue.ToList(),
                Settings=scenario.ReadOptions(),SavedAtUtc=new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc).AddTicks(1)},AppPersistence.Json).Length;
            completed.Title=new string('Ж',(AppPersistence.MaxCheckpointBytes-baseline-96)/6);
            scenario.Reindex();Require(!scenario.Subject.CheckpointReadOnly,"The original queue must fit before real preview enrichment");
            byte[] original=File.ReadAllBytes(storage.SessionPath);

            // Exercise the real metadata callback and its SaveCheckpoint owner;
            // no test-only flag or direct read-only state mutation is involved.
            var ready=new PreviewData(pending.Url,new string('Ж',512),"","",0,"fixture",0,0,"","","",DateTime.UtcNow);
            scenario.Preview.ReceiveReady(ready);
            Require(scenario.Subject.CheckpointReadOnly,"Real preview title growth must protect checkpoint writes; original bytes="+original.Length);
            Require(pending.Title==ready.Title,"The production preview callback must retain the new title in memory; actual characters="+pending.Title.Length);
            Require(scenario.SessionState.Queue.SequenceEqual(new[]{completed,pending}),
                "Capacity recovery must preserve both original queue row objects and their order");
            byte[] protectedCheckpoint=File.ReadAllBytes(storage.SessionPath);
            Require(protectedCheckpoint.SequenceEqual(original),
                $"Capacity refusal must preserve the original checkpoint bytes; original={original.Length}, current={protectedCheckpoint.Length}");
            Require(!File.Exists(storage.HistoryPath),"Preview enrichment must not create a successful-download history");
            Require(completed.OwnedPaths.SequenceEqual(new[]{media}) && File.ReadAllText(media)=="published sentinel",
                "Capacity recovery must preserve the published media and its exact ownership manifest");
            string expectedStatus=ErrorMessages.Text("● Требуется сокращение очереди","● Queue reduction required");
            Require(scenario.T("HeaderStatus").Text==expectedStatus,
                $"Recovery must display the reduction state in the active UI language; UI={System.Globalization.CultureInfo.CurrentUICulture.Name}, expected={expectedStatus}, actual={scenario.T("HeaderStatus").Text}");
            scenario.Preview.ReceiveReady(ready with {Title="A later title must not modify the protected queue"});
            scenario.AddUrls(["https://capacity.invalid/blocked-after-title"]);
            scenario.F<Button>("StartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(pending.Title==ready.Title && scenario.SessionState.Queue.Count==2 && scenario.SessionState.Coordinator is null &&
                !scenario.F<Button>("StartButton").IsEnabled && !scenario.F<Button>("AddUrlButton").IsEnabled,
                "Recovery blocks further enrichment, ingress and Start until an explicit queue reduction");

            scenario.Q.SelectedItem=completed;
            Require(scenario.F<Button>("ForgetQueueButton").IsEnabled,"Safe record removal remains available after preview overflow");
            scenario.F<Button>("ForgetQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var deadline=DateTime.UtcNow.AddSeconds(8);var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            bool Reduced()=>scenario.SessionState.Queue.Count==1 && !scenario.Subject.CheckpointReadOnly;
            timer.Tick+=(_,_)=>{if(Reduced() || DateTime.UtcNow>=deadline)frame.Continue=false;};timer.Start();
            try{if(!Reduced())Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            var restored=storage.Restore()!;
            Require(Reduced() && restored.Queue.Single().Url==pending.Url && restored.Queue.Single().Title==ready.Title &&
                File.ReadAllText(media)=="published sentinel" && !File.Exists(storage.HistoryPath),
                "Removing a confirmed record saves the reduced enriched queue and reopens normal operation without deleting media/history");

            byte[] reduced=File.ReadAllBytes(storage.SessionPath);var attributes=File.GetAttributes(storage.SessionPath);
            try
            {
                File.SetAttributes(storage.SessionPath,attributes|FileAttributes.ReadOnly);
                pending.Title="changed after reduction";scenario.SaveCheckpoint();scenario.Subject.UpdateState();
            }
            finally{File.SetAttributes(storage.SessionPath,attributes);}
            scenario.Q.SelectedItem=pending;
            scenario.F<Button>("ForgetQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            scenario.SaveCheckpoint();
            Require(scenario.Subject.CheckpointReadOnly && scenario.SessionState.Queue.SequenceEqual(new[]{pending}) &&
                !scenario.F<Button>("ForgetQueueButton").IsEnabled && File.ReadAllBytes(storage.SessionPath).SequenceEqual(reduced),
                "An actual later I/O failure stays strict read-only after access returns and cannot inherit the capacity removal exception");
        }
        finally{scenario.Dispose();window.Close();}
    }
}
