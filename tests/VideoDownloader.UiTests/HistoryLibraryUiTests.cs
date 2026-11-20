using System.Collections.Concurrent;
using System.Diagnostics;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestHistoryLibrary(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("History-library WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message,Func<string>? diagnostics=null)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(6))frame.Continue=false;};
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
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)??throw new InvalidOperationException("Missing history control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static bool VisibleInWindow(Window window,FrameworkElement control)
        {
            var bounds=control.TransformToAncestor(window).TransformBounds(new Rect(0,0,control.ActualWidth,control.ActualHeight));
            return control.IsVisible && bounds.Width>0 && bounds.Height>0 && bounds.Top>=0 && bounds.Bottom<=window.ActualHeight;
        }
        string root=Path.Combine(testRoot,"history-ui-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        string existing=Path.Combine(root,"existing.mp4"),missing=Path.Combine(root,"missing.mp4"),denied=Path.Combine(root,"denied.mp4"),unsafePath=Path.Combine(root,"untrusted.exe");
        File.WriteAllText(existing,"media sentinel");
        File.WriteAllText(storage.SessionPath,"damaged queue must remain untouched");
        File.WriteAllText(storage.HistoryPath,JsonSerializer.Serialize(new{Version=1,Url="https://history-ui.invalid/legacy"})+"\n"+
            JsonSerializer.Serialize(new{Version=1,Url="https://history-ui.invalid/Video?token=AbC",CompletedAtUtc="2026-01-02T03:04:05Z",
                Title="Исторический Ёж",Source="Канал",OutputPaths=new[]{existing,missing,denied,unsafePath}})+"\n");
        byte[] history=File.ReadAllBytes(storage.HistoryPath),session=File.ReadAllBytes(storage.SessionPath);
        using var markup=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(markup);
        var owner=(Window)XamlReader.Load(reader);
        var probe=new HistoryUiProbe(existing,missing,denied);
        var opened=new ConcurrentQueue<HistoryOpenCommand>();
        DownloadHistoryWindow? window=null;
        try
        {
            owner.Show();owner.Width=1280;owner.Height=760;
            int dispatcherThread=Environment.CurrentManagedThreadId;
            window=new DownloadHistoryWindow(owner,storage,new HistoryFileAccess(probe),opened.Enqueue);window.Show();
            window.Width=900;window.Height=650;window.UpdateLayout();
            var grid=Control<DataGrid>(window,"HistoryGrid");var files=Control<DataGrid>(window,"HistoryFilesGrid");
            var search=Control<TextBox>(window,"HistorySearchBox");var status=Control<TextBlock>(window,"HistoryStatusText");
            var emptyState=Control<FrameworkElement>(window,"HistoryEmptyState");
            var emptyText=Control<TextBlock>(window,"HistoryEmptyStateText");
            var clearSearch=Control<Button>(window,"ClearHistorySearchButton");
            PumpUntil(()=>grid.Items.Count==2,"The actual history grid did not load modern and legacy events.");
            window.UpdateLayout();
            var fileExpander=Control<Expander>(window,"HistoryFilesExpander");
            var checkFiles=Control<Button>(window,"CheckHistoryFilesButton");
            var cancelCheck=Control<Button>(window,"CancelHistoryFileCheckButton");
            var closeHistory=Control<Button>(window,"CloseHistoryButton");
            Require(!fileExpander.IsExpanded && !files.IsVisible && !checkFiles.IsVisible &&
                cancelCheck.Visibility==Visibility.Collapsed && VisibleInWindow(window,closeHistory),
                "History must initially devote its viewport to results, with file actions disclosed separately and Close still reachable.");
            Require(System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(fileExpander)?.GetName()=="Файлы выбранной загрузки",
                "The file disclosure must expose its purpose through its real UI Automation peer.");
            Require(grid.IsReadOnly && files.IsReadOnly && probe.Calls==0,"Opening the library must not probe the filesystem or change records.");
            var modern=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.Title.Length>0);
            grid.SelectedItem=modern;grid.UpdateLayout();
            Require(modern.Title=="Исторический Ёж" && modern.Source=="Канал" && modern.Url.EndsWith("token=AbC",StringComparison.Ordinal) &&
                modern.Date.Length>0 && files.Items.Count==4,"The actual grid must retain title, source, exact URL, date and captured file paths.");
            Require(!fileExpander.IsExpanded,"Automatic or explicit record selection must not reopen the file group.");
            grid.Items.SortDescriptions.Add(new SortDescription(nameof(HistoryRecordRow.Date),ListSortDirection.Descending));
            var sortedRows=grid.Items.Cast<HistoryRecordRow>().ToArray();var historySource=grid.ItemsSource;
            grid.CurrentCell=new DataGridCellInfo(modern,grid.Columns[1]);
            PumpUntil(()=>window.IsLoaded,"History must finish loading before synthetic viewport probes.",()=>Geometry(window,grid));
            // Force a real wide content layout beyond the native CI desktop's
            // maximum tracking width. Temporarily detach the separately tested
            // bounds controller, which otherwise restores the production minimum.
            OwnedWindowLayout.Attach(window,owner).Dispose();
            window.MinWidth=0;window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
            window.MinWidth=1120;window.Width=1120;window.UpdateLayout();
            PumpUntil(()=>Math.Abs(window.ActualWidth-1120)<=1 &&
                grid.Columns.All(column=>column.Visibility==Visibility.Visible && column.ActualWidth>0) &&
                grid.Columns.Sum(column=>column.ActualWidth)<=grid.ActualWidth+1,
                "Wide history must restore its complete actual column layout.",()=>Geometry(window,grid));
            window.MinWidth=660;window.Width=660;window.UpdateLayout();
            PumpUntil(()=>Math.Abs(window.ActualWidth-660)<=1 &&
                new[]{2,4,5}.All(index=>grid.Columns[index].Visibility==Visibility.Collapsed) &&
                grid.Columns[1].ActualWidth>=200 && grid.Columns[3].ActualWidth>=200 &&
                grid.Columns.Where(column=>column.Visibility==Visibility.Visible).Sum(column=>column.ActualWidth)<=grid.ActualWidth+1,
                "A narrow history must keep date, title and exact URL useful without horizontal overflow.",()=>Geometry(window,grid));
            Require(ReferenceEquals(grid.ItemsSource,historySource) && ReferenceEquals(grid.SelectedItem,modern) &&
                ReferenceEquals(grid.CurrentCell.Item,modern) && grid.Items.Cast<HistoryRecordRow>().SequenceEqual(sortedRows) &&
                grid.Items.SortDescriptions.Single().Equals(new SortDescription(nameof(HistoryRecordRow.Date),ListSortDirection.Descending)) && probe.Calls==0,
                "Responsive history columns must not change the source, current row, sort or filesystem state.");
            window.Width=900;window.UpdateLayout();
            PumpUntil(()=>Math.Abs(window.ActualWidth-900)<=1 && grid.Columns[1].ActualWidth>=200,
                "The history layout must settle when returning to its ordinary width.",()=>Geometry(window,grid));
            grid.Items.SortDescriptions.Clear();
            window.MinWidth=0;window.MinHeight=0;
            window.MaxWidth=double.PositiveInfinity;window.MaxHeight=double.PositiveInfinity;
            window.MinWidth=660;window.MinHeight=440;
            OwnedWindowLayout.Attach(window,owner);window.UpdateLayout();
            double collapsedResultsHeight=grid.ActualHeight;
            fileExpander.IsExpanded=true;window.UpdateLayout();
            checkFiles.BringIntoView();window.UpdateLayout();
            Require(files.IsVisible && VisibleInWindow(window,checkFiles) && VisibleInWindow(window,closeHistory) &&
                grid.ActualHeight+75<collapsedResultsHeight,
                "Expanding files must expose the actual file actions while reserving a bounded viewport and keeping Close reachable.");
            var recordDetails=Control<TextBlock>(window,"HistoryRecordDetails");
            recordDetails.BringIntoView();window.UpdateLayout();
            var detailsViewport=Control<ScrollViewer>(window,"HistoryFilesScroll");
            var detailsBounds=recordDetails.TransformToAncestor(detailsViewport).TransformBounds(new Rect(recordDetails.RenderSize));
            Require(recordDetails.IsVisible && detailsBounds.Top>=0 && detailsBounds.Bottom<=detailsViewport.ActualHeight+1 &&
                recordDetails.Text.Contains("Источник: Канал",StringComparison.Ordinal) && recordDetails.Text.Contains(modern.Date,StringComparison.Ordinal) &&
                recordDetails.Text.Contains("Файлы: 4",StringComparison.Ordinal),
                "The selected record's source, date and file metadata must remain available when compact columns are hidden.");
            double expandedResultsHeight=grid.ActualHeight;
            fileExpander.IsExpanded=false;window.UpdateLayout();
            Require(grid.ActualHeight>expandedResultsHeight+75 && !files.IsVisible && probe.Calls==0 &&
                history.SequenceEqual(File.ReadAllBytes(storage.HistoryPath)) && session.SequenceEqual(File.ReadAllBytes(storage.SessionPath)),
                "Closing file details must return meaningful space to results without probing files, downloading or rewriting user data.");
            fileExpander.IsExpanded=true;window.UpdateLayout();
            grid.ScrollIntoView(modern,grid.Columns[1]);grid.UpdateLayout();
            Require((grid.Columns[1].GetCellContent(modern) as TextBlock)?.Text=="Исторический Ёж",
                "The title must be rendered through the actual history DataGrid binding.");
            search.Text="ёж";PumpUntil(()=>grid.Items.Count==1 && status.Text.Contains("Найдено",StringComparison.Ordinal),"Unicode title search did not filter the real library.");
            search.Text="2026";PumpUntil(()=>grid.Items.Count==1 && status.Text.Contains("Найдено",StringComparison.Ordinal) && ((HistoryRecordRow)grid.Items[0]).Entry.CompletedAtUtc.HasValue,"Date search did not retain its dated success.");
            search.Text="legacy";PumpUntil(()=>grid.Items.Count==1 && ((HistoryRecordRow)grid.Items[0]).Entry.OutputPaths.Count==0,"Legacy search did not expose unknown path metadata.");
            Require(Control<TextBlock>(window,"HistorySelectionStatus").Text.Contains("не сохранены",StringComparison.Ordinal) &&
                !Control<Button>(window,"CheckHistoryFilesButton").IsEnabled,"Legacy records must explain unknown paths, without inventing a missing file.");
            fileExpander.IsExpanded=false;window.UpdateLayout();
            search.Text="no-such-history-entry";
            Require(emptyState.Visibility==Visibility.Collapsed,
                "A pending debounced query must not present the preceding query's empty result as its own.");
            PumpUntil(()=>grid.Items.Count==0 && emptyState.IsVisible && emptyText.Text=="Поиск не нашёл записей.",
                "A no-match query must explain its result in the main viewport, outside collapsed file details.");
            window.UpdateLayout();
            Require(VisibleInWindow(window,emptyText) && VisibleInWindow(window,clearSearch) && !files.IsVisible &&
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(clearSearch)?.GetName()=="Сбросить поиск",
                "Search recovery must remain visible and accessibly labelled without reopening the file group.");
            Click(window,"ClearHistorySearchButton");
            Require(search.Text=="" && emptyState.Visibility==Visibility.Collapsed,
                "Explicit search reset must clear only the query and hide stale no-match feedback while loading.");
            PumpUntil(()=>grid.Items.Count==2,"Clearing search must restore both historical attempts.");
            Require(!emptyState.IsVisible && !fileExpander.IsExpanded && search.IsKeyboardFocusWithin,
                "Search recovery must restore results and keyboard focus without opening file details.");
            fileExpander.IsExpanded=true;window.UpdateLayout();
            modern=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.Title.Length>0);grid.SelectedItem=modern;
            checkFiles.BringIntoView();window.UpdateLayout();
            Click(window,"CheckHistoryFilesButton");
            PumpUntil(()=>modern.Availability.Contains("Доступно 1",StringComparison.Ordinal),"The selected event's file check did not finish.");
            PumpUntil(()=>Control<TextBlock>(window,"HistoryRecordDetails").Text.Contains(modern.Availability,StringComparison.Ordinal),
                "A background check must refresh the selected record details as well as the hidden availability column.");
            Require(cancelCheck.Visibility==Visibility.Collapsed,"The completed file check must remove its inactive cancellation command.");
            var fileRows=files.Items.Cast<HistoryFileRow>().ToArray();
            Require(fileRows.Single(row=>row.Path==existing).Availability.State==HistoryFileState.Present &&
                fileRows.Single(row=>row.Path==missing).Availability.State==HistoryFileState.Missing &&
                fileRows.Single(row=>row.Path==denied).Availability.State==HistoryFileState.Unavailable &&
                fileRows.Single(row=>row.Path==unsafePath).Availability.State==HistoryFileState.Unsafe,
                "Availability must distinguish existing, missing, inaccessible and unsafe paths in actual rows.");
            Require(probe.Threads.All(thread=>thread!=dispatcherThread),"Filesystem work must execute outside the WPF dispatcher.");
            files.SelectedItem=fileRows.Single(row=>row.Path==existing);Click(window,"OpenHistoryFileButton");
            PumpUntil(()=>opened.Count==1 && Control<Button>(window,"OpenHistoryFileButton").IsEnabled,"Explicit opening of a checked media file did not reach the safe launcher.");
            Require(opened.Last().Kind==HistoryOpenKind.Media && opened.Last().MediaPath==existing,"The launch must retain the selected media path.");
            files.SelectedItem=fileRows.Single(row=>row.Path==missing);Click(window,"OpenHistoryFileButton");
            PumpUntil(()=>status.Text.Contains("Не удалось открыть",StringComparison.Ordinal),"A missing file must report its absence.");
            Require(opened.Count==1,"Missing files must not reach the shell launcher.");
            Click(window,"OpenHistoryFolderButton");PumpUntil(()=>opened.Count==2 && Control<Button>(window,"OpenHistoryFolderButton").IsEnabled,"A missing file's existing folder should still be accessible explicitly.");
            Require(opened.Last().Kind==HistoryOpenKind.Folder && opened.Last().TargetPath==root,"Folder actions must target the containing directory, not interpolate a shell command.");
            files.SelectedItem=fileRows.Single(row=>row.Path==unsafePath);
            int before=probe.Calls;Click(window,"OpenHistoryFileButton");Click(window,"OpenHistoryFolderButton");
            Require(!Control<Button>(window,"OpenHistoryFileButton").IsEnabled && opened.Count==2 && probe.Calls==before,
                "Directly raised clicks cannot turn an imported executable path into a shell action.");
            Require(history.SequenceEqual(File.ReadAllBytes(storage.HistoryPath)) && session.SequenceEqual(File.ReadAllBytes(storage.SessionPath)),
                "Reading, searching, missing-file checks and explicit opening cannot rewrite successes or a damaged queue checkpoint.");
            window.Close();window=null;

            var damaged=new AppPersistence(Path.Combine(root,"damaged-profile"));
            File.WriteAllText(damaged.HistoryPath,"not a history JSON record\n");
            byte[] damagedHistory=File.ReadAllBytes(damaged.HistoryPath);
            window=new DownloadHistoryWindow(owner,damaged);window.Show();
            PumpUntil(()=>Control<TextBlock>(window,"HistoryStatusText").Text.StartsWith("Не удалось прочитать историю",StringComparison.Ordinal),
                "A damaged history must report its read failure.");
            Require(Control<FrameworkElement>(window,"HistoryEmptyState").Visibility==Visibility.Collapsed &&
                damagedHistory.SequenceEqual(File.ReadAllBytes(damaged.HistoryPath)),
                "A read failure must not masquerade as an empty history or rewrite the damaged input.");
            window.Close();window=null;

            using(var held=new HistoryUiHeldProbe())
            {
                window=new DownloadHistoryWindow(owner,storage,new HistoryFileAccess(held),opened.Enqueue);window.Show();
                grid=Control<DataGrid>(window,"HistoryGrid");
                PumpUntil(()=>grid.Items.Count==2,"The cancellable history view did not load.");
                grid.SelectedItem=grid.Items.Cast<HistoryRecordRow>().Single(row=>row.Entry.OutputPaths.Count>0);
                window.Width=720;window.Height=440;
                fileExpander=Control<Expander>(window,"HistoryFilesExpander");
                Require(!fileExpander.IsExpanded,"A new history window must not restore the previous window's expanded file group.");
                fileExpander.IsExpanded=true;window.UpdateLayout();
                Control<Button>(window,"CheckHistoryFilesButton").BringIntoView();window.UpdateLayout();
                Click(window,"CheckHistoryFilesButton");PumpUntil(()=>held.Started.Task.IsCompleted,"The background file probe did not start.");
                fileExpander.IsExpanded=false;window.UpdateLayout();
                Require(VisibleInWindow(window,Control<Button>(window,"CancelHistoryFileCheckButton")) &&
                    VisibleInWindow(window,Control<Button>(window,"CloseHistoryButton")),
                    "A short history window must keep cancellation and Close reachable after file details are collapsed during an active probe.");
                window.Close();string closedStatus=Control<TextBlock>(window,"HistoryStatusText").Text;
                held.Release.Set();PumpUntil(()=>held.Active==0,"Closed-window file work did not release its bounded probe slots.");
                Require(Control<TextBlock>(window,"HistoryStatusText").Text==closedStatus,"Late file-check results must not mutate a closed history window.");
                window=null;
            }

            // Observe the actual main-window button/owner lifecycle without a
            // prepared history cache, custom product switches or live metadata.
            var isolated=new AppPersistence(Path.Combine(root,"main-button-profile"));
            using(var native=new NativeWindow(owner,isolated,"must-not-launch"))
            {
                Click(owner,"OpenHistoryButton");PumpUntil(()=>native.HistoryLibrary?.IsVisible==true,"The main history button did not open the actual library.");
                Require(native.SessionState.Coordinator==null && native.SessionState.RunningTask==null && !File.Exists(isolated.HistoryPath),
                    "Opening the main history library must neither create successful history nor start a download.");
                var ownedWindow=native.HistoryLibrary!;
                PumpUntil(()=>Control<FrameworkElement>(ownedWindow,"HistoryEmptyState").IsVisible &&
                    Control<TextBlock>(ownedWindow,"HistoryEmptyStateText").Text=="История пока пуста.",
                    "A fresh profile must explain the empty history in the main results area.");
                ownedWindow.UpdateLayout();
                Require(VisibleInWindow(ownedWindow,Control<TextBlock>(ownedWindow,"HistoryEmptyStateText")) &&
                    Control<Button>(ownedWindow,"ClearHistorySearchButton").Visibility==Visibility.Collapsed &&
                    !Control<Expander>(ownedWindow,"HistoryFilesExpander").IsExpanded && !File.Exists(isolated.HistoryPath),
                    "A truly empty history must stay distinct from no search matches, without a pointless reset or data writes.");
                native.Dispose();Require(!ownedWindow.IsVisible,"Disposing the main owner must close its history window.");
            }
            Console.WriteLine("PASS: real WPF history search, legacy metadata, distinct availability, explicit safe open actions and cancelled close without queue/history writes");
        }
        finally{window?.Close();if(owner.IsVisible)owner.Close();}
    }
    private sealed class HistoryUiProbe(string present,string missing,string denied):IHistoryFileProbe
    {
        private int _calls;
        internal int Calls=>Volatile.Read(ref _calls);
        internal readonly ConcurrentQueue<int> Threads=new();
        public HistoryFileAvailability Check(string path,bool directory)
        {
            Interlocked.Increment(ref _calls);Threads.Enqueue(Environment.CurrentManagedThreadId);
            if(directory)return new(HistoryFileState.Present,"Папка доступна");
            if(path==present)return new(HistoryFileState.Present,"Файл доступен");
            if(path==missing)return new(HistoryFileState.Missing,"Файл не найден");
            return new(HistoryFileState.Unavailable,path==denied?"Нет доступа для проверки":"Неизвестный путь");
        }
    }
    private sealed class HistoryUiHeldProbe:IDisposable,IHistoryFileProbe
    {
        internal readonly ManualResetEventSlim Release=new();
        internal readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _active;
        internal int Active=>Volatile.Read(ref _active);
        public HistoryFileAvailability Check(string path,bool directory)
        {
            Interlocked.Increment(ref _active);Started.TrySetResult();
            try{Release.Wait();return new(HistoryFileState.Present,"late result");}
            finally{Interlocked.Decrement(ref _active);}
        }
        public void Dispose()=>Release.Dispose();
    }
}
