using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestDownloadsActiveFirst(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Active-first Downloads WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(5))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static void Drain()
        {
            var work=Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            PumpUntil(()=>work.Status==DispatcherOperationStatus.Completed,"Pending UI notifications must finish");
        }
        static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject
        {
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(root);index++)
            {
                var child=VisualTreeHelper.GetChild(root,index);
                if(child is T match)yield return match;
                foreach(var nested in Descendants<T>(child))yield return nested;
            }
        }
        static string CheckpointData(string text)
        {
            using var json=JsonDocument.Parse(text);
            return json.RootElement.GetProperty("Queue").GetRawText()+"|"+json.RootElement.GetProperty("Settings").GetRawText();
        }

        string root=Path.Combine(testRoot,"downloads-active-first");Directory.CreateDirectory(root);
        string media=Path.Combine(root,"selected-result.mp4");File.WriteAllText(media,"published file sentinel");
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch",automaticSubscriptionChecks:false);
            window.Show();window.Width=1280;window.Height=760;window.UpdateLayout();
            var grid=(DataGrid)window.FindName("DownloadGrid")!;
            var urlInput=(TextBox)window.FindName("UrlInput")!;
            Button Action(string name)=>(Button)window.FindName(name)!;
            var complete=new DownloadItem{Url="https://downloads-order.invalid/Video?token=AbC",Status="Completed",State="Completed",Slot="002"};
            var running=new DownloadItem{Url="https://downloads-order.invalid/video?token=AbC",Status="Running",State="Downloading",Slot="009",
                Path=media,OutputRoot=root,OwnedPaths=[media]};
            var failed=new DownloadItem{Url="https://downloads-order.invalid/failed",Status="Failed",State="Failed",Slot="001"};
            var starting=new DownloadItem{Url="https://downloads-order.invalid/starting",Status="Starting",State="Starting",Slot="003"};
            var secondRunning=new DownloadItem{Url="https://downloads-order.invalid/second",Status="Running",State="Downloading",Slot="004"};
            var cancelled=new DownloadItem{Url="https://downloads-order.invalid/cancelled",Status="Interrupted",State="Cancelled",Slot="000"};
            var queued=new DownloadItem{Url="https://downloads-order.invalid/queued",Status="Queued",State="Queued",Slot="008"};
            DownloadItem[] rows=[complete,running,failed,starting,secondRunning,cancelled,queued,
                ..Enumerable.Range(100,36).Select(index=>new DownloadItem
                {Url="https://downloads-order.invalid/archive/"+index,Status="Completed",State="Completed",Slot=index.ToString()})];
            foreach(var row in rows)
            {
                Require(subject.SessionState.TryAdd(row),"Each exact URL must be independently admitted");
                subject.SessionState.Downloads.Add(row);
            }
            subject.Reindex();
            int sourceChanges=0,queueChanges=0;
            subject.SessionState.Downloads.CollectionChanged+=(_,_)=>sourceChanges++;
            subject.SessionState.Queue.CollectionChanged+=(_,_)=>queueChanges++;
            var identities=rows.Select(row=>row.Url).ToArray();
            var owned=running.OwnedPaths.ToArray();
            void RequireSources()
            {
                Require(subject.SessionState.Downloads.SequenceEqual(rows) && subject.SessionState.Queue.SequenceEqual(rows) &&
                    rows.Select(row=>row.Order).SequenceEqual(Enumerable.Range(1,rows.Length)) &&
                    rows.Select(row=>row.Url).SequenceEqual(identities,StringComparer.Ordinal) && sourceChanges==0 && queueChanges==0,
                    "Presentation changes must not move/replace source rows, change admission numbers or normalize exact URLs");
                Require(running.OwnedPaths.SequenceEqual(owned) && running.OutputRoot==root && running.Path==media &&
                    File.ReadAllText(media)=="published file sentinel" && !File.Exists(storage.HistoryPath) &&
                    subject.SessionState.Coordinator==null && subject.SessionState.RunningTask==null,
                    "Sorting must preserve published paths, ownership and history without starting an engine");
            }
            void ExpectActive(DownloadItem[] active,string message)
            {
                var expected=active.Concat(rows.Where(row=>!active.Contains(row))).ToArray();
                PumpUntil(()=>grid.Items.Cast<DownloadItem>().SequenceEqual(expected),message);
                grid.UpdateLayout();RequireSources();
            }
            Require(grid.ItemsSource is ListCollectionView && grid.IsReadOnly,"The real Downloads grid must use its read-only presentation view");
            ExpectActive([running,starting,secondRunning],"All running/starting rows must precede finished rows in stable admission order");
            var defaultView=CollectionViewSource.GetDefaultView(subject.SessionState.Downloads);
            Require(!ReferenceEquals(defaultView,grid.ItemsSource) && defaultView.Cast<DownloadItem>().SequenceEqual(rows) &&
                grid.ItemContainerGenerator.ContainerFromItem(running) is DataGridRow {IsVisible:true} firstContainer && firstContainer.GetIndex()==0,
                "The first rendered row must be active while the collection's independent default view retains source order");
            grid.SelectedItem=running;grid.CurrentCell=new DataGridCellInfo(running,grid.Columns[0]);grid.Items.MoveCurrentTo(running);
            void RequireSelected()
            {
                Require(ReferenceEquals(grid.SelectedItem,running) && ReferenceEquals(grid.CurrentCell.Item,running) &&
                    ReferenceEquals(grid.Items.CurrentItem,running) && Action("OpenFileButton").IsEnabled &&
                    Action("OpenFolderButton").IsEnabled && Action("CopyPathButton").IsEnabled && Action("OpenUrlButton").IsEnabled,
                    "A moving selected row must remain the current row and retain its exact-file/URL actions");
            }
            RequireSelected();

            // Status notifications really originate off the WPF thread, as they
            // do in DownloadCoordinator. No explicit Items.Refresh is allowed.
            void SetFromWorker(DownloadItem row,string status,string state)
            {
                var change=Task.Run(()=>{row.State=state;row.Status=status;});
                PumpUntil(()=>change.IsCompleted,"A background status change must complete");
                change.GetAwaiter().GetResult();Drain();
            }
            SetFromWorker(running,"Completed","Completed");
            ExpectActive([starting,secondRunning],"A published row must leave the active group without disturbing the other active rows");RequireSelected();
            SetFromWorker(starting,"Failed","Failed");
            ExpectActive([secondRunning],"A failed starting job must return to its stable inactive position");RequireSelected();
            SetFromWorker(secondRunning,"Interrupted","Cancelled");
            ExpectActive([],"The final cancelled worker must leave the original inactive admission order");RequireSelected();
            SetFromWorker(queued,"Starting","Starting");
            ExpectActive([queued],"A later queued row must move above every earlier finished row when it starts");
            SetFromWorker(failed,"Running","Merging");SetFromWorker(starting,"Starting","Cancelling");
            queued.Status="Running";queued.State="Processing";
            ExpectActive([failed,starting,queued],"Parallel starting, processing and merging jobs must all remain above inactive rows");RequireSelected();
            failed.State="Publishing";starting.State="Cancelling";queued.State="Paused";Drain();
            ExpectActive([failed,starting,queued],"Postprocessing, pending cancellation and paused running rows must retain active priority");

            int viewReorders=0;
            NotifyCollectionChangedEventHandler changed=(_,args)=>
            {if(args.Action is NotifyCollectionChangedAction.Move or NotifyCollectionChangedAction.Reset)viewReorders++;};
            ((INotifyCollectionChanged)grid.Items).CollectionChanged+=changed;
            try
            {
                for(int index=0;index<20;index++)
                {failed.Progress=index;failed.Speed=index+" MiB/s";queued.ProgressDetail="progress "+index;}
                Drain();
                Require(viewReorders==0,"Ordinary progress/speed updates must not reset or reorder the default view");
            }
            finally {((INotifyCollectionChanged)grid.Items).CollectionChanged-=changed;}
            ExpectActive([failed,starting,queued],"Progress updates must keep stable order within the active group");

            grid.ScrollIntoView(rows[^1]);grid.UpdateLayout();
            var scroll=Descendants<ScrollViewer>(grid).First();
            scroll.ScrollToVerticalOffset(15);Drain();grid.UpdateLayout();
            Require(scroll.VerticalOffset>0,"The real Downloads viewport must be scrolled away from its top");
            urlInput.Focus();var focus=Keyboard.FocusedElement;double offset=scroll.VerticalOffset;
            Require(ReferenceEquals(focus,urlInput),"A different actual control must own keyboard focus before a background row move");
            SetFromWorker(complete,"Running","Downloading");
            ExpectActive([complete,failed,starting,queued],"A background promotion must update the view while it is scrolled");
            RequireSelected();
            Require(ReferenceEquals(Keyboard.FocusedElement,focus) && Math.Abs(scroll.VerticalOffset-offset)<0.01,
                "A background status move must preserve keyboard focus and the user's scroll position");
            SetFromWorker(complete,"Completed","Completed");
            ExpectActive([failed,starting,queued],"Completing the promoted row must restore stable inactive placement");

            subject.SaveCheckpoint();string checkpoint=CheckpointData(File.ReadAllText(storage.SessionPath));
            void SortSlot(ListSortDirection expectedDirection)
            {
                grid.UpdateLayout();
                var header=Descendants<DataGridColumnHeader>(grid).Single(item=>ReferenceEquals(item.Column,grid.Columns[0]));
                var peer=new DataGridColumnHeaderAutomationPeer(header);
                Require(peer.GetPattern(PatternInterface.Invoke) is IInvokeProvider,"The actual column header must expose its normal sort invocation");
                ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)!).Invoke();
                PumpUntil(()=>grid.Columns[0].SortDirection==expectedDirection,"Invoking the real header must update its sort arrow");
                Drain();
            }
            var active=new[]{failed,starting,queued};
            DownloadItem[] ExpectedSlot(bool descending)
            {
                IEnumerable<DownloadItem> Sort(IEnumerable<DownloadItem> items)=>descending
                    ? items.OrderByDescending(row=>row.Slot,StringComparer.CurrentCulture)
                    : items.OrderBy(row=>row.Slot,StringComparer.CurrentCulture);
                return [..Sort(active),..Sort(rows.Where(row=>!active.Contains(row)))];
            }
            SortSlot(ListSortDirection.Ascending);
            Require(grid.Items.Cast<DownloadItem>().SequenceEqual(ExpectedSlot(false)),
                "An ascending header sort must order each group while keeping all active jobs first");RequireSelected();RequireSources();
            SortSlot(ListSortDirection.Descending);
            Require(grid.Items.Cast<DownloadItem>().SequenceEqual(ExpectedSlot(true)),
                "A descending header sort must never place archived rows ahead of active jobs");RequireSelected();RequireSources();
            Action("CopyPathButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Clipboard.GetText()==media,"Actions after sorting must still target the selected row's exact stored path");
            subject.SaveCheckpoint();
            Require(CheckpointData(File.ReadAllText(storage.SessionPath))==checkpoint,
                "Header sorting, selection restoration and copying a path must not alter persisted row or settings data");

            queued.Slot="000";Drain();
            Require(grid.Items.Cast<DownloadItem>().SequenceEqual(ExpectedSlot(true)),
                "The selected manual sort property must keep updating within its active group");
            SetFromWorker(running,"Running","Downloading");active=[failed,starting,queued,running];
            PumpUntil(()=>grid.Items.Cast<DownloadItem>().SequenceEqual(ExpectedSlot(true)),
                "A new active status must preserve the user's descending column sort within both groups");RequireSelected();
            SetFromWorker(running,"Failed","Failed");active=[failed,starting,queued];
            PumpUntil(()=>grid.Items.Cast<DownloadItem>().SequenceEqual(ExpectedSlot(true)),
                "A selected failed row must leave the active group under the retained manual sort");RequireSelected();RequireSources();
            Console.WriteLine("PASS: real Downloads view, worker-thread status moves, parallel/paused processing, stable source order, header sorts, selection/actions, scroll/focus and unchanged history/checkpoint");
        }
        finally {subject?.Dispose();window.Close();}
    }
}
