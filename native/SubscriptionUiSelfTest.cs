using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Invoked by the isolated --ui-self-test path. No network or downloader is
    // launched: new row prefetch hits an intentionally title-less local cache.
    public void TestSubscriptionsUi(string testRoot)
    {
        var subscriptions=Subscriptions;
        var originalQueue=SessionState.Queue.ToArray();
        var originalSession=SessionState.Coordinator;
        string originalPreviewUrl=_preview.SelectedUrl;
        string? sourceId=null;
        string suffix=Guid.NewGuid().ToString("N");
        string baselineUrl="https://example.com/subscription-ui-baseline/"+suffix;
        string queueUrl="https://example.com/subscription-ui-queue/"+suffix;
        string skipUrl="https://example.com/subscription-ui-skip/"+suffix;
        string freshUrl="https://example.invalid/subscription-import/"+suffix+"/Video";
        string caseUrl="https://example.invalid/subscription-import/"+suffix+"/video";
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
            Require(condition(),error+" | "+(subscriptions.TestWindow==null?"window closed":subscriptions.TestControl<TextBlock>("SubscriptionStatusText").Text));
        }
        void DrainProgressCallbacks()
        {
            bool drained=false;
            _=Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>drained=true));
            WaitFor(()=>drained,"Progress callbacks must drain through the real WPF dispatcher");
        }
        try
        {
            var definition=subscriptions.TestStore.Add("https://example.com/subscription-ui-source/"+suffix,false);
            sourceId=definition.Id;
            subscriptions.TestStore.ApplyScan(sourceId,new SubscriptionScan("Проверка интерфейса",
                [new DiscoveredMedia(baselineUrl,"Исходный материал")],"","","Канал 123"),[]);
            subscriptions.TestStore.ApplyScan(sourceId,new SubscriptionScan("Проверка интерфейса",
                [new DiscoveredMedia(baselineUrl,"Исходный материал"),new DiscoveredMedia(queueUrl,"Добавить вручную"),
                 new DiscoveredMedia(skipUrl,"Пропустить вручную"),new DiscoveredMedia(freshUrl,"Название из подписки"),
                 new DiscoveredMedia(caseUrl,"Другое название")],"",""),[]);
            var beforeOpen=subscriptions.TestStore.Read().Subscriptions.Single(x=>x.Id==sourceId);
            Require(SessionState.Queue.SequenceEqual(originalQueue),"Seeding a subscription must not add anything to the download queue");

            // The production callback recognizes this as already accepted.
            // It therefore exercises the real Queue button without prefetch.
            var queuedRow=new DownloadItem{Url=queueUrl};
            SessionState.Queue.Add(queuedRow);
            string selectedCache=Path.Combine(_storage.PreviewDir,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(queueUrl))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(selectedCache,JsonSerializer.Serialize(new {
                CacheVersion=1,PreviewFetchVersion=2,SavedAtUtc=DateTime.UtcNow,Url=queueUrl,Success=true,
                Title="",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,
                Codecs="",DynamicRange="",ThumbnailBase64=""
            },AppPersistence.Json));
            Q.SelectedItem=queuedRow;
            T("PreviewTitle").Text="Не удалось получить метаданные";
            T("PreviewMeta").Text="Сохранённая ошибка запроса";
            subscriptions.Open();
            WaitFor(()=>subscriptions.TestWindow!=null && subscriptions.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Any(x=>x.Id==sourceId),
                "Modeless subscriptions window must bind persisted sources");
            var window=subscriptions.TestWindow!;
            Require(window.Owner==_window && window.IsVisible && _window.IsEnabled,"Subscriptions must be a modeless window owned by the main window");
            Require(subscriptions.TestControl<CheckBox>("DownloadExistingCheck").IsChecked==false,"The initial-scan choice must default to baseline");
            Require(subscriptions.TestControl<TextBox>("SubscriptionFeedBox")!=null,"Optional RSS / Atom input must be available");
            Require(subscriptions.TestControl<TextBox>("NewSubscriptionNameBox").Text.Length==0,
                "A new subscription name must default to automatic channel detection");
            Require(subscriptions.TestControl<Border>("SubscriptionCheckProgressPanel").Visibility==Visibility.Collapsed,
                "An idle subscriptions window must not show a fictitious check");
            var progressSource=new SubscriptionDefinition {Id=sourceId,Url=definition.Url,Title="Источник прогресса"};
            long progressBatch=subscriptions.TestProgress.Begin(window,3);
            subscriptions.TestProgress.StartSource(progressBatch,progressSource);
            var firstProgress=subscriptions.TestProgress.CreateObserver(progressBatch,sourceId);
            Require(subscriptions.TestControl<Border>("SubscriptionCheckProgressPanel").Visibility==Visibility.Visible &&
                    subscriptions.TestControl<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate &&
                    subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Value==0 &&
                    subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Maximum==3,
                "A real progress card must animate a running scan without inventing completion inside an unknown collection");
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckSourceText").Text==progressSource.Title &&
                    (string)subscriptions.TestControl<TextBlock>("SubscriptionCheckSourceText").ToolTip==progressSource.Url,
                "The progress card must identify the current source and exact URL");
            Task.Run(()=>firstProgress.Report(new("Enumerating",MetadataBytes:1024))).GetAwaiter().GetResult();
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckDetailText").Text.Contains("после полного обхода") &&
                    subscriptions.TestControl<TextBlock>("SubscriptionCheckDetailText").Text.Contains("1 KB"),
                "Enumerating must show real received bytes while the material count remains unknown");
            firstProgress.Report(new("Parsing",3,2048));
            firstProgress.Report(new("Feed",3,2048));
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text.Contains("RSS / Atom") &&
                    subscriptions.TestControl<TextBlock>("SubscriptionCheckDetailText").Text.Contains("полном списке: 3"),
                "Confirmed item counts and RSS stage must reach actual WPF controls");
            var previewResult=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(previewResult))
            {
                // Export the actual isolated WPF window for visual inspection,
                // without recording the user's settings or opening a browser.
                window.UpdateLayout();
                var content=(FrameworkElement)window.Content;
                int width=(int)Math.Ceiling(content.ActualWidth),height=(int)Math.Ceiling(content.ActualHeight);
                var drawing=new DrawingVisual();
                using(var context=drawing.RenderOpen())
                    context.DrawRectangle(new VisualBrush(content),null,new Rect(0,0,width,height));
                var image=new RenderTargetBitmap(width,height,96,96,PixelFormats.Pbgra32);
                image.Render(drawing);
                var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(image));
                using var file=File.Create(Path.Combine(Path.GetDirectoryName(previewResult)!,"subscription-progress.png"));
                encoder.Save(file);
            }
            subscriptions.TestProgress.SetStage(progressBatch,sourceId,5,"Сравниваем и сохраняем результат…");
            firstProgress.Report(new("Ready",3,2048));
            firstProgress.Report(new("Enumerating",MetadataBytes:1));
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Сравниваем и сохраняем результат…" &&
                    subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Value==0,
                "Late backend callbacks cannot overwrite saving or claim source completion before persistence");
            subscriptions.TestProgress.CompleteSource(progressBatch,sourceId);
            var nextProgressSource=new SubscriptionDefinition {Id=sourceId+"-next",Url=definition.Url+"/next",Title="Следующий источник"};
            subscriptions.TestProgress.StartSource(progressBatch,nextProgressSource);
            firstProgress.Report(new("Parsing",999,99999));
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckSourceText").Text==nextProgressSource.Title &&
                    !subscriptions.TestControl<TextBlock>("SubscriptionCheckDetailText").Text.Contains("999"),
                "A late report for the previous exact source must not alter the next source");
            subscriptions.TestProgress.SetStage(progressBatch,nextProgressSource.Id,7,"Источник не проверен: тестовая ошибка");
            subscriptions.TestProgress.CompleteSource(progressBatch,nextProgressSource.Id);
            subscriptions.TestProgress.SkipSource(progressBatch);
            subscriptions.TestProgress.Finish(progressBatch,false,1,1,0);
            Require(subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Value==2 &&
                    subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Maximum==2 &&
                    subscriptions.TestControl<TextBlock>("SubscriptionCheckCountText").Text.Contains("пропущено: 1") &&
                    subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Проверка завершена с ошибками" &&
                    !subscriptions.TestControl<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate && !subscriptions.TestProgress.HasTimer,
                "Completed attempts, skipped sources and errors must produce an honest terminal card without a running timer");
            string finishedElapsed=subscriptions.TestControl<TextBlock>("SubscriptionCheckElapsedText").Text;
            firstProgress.Report(new("Feed",999));
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckElapsedText").Text==finishedElapsed &&
                    subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Проверка завершена с ошибками",
                "Terminal progress must reject queued callbacks and freeze elapsed time");
            using(var progressCancellation=new CancellationTokenSource())
            {
                subscriptions.TestCancellation=progressCancellation;
                long cancelledBatch=subscriptions.TestProgress.Begin(window,2);
                subscriptions.TestProgress.StartSource(cancelledBatch,progressSource);
                var cancelledProgress=subscriptions.TestProgress.CreateObserver(cancelledBatch,sourceId);
                string initialElapsed=subscriptions.TestControl<TextBlock>("SubscriptionCheckElapsedText").Text;
                WaitFor(()=>subscriptions.TestControl<TextBlock>("SubscriptionCheckElapsedText").Text!=initialElapsed,
                    "Elapsed time must update while the real WPF dispatcher remains responsive");
                subscriptions.TestUpdateButtons();
                subscriptions.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                cancelledProgress.Report(new("Parsing",999));
                DrainProgressCallbacks();
                Require(progressCancellation.IsCancellationRequested && subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Отменяем проверку…" &&
                        !subscriptions.TestProgress.IsClockRunning && !subscriptions.TestProgress.HasTimer &&
                        !subscriptions.TestControl<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate,
                    "The real cancel button must stop incoming progress while cancellation is acknowledged");
                subscriptions.TestProgress.Finish(cancelledBatch,true,0,0,0);
                Require(subscriptions.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Проверка отменена" &&
                        subscriptions.TestControl<ProgressBar>("SubscriptionCheckOverallProgress").Value==0 &&
                        !subscriptions.TestControl<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate && !subscriptions.TestProgress.HasTimer,
                    "Cancellation must preserve partial source completion and stop the activity and elapsed timer");
                subscriptions.TestCancellation=null;
                subscriptions.TestUpdateButtons();
            }
            subscriptions.TestSources.SelectedItem=subscriptions.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(x=>x.Id==sourceId);
            var nameBox=subscriptions.TestControl<TextBox>("SubscriptionNameBox");
            Require(nameBox.Text=="Канал 123" && ((SubscriptionController.SubscriptionSourceView)subscriptions.TestSources.SelectedItem).ShortName=="Канал 123",
                "The source grid and editable folder must show the detected channel name");
            nameBox.Text="Черновик";
            var beforeRefresh=subscriptions.TestSources.SelectedItem;
            subscriptions.Refresh();
            WaitFor(()=>!ReferenceEquals(beforeRefresh,subscriptions.TestSources.SelectedItem),"The source projection must refresh");
            Require(nameBox.Text=="Черновик","Refreshing the same stored source must preserve an unsaved name edit");
            nameBox.Text="123";
            var saveName=subscriptions.TestControl<Button>("SaveSubscriptionNameButton");
            Require(saveName.IsEnabled,"A changed short name must enable Save");
            saveName.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating && subscriptions.TestStore.Read().Subscriptions.Single(x=>x.Id==sourceId).ShortName=="123",
                "Saving the short name must persist it without scanning or downloading");
            Require(nameBox.Text=="123" && ((SubscriptionController.SubscriptionSourceView)subscriptions.TestSources.SelectedItem).Title=="Проверка интерфейса",
                "Saving a short name must retain the original collection title");
            subscriptions.TestSources.UpdateLayout();
            WaitFor(()=>subscriptions.TestSources.Columns[1].GetCellContent(subscriptions.TestSources.SelectedItem) is TextBlock folder && folder.Text=="123",
                "The actual source grid cell must bind the short folder name");
            Require(subscriptions.TestInbox.Items.Count==4,"Baseline materials must stay out of the inbox");
            Require(subscriptions.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Any(x=>x.Title=="Добавить вручную" && x.StateLabel=="Новое"),
                "Inbox title and localized state must bind through actual WPF columns");

            var queueView=subscriptions.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Single(x=>x.Url==queueUrl);
            subscriptions.TestInbox.ScrollIntoView(queueView);
            subscriptions.TestInbox.UpdateLayout();
            WaitFor(()=>subscriptions.TestInbox.Columns[0].GetCellContent(queueView) is TextBlock title && title.Text=="Добавить вручную" &&
                        subscriptions.TestInbox.Columns[1].GetCellContent(queueView) is TextBlock state && state.Text=="Новое",
                "Rendered inbox cells must resolve their title and Russian status bindings");

            subscriptions.TestInbox.SelectedItem=queueView;
            var queueButton=subscriptions.TestControl<Button>("QueueSubscriptionItemsButton");
            Require(queueButton.IsEnabled,"New materials must support explicit Queue");
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating && subscriptions.TestStore.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==queueUrl).State=="Queued",
                "Queue button must record only accepted ordinary-queue URLs");
            Require(SessionState.Queue.Count==originalQueue.Length+1 && SessionState.Queue.Contains(queuedRow) && ReferenceEquals(SessionState.Coordinator,originalSession),
                "Queue must not create duplicate rows or start a download session");
            Require(queuedRow.SubscriptionId.Length==0 && queuedRow.SubscriptionName.Length==0,
                "Accepting a URL already in the ordinary queue must preserve its existing source and folder");
            Require(queuedRow.Title=="Добавить вручную",
                "Discovery must fill an existing empty title even when no preview request is started");
            Require(T("PreviewTitle").Text==queuedRow.Title && T("PreviewMeta").Text=="Сохранённая ошибка запроса",
                "Importing a selected existing URL must immediately replace only its panel title without a metadata request");
            Require(subscriptions.TestStore.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==queueUrl).State!="Processed",
                "Queue acceptance must not claim successful processing");
            Require(queueButton.IsEnabled,"Queued materials must support manual requeue after ordinary queue removal");
            queuedRow.Title="Уже известное название";
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating,"Requeue must finish through the same explicit queue acceptance path");
            Require(SessionState.Queue.Count==originalQueue.Length+1 && SessionState.Queue.Contains(queuedRow),
                "Requeue of an already present URL must remain idempotent");
            Require(queuedRow.Title=="Уже известное название",
                "A repeated import must preserve a real title already known by the ordinary queue");
            queuedRow.Title=queuedRow.Url;
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating,"Importing an URL-fallback title must finish");
            Require(queuedRow.Title=="Добавить вручную","A stored URL fallback must be enriched with the discovery title");
            _preview.SelectedUrl=caseUrl;
            T("PreviewTitle").Text="Другое выбранное видео";
            queuedRow.Title="";
            queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating,"Importing an unselected existing row must finish");
            Require(queuedRow.Title=="Добавить вручную" && T("PreviewTitle").Text=="Другое выбранное видео",
                "Enriching a different URL must leave the selected preview title alone");

            foreach(string url in new[]{freshUrl,caseUrl})
            {
                string cacheFile=Path.Combine(_storage.PreviewDir,
                    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
                AppPersistence.AtomicWrite(cacheFile,JsonSerializer.Serialize(new {
                    CacheVersion=1,PreviewFetchVersion=2,SavedAtUtc=DateTime.UtcNow,Url=url,Success=true,
                    Title="",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,
                    Codecs="",DynamicRange="",ThumbnailBase64=""
                },AppPersistence.Json));
            }
            var titlesAtInsertion=new Dictionary<string,string>(StringComparer.Ordinal);
            System.Collections.Specialized.NotifyCollectionChangedEventHandler observe=(_,change)=>
            {
                if(change.NewItems==null)return;
                foreach(var row in change.NewItems.OfType<DownloadItem>())titlesAtInsertion[row.Url]=row.Title;
            };
            SessionState.Queue.CollectionChanged+=observe;
            try
            {
                subscriptions.TestInbox.SelectedItems.Clear();
                foreach(var row in subscriptions.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Where(x=>x.Url==freshUrl || x.Url==caseUrl))
                    subscriptions.TestInbox.SelectedItems.Add(row);
                queueButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitFor(()=>!subscriptions.TestIsMutating && SessionState.Queue.Any(row=>row.Url==freshUrl) && SessionState.Queue.Any(row=>row.Url==caseUrl),
                    "New subscription materials must be accepted into the main queue");
            }
            finally {SessionState.Queue.CollectionChanged-=observe;}
            var firstImported=SessionState.Queue.Single(row=>row.Url==freshUrl);
            var secondImported=SessionState.Queue.Single(row=>row.Url==caseUrl);
            Require(titlesAtInsertion.GetValueOrDefault(freshUrl)=="Название из подписки" &&
                    titlesAtInsertion.GetValueOrDefault(caseUrl)=="Другое название" &&
                    firstImported.Title=="Название из подписки" && secondImported.Title=="Другое название",
                "Exact discovery titles must exist before row insertion/prefetch and survive an image-less, title-less preview");
            Require(firstImported.SubscriptionId==sourceId && firstImported.SubscriptionName=="123" &&
                    firstImported.Status=="Queued" && ReferenceEquals(SessionState.Coordinator,originalSession),
                "Title import must preserve subscription routing and wait for manual Start");
            var savedTitles=_storage.Restore()!.Queue;
            Require(savedTitles.Single(row=>row.Url==freshUrl).Title=="Название из подписки" &&
                    savedTitles.Single(row=>row.Url==caseUrl).Title=="Другое название",
                "Imported titles must be checkpointed before any download or successful preview");

            subscriptions.TestInbox.SelectedItem=subscriptions.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Single(x=>x.Url==skipUrl);
            subscriptions.TestControl<Button>("SkipSubscriptionItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating && subscriptions.TestStore.Read().Items.Single(x=>x.SubscriptionId==sourceId && x.Url==skipUrl).State=="Skipped",
                "Skip must be durable without claiming processing");
            Require(subscriptions.TestControl<Button>("QueueSubscriptionItemsButton").IsEnabled,"Skipped items must remain available for an explicit Queue");

            subscriptions.TestControl<Button>("PauseSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating && subscriptions.TestStore.Read().Subscriptions.Single(x=>x.Id==sourceId).Paused,
                "Pause button must persist the paused state");
            Require(!subscriptions.TestControl<Button>("CheckSubscriptionButton").IsEnabled,"Paused sources must not be checked accidentally");
            subscriptions.TestControl<Button>("PauseSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            WaitFor(()=>!subscriptions.TestIsMutating && !subscriptions.TestStore.Read().Subscriptions.Single(x=>x.Id==sourceId).Paused,
                "Resume button must persist the resumed state without starting a scan");

            using(var scanCancellation=new CancellationTokenSource())
            {
                subscriptions.TestCancellation=scanCancellation;
                subscriptions.TestUpdateButtons();
                subscriptions.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(scanCancellation.IsCancellationRequested && ReferenceEquals(SessionState.Coordinator,originalSession),
                    "Scan cancellation must use its own token and leave the downloader alone");
                subscriptions.TestCancellation=null;
                subscriptions.TestUpdateButtons();
            }
            long closingBatch=subscriptions.TestProgress.Begin(window,1);
            subscriptions.TestProgress.StartSource(closingBatch,progressSource);
            var closingProgress=subscriptions.TestProgress.CreateObserver(closingBatch,sourceId);
            window.Close();
            Require(!subscriptions.TestProgress.HasTimer && !subscriptions.TestProgress.IsActive,
                "Closing the subscriptions window must stop its independent progress timer");
            subscriptions.Open();
            WaitFor(()=>subscriptions.TestWindow!=null && subscriptions.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Any(x=>x.Id==sourceId),
                "Reopening subscriptions must create a fresh progress presentation");
            closingProgress.Report(new("Parsing",999));
            DrainProgressCallbacks();
            Require(subscriptions.TestControl<Border>("SubscriptionCheckProgressPanel").Visibility==Visibility.Collapsed,
                "A callback from a closed window must not reopen progress in its replacement");
            subscriptions.TestWindow!.Close();
            var afterClose=subscriptions.TestStore.Read();
            var source=afterClose.Subscriptions.Single(x=>x.Id==sourceId);
            Require(source.LastAttemptUtc==beforeOpen.LastAttemptUtc && source.LastSuccessUtc==beforeOpen.LastSuccessUtc,
                "Opening, queueing, skipping and closing must never run a background scan");
            Require(afterClose.Items.Single(x=>x.SubscriptionId==sourceId && x.Url==baselineUrl).State=="Baseline",
                "Closing the subscriptions window must preserve its baseline");
        }
        finally
        {
            subscriptions.TestWindow?.Close();
            if (sourceId!=null) subscriptions.TestStore.Remove(sourceId);
            SessionState.Queue.Clear();
            foreach (var row in originalQueue) SessionState.Queue.Add(row);
            _preview.SelectedUrl=originalPreviewUrl;
            Reindex();
        }
    }
}

// The existing in-assembly UI harness inspects the new owner explicitly. These
// accessors live with the scenarios, not in the production controller contract;
// the separate test-host migration can replace them without restoring window state.
internal sealed partial class SubscriptionController
{
    internal Window? TestWindow=>_window;
    internal SubscriptionStore TestStore=>_store;
    internal DataGrid TestSources=>Sources;
    internal DataGrid TestInbox=>Inbox;
    internal bool TestIsMutating=>_mutation;
    internal CancellationTokenSource? TestCancellation {get=>_cancellation;set=>_cancellation=value;}
    internal SubscriptionCheckProgressController TestProgress=>_checkProgress;
    internal T TestControl<T>(string name) where T:class => Control<T>(name);
    internal void TestUpdateButtons()=>UpdateButtons();
}
