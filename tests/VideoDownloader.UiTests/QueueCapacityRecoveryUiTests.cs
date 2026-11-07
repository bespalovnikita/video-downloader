using System.Text;
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
            string clearTitle=ErrorMessages.Text("Очистить всю очередь","Clear entire queue");
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
                    scenario.F<Button>("ClearQueueButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    PumpUntil(()=>scenario.SessionState.Queue.All(row=>row.Url!=fixture.PaddedUrl) && !scenario.Subject.CheckpointReadOnly);
                    Require(clears>0 && scenario.SessionState.Queue.Any(row=>row.Url=="https://legacy.example/cleanup-saved?token=Exact" &&
                        row.PendingRemoval && row.OwnedPaths.Contains(fixture.SavedMedia)) && File.Exists(fixture.FinishedMedia) && File.Exists(fixture.UnownedMedia),
                        "Confirmed Clear reduces the queue while retaining pending cleanup and preserving downloaded or unowned files");
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
        Console.WriteLine("PASS: real WPF near-cap queued/legacy Result recovery preserves rows, modal ingress barriers, export, confirmed owned cleanup and strict read-only after a sharing-lock save failure");
    }
}
