using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestJobDiagnostics(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Job-diagnostics WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)??throw new InvalidOperationException("Missing diagnostics control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Window Load()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);return (Window)XamlReader.Load(reader);
        }
        static JournalRecord Record(JobDiagnosticTarget target,string name,int invocation=1,JournalLevel level=JournalLevel.Information,string? message=null,JobDiagnosticSettings? settings=null)=>
            new(level,"download",message??name,"session-fixture",target.Job,"T1",target.Identity,name,1,invocation,settings);
        string root=Path.Combine(testRoot,"job-diagnostics");Directory.CreateDirectory(root);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        File.WriteAllText(storage.SessionPath,"damaged checkpoint must remain byte-identical");
        File.WriteAllText(storage.HistoryPath,"{\"Version\":1,\"Url\":\"https://diagnostics.invalid/old\"}\n");
        byte[] session=File.ReadAllBytes(storage.SessionPath),history=File.ReadAllBytes(storage.HistoryPath);
        var row=new DownloadItem{Url="https://diagnostics.invalid/Video?token=URL_PRIVATE_CANARY",Title="Выбранное видео",Status="Failed",RawError="HTTP Error 403: Forbidden"};
        var target=JobDiagnosticTarget.Capture(row);
        var other=JobDiagnosticTarget.Capture(new DownloadItem{Url="https://diagnostics.invalid/video?token=URL_PRIVATE_CANARY"});
        var settings=JobDiagnosticSettings.Capture(new DownloadOptions{Quality="1080",Output="OUTPUT_PATH_CANARY",Cookies="COOKIES_PATH_CANARY"},null);
        using var journal=new DiagnosticJournal(Path.Combine(root,"logs"),JournalProfile.Detailed);
        journal.Write(Record(target,"admitted",settings:settings));
        journal.Write(Record(target,"failure",level:JournalLevel.Error,message:"HTTP Error 403: Forbidden\nAuthorization: Bearer HEADER_PRIVATE_CANARY\nC:\\Private Folder\\LOCAL_PATH_CANARY\\output.mp4"));
        journal.Write(Record(target,"retry",invocation:2,level:JournalLevel.Warning));
        journal.Write(Record(other,"foreign-event",level:JournalLevel.Error,message:"OTHER_JOB_CANARY"));
        string destination=Path.Combine(root,"selected.zip");
        bool cancelProbe=false,raceCollision=false;int probes=0;
        var versionsStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<IReadOnlyList<DiagnosticComponentVersion>> Versions(CancellationToken token)
        {
            Interlocked.Increment(ref probes);
            if(raceCollision)File.WriteAllText(destination,"FOREIGN ZIP SENTINEL");
            if(cancelProbe){versionsStarted.TrySetResult();await Task.Delay(Timeout.InfiniteTimeSpan,token);}
            return new[]{new DiagnosticComponentVersion("Video Downloader","Доступен","fixture"),new DiagnosticComponentVersion("FFmpeg","Не установлен","")};
        }
        var service=new JobDiagnosticsService(journal,"must-not-launch",Versions);
        var owner=Load();JobDiagnosticsWindow? window=null;
        try
        {
            owner.Show();
            window=new JobDiagnosticsWindow(owner,target,service,()=>journal.Sequence,_=>new(destination));window.Show();
            var grid=Control<DataGrid>(window,"JobDiagnosticsEventsGrid");
            var search=Control<TextBox>(window,"JobDiagnosticsSearchBox");
            var attempts=Control<ComboBox>(window,"JobDiagnosticsAttemptCombo");
            var level=Control<ComboBox>(window,"JobDiagnosticsLevelCombo");
            var details=Control<TextBox>(window,"JobDiagnosticsEventDetails");
            var status=Control<TextBlock>(window,"JobDiagnosticsStatus");
            var purpose=Control<TextBlock>(window,"JobDiagnosticsPurpose");
            var help=Control<TextBlock>(window,"JobDiagnosticsHelp");
            var empty=Control<TextBlock>(window,"JobDiagnosticsEmptyHint");
            PumpUntil(()=>grid.Items.Count==3 && window.PendingOperations.IsCompleted,"The real journal/service must load only the captured exact URL.");
            Require(grid.IsReadOnly && attempts.Items.Count==3 && probes==0,"Opening must expose two typed invocations without component probes.");
            Require(purpose.IsVisible && purpose.Text.Contains("причину ошибки") && !help.IsVisible,"The window must explain its purpose before the user asks for help.");
            Click(window,"JobDiagnosticsHelpButton");window.UpdateLayout();
            Require(help.IsVisible && !details.IsVisible && help.Text.Contains("скрытые фильтрами") && help.Text.Contains("что хотели сделать"),"The actual help action must explain reading errors and sharing a complete report.");
            Click(window,"JobDiagnosticsDetailsButton");window.UpdateLayout();
            Require(details.IsVisible && !help.IsVisible,"The user must be able to return from help to the selected event.");
            window.Width=window.MinWidth;window.Height=window.MinHeight;window.UpdateLayout();
            var content=(FrameworkElement)window.Content;
            foreach(string action in new[]{"RefreshJobDiagnosticsButton","CopyJobDiagnosticsButton","ExportJobDiagnosticsButton","CloseJobDiagnosticsButton"})
            {
                var button=Control<Button>(window,action);var point=button.TranslatePoint(new System.Windows.Point(),content);
                Require(button.IsVisible && point.Y>=0 && point.Y+button.ActualHeight<=content.ActualHeight+1,"Diagnostics actions must remain reachable at the minimum window size: "+action);
            }
            Require(grid.ActualHeight>27,"The minimum window size must retain a usable events area.");
            row.Title="Позднее имя";row.Url=other.Url;row.Status="Completed";
            Require(window.Title.Contains("Выбранное видео") && !window.Title.Contains("Позднее"),"The target must remain captured when its source row changes.");
            Require(Control<TextBlock>(window,"JobDiagnosticExplanation").Text.Length>0 && Control<TextBox>(window,"JobDiagnosticError").Text.Contains("403"),"An explanation and original diagnostic must both remain visible.");
            grid.SelectedIndex=0;grid.UpdateLayout();
            Require(details.Text.Contains("Параметры этой попытки") && details.Text.Contains("1080"),"Selecting an actual admitted event must expose captured settings.");
            Require((grid.Columns[3].GetCellContent(grid.SelectedItem) as TextBlock)?.Text=="admitted","The actual DataGrid binding must render the typed event name.");
            attempts.SelectedIndex=1;Require(grid.Items.Count==2,"Attempt filter must isolate the first invocation.");
            level.SelectedValue=nameof(JournalLevel.Error);Require(grid.Items.Count==1,"Level and attempt filters must compose.");
            search.Text="not-present";Require(grid.Items.Count==0 && !Control<Button>(window,"CopyJobDiagnosticsButton").IsEnabled && empty.IsVisible && empty.Text.Contains("Очистите поиск"),"No-match search must explain how to restore results without copying hidden events.");
            search.Text="403";Require(grid.Items.Count==1 && !empty.IsVisible,"Search must match retained raw diagnostics and clear the empty-state guidance.");
            Click(window,"CopyJobDiagnosticsButton");
            string copied=Clipboard.GetText();
            Require(copied.Contains("failure") && !copied.Contains("event=retry") && !copied.Contains("event=admitted"),"Clipboard copy must contain exactly the visible event subset.");
            Click(window,"ExportJobDiagnosticsButton");
            PumpUntil(()=>window.PendingOperations.IsCompleted && File.Exists(destination),"Real ZIP export did not complete.");
            using(var archive=ZipFile.OpenRead(destination))
            {
                string text=string.Join("\n",archive.Entries.Select(entry=>{using var reader=new StreamReader(entry.Open());return reader.ReadToEnd();}));
                using var eventsReader=new StreamReader(archive.GetEntry("events.jsonl")!.Open());
                string events=eventsReader.ReadToEnd();
                Require(events.Contains("admitted") && events.Contains("failure") && events.Contains("retry"),"ZIP must retain all captured events even while the view is filtered.");
                Require(!new[]{"URL_PRIVATE_CANARY","HEADER_PRIVATE_CANARY","LOCAL_PATH_CANARY","OUTPUT_PATH_CANARY","COOKIES_PATH_CANARY","OTHER_JOB_CANARY"}.Any(canary=>text.Contains(canary,StringComparison.Ordinal)),"Actual ZIP leaked private paths, secrets, settings or another job.");
            }
            attempts.SelectedValue="*";level.SelectedValue="*";search.Text="";
            journal.Write(Record(target,"late-event",invocation:2));
            PumpUntil(()=>grid.Items.Count==4,"The live sequence timer must pick up a new event without changing the captured target.");
            Require(probes==1,"Live reads must not probe component versions.");
            Require(session.SequenceEqual(File.ReadAllBytes(storage.SessionPath)) && history.SequenceEqual(File.ReadAllBytes(storage.HistoryPath)),"Opening/filtering/copy/export must preserve damaged checkpoint and history bytes.");
            window.Close();PumpUntil(()=>window.PendingOperations.IsCompleted,"Closed diagnostics must finish reads.");window=null;

            // Create a competing destination only after the UI's absence check.
            // This exercises the real ZIP publication guard without a dialog seam.
            destination=Path.Combine(root,"racing.zip");raceCollision=true;
            window=new JobDiagnosticsWindow(owner,target,service,()=>journal.Sequence,_=>new(destination));window.Show();
            PumpUntil(()=>Control<Button>(window,"ExportJobDiagnosticsButton").IsEnabled,"Collision test view did not load.");
            Click(window,"ExportJobDiagnosticsButton");
            PumpUntil(()=>window.PendingOperations.IsCompleted && Control<TextBlock>(window,"JobDiagnosticsStatus").Text.Contains("Не удалось сохранить"),"A fresh-name collision must fail visibly.");
            Require(File.ReadAllText(destination)=="FOREIGN ZIP SENTINEL","Unconfirmed export collision must preserve the existing file.");
            window.Close();window=null;raceCollision=false;cancelProbe=true;destination=Path.Combine(root,"cancelled.zip");
            window=new JobDiagnosticsWindow(owner,target,service,()=>journal.Sequence,_=>new(destination));window.Show();
            PumpUntil(()=>Control<Button>(window,"ExportJobDiagnosticsButton").IsEnabled,"Cancellation test view did not load.");
            Click(window,"ExportJobDiagnosticsButton");PumpUntil(()=>versionsStarted.Task.IsCompleted,"Export version work did not start.");
            window.Close();string closedStatus=Control<TextBlock>(window,"JobDiagnosticsStatus").Text;
            PumpUntil(()=>window.PendingOperations.IsCompleted,"Close must cancel and join pending export work.");
            Require(!File.Exists(destination) && Control<TextBlock>(window,"JobDiagnosticsStatus").Text==closedStatus,"Cancelled close must not publish ZIP or mutate closed controls.");window=null;

            window=new JobDiagnosticsWindow(owner,target,service,()=>journal.Sequence,_=>null);window.Show();
            Click(window,"RefreshJobDiagnosticsButton");window.Close();
            PumpUntil(()=>window.PendingOperations.IsCompleted,"Closing before a posted read starts must join it without touching closed controls.");
            Require(!window.IsVisible,"A cancelled queued read cannot reopen its closed window.");window=null;

            var untouched=JobDiagnosticTarget.Capture(new DownloadItem{Url="https://diagnostics.invalid/not-started",Title="Ещё не запущено",Status="Queued"});
            window=new JobDiagnosticsWindow(owner,untouched,service,()=>journal.Sequence,_=>null);window.Show();
            PumpUntil(()=>window.PendingOperations.IsCompleted && Control<TextBlock>(window,"JobDiagnosticsEmptyHint").IsVisible,"A job with no saved events must finish loading its guidance.");
            Require(Control<TextBlock>(window,"JobDiagnosticsPurpose").IsVisible && !Control<TextBlock>(window,"JobDiagnosticExplanation").IsVisible &&
                Control<TextBlock>(window,"JobDiagnosticsEmptyHint").Text.Contains("нет сохранённых событий"),"Purpose and a no-events explanation must remain available even without an error.");
            Click(window,"JobDiagnosticsHelpButton");window.UpdateLayout();
            Require(Control<TextBlock>(window,"JobDiagnosticsHelp").IsVisible,"A job without events must still offer the full in-window guide.");
            window.Close();PumpUntil(()=>window.PendingOperations.IsCompleted,"Closing the empty job guide must join its read.");window=null;

            var mainStorage=new AppPersistence(Path.Combine(root,"main-profile"));
            using(var native=new NativeWindow(owner,mainStorage,"must-not-launch"))
            {
                var first=new DownloadItem{Url="https://diagnostics.invalid/first",Title="Первая",Status="Failed"};
                var second=new DownloadItem{Url="https://diagnostics.invalid/second",Title="Вторая",Status="Completed"};
                native.SessionState.TryAdd(first);native.SessionState.TryAdd(second);native.SessionState.Downloads.Add(second);
                var queue=Control<DataGrid>(owner,"QueueGrid");var downloads=Control<DataGrid>(owner,"DownloadGrid");
                queue.SelectedItem=first;native.UpdateState();Click(owner,"QueueDiagnosticsButton");
                PumpUntil(()=>native.JobDiagnosticWindows.Count==1,"The queue button must open a captured diagnostics window.");
                var firstWindow=native.JobDiagnosticWindows.Single();
                queue.SelectedItem=second;native.UpdateState();
                Require(firstWindow.Title.Contains("Первая"),"Changing main selection must not rewrite an open target.");
                downloads.SelectedItem=second;Click(owner,"DownloadDiagnosticsButton");
                PumpUntil(()=>native.JobDiagnosticWindows.Count==2,"The download button must open its own target.");
                var menu=queue.ContextMenu!.Items.OfType<MenuItem>().Single(item=>item.Name=="QueueDiagnosticsMenuItem");
                queue.SelectedItem=first;menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(native.JobDiagnosticWindows.Count==2,"The context menu must reuse the exact same job window.");
                Require(downloads.ContextMenu!.Items.OfType<MenuItem>().Any(item=>item.Name=="DownloadDiagnosticsMenuItem"),"Downloads context menu must expose diagnostics.");
                Control<TextBox>(owner,"QueueSearchBox").Text="no-visible-match";native.UpdateState();
                PumpUntil(()=>!Control<Button>(owner,"QueueDiagnosticsButton").IsEnabled,"The filtered queue must clear or reject its hidden selection.");
                Click(owner,"QueueDiagnosticsButton");menu.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(!Control<Button>(owner,"QueueDiagnosticsButton").IsEnabled && native.JobDiagnosticWindows.Count==2,"Hidden queue selection must not be actionable through direct routed events.");
                Require(native.SessionState.Coordinator is null && native.SessionState.RunningTask is null && native.SessionState.Queue.Count==2 && !File.Exists(mainStorage.HistoryPath),"Diagnostics controls must not admit downloads or create successful history.");
                native.Dispose();PumpUntil(()=>!firstWindow.IsVisible,"Owner disposal must close diagnostic windows.");
            }
            Console.WriteLine("PASS: real WPF per-job diagnostics, in-window help/minimum layout/empty guidance, immutable targets, typed filters/live events, clipboard, private ZIP, collision/cancel and main owner lifecycle");
        }
        finally{window?.Close();if(owner.IsVisible)owner.Close();}
    }
}
