using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
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
    private static void TestQueueReset(string testRoot)
    {
        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        ResetUi.Require(File.Exists(fixture),"The actual native metadata/download fixture is required.");
        string root=Path.Combine(testRoot,"queue-reset");Directory.CreateDirectory(root);
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"preflight fixture");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"preflight fixture");
        string? previousPath=Environment.GetEnvironmentVariable("PATH"),previousLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG"),
            previousGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE");
        var previousCulture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+previousPath);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        try
        {
            ResetSelectedOwnedFiles(Path.Combine(root,"selected"),fixture);
            ResetQueuedMetadataCallback(Path.Combine(root,"queued-metadata"),fixture);
            ResetActiveAttempt(Path.Combine(root,"active"),fixture);
            ResetHonorsStopDuringMetadataJoin(Path.Combine(root,"late-stop"),fixture);
            ResetRejectsIncompatibleActivePlan(Path.Combine(root,"active-plan-refusal"),fixture);
            ResetProtectedOwnedFile(Path.Combine(root,"protected"),fixture);
            Console.WriteLine("PASS: guarded selected reset, No/Yes confirmation, exact owned deletion, fresh metadata and stale Dispatcher rejection, same-engine native restart/child join and protected-file recovery");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH",previousPath);Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousLog);
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",previousGate);CultureInfo.CurrentUICulture=previousCulture;
        }
    }

    private static void ResetSelectedOwnedFiles(string root,string fixture)
    {
        Directory.CreateDirectory(root);
        using var oldImages=new PreviewThumbnailServer(24);using var freshImages=new PreviewThumbnailServer(180);
        string control=Path.Combine(root,"metadata.json");
        void Metadata(string title,string image)=>File.WriteAllText(control,JsonSerializer.Serialize(new
        {
            title,duration=10,thumbnail=image,extractor="reset-ui-fixture",
            formats=new[]{new{format_id="18",ext="mp4",width=640,height=360,fps=25,vcodec="avc1",acodec="mp4a"}}
        }),Encoding.UTF8);
        Metadata("Old metadata",oldImages.Url);
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"new-output"),Cookies=control,Threads="2",Container="MP4"});
        string ownedRoot=Path.Combine(root,"previous-output");Directory.CreateDirectory(ownedRoot);
        string firstFile=Path.Combine(ownedRoot,"first.mp4"),firstSidecar=Path.Combine(ownedRoot,"first.en.vtt"),secondFile=Path.Combine(ownedRoot,"second.mp4"),
            foreign=Path.Combine(ownedRoot,"foreign-user-file.mp4");
        File.WriteAllText(firstFile,"first owned media");File.WriteAllText(firstSidecar,"first owned subtitles");
        File.WriteAllText(secondFile,"second owned media");File.WriteAllText(foreign,"unowned media sentinel");
        var first=new DownloadItem{Url="https://lifecycle-regression.invalid/reset-scope-first",Status="Completed",State="Done",Progress=100,
            AttemptFinished=true,Path=firstFile,OwnedPaths=[firstFile,firstSidecar],OutputRoot=ownedRoot,TransferredBytes=12345,Downloaded="old bytes",
            SubscriptionId="reset-subscription",SubscriptionName="Сохраняемая папка"};
        var selection=new DownloadSelection(first.Url,new DownloadSection(1,2),DurationSeconds:10);first.Selection=selection;
        var second=new DownloadItem{Url="https://lifecycle-regression.invalid/reset-scope-second",Status="Completed",State="Done",Progress=100,
            AttemptFinished=true,Path=secondFile,OwnedPaths=[secondFile],OutputRoot=ownedRoot,TransferredBytes=54321,Downloaded="other old bytes"};
        var hidden=new DownloadItem{Url="https://lifecycle-regression.invalid/reset-hidden",Title="Hidden queued row"};
        ui.Subject.SessionState.AddBatch([first,second,hidden],ui.Subject.ReadOptions());ui.Subject.Reindex();
        var finalMetadata=new HashSet<string>(StringComparer.Ordinal);
        ui.Subject.Preview.Changed+=(data,final)=>{if(final)finalMetadata.Add(data.Url);};
        foreach(var row in ui.Subject.SessionState.Queue)ui.Subject.Preview.RequestRow(row);
        ResetUi.Until(()=>new[]{first,second,hidden}.All(row=>finalMetadata.Contains(row.Url) &&
            ui.Subject.Preview.GetCached(row.Url)?.ThumbnailBase64==oldImages.Encoded && File.Exists(ui.Cache(row.Url))),
            "Initial metadata and genuine HTTP cover bytes must arrive for every exact URL.");
        ui.Data.AppendSuccess(first);ui.Data.AppendSuccess(second);ui.Subject.SaveCheckpoint();
        byte[] history=File.ReadAllBytes(ui.Data.HistoryPath),hiddenCache=File.ReadAllBytes(ui.Cache(hidden.Url));
        var firstCached=ui.Subject.Preview.GetCached(first.Url);var secondCached=ui.Subject.Preview.GetCached(second.Url);
        int firstOrder=first.Order,secondOrder=second.Order;
        var queue=ui.Control<DataGrid>("QueueGrid");ResetUi.Select(queue,first,second,hidden);
        ui.Control<TextBox>("QueueSearchBox").Text="reset-scope-";
        ResetUi.Until(()=>queue.Items.Count==2 && queue.SelectedItems.Count==2,"Only the still-visible selected rows may be reset.");

        int forbiddenDialogs=0;var guardReplies=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        uint thread=GetCurrentThreadId();guardReplies.Tick+=(_,_)=>{if(DismissDialog(thread,ResetUi.ResetTitle,7))forbiddenDialogs++;};
        guardReplies.Start();
        try
        {
            foreach(string name in new[]{"_checkpointReadOnly","_restoringSession","_profileBackupBusy","_profileBackupRecoveryRequired",
                "_profileBackupReplaying","_appUpdateHandoff","_legacyEngineBlocked","_dependencyOperationRunning","_startingSession","_closing","_disposed","_queueItemActionBusy"})
            {
                var field=typeof(NativeWindow).GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)??throw new MissingFieldException(name);
                object? previousTask=ResetUi.Field(ui.Subject,"_queueItemActionTask");field.SetValue(ui.Subject,true);
                try
                {
                    ui.Click("ResetQueueItemsButton");
                    ResetUi.Require(forbiddenDialogs==0 && ReferenceEquals(previousTask,ResetUi.Field(ui.Subject,"_queueItemActionTask")) &&
                        first.Status=="Completed" && second.Status=="Completed" && File.Exists(firstFile) && File.Exists(secondFile) &&
                        ui.Subject.SessionState.Coordinator is null,"A directly raised Reset event bypassed "+name+".");
                }
                finally{field.SetValue(ui.Subject,false);ui.Subject.UpdateState();}
            }
        }
        finally{guardReplies.Stop();}

        var resetMenu=ui.Menu(queue);
        ResetUi.Require(resetMenu.IsEnabled,"The queue menu must offer Reset for the visible completed multi-selection.");
        ResetUi.Confirm(7,()=>ui.Click(resetMenu));
        ResetUi.Require(first.Path==firstFile && second.Path==secondFile && File.Exists(firstSidecar) &&
            ReferenceEquals(firstCached,ui.Subject.Preview.GetCached(first.Url)) && ReferenceEquals(secondCached,ui.Subject.Preview.GetCached(second.Url)) &&
            first.TransferredBytes==12345 && second.TransferredBytes==54321 && ui.Subject.SessionState.Coordinator is null &&
            File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),"Declining Reset must leave media, metadata, counters, history and session untouched.");

        Metadata("Fresh metadata",freshImages.Url);
        int duplicateApprovals=0;var duplicates=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        duplicates.Tick+=(_,_)=>
        {
            var dialog=Application.Current.Windows.Cast<Window>().LastOrDefault(candidate=>candidate.Owner==ui.Window &&
                candidate.FindName("YesAllButton") is Button);
            if(dialog is null)return;
            duplicateApprovals++;((Button)dialog.FindName("YesAllButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        };
        duplicates.Start();
        try
        {
            ResetUi.Confirm(6,()=>ui.Click("ResetQueueItemsButton"));
            ResetUi.Until(()=>first.Status=="Completed" && second.Status=="Completed" && first.Path!=firstFile && second.Path!=secondFile &&
                ui.Subject.SessionState.Coordinator is null && ui.Subject.SessionState.RunningTask?.IsCompleted==true && !ui.Busy,
                "A confirmed group Reset must finish two genuinely new native attempts.");
            ResetUi.Until(()=>ui.Subject.Preview.GetCached(first.Url)?.Title=="Fresh metadata" &&
                ui.Subject.Preview.GetCached(second.Url)?.Title=="Fresh metadata" &&
                ui.Subject.Preview.GetCached(first.Url)?.ThumbnailBase64==freshImages.Encoded &&
                ui.Subject.Preview.GetCached(second.Url)?.ThumbnailBase64==freshImages.Encoded,
                "Reset must bypass warm TTL and read changed metadata/HTTP images through fresh native metadata extraction.");
        }
        finally{duplicates.Stop();}
        ResetUi.Require(duplicateApprovals>0 && !File.Exists(firstFile) && !File.Exists(firstSidecar) && !File.Exists(secondFile) &&
            File.ReadAllText(foreign)=="unowned media sentinel" && first.Order==firstOrder && second.Order==secondOrder &&
            first.SubscriptionId=="reset-subscription" && first.SubscriptionName=="Сохраняемая папка" && ReferenceEquals(first.Selection,selection) &&
            first.TransferredBytes==0 && second.TransferredBytes==0 && first.Downloaded.Length==0 && second.Downloaded.Length==0 &&
            first.RawError.Length==0 && second.RawError.Length==0 && File.Exists(first.Path) && File.Exists(second.Path),
            "Reset must delete every old owned output, preserve unowned files/order/source/selection, clear counters/errors and publish only fresh ownership.");
        ResetUi.Require(hidden.Status=="Queued" && ui.Subject.Preview.GetCached(hidden.Url)?.Title=="Old metadata" &&
            File.ReadAllBytes(ui.Cache(hidden.Url)).SequenceEqual(hiddenCache) && oldImages.Requests==3 && freshImages.Requests==2 &&
            File.ReadAllBytes(ui.Data.HistoryPath).Take(history.Length).SequenceEqual(history) &&
            ui.Subject.Preview.GetCached(first.Url)!.SavedAtUtc>firstCached!.SavedAtUtc &&
            ui.Subject.Preview.GetCached(second.Url)!.SavedAtUtc>secondCached!.SavedAtUtc,
            "Hidden work and cache bytes must remain intact, fresh selected images must be fetched once each, and prior success records must remain append-only.");
    }

    private static void ResetQueuedMetadataCallback(string root,string fixture)
    {
        Directory.CreateDirectory(root);string control=Path.Combine(root,"metadata.json");
        void Metadata(string title)=>File.WriteAllText(control,JsonSerializer.Serialize(new{title,duration=10,thumbnail=""}),Encoding.UTF8);
        Metadata("Stale queued title");
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"output"),Cookies=control});
        var row=new DownloadItem{Url="https://lifecycle-regression.invalid/reset-queued-callback",Title="Known row title"};
        ui.Subject.SessionState.TryAdd(row);ui.Subject.Reindex();
        var service=(PreviewService)ResetUi.Field(ui.Subject.Preview,"_service")!;
        using var published=new ManualResetEventSlim();long queuedGeneration=-1;PreviewData? queued=null;
        void Published(PreviewData data,bool final,long generation)
        {
            if(data.Url!=row.Url || !final)return;
            queued=data;queuedGeneration=generation;published.Set();
        }
        service.ResultAvailable+=Published;
        try
        {
            // Deliberately hold only this isolated test Dispatcher while the
            // real background child queues its production notification. No
            // ReceiveReady spoof or fabricated generation enters the product.
            ui.Control<DataGrid>("QueueGrid").SelectedItem=row;
            ResetUi.Require(published.Wait(TimeSpan.FromSeconds(8)) && queued?.Title=="Stale queued title",
                "A genuine native metadata result must be queued before the reset race is tested.");
            Task? reset=null;
            var begin=ui.Window.Dispatcher.BeginInvoke(DispatcherPriority.Send,
                new Action(()=>{reset=ui.Subject.Preview.ResetAsync([row]);}));
            ResetUi.Until(()=>begin.Task.IsCompleted,"The reset must enter the real Dispatcher before pending normal-priority results.");
            begin.Task.GetAwaiter().GetResult();
            ResetUi.Require(reset is not null && service.Generation(row.Url)>queuedGeneration,
                "Reset must invalidate the queued result before its old Dispatcher notification can be admitted.");
            ResetUi.Until(()=>reset!.IsCompleted,"The real preview reset must finish its joined invalidation.");reset!.GetAwaiter().GetResult();
            ResetUi.Require(row.Title=="Known row title" && ui.Subject.Preview.GetCached(row.Url) is null && !File.Exists(ui.Cache(row.Url)) &&
                ui.Control<TextBlock>("PreviewTitle").Text.Contains("сброшены",StringComparison.Ordinal),
                "A previously queued service callback must not resurrect old title, panel or cache after reset.");
            Metadata("Fresh after queued reset");ui.Subject.Preview.RequestRow(row,true);
            ResetUi.Until(()=>row.Title=="Fresh after queued reset" && ui.Subject.Preview.GetCached(row.Url)?.Title=="Fresh after queued reset",
                "A fresh explicit request must read changed child metadata after stale Dispatcher callbacks were rejected.");
        }
        finally{service.ResultAvailable-=Published;}
    }

    private static void ResetActiveAttempt(string root,string fixture)
    {
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"output"),Threads="2",Container="MP4"});
        string firstGate=Path.Combine(root,"first-gate"),keeperGate=Path.Combine(root,"keeper-gate"),restartGate=Path.Combine(root,"restart-gate");
        var row=new DownloadItem{Url="https://startup-download.invalid/reset-first-timer-slow",SubscriptionId="active-source",SubscriptionName="Папка источника"};
        var selection=new DownloadSelection(row.Url,new DownloadSection(1,2),DurationSeconds:10);row.Selection=selection;
        ui.Subject.SessionState.TryAdd(row);ui.Subject.Reindex();Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",firstGate);
        ui.Click("StartButton");
        ResetUi.Until(()=>row.Status=="Running" && ResetUi.Pid(firstGate)>0,"The first native attempt must hold its actual owned child.");
        int oldPid=ResetUi.Pid(firstGate);var engine=ui.Subject.SessionState.Coordinator;string oldStage=row.StageDir,oldToken=row.ResumeState!.Token;
        Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",keeperGate);
        string keeperUrl="https://startup-download.invalid/reset-keeper-timer-slow";ui.Subject.ReceiveArguments([keeperUrl]);
        ResetUi.Until(()=>ui.Subject.SessionState.Queue.Any(item=>item.Url==keeperUrl && item.Status=="Running") && ResetUi.Pid(keeperGate)>0,
            "An unrelated live URL must occupy the other native worker before Reset.");
        var keeper=ui.Subject.SessionState.Queue.Single(item=>item.Url==keeperUrl);int keeperPid=ResetUi.Pid(keeperGate);
        ResetUi.Select(ui.Control<DataGrid>("QueueGrid"),row);
        row.Progress=42;row.TransferredBytes=987654;row.Downloaded="19 MiB";row.RawError="old attempt diagnostic";
        ResetUi.Confirm(7,()=>ui.Click("ResetQueueItemsButton"));
        ResetUi.Require(ResetUi.Alive(oldPid) && ResetUi.Alive(keeperPid) && row.Status=="Running" && row.Progress==42 &&
            row.TransferredBytes==987654 && row.StageDir==oldStage && row.ResumeState?.Token==oldToken,
            "Declining active Reset must not stop either worker or clear retained bytes and counters.");
        Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",restartGate);
        ResetUi.Select(ui.Control<DataGrid>("QueueGrid"),keeper);
        var downloads=ui.Control<DataGrid>("DownloadGrid");
        ResetUi.Until(()=>downloads.Items.Contains(row),"Downloads must expose the actual active attempt to reset.");
        ResetUi.Select(downloads,row);var resetMenu=ui.Menu(downloads);
        ResetUi.Require(resetMenu.IsEnabled,"Downloads must offer Reset for its independently selected active row.");
        ResetUi.Require(ui.Subject.Preview.SelectedUrl==row.Url &&
            ReferenceEquals(ui.Control<DataGrid>("QueueGrid").SelectedItem,keeper) && ReferenceEquals(downloads.SelectedItem,row),
            "Selecting the Downloads reset target must show its preview independently of the queue's keeper selection.");
        ResetUi.Confirm(6,()=>ui.Click(resetMenu));
        ResetUi.Until(()=>row.Status=="Running" && ResetUi.Pid(restartGate)>0 && row.StageDir.Length>0 && row.StageDir!=oldStage && !ui.Busy,
            "Confirmed Reset must create a genuinely fresh stage and native child in the same active coordinator.");
        int restartedPid=ResetUi.Pid(restartGate);
        ResetUi.Require(!ResetUi.Alive(oldPid) && restartedPid!=oldPid && !Directory.Exists(oldStage) &&
            ReferenceEquals(engine,ui.Subject.SessionState.Coordinator) && keeper.Status=="Running" && ResetUi.Alive(keeperPid) &&
            ui.Subject.Preview.SelectedUrl==row.Url && ReferenceEquals(ui.Control<DataGrid>("QueueGrid").SelectedItem,keeper) &&
            row.ResumeState?.Token!=oldToken && row.Progress==0 && row.TransferredBytes==0 && row.Downloaded.Length==0 && row.RawError.Length==0 &&
            ReferenceEquals(row.Selection,selection) && row.SubscriptionId=="active-source" && row.SubscriptionName=="Папка источника" &&
            !ui.Data.ReadHistory().ContainsKey(row.Url),
            "Reset must join only its old child, erase its old stage/counters, retain identity/selection/source and leave the unrelated worker alive.");
        File.WriteAllText(restartGate,"release");File.WriteAllText(keeperGate,"release");
        ResetUi.Until(()=>row.Status=="Completed" && keeper.Status=="Completed" && ui.Subject.SessionState.Coordinator is null &&
            ui.Subject.SessionState.RunningTask?.IsCompleted==true,"Both fresh and unrelated attempts must finish through ordinary publication.");
        ResetUi.Require(File.Exists(row.Path) && File.Exists(keeper.Path) && row.CompletedSelection==selection &&
            !ui.Data.ReadHistory().ContainsKey(row.Url) && ui.Data.ReadHistory().ContainsKey(keeper.Url),
            "Fresh partial success must retain the selected fragment without recording the cancelled attempt or a false full-URL success.");
    }

    private static void ResetHonorsStopDuringMetadataJoin(string root,string fixture)
    {
        using var metadataHeld=new ManualResetEventSlim();using var releaseMetadata=new ManualResetEventSlim();
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"output"),Threads="2",Container="MP4"});
        string firstGate=Path.Combine(root,"first-gate"),keeperGate=Path.Combine(root,"keeper-gate"),restartGate=Path.Combine(root,"unexpected-restart");
        var row=new DownloadItem{Url="https://startup-download.invalid/reset-stop-first-timer-slow"};
        ui.Subject.SessionState.TryAdd(row);ui.Subject.Reindex();Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",firstGate);
        ui.Click("StartButton");
        ResetUi.Until(()=>row.Status=="Running" && ResetUi.Pid(firstGate)>0,"The late-Stop scenario must own its first native child.");
        var original=ui.Subject.SessionState.Coordinator;var originalRun=ui.Subject.SessionState.RunningTask;
        var originalStarted=ui.Subject.SessionState.StartedAt;int oldPid=ResetUi.Pid(firstGate);string oldStage=row.StageDir;
        Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",keeperGate);
        string keeperUrl="https://startup-download.invalid/reset-stop-keeper-timer-slow";ui.Subject.ReceiveArguments([keeperUrl]);
        ResetUi.Until(()=>ui.Subject.SessionState.Queue.Any(item=>item.Url==keeperUrl && item.Status=="Running") && ResetUi.Pid(keeperGate)>0,
            "A second real worker must keep the old session alive during Reset.");
        var keeper=ui.Subject.SessionState.Queue.Single(item=>item.Url==keeperUrl);int keeperPid=ResetUi.Pid(keeperGate);
        var service=(PreviewService)ResetUi.Field(ui.Subject.Preview,"_service")!;
        long generation=service.Generation(row.Url);int firstNotification=0;
        var callbackFinished=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void HoldMetadata(PreviewData data,bool final,long _)
        {
            if(data.Url!=row.Url || !final || Interlocked.Exchange(ref firstNotification,1)!=0)return;
            metadataHeld.Set();
            try{releaseMetadata.Wait();}finally{callbackFinished.TrySetResult();}
        }
        service.ResultAvailable+=HoldMetadata;
        try
        {
            ui.Subject.Preview.RequestRow(row,true);
            ResetUi.Until(()=>metadataHeld.IsSet,"A genuine selected metadata result must hold the sole worker before Reset.");
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",restartGate);
            Task? fullReset=null;
            var resetMethod=typeof(NativeWindow).GetMethod("ResetRowsAsync",BindingFlags.NonPublic|BindingFlags.Instance)
                ??throw new MissingMethodException(typeof(NativeWindow).FullName,"ResetRowsAsync");
            // Capture the full owner task, including its post-cleanup admission.
            // Waiting only for _queueItemActionTask would miss a new session
            // incorrectly started after an explicit Stop during the join.
            ResetUi.Confirm(6,()=>
            {
                var begin=ui.Window.Dispatcher.BeginInvoke(new Action(()=>
                    fullReset=(Task)resetMethod.Invoke(ui.Subject,new object[]{new[]{row}})!));
                ResetUi.Until(()=>begin.Task.IsCompleted,"The full Reset operation must enter its real Dispatcher.");begin.Task.GetAwaiter().GetResult();
            });
            ResetUi.Until(()=>ui.Busy && !ResetUi.Alive(oldPid) && service.Generation(row.Url)>generation,
                "Reset must join its own native attempt and then wait for the held metadata worker.");
            ResetUi.Require(fullReset is not null && !fullReset.IsCompleted && !callbackFinished.Task.IsCompleted &&
                ReferenceEquals(original,ui.Subject.SessionState.Coordinator) && ResetUi.Alive(keeperPid) &&
                ui.Control<Button>("StopButton").IsEnabled,"The controlled join must leave the real Stop action available in the original session.");
            long beforeStop=ui.Subject.SessionState.StopVersion;ui.Click("StopButton");
            ResetUi.Require(ui.Subject.SessionState.StopVersion>beforeStop,"The actual Stop event must revoke the pending restart intent.");
            releaseMetadata.Set();
            ResetUi.Until(()=>fullReset!.IsCompleted,"The complete Reset task must return after Stop without awaiting a newly started download.");
            fullReset!.GetAwaiter().GetResult();
            ResetUi.Until(()=>ui.Subject.SessionState.Coordinator is null && originalRun?.IsCompleted==true && !ui.Busy,
                "Stop must join both original workers while Reset finishes without replacement.");
            ResetUi.Require(row.Status=="Queued" && row.DeferredToNextStart && row.AttemptFinished && row.StageDir.Length==0 &&
                row.RunDir.Length==0 && row.ResumeState is null && row.Path.Length==0 && row.OwnedPaths.Count==0 && row.CleanupPaths.Count==0 &&
                !Directory.Exists(oldStage) && !File.Exists(restartGate+".started") && !ResetUi.Alive(oldPid) && !ResetUi.Alive(keeperPid) &&
                keeper.Status=="Interrupted" && ui.Subject.SessionState.Stopping && ui.Subject.SessionState.StartedAt==originalStarted &&
                ReferenceEquals(originalRun,ui.Subject.SessionState.RunningTask) && !ui.Data.ReadHistory().ContainsKey(row.Url) &&
                !ui.Data.ReadHistory().ContainsKey(keeper.Url),
                "A late Stop must leave reset work queued for explicit Start, preserve the old session identity and prevent any new native attempt or false success.");
        }
        finally
        {
            releaseMetadata.Set();service.ResultAvailable-=HoldMetadata;
            if(metadataHeld.IsSet)ResetUi.Until(()=>callbackFinished.Task.IsCompleted,"The test metadata gate must always release its background callback.");
        }
    }

    private static void ResetRejectsIncompatibleActivePlan(string root,string fixture)
    {
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",Container="WebM"});
        string original=Path.Combine(root,"previous-output");Directory.CreateDirectory(original);
        string compatibleFile=Path.Combine(original,"compatible.webm"),manualFile=Path.Combine(original,"manual.mp4"),
            foreign=Path.Combine(original,"unowned.mp4");
        File.WriteAllText(compatibleFile,"compatible completed sentinel");File.WriteAllText(manualFile,"manual completed sentinel");
        File.WriteAllText(foreign,"foreign completed sentinel");
        var keeper=new DownloadItem{Url="https://startup-download.invalid/reset-plan-keeper-timer-slow"};
        // The first selected row is compatible with both settings snapshots.
        // Validating and deleting per row would already destroy it before the
        // second selected row reveals the active WebM incompatibility.
        var compatible=new DownloadItem{Url="https://startup-download.invalid/reset-compatible-completed",Status="Completed",State="Done",
            Progress=100,AttemptFinished=true,Path=compatibleFile,OwnedPaths=[compatibleFile],OutputRoot=original};
        var manual=new DownloadItem{Url="https://startup-download.invalid/reset-incompatible-completed",Status="Completed",State="Done",
            Progress=100,AttemptFinished=true,Path=manualFile,OwnedPaths=[manualFile],OutputRoot=original};
        var selection=new DownloadSelection(manual.Url,Format:new MediaFormat("18","mp4",640,360,30,
            "avc1.640028","mp4a.40.2","en",1024,false));
        manual.Selection=selection;manual.CompletedSelection=selection;
        ui.Subject.SessionState.AddBatch([keeper,compatible,manual],ui.Subject.ReadOptions());ui.Subject.Reindex();
        var metadata=new HashSet<string>(StringComparer.Ordinal);
        ui.Subject.Preview.Changed+=(data,final)=>{if(final)metadata.Add(data.Url);};
        ui.Subject.Preview.RequestRow(compatible);ui.Subject.Preview.RequestRow(manual);
        ResetUi.Until(()=>metadata.Contains(compatible.Url) && metadata.Contains(manual.Url) &&
            File.Exists(ui.Cache(compatible.Url)) && File.Exists(ui.Cache(manual.Url)),
            "Both completed results must have durable metadata before active-plan refusal is tested.");
        ui.Data.AppendSuccess("https://startup-download.invalid/reset-plan-history-sentinel");
        byte[] history=File.ReadAllBytes(ui.Data.HistoryPath),compatibleCache=File.ReadAllBytes(ui.Cache(compatible.Url)),
            manualCache=File.ReadAllBytes(ui.Cache(manual.Url));
        var compatibleMetadata=ui.Subject.Preview.GetCached(compatible.Url);var manualMetadata=ui.Subject.Preview.GetCached(manual.Url);
        string gate=Path.Combine(root,"keeper-gate");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
        ui.Click("StartButton");
        ResetUi.Until(()=>keeper.Status=="Running" && ResetUi.Pid(gate)>0,"The WebM session must own its real keeper child before Reset.");
        var engine=ui.Subject.SessionState.Coordinator;int keeperPid=ResetUi.Pid(gate);string keeperStage=keeper.StageDir;
        // UI changes must not replace the already pinned active-session plan.
        // A preflight against these newer MP4 controls would miss the defect.
        ui.Subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",Container="MP4"});
        ResetUi.Require(ui.Subject.SessionState.Options.Container=="WebM" && ui.Subject.ReadOptions().Container=="MP4" &&
            selection.TryValidate(manual.Url,ui.Subject.ReadOptions(),out _) &&
            !selection.TryValidate(manual.Url,ui.Subject.SessionState.Options,out _),
            "The fixture must be compatible with current controls and incompatible only with the actual active plan.");
        ResetUi.Select(ui.Control<DataGrid>("QueueGrid"),compatible,manual);
        int warnings=0;uint thread=GetCurrentThreadId();
        var responder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        responder.Tick+=(_,_)=>{if(DismissDialog(thread,ErrorMessages.Text("Операция с очередью не завершена","Queue operation did not finish"),1))warnings++;};
        responder.Start();
        try
        {
            ResetUi.Confirm(6,()=>ui.Click("ResetQueueItemsButton"));
            ResetUi.Until(()=>warnings>0 && !ui.Busy,"Incompatible active settings must produce an explicit preflight refusal.");
            ResetUi.Require(ReferenceEquals(engine,ui.Subject.SessionState.Coordinator) && engine!.Outstanding==1 && engine.IsPending(keeper) &&
                keeper.Status=="Running" && keeper.StageDir==keeperStage && ResetUi.Alive(keeperPid) &&
                ui.Subject.SessionState.Queue.Contains(compatible) && ui.Subject.SessionState.Queue.Contains(manual) &&
                compatible.Status=="Completed" && manual.Status=="Completed" && compatible.Path==compatibleFile && manual.Path==manualFile &&
                compatible.OwnedPaths.SequenceEqual(new[]{compatibleFile}) && manual.OwnedPaths.SequenceEqual(new[]{manualFile}) &&
                compatible.CleanupPaths.Count==0 && manual.CleanupPaths.Count==0 && !compatible.PendingRemoval && !manual.PendingRemoval &&
                compatible.ResumeState is null && manual.ResumeState is null && compatible.StageDir.Length==0 && manual.StageDir.Length==0 &&
                ReferenceEquals(manual.Selection,selection) && ReferenceEquals(manual.CompletedSelection,selection),
                "Batch preflight must refuse before cancelling any worker, resetting any compatible earlier row or creating another native attempt.");
            ResetUi.Require(File.ReadAllText(compatibleFile)=="compatible completed sentinel" && File.ReadAllText(manualFile)=="manual completed sentinel" &&
                File.ReadAllText(foreign)=="foreign completed sentinel" && ReferenceEquals(compatibleMetadata,ui.Subject.Preview.GetCached(compatible.Url)) &&
                ReferenceEquals(manualMetadata,ui.Subject.Preview.GetCached(manual.Url)) &&
                File.ReadAllBytes(ui.Cache(compatible.Url)).SequenceEqual(compatibleCache) && File.ReadAllBytes(ui.Cache(manual.Url)).SequenceEqual(manualCache) &&
                File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
                "The refused reset must preserve all old owned/unowned bytes, exact metadata caches and successful history.");
        }
        finally{responder.Stop();}
        // Dispose cancels the keeper; no release is needed to prove admission
        // refusal, and no fake container output is published by this scenario.
    }

    private static void ResetProtectedOwnedFile(string root,string fixture)
    {
        using var ui=new ResetUi(root,fixture,new DownloadOptions{Output=Path.Combine(root,"new-output")});
        string owned=Path.Combine(root,"protected.mp4"),foreign=Path.Combine(root,"foreign.mp4");
        File.WriteAllText(owned,"protected owned sentinel");File.WriteAllText(foreign,"foreign sentinel");
        var row=new DownloadItem{Url="https://startup-download.invalid/reset-protected",Status="Completed",State="Done",Progress=100,AttemptFinished=true,
            Path=owned,OwnedPaths=[owned],OutputRoot=root,Title="Known protected result"};
        ui.Subject.SessionState.TryAdd(row);ui.Subject.Reindex();ui.Data.AppendSuccess("https://startup-download.invalid/protected-history-sentinel");
        byte[] history=File.ReadAllBytes(ui.Data.HistoryPath);ResetUi.Select(ui.Control<DataGrid>("QueueGrid"),row);
        File.SetAttributes(owned,FileAttributes.ReadOnly);int warnings=0;uint thread=GetCurrentThreadId();
        var responder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        responder.Tick+=(_,_)=>{if(DismissDialog(thread,ErrorMessages.Text("Операция с очередью не завершена","Queue operation did not finish"),1) ||
            DismissDialog(thread,ErrorMessages.Text("Удаление заблокировано","Removal blocked"),1) ||
            DismissDialog(thread,ErrorMessages.Text("Сброс выполнен частично","Reset partially completed"),1))warnings++;};
        responder.Start();
        try
        {
            ResetUi.Confirm(6,()=>ui.Click("ResetQueueItemsButton"));
            ResetUi.Until(()=>row.CleanupError.Length>0 && !ui.Busy && warnings>0,"A real read-only deletion refusal must finish with an explicit recoverable warning.");
            ResetUi.Require(ui.Subject.SessionState.Queue.Contains(row) && row.PendingRemoval && row.OwnedPaths.Contains(owned) &&
                row.CleanupPaths.Contains(owned) && row.Path==owned && File.ReadAllText(owned)=="protected owned sentinel" &&
                File.ReadAllText(foreign)=="foreign sentinel" && ui.Subject.SessionState.Coordinator is null && ui.Subject.SessionState.RunningTask is null &&
                File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
                "Failed cleanup must preserve its row and exact ownership proof, never restart, and leave foreign files and history untouched.");
        }
        finally{responder.Stop();if(File.Exists(owned))File.SetAttributes(owned,FileAttributes.Normal);}
    }

    private sealed class ResetUi:IDisposable
    {
        internal static string ResetTitle=>ErrorMessages.Text("Сбросить загрузки","Reset downloads");
        internal readonly Window Window;
        internal readonly NativeWindow Subject;
        internal readonly AppPersistence Data;
        internal bool Busy=>Field(Subject,"_queueItemActionBusy") is true;
        internal ResetUi(string root,string fixture,DownloadOptions options)
        {
            Directory.CreateDirectory(root);Data=new AppPersistence(Path.Combine(root,"profile"));
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(input);
            Window=(Window)XamlReader.Load(reader);
            Subject=new NativeWindow(Window,Data,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
                localPosterFfmpegPath:Path.Combine(root,"missing-local-poster-tool.exe"));
            Subject.ApplyOptions(options);Window.Show();
        }
        internal T Control<T>(string name) where T:class=>(T)(Window.FindName(name)??throw new InvalidOperationException("Missing actual reset control: "+name));
        internal void Click(string name)
        {
            var operation=Window.Dispatcher.BeginInvoke(()=>Control<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Until(()=>operation.Task.IsCompleted,"The actual Dispatcher must deliver its Reset action.");operation.Task.GetAwaiter().GetResult();
        }
        internal void Click(MenuItem item)
        {
            var operation=Window.Dispatcher.BeginInvoke(()=>item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent)));
            Until(()=>operation.Task.IsCompleted,"The actual Dispatcher must deliver its reset menu action.");operation.Task.GetAwaiter().GetResult();
        }
        internal MenuItem Menu(DataGrid grid)
        {
            var menu=grid.ContextMenu??throw new InvalidOperationException("The actual reset context menu is missing.");
            menu.RaiseEvent(new RoutedEventArgs(ContextMenu.OpenedEvent));
            return menu.Items.OfType<MenuItem>().Single(item=>item.Header?.ToString()?.StartsWith("Сбросить",StringComparison.Ordinal)==true);
        }
        internal string Cache(string url)=>Path.Combine(Data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        internal static object? Field(object owner,string name)=>(owner.GetType().GetField(name,BindingFlags.NonPublic|BindingFlags.Instance)
            ??throw new MissingFieldException(owner.GetType().FullName,name)).GetValue(owner);
        internal static void Select(DataGrid grid,params DownloadItem[] rows)
        {grid.SelectedItems.Clear();foreach(var row in rows)grid.SelectedItems.Add(row);}
        internal static int Pid(string gate)
        {try{return int.TryParse(File.ReadAllText(gate+".started"),out int pid)?pid:0;}catch(IOException){return 0;}}
        internal static bool Alive(int pid)
        {try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
        internal static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Queue reset WPF test: "+message);}
        internal static void Until(Func<bool> ready,string message)
        {
            if(ready())return;var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(ready() || watch.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}Require(ready(),message);
        }
        internal static void Confirm(int answer,Action action)
        {
            uint thread=GetCurrentThreadId();bool observed=false;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(DismissDialog(thread,ResetTitle,answer)){observed=true;timer.Stop();}};
            using var watchdog=new CancellationTokenSource();
            _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
            {if(!task.IsCanceled)DismissDialog(thread,ResetTitle,7);},TaskScheduler.Default);
            timer.Start();try{action();}finally{watchdog.Cancel();timer.Stop();}Require(observed,"The real Reset confirmation must be displayed.");
        }
        public void Dispose()
        {
            if(Subject.SessionState.Coordinator is {} active)
            {active.Stop();Until(()=>Subject.SessionState.Coordinator is null,"The reset fixture must join remaining native workers.");}
            Subject.Dispose();Window.Close();
        }
    }
}
