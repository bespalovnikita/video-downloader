using System.Diagnostics;
using System.ComponentModel;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestJobDiagnostics(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Job-diagnostics WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message,Func<string>? diagnostics=null)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(!condition())Require(false,message+(diagnostics is null?"":Environment.NewLine+diagnostics()));
        }
        static string Geometry(Window window,DataGrid grid)
        {
            var content=window.Content as FrameworkElement;
            return $"Loaded={window.IsLoaded}; window actual={window.ActualWidth:0.###}x{window.ActualHeight:0.###}; requested={window.Width:0.###}x{window.Height:0.###}; "+
                $"min={window.MinWidth:0.###}x{window.MinHeight:0.###}; max={window.MaxWidth:0.###}x{window.MaxHeight:0.###}; "+
                $"content actual={content?.ActualWidth:0.###}x{content?.ActualHeight:0.###}; grid actual={grid.ActualWidth:0.###}x{grid.ActualHeight:0.###}; "+
                $"work area={SystemParameters.WorkArea}; DPI={System.Windows.Media.VisualTreeHelper.GetDpi(window)}; columns="+
                string.Join("; ",grid.Columns.Select(column=>$"{column.Header}: {column.Visibility}, actual={column.ActualWidth:0.###}, requested={column.Width}"));
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
        bool cancelProbe=false,raceCollision=false,exportAllowed=true,blockAfterChoice=false;int probes=0,exportChoices=0;
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
            window=new JobDiagnosticsWindow(owner,target,service,()=>journal.Sequence,_=>
            {
                exportChoices++;
                if(blockAfterChoice)exportAllowed=false;
                return new(destination);
            },()=>exportAllowed);window.Show();
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
            exportAllowed=false;window.RefreshExportAvailability();
            Require(!Control<Button>(window,"ExportJobDiagnosticsButton").IsEnabled &&
                Control<Button>(window,"CopyJobDiagnosticsButton").IsEnabled,
                "Component replacement must disable only version-probing export while diagnostic reads and copying remain usable.");
            Click(window,"ExportJobDiagnosticsButton");
            Require(exportChoices==0 && probes==0 && window.PendingOperations.IsCompleted && !File.Exists(destination),
                "Even a directly raised export click must not choose a destination or probe components while replacement is active.");
            search.Text="403";Require(grid.Items.Count==1,"Read-only diagnostics search must remain available during component replacement.");
            search.Text="";
            exportAllowed=true;window.RefreshExportAvailability();blockAfterChoice=true;
            Require(Control<Button>(window,"ExportJobDiagnosticsButton").IsEnabled,"Completing component work must re-enable the export command.");
            Click(window,"ExportJobDiagnosticsButton");
            Require(exportChoices==1 && probes==0 && !window.IsExporting && !File.Exists(destination) &&
                !Control<Button>(window,"ExportJobDiagnosticsButton").IsEnabled,
                "Component work admitted during the destination dialog must prevent export after that dialog returns.");
            blockAfterChoice=false;exportAllowed=true;window.RefreshExportAvailability();
            Require(purpose.IsVisible && purpose.Text.Contains("причину ошибки") && !help.IsVisible,"The window must explain its purpose before the user asks for help.");
            Click(window,"JobDiagnosticsHelpButton");window.UpdateLayout();
            Require(help.IsVisible && !details.IsVisible && help.Text.Contains("скрытые фильтрами") && help.Text.Contains("что хотели сделать"),"The actual help action must explain reading errors and sharing a complete report.");
            Click(window,"JobDiagnosticsDetailsButton");window.UpdateLayout();
            Require(details.IsVisible && !help.IsVisible,"The user must be able to return from help to the selected event.");
            PumpUntil(()=>window.IsLoaded,"Diagnostics must finish loading before synthetic viewport probes.",()=>Geometry(window,grid));
            // WPF's native desktop maximum also constrains content layout. A
            // forced minimum permits the actual wide grid on a small CI display;
            // isolate this probe from the separately tested work-area controller,
            // which correctly restores the dialog's production minimum on resize.
            OwnedWindowLayout.Attach(window,owner).Dispose();
            window.MinWidth=0;window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
            window.MinWidth=1120;window.Width=1120;window.Height=650;window.UpdateLayout();
            PumpUntil(()=>Math.Abs(window.ActualWidth-1120)<=1 &&
                grid.Columns.All(column=>column.Visibility==Visibility.Visible && column.ActualWidth>0) &&
                grid.Columns.Sum(column=>column.ActualWidth)<=grid.ActualWidth+1,
                "Wide diagnostics must restore every actual column after deferred WPF layout finishes.",()=>Geometry(window,grid));
            grid.SelectedIndex=0;grid.UpdateLayout();
            Require((grid.Columns[3].GetCellContent(grid.SelectedItem) as TextBlock)?.Text=="admitted",
                "The wide DataGrid must retain the actual typed event binding.");
            var selectedEvent=grid.SelectedItem??throw new InvalidOperationException("The diagnostics fixture must have a selected event.");
            grid.Items.SortDescriptions.Add(new SortDescription("Event",ListSortDirection.Descending));
            grid.SelectedItem=selectedEvent;var eventSource=grid.ItemsSource;
            var sortedEvents=grid.Items.Cast<object>().ToArray();
            grid.CurrentCell=new DataGridCellInfo(selectedEvent,grid.Columns[4]);
            window.MinWidth=700;window.Width=700;window.Height=window.MinHeight;window.UpdateLayout();
            PumpUntil(()=>Math.Abs(window.ActualWidth-700)<=1 &&
                grid.Columns[2].Visibility==Visibility.Collapsed && grid.Columns[3].Visibility==Visibility.Collapsed &&
                grid.Columns[4].ActualWidth>=300 &&
                grid.Columns.Where(column=>column.Visibility==Visibility.Visible).Sum(column=>column.ActualWidth)<=grid.ActualWidth+1,
                "Narrow diagnostics must devote a useful actual width to the message without horizontal overflow.",()=>Geometry(window,grid));
            Require(ReferenceEquals(grid.ItemsSource,eventSource) && ReferenceEquals(grid.SelectedItem,selectedEvent) &&
                ReferenceEquals(grid.CurrentCell.Item,selectedEvent) && grid.Items.Cast<object>().SequenceEqual(sortedEvents) &&
                grid.Items.SortDescriptions.Single().Equals(new SortDescription("Event",ListSortDirection.Descending)) &&
                details.Text.Contains("admitted") && details.Text.Contains("Параметры этой попытки") &&
                System.Windows.Automation.AutomationProperties.GetHelpText(grid).Contains("Выбранном событии"),
                "Resizing must preserve the selected event, sorting, source and full hidden technical details.");
            grid.Items.SortDescriptions.Clear();
            window.MinWidth=0;window.MinHeight=0;
            window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
            window.MinWidth=700;window.MinHeight=500;
            OwnedWindowLayout.Attach(window,owner);window.UpdateLayout();
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
            Require((grid.Columns[4].GetCellContent(grid.SelectedItem) as TextBlock)?.Text=="admitted",
                "The message must remain rendered through its actual binding while technical columns are hidden.");
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
            search.Text="403";attempts.SelectedIndex=1;level.SelectedValue=nameof(JournalLevel.Error);
            var selectedAttempt=attempts.SelectedValue;var selectedLevel=level.SelectedValue;
            foreach(var combo in new[]{attempts,level})
            {
                window.Activate();combo.BringIntoView();combo.Focus();
                PumpUntil(()=>window.IsActive && combo.IsKeyboardFocusWithin,
                    "An actual diagnostics filter must have keyboard focus before testing Escape.");
                combo.IsDropDownOpen=true;
                PumpUntil(()=>combo.IsDropDownOpen,"The actual diagnostics dropdown must open before Escape.");
                NativeFilterEscape(window,()=>!combo.IsDropDownOpen,
                    "The first native Escape must close the diagnostics filter dropdown.");
                Require(window.IsVisible && search.Text=="403" && Equals(attempts.SelectedValue,selectedAttempt) &&
                    Equals(level.SelectedValue,selectedLevel) && grid.Items.Count==1,
                    "Dismissing a diagnostics dropdown must preserve its window, query, attempt and level.");
            }
            NativeFilterEscape(window,()=>!window.IsVisible,
                "A second native Escape with closed dropdowns must close the diagnostics window.");
            PumpUntil(()=>window.PendingOperations.IsCompleted,"Closed diagnostics must finish reads.");window=null;

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

    // Both filter scenarios use actual HWND input so child ComboBox class
    // handling participates before the Window's bubbling Escape action.
    private static void NativeFilterEscape(Window window,Func<bool> ready,string message)
    {
        int observed=0;
        PreProcessInputEventHandler observe=(_,e)=>
        {
            if(e.StagingItem.Input is KeyEventArgs key && key.Key==Key.Escape &&
                key.RoutedEvent==Keyboard.PreviewKeyDownEvent)observed++;
        };
        InputManager.Current.PreProcessInput+=observe;
        try
        {
            nint target=FilterKeyboardInput.GetFocus();
            if(target==0)target=new WindowInteropHelper(window).Handle;
            const int down=(1<<16)|1;
            if(!PostMessageW(target,0x0100,(nint)0x1b,(nint)down) ||
                !PostMessageW(target,0x0101,(nint)0x1b,(nint)(down|unchecked((int)0xc0000000))))
                throw new InvalidOperationException("The actual filter HWND Escape messages must be queued.");
            var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(observed>0 && ready() || clock.Elapsed>TimeSpan.FromSeconds(8))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(observed==0 || !ready())throw new InvalidOperationException(message+" Observed native Escape events: "+observed);
        }
        finally{InputManager.Current.PreProcessInput-=observe;}
    }

    private static class FilterKeyboardInput
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint GetFocus();
    }
}
