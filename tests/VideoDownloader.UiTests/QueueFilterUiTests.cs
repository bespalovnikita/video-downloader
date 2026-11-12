using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestQueueFilters(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Queue-filter WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(20))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static void PumpFor(int milliseconds)
        {var watch=Stopwatch.StartNew();PumpUntil(()=>watch.ElapsedMilliseconds>=milliseconds,"The dispatcher did not advance");}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual queue-filter control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static string CheckpointData(string text)
        {
            using var json=JsonDocument.Parse(text);
            return json.RootElement.GetProperty("Queue").GetRawText()+"|"+json.RootElement.GetProperty("Settings").GetRawText();
        }

        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The compiled native metadata/download fixture must exist");
        string root=Path.Combine(testRoot,"queue-filters");Directory.CreateDirectory(root);
        string calls=Path.Combine(root,"metadata.jsonl");
        string? oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        string? oldThumbnail=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL");
        string? oldPath=Environment.GetEnvironmentVariable("PATH");
        string? oldGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE");
        var oldCulture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        using var thumbnails=new PreviewThumbnailServer();
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",calls);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL",thumbnails.Url);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,storage,fixture);
            window.Show();window.Width=1280;window.Height=760;window.UpdateLayout();
            var queue=Control<DataGrid>(window,"QueueGrid");
            var search=Control<TextBox>(window,"QueueSearchBox");
            var status=Control<ComboBox>(window,"QueueStatusFilter");
            var subscription=Control<ComboBox>(window,"QueueSubscriptionFilter");
            var count=Control<TextBlock>(window,"QueueCount");
            var empty=Control<TextBlock>(window,"QueueEmptyFilterHint");
            var panel=Control<FrameworkElement>(window,"QueueFilterPanel");
            var badge=Control<TextBlock>(window,"QueueActiveFiltersBadge");
            Require(panel.Visibility==Visibility.Collapsed && panel.DesiredSize.Height==0 && badge.Visibility==Visibility.Collapsed,
                "Search and filters must occupy no panel height on startup, while their header button stays available.");
            string[] urls=["https://title-ingress.invalid/Video?token=AbC","https://title-ingress.invalid/video?token=AbC",
                "https://title-ingress.invalid/Shortcut?token=XyZ","https://title-ingress.invalid/SubscriptionPreview?token=AbC"];
            var rows=urls.Select(url=>new DownloadItem{Url=url}).ToArray();
            var ready=new HashSet<string>(StringComparer.Ordinal);
            subject.Preview.Changed+=(data,isFinal)=>{if(isFinal)ready.Add(data.Url);};
            rows[0].SubscriptionId=rows[1].SubscriptionId="source-A";
            rows[0].SubscriptionName=rows[1].SubscriptionName="Север";
            rows[2].SubscriptionId="source-B";rows[2].SubscriptionName="Север";
            foreach(var row in rows)
            {subject.SessionState.TryAdd(row);subject.Preview.RequestRow(row);}
            subject.Reindex();
            PumpUntil(()=>urls.All(url=>ready.Contains(url) && subject.Preview.GetCached(url)?.ThumbnailBase64.Length>0),
                "Cold metadata and actual HTTP thumbnails must finish before testing network-free filters");
            // A real selection reapplies its cached metadata to the row. Do it
            // before assigning the controlled titles used by the search test.
            queue.SelectedItem=rows[0];
            PumpUntil(()=>subject.Preview.SelectedUrl==rows[0].Url,"Selecting a cached row must still show its preview");
            rows[0].Title="Север — Ёжик один";rows[1].Title="Север — Вторая серия";
            rows[2].Title="Южный берег";rows[3].Title="Архив";
            DownloadSection[] plannedRanges=[new(5,7),new(1,2)];
            var planned=new DownloadSelection(rows[0].Url,plannedRanges[0],DurationSeconds:12)
                {Sections=new DownloadSectionSet(plannedRanges)};
            rows[0].Selection=planned;
            rows[2].Status="Failed";rows[3].Status="Completed";
            string keptMedia=Path.Combine(root,"kept.mp4");File.WriteAllText(keptMedia,"user media sentinel");
            string keptSecond=Path.Combine(root,"kept-part02.mp4");File.WriteAllText(keptSecond,"second fragment sentinel");
            rows[3].Path=keptMedia;rows[3].OutputRoot=root;rows[3].OwnedPaths=[keptMedia,keptSecond];
            var completed=new DownloadSelection(rows[3].Url,plannedRanges[0],DurationSeconds:12)
                {Sections=new DownloadSectionSet(plannedRanges)};
            rows[3].CompletedSelection=completed;
            subject.Reindex();subject.SaveCheckpoint();
            string checkpoint=CheckpointData(File.ReadAllText(storage.SessionPath));
            int processes=File.ReadAllLines(calls).Length,http=thumbnails.Requests;
            Require(queue.ItemsSource is ListCollectionView && queue.Items.Count==4 && count.Text=="Показано 4 из 4",
                "The actual grid must use a view of all underlying rows and display visible/total counts");
            window.UpdateLayout();double collapsedHeight=queue.ActualHeight;
            Click(window,"ToggleQueueFiltersButton");window.UpdateLayout();
            Require(panel.Visibility==Visibility.Visible && panel.ActualHeight>60 && collapsedHeight-queue.ActualHeight>60,
                "Opening the real search panel must allocate its controls' space from the queue grid only while shown.");
            search.Focus();search.Text="ёжик";
            PumpUntil(()=>queue.Items.Count==1,"Unicode title search must filter the real grid");
            Require(ReferenceEquals(queue.Items[0],rows[0]) && ReferenceEquals(queue.SelectedItem,rows[0]) &&
                subject.Preview.SelectedUrl==rows[0].Url && count.Text=="Показано 1 из 4" &&
                Control<Button>(window,"MediaSelectionButton").IsEnabled &&
                Control<TextBlock>(window,"MediaSelectionSummary").Text.Contains("Фрагментов: 2",StringComparison.Ordinal) &&
                ReferenceEquals(rows[0].Selection,planned) && planned.EffectiveSections.SequenceEqual(plannedRanges),
                "A surviving multi-fragment row must retain its ordered plan, editor summary and independent preview");
            search.Text+=" "; // Collapse must flush the pending search debounce.
            Click(window,"ToggleQueueFiltersButton");window.UpdateLayout();
            Require(panel.Visibility==Visibility.Collapsed && panel.DesiredSize.Height==0 &&
                Math.Abs(queue.ActualHeight-collapsedHeight)<2 && badge.Visibility==Visibility.Visible && badge.Text=="1" &&
                queue.Items.Count==1 && ReferenceEquals(queue.SelectedItem,rows[0]),
                "Collapsing must return grid space and retain pending/applied search and its selected row, with a visible active-filter badge.");
            Click(window,"ToggleQueueFiltersButton");window.UpdateLayout();
            Require(panel.Visibility==Visibility.Visible && search.IsKeyboardFocusWithin && search.Text=="ёжик ",
                "Reopening search must focus the existing draft without resetting its criteria or selecting another video.");
            Require(!Control<Button>(window,"MoveUpButton").IsEnabled && !queue.AllowDrop &&
                Control<TextBlock>(window,"QueueFilterScopeHint").Text.Contains("скрытые",StringComparison.Ordinal),
                "Filtered order and whole-queue action scope must be visibly unambiguous");
            Click(window,"MoveUpButton");
            Require(subject.SessionState.Queue.SequenceEqual(rows),"A programmatic reorder click must not bypass the filtered-order guard");

            search.Text="title-ingress.invalid/VIDEO?TOKEN=abc";
            PumpUntil(()=>queue.Items.Count==2,"URL-text search must support case-insensitive display matching");
            Require(subject.SessionState.Queue.Count==4 && rows[0].Url!=rows[1].Url && rows[0].Order==1 && rows[1].Order==2,
                "Display matching must not merge ordinal URL identities or change source order");
            subscription.SelectedValue="source-B";
            PumpUntil(()=>queue.Items.Count==0 && empty.Visibility==Visibility.Visible,"Combined filters must display an empty-result explanation");
            Require(queue.SelectedItems.Count==0 && !Control<Button>(window,"RemoveQueueButton").IsEnabled &&
                !Control<Button>(window,"MediaSelectionButton").IsEnabled && ReferenceEquals(rows[0].Selection,planned),
                "A hidden multi-fragment row must retain its plan while no longer remaining actionable");
            search.RaiseEvent(new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice,
                PresentationSource.FromVisual(search),Environment.TickCount,System.Windows.Input.Key.Escape)
                {RoutedEvent=System.Windows.Input.Keyboard.KeyDownEvent});
            Require(queue.Items.Count==4 && empty.Visibility==Visibility.Collapsed,"Reset must restore the whole view without reordering it");
            subscription.SelectedValue="source-A";status.SelectedValue="Queued";
            Require(queue.Items.Cast<DownloadItem>().SequenceEqual(rows.Take(2)),"Status and exact subscription IDs must combine");
            Require(badge.Visibility==Visibility.Visible && badge.Text=="2","The header must count status and subscription criteria independently.");
            subscription.SelectedValue="source-B";
            Require(queue.Items.Count==0,"Subscriptions with equal display names must remain independent by ID");
            status.SelectedValue="Failed";
            Require(queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[2]),"Machine status IDs must control the status filter");
            rows[2].Status="Queued";
            PumpUntil(()=>queue.Items.Count==0,"A row's changing status must update the filtered view");
            rows[2].Status="Failed";
            PumpUntil(()=>queue.Items.Count==1,"A row returning to the selected status must reappear");
            subscription.SelectedValue="";status.SelectedIndex=0;
            Require(queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[3]),"No-subscription is distinct from all subscriptions");
            Click(window,"ResetQueueFiltersButton");
            Require(badge.Visibility==Visibility.Collapsed,"Reset must clear the header's active-filter indicator as well as the predicate.");
            status.SelectedValue="NotCompleted";
            Require(status.Text=="НЕ Готово" && queue.Items.Cast<DownloadItem>().SequenceEqual(rows.Take(3)) &&
                count.Text=="Показано 3 из 4" && badge.Text=="1",
                "Not completed must show every row except Completed with its Russian label and ordinary filter count.");
            subscription.SelectedValue="source-B";search.Text="Южный";
            PumpUntil(()=>queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[2]),
                "Not completed must combine with exact subscription and title filters.");
            foreach(string state in new[]{"Queued","Starting","Running","Skipped","Interrupted","Cancelled","Failed"})
            {
                rows[2].Status=state;
                ((ListCollectionView)queue.ItemsSource).Refresh();
                Require(queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[2]),
                    "Every unfinished status must remain visible: "+state);
            }
            rows[2].Status="Completed";
            PumpUntil(()=>queue.Items.Count==0 && empty.Visibility==Visibility.Visible,
                "A newly completed row must disappear from Not completed without changing source membership.");
            rows[2].Status="Failed";
            PumpUntil(()=>queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[2]),
                "A row returning to an unfinished status must reappear.");
            subject.Reindex();
            Require((string?)status.SelectedValue=="NotCompleted" && queue.Items.Count==1,
                "Rebuilding filter choices must preserve Not completed and its combined criteria.");
            Click(window,"ResetQueueFiltersButton");
            search.Text="incoming title";PumpUntil(()=>queue.Items.Count==0,"The unmatched title filter must start empty");
            rows[0].Title="Incoming TITLE from metadata";
            PumpUntil(()=>queue.Items.Count==1 && ReferenceEquals(queue.Items[0],rows[0]),"Late metadata titles must update search without a new request");
            rows[0].Title="Север — Ёжик один";PumpUntil(()=>queue.Items.Count==0,"A changed title must leave the old search result");
            Click(window,"ResetQueueFiltersButton");
            subject.SaveCheckpoint();
            Require(subject.SessionState.Queue.SequenceEqual(rows) && rows.Select(row=>row.Order).SequenceEqual(new[]{1,2,3,4}) &&
                CheckpointData(File.ReadAllText(storage.SessionPath))==checkpoint && !File.Exists(storage.HistoryPath) &&
                ReferenceEquals(rows[0].Selection,planned) && rows[0].Selection?.EffectiveSections.SequenceEqual(plannedRanges)==true &&
                ReferenceEquals(rows[3].CompletedSelection,completed) && rows[3].OwnedPaths.SequenceEqual(new[]{keptMedia,keptSecond}),
                "Filtering must leave each exact URL, ordered fragment plan, completed outputs, checkpoint and history unchanged");
            PumpFor(300);
            Require(File.ReadAllLines(calls).Length==processes && thumbnails.Requests==http,
                "Typing, resetting, status/subscription filters and selection restoration must not launch metadata or HTTP requests");

            queue.SelectedItems.Add(rows[0]);queue.SelectedItems.Add(rows[1]);
            // Establish the controlled title after the actual selection path,
            // which may restore the fixture's cached title before filtering.
            rows[0].Title="Север — Ёжик один";
            search.Text="ёжик";PumpUntil(()=>queue.Items.Count==1,"The remove test must hide one selected row");
            Click(window,"RemoveQueueButton");
            Require(!subject.SessionState.Queue.Contains(rows[0]) && subject.SessionState.Queue.Contains(rows[1]) &&
                subject.SessionState.Queue.Count==3,"Remove selected must affect only the still-visible selection");
            search.Text="нет совпадений";PumpUntil(()=>queue.Items.Count==0,"The global clear test must hide all remaining rows");
            void ClearWithAnswer(int button,Action? beforeAnswer=null)
            {
                uint thread=GetCurrentThreadId();bool observed=false;
                string title=ErrorMessages.Text("Очистить всю очередь","Clear entire queue");
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
                    beforeAnswer?.Invoke();beforeAnswer=null;
                    if(DismissDialog(thread,title,button)){observed=true;timer.Stop();}
                };
                using var watchdog=new CancellationTokenSource();
                _=Task.Delay(TimeSpan.FromSeconds(10),watchdog.Token).ContinueWith(task=>
                {if(!task.IsCanceled)DismissDialog(thread,title,7);},TaskScheduler.Default);
                timer.Start();try{Click(window,"ClearQueueButton");}finally{watchdog.Cancel();timer.Stop();}
                Require(observed,"The actual global clear confirmation must identify the whole queue");
            }
            Require(Control<Button>(window,"ClearQueueButton").Content.ToString()!.Contains("всю очередь",StringComparison.Ordinal) &&
                Control<Button>(window,"StartButton").Content.ToString()!.Contains("всю очередь",StringComparison.Ordinal),
                "The visible global actions must explicitly say that they target the whole queue");
            ClearWithAnswer(7);Require(subject.SessionState.Queue.Count==3,"Cancelling clear must retain all hidden entries");
            ClearWithAnswer(6,()=>subject.ReceiveArguments([rows[0].Url]));
            Require(subject.SessionState.Queue.Count==1 && subject.SessionState.Queue[0].Url==rows[0].Url && queue.Items.Count==0 &&
                File.ReadAllText(keptMedia)=="user media sentinel" && File.ReadAllText(keptSecond)=="second fragment sentinel",
                "Global clear must remove confirmed hidden entries, keep every fragment output and preserve a late IPC arrival outside its snapshot");
            ClearWithAnswer(6);Require(subject.SessionState.Queue.Count==0,"A new confirmation must clear the remaining hidden arrival");

            // Exercise the actual Start continuation and native worker while
            // no rows match. Filtering must not become an execution filter.
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
            string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
            File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"preflight fixture");
            File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"preflight fixture");
            Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+oldPath);
            string gate=Path.Combine(root,"release-active");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="2",Container="MP4"});
            var fast=new DownloadItem{Url="https://startup-download.invalid/filter-fast"};
            var slow=new DownloadItem{Url="https://startup-download.invalid/filter-timer-slow"};
            subject.SessionState.TryAdd(fast);subject.SessionState.TryAdd(slow);subject.Reindex();
            Require(queue.Items.Count==0,"Active fixture rows must remain hidden by the search");
            Click(window,"StartButton");
            PumpUntil(()=>fast.Status=="Completed" && slow.Status=="Running" && File.Exists(gate+".started"),
                "Start all must publish a hidden fast row while a second hidden worker remains active");
            var runningSession=subject.SessionState.Coordinator;
            search.Clear();status.SelectedValue="NotCompleted";
            PumpUntil(()=>queue.Items.Count==1 && ReferenceEquals(queue.Items[0],slow),
                "Not completed must hide the published fast row while keeping the actual running worker visible.");
            Click(window,"ToggleQueueFiltersButton");
            Require(panel.Visibility==Visibility.Collapsed && badge.Visibility==Visibility.Visible && badge.Text=="1" &&
                ReferenceEquals(subject.SessionState.Coordinator,runningSession) && slow.Status=="Running",
                "Closing filters during a real transfer must preserve the active view and session without pausing or restarting it.");
            Click(window,"ToggleQueueFiltersButton");
            Require(!Control<Button>(window,"ClearQueueButton").IsEnabled && !queue.AllowDrop,"Active session guards must remain enabled under filters");
            Click(window,"ClearQueueButton");Require(subject.SessionState.Queue.Count==2,"A raised clear event must not bypass active-session guards");
            Clipboard.SetText("https://startup-download.invalid/filter-live");Click(window,"PasteClipboardButton");
            PumpUntil(()=>subject.SessionState.Queue.Any(row=>row.Url=="https://startup-download.invalid/filter-live" && row.Status=="Completed"),
                "Explicit clipboard ingress must still join and complete in the same active session while filtered");
            PumpUntil(()=>queue.Items.Count==1 && ReferenceEquals(queue.Items[0],slow),
                "A live-added task must disappear from Not completed after actual successful publication.");
            Require(ReferenceEquals(subject.SessionState.Coordinator,runningSession) && slow.Status=="Running" &&
                subject.SessionState.Urls.Count==3,"Filters and live Paste must preserve the active session and all admitted URL identities");
            Click(window,"PauseButton");Require(subject.SessionState.ReadMetrics().Paused,"Pause must still control the filtered session");
            Click(window,"PauseButton");
            Click(window,"StopButton");
            PumpUntil(()=>subject.SessionState.Coordinator==null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Stop must join the active hidden worker and its cleanup");
            Require(slow.Status=="Interrupted" && !storage.ReadHistory().ContainsKey(slow.Url),"Filtered cancellation must not turn an interrupted row into success");
            Console.WriteLine("PASS: actual queue title/URL/status/subscription filters, exact identity, view-only selection, no extra network, visible remove/global clear and hidden Start/live Paste/Pause/Stop");
        }
        finally
        {
            if(subject?.SessionState.Coordinator is {} active)
            {active.Stop();PumpUntil(()=>subject.SessionState.Coordinator==null,"Queue-filter fixture cancellation did not finish");}
            subject?.Dispose();window.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL",oldThumbnail);
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",oldGate);
            Environment.SetEnvironmentVariable("PATH",oldPath);
            CultureInfo.CurrentUICulture=oldCulture;
        }
    }
}
