using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Invoked by the isolated --ui-self-test path. No scanner, feed, preview or
    // downloader is launched: queue acceptance reuses an existing queued row.
    public void TestSubscriptionsUi(string testRoot)
    {
        var originalQueue=_queue.ToArray();
        var originalSession=_session;
        string? sourceId=null;
        string suffix=Guid.NewGuid().ToString("N");
        string baselineUrl="https://example.com/subscription-ui-baseline/"+suffix;
        string queueUrl="https://example.com/subscription-ui-queue/"+suffix;
        string skipUrl="https://example.com/subscription-ui-skip/"+suffix;
        string profile=Path.GetDirectoryName(_storage.SessionPath)!;
        if (!Path.GetFullPath(profile).StartsWith(Path.GetFullPath(testRoot).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Subscription UI test requires the isolated test persistence directory.");

        static void Require(bool condition,string error)
        {
            if (!condition) throw new InvalidOperationException("Subscription WPF test failed: "+error);
        }
        void WaitFor(Func<bool> condition,string error)
        {
            if (condition()) return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(15);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            Exception? failure=null;
            timer.Tick+=(_,_)=>
            {
                try { if (condition() || DateTime.UtcNow>deadline) frame.Continue=false; }
                catch(Exception e) { failure=e;frame.Continue=false; }
            };
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if (failure!=null) throw new InvalidOperationException(error,failure);
            Require(condition(),error+" | "+(_subscriptionsWindow==null?"window closed":SubscriptionControl<TextBlock>("SubscriptionStatusText").Text));
        }
        try
        {
            var definition=SubscriptionStorage.Add("https://example.com/subscription-ui-source/"+suffix,false);
            sourceId=definition.Id;
            SubscriptionStorage.ApplyScan(sourceId,new SubscriptionScan("Проверка интерфейса",
                [new DiscoveredMedia(baselineUrl,"Исходный материал")],"","","Канал 123"),[]);
            SubscriptionStorage.ApplyScan(sourceId,new SubscriptionScan("Проверка интерфейса",
                [new DiscoveredMedia(baselineUrl,"Исходный материал"),new DiscoveredMedia(queueUrl,"Добавить вручную"),
                 new DiscoveredMedia(skipUrl,"Пропустить вручную")],"",""),[]);
            var beforeOpen=SubscriptionStorage.Read().Subscriptions.Single(x=>x.Id==sourceId);
            Require(_queue.SequenceEqual(originalQueue),"Seeding a subscription must not add anything to the download queue");

            // The production callback recognizes this as already accepted.
            // It therefore exercises the real Queue button without prefetch.
            var queuedRow=new DownloadItem{Url=queueUrl};
            _queue.Add(queuedRow);
            OpenSubscriptions();
            WaitFor(()=>_subscriptionsWindow!=null && SubscriptionSources.Items.Cast<SubscriptionSourceView>().Any(x=>x.Id==sourceId),
                "Modeless subscriptions window must bind persisted sources");
            var window=_subscriptionsWindow!;
            Require(window.Owner==_window && window.IsVisible && _window.IsEnabled,"Subscriptions must be a modeless window owned by the main window");
            Require(SubscriptionControl<CheckBox>("DownloadExistingCheck").IsChecked==false,"The initial-scan choice must default to baseline");
            Require(SubscriptionControl<TextBox>("SubscriptionFeedBox")!=null,"Optional RSS / Atom input must be available");
            Require(SubscriptionControl<TextBox>("NewSubscriptionNameBox").Text.Length==0,
                "A new subscription name must default to automatic channel detection");
            SubscriptionSources.SelectedItem=SubscriptionSources.Items.Cast<SubscriptionSourceView>().Single(x=>x.Id==sourceId);
            var nameBox=SubscriptionControl<TextBox>("SubscriptionNameBox");
            Require(nameBox.Text=="Канал 123" && ((SubscriptionSourceView)SubscriptionSources.SelectedItem).ShortName=="Канал 123",
                "The source grid and editable folder must show the detected channel name");
            nameBox.Text="Черновик";
            var beforeRefresh=SubscriptionSources.SelectedItem;
            RefreshSubscriptions();
            WaitFor(()=>!ReferenceEquals(beforeRefresh,SubscriptionSources.SelectedItem),"The source projection must refresh");
            Require(nameBox.Text=="Черновик","Refreshing the same stored source must preserve an unsaved name edit");
            nameBox.Text="123";
            var saveName=SubscriptionControl<Button>("SaveSubscriptionNameButton");
            Require(saveName.IsEnabled,"A changed short name must enable Save");
            saveName.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation && SubscriptionStorage.Read().Subscriptions.Single(x=>x.Id==sourceId).ShortName=="123",
                "Saving the short name must persist it without scanning or downloading");
            Require(nameBox.Text=="123" && ((SubscriptionSourceView)SubscriptionSources.SelectedItem).Title=="Проверка интерфейса",
                "Saving a short name must retain the original collection title");
            SubscriptionSources.UpdateLayout();
            WaitFor(()=>SubscriptionSources.Columns[1].GetCellContent(SubscriptionSources.SelectedItem) is TextBlock folder && folder.Text=="123",
                "The actual source grid cell must bind the short folder name");
            Require(SubscriptionInbox.Items.Count==2,"Baseline materials must stay out of the inbox");
            Require(SubscriptionInbox.Items.Cast<SubscriptionItemView>().Any(x=>x.Title=="Добавить вручную" && x.StateLabel=="Новое"),
                "Inbox title and localized state must bind through actual WPF columns");

            var queueView=SubscriptionInbox.Items.Cast<SubscriptionItemView>().Single(x=>x.Url==queueUrl);
            SubscriptionInbox.ScrollIntoView(queueView);
            SubscriptionInbox.UpdateLayout();
            WaitFor(()=>SubscriptionInbox.Columns[0].GetCellContent(queueView) is TextBlock title && title.Text=="Добавить вручную" &&
                        SubscriptionInbox.Columns[1].GetCellContent(queueView) is TextBlock state && state.Text=="Новое",
                "Rendered inbox cells must resolve their title and Russian status bindings");

            SubscriptionInbox.SelectedItem=queueView;
            var queueButton=SubscriptionControl<Button>("QueueSubscriptionItemsButton");
            Require(queueButton.IsEnabled,"New materials must support explicit Queue");
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation && SubscriptionStorage.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==queueUrl).State=="Queued",
                "Queue button must record only accepted ordinary-queue URLs");
            Require(_queue.Count==originalQueue.Length+1 && _queue.Contains(queuedRow) && ReferenceEquals(_session,originalSession),
                "Queue must not create duplicate rows or start a download session");
            Require(queuedRow.SubscriptionId.Length==0 && queuedRow.SubscriptionName.Length==0,
                "Accepting a URL already in the ordinary queue must preserve its existing source and folder");
            Require(SubscriptionStorage.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==queueUrl).State!="Processed",
                "Queue acceptance must not claim successful processing");
            Require(queueButton.IsEnabled,"Queued materials must support manual requeue after ordinary queue removal");
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation,"Requeue must finish through the same explicit queue acceptance path");
            Require(_queue.Count==originalQueue.Length+1 && _queue.Contains(queuedRow),
                "Requeue of an already present URL must remain idempotent");

            SubscriptionInbox.SelectedItem=SubscriptionInbox.Items.Cast<SubscriptionItemView>().Single(x=>x.Url==skipUrl);
            SubscriptionControl<Button>("SkipSubscriptionItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation && SubscriptionStorage.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==skipUrl).State=="Skipped",
                "Skip must be durable without claiming processing");
            Require(SubscriptionControl<Button>("QueueSubscriptionItemsButton").IsEnabled,"Skipped items must remain available for an explicit Queue");

            SubscriptionControl<Button>("PauseSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation && SubscriptionStorage.Read().Subscriptions.Single(x=>x.Id==sourceId).Paused,
                "Pause button must persist the paused state");
            Require(!SubscriptionControl<Button>("CheckSubscriptionButton").IsEnabled,"Paused sources must not be checked accidentally");
            SubscriptionControl<Button>("PauseSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!_subscriptionMutation && !SubscriptionStorage.Read().Subscriptions.Single(x=>x.Id==sourceId).Paused,
                "Resume button must persist the resumed state without starting a scan");

            using(var scanCancellation=new CancellationTokenSource())
            {
                _subscriptionCancellation=scanCancellation;
                UpdateSubscriptionButtons();
                SubscriptionControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(scanCancellation.IsCancellationRequested && ReferenceEquals(_session,originalSession),
                    "Scan cancellation must use its own token and leave the downloader alone");
                _subscriptionCancellation=null;
                UpdateSubscriptionButtons();
            }
            window.Close();
            var afterClose=SubscriptionStorage.Read();
            var source=afterClose.Subscriptions.Single(x=>x.Id==sourceId);
            Require(source.LastAttemptUtc==beforeOpen.LastAttemptUtc && source.LastSuccessUtc==beforeOpen.LastSuccessUtc,
                "Opening, queueing, skipping and closing must never run a background scan");
            Require(afterClose.Items.Single(x=>x.SubscriptionId==sourceId && x.Url==baselineUrl).State=="Baseline",
                "Closing the subscriptions window must preserve its baseline");
        }
        finally
        {
            _subscriptionsWindow?.Close();
            if (sourceId!=null) SubscriptionStorage.Remove(sourceId);
            _queue.Clear();
            foreach (var row in originalQueue) _queue.Add(row);
            Reindex();
        }
    }
}
