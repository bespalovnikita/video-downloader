using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static readonly object RequeueStageGate=new();
    private static void RequeueStage(string detail)
    {
        string message="subscription requeue: "+detail;
        lock(RequeueStageGate)
        {
            Console.WriteLine(message);
            string? result=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(result))
            {
                try{File.WriteAllText(result,message);}
                catch(IOException error){Console.WriteLine("Requeue stage file diagnostic: "+error.HResult);}
            }
        }
    }

    private static void TestSubscriptionRequeue(string testRoot)
    {
        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        RequeueUi.Require(File.Exists(fixture),"The actual native fixture must be available.");
        var culture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        try
        {
            RequeueStage("choices scene");
            SubscriptionRequeueChoices(Path.Combine(testRoot,"subscription-requeue-choices"),fixture);
            RequeueStage("modal changes scene");
            SubscriptionRequeueModalChanges(Path.Combine(testRoot,"subscription-requeue-modal"),fixture);
            RequeueStage("existing results scene");
            SubscriptionRequeueExistingResults(Path.Combine(testRoot,"subscription-requeue-existing"),fixture);
            Console.WriteLine("PASS: actual subscription requeue choices for every state, cancellation/history consent, ordinal keys, captured modal selection, retained ownership and explicit repeat/resume through ordinary Start");
        }
        finally {CultureInfo.CurrentUICulture=culture;}
    }

    private static void SubscriptionRequeueChoices(string root,string fixture)
    {
        using var ui=new RequeueUi(root,fixture);
        byte[] history=File.ReadAllBytes(ui.Data.HistoryPath);
        string processed=JsonSerializer.Serialize(ui.Item(ui.Processed),AppPersistence.Json);
        string duplicate=JsonSerializer.Serialize(ui.Item(ui.Duplicate),AppPersistence.Json);
        int prompts=ui.ChooserCount;
        foreach(string decision in new[]{"Cancel","Close"})
        {
            ui.Select(ui.NewUpper,ui.NewLower,ui.Baseline,ui.Skipped,ui.Processed,ui.Duplicate);
            byte[] checkpoint=File.ReadAllBytes(ui.Data.SessionPath);
            ui.Queue(decision);
            RequeueUi.Require(ui.Subject.SessionState.Queue.Count==0 && File.ReadAllBytes(ui.Data.SessionPath).SequenceEqual(checkpoint) &&
                File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
                "Cancel/X of the mixed choice must preserve queue, checkpoint and successful history.");
        }
        RequeueUi.Require(ui.ChooserCount==prompts+2,"Both actual mixed-selection cancellation paths must be exercised.");

        ui.Select(ui.NewUpper,ui.Processed);
        byte[] beforeHistoryCancel=File.ReadAllBytes(ui.Data.SessionPath);
        int beforeDuplicates=ui.DuplicateCount;ui.HistoryAnswer="CancelButton";
        ui.Queue("All");
        RequeueUi.Require(ui.DuplicateCount==beforeDuplicates+1 && ui.Subject.SessionState.Queue.Count==0 &&
            File.ReadAllBytes(ui.Data.SessionPath).SequenceEqual(beforeHistoryCancel),
            "Cancelling the real repeat-history dialog must cancel the entire captured batch without admitting its fresh URL.");
        ui.HistoryAnswer="YesAllButton";

        ui.Select(ui.NewUpper,ui.NewLower,ui.Baseline,ui.Processed,ui.Skipped,ui.Duplicate);
        beforeDuplicates=ui.DuplicateCount;ui.Queue("New");
        RequeueUi.Require(ui.Subject.SessionState.Queue.Select(row=>row.Url).SequenceEqual(new[]{ui.NewUpper,ui.NewLower}) &&
            ui.DuplicateCount==beforeDuplicates && ui.Item(ui.NewUpper).State=="Queued" && ui.Item(ui.NewLower).State=="Queued" &&
            ui.Item(ui.Baseline).State=="Baseline" && ui.Item(ui.Skipped).State=="Skipped",
            "Only-new admission must preserve both case-distinct exact URLs and exclude every captured non-New row.");
        RequeueUi.Require(ui.Subject.SessionState.Queue.All(row=>row.SubscriptionId==ui.Source.Id && row.SubscriptionName=="Повторы" &&
            row.Title==ui.Title(row.Url)),"Accepted discovery titles and source routing must already be present on queued rows.");

        ui.Select(ui.Baseline,ui.Skipped,ui.Processed,ui.Duplicate);beforeDuplicates=ui.DuplicateCount;
        ui.Queue("All",dialog=>
        {
            RequeueUi.Require(!ui.DialogButton(dialog,"New").IsEnabled && ui.DialogButton(dialog,"Cancel").IsDefault,
                "A mixed selection without New must disable Only new and make Cancel the safe default.");
        });
        RequeueUi.Require(ui.DuplicateCount==beforeDuplicates+1 &&
            ui.Subject.SessionState.Queue.Select(row=>row.Url).SequenceEqual(new[]{ui.NewUpper,ui.NewLower,ui.Baseline,ui.Skipped,ui.Processed,ui.Duplicate}) &&
            ui.Item(ui.Baseline).State=="Queued" && ui.Item(ui.Skipped).State=="Queued",
            "All-selected must admit baseline, skipped and both historical states in source order with normal history confirmation.");
        RequeueUi.Require(JsonSerializer.Serialize(ui.Item(ui.Processed),AppPersistence.Json)==processed &&
            JsonSerializer.Serialize(ui.Item(ui.Duplicate),AppPersistence.Json)==duplicate &&
            File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
            "Explicit historical admission must preserve Processed/AlreadyProcessed, attribution, duplicate provenance and append-only history.");

        prompts=ui.ChooserCount;
        foreach(string url in new[]{ui.Processed,ui.Duplicate,ui.Baseline,ui.Skipped,ui.Queued})
        {
            ui.Select(url);ui.Queue();
            RequeueUi.Require(ui.ChooserCount==prompts,"A uniform single-state selection must not ask the mixed-state question.");
        }
        var checkpointRows=ui.Data.Restore()!.Queue;
        RequeueUi.Require(checkpointRows.Count==7 && checkpointRows.Select(row=>row.Url).Distinct(StringComparer.Ordinal).Count()==7 &&
            ui.Subject.SessionState.Queue.Count==7 && checkpointRows.All(row=>row.SubscriptionId==ui.Source.Id) &&
            ui.Store.Read().Items.Where(item=>item.SubscriptionId==ui.Source.Id).Select(item=>item.Url).SequenceEqual(ui.SourceOrder),
            "Uniform historical/manual requeue must be idempotent, durably add the absent Queued item and preserve subscription ordering.");
        ui.RequireIdle(history);
    }

    private static void SubscriptionRequeueModalChanges(string root,string fixture)
    {
        using var ui=new RequeueUi(root,fixture);
        byte[] history=File.ReadAllBytes(ui.Data.HistoryPath);
        ui.Select(ui.NewUpper,ui.Skipped);
        ui.Queue("New",_=>{ui.Store.Skip(ui.Source.Id,[ui.NewUpper]);ui.Select(ui.Baseline);});
        RequeueUi.Require(ui.Subject.SessionState.Queue.Count==0 && ui.Item(ui.NewUpper).State=="Skipped",
            "Only-new must recheck the captured New state after the modal instead of admitting a newly skipped row or the replacement UI selection.");

        ui.Select(ui.NewLower,ui.Skipped);
        string late=ui.Url("late-not-shown");ui.Cache(late);
        ui.Queue("All",_=>
        {
            ui.Select(ui.Baseline);
            ui.Store.ApplyScan(ui.Source.Id,new("Повторная подписка",
                ui.SourceOrder.Append(late).Select(url=>new DiscoveredMedia(url,ui.Title(url))).ToArray(),"",""),[]);
        });
        RequeueUi.Require(ui.Subject.SessionState.Queue.Select(row=>row.Url).SequenceEqual(new[]{ui.NewLower,ui.Skipped}) &&
            ui.Item(late).State=="New" && ui.Item(ui.Baseline).State=="Baseline",
            "All-selected must use only the captured exact keys, excluding replacement selections and discoveries made while its dialog was open.");

        ui.Refresh();ui.Select(late,ui.Baseline);
        ui.Queue("New",_=>ui.Data.AppendSuccess(late));
        RequeueUi.Require(ui.Subject.SessionState.Queue.Count==2 && ui.Item(late).State=="Processed",
            "A newly successful captured row must leave the Only-new set after history reconciliation.");
        history=File.ReadAllBytes(ui.Data.HistoryPath);

        ui.Refresh();ui.Select(ui.Baseline,ui.Processed);
        ui.Queue("All",_=>ui.Store.Remove(ui.Source.Id));
        RequeueUi.Require(ui.Subject.SessionState.Queue.Count==2 && ui.Store.Read().Subscriptions.All(source=>source.Id!=ui.Source.Id),
            "Deleting a source during the real choice must prevent stale admission without changing existing queued rows.");
        ui.RequireIdle(history);

        using var closing=new RequeueUi(Path.Combine(root,"closing"),fixture);
        byte[] closingHistory=File.ReadAllBytes(closing.Data.HistoryPath);
        closing.Select(closing.NewUpper,closing.Baseline);
        closing.Queue("All",_=>closing.Driver.TestWindow!.Close());
        RequeueUi.Require(closing.Driver.TestWindow is null && closing.Subject.SessionState.Queue.Count==0,
            "Closing the subscription owner while its modal is open must retire the captured acceptance.");
        closing.RequireIdle(closingHistory);
    }

    private static void SubscriptionRequeueExistingResults(string root,string fixture)
    {
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"preflight fixture");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"preflight fixture");
        string? previousPath=Environment.GetEnvironmentVariable("PATH"),previousGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE");
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+previousPath);
        string gate=Path.Combine(root,"native-gate");Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
        try
        {
            using var ui=new RequeueUi(root,fixture);
            string output=Path.Combine(root,"output","Старый каталог");Directory.CreateDirectory(output);
            string oldMedia=Path.Combine(output,"fixture.video.mp4"),oldSidecar=Path.Combine(output,"fixture.video.en.srt");
            File.WriteAllText(oldMedia,"previous owned media");File.WriteAllText(oldSidecar,"previous owned subtitles");
            var selection=new DownloadSelection(ui.Processed,DurationSeconds:10);
            var completed=new DownloadItem{Url=ui.Processed,Title="Existing completed result",Status="Completed",State="Done",AttemptFinished=true,Progress=100,
                Path=oldMedia,OwnedPaths=[oldMedia,oldSidecar],OutputRoot=output,TransferredBytes=1234,Downloaded="old transfer",
                Selection=selection,CompletedSelection=selection,SubscriptionId="earlier-source",SubscriptionName="Старый каталог"};
            string slowUrl=ui.Url("retained-timer-slow");ui.Cache(slowUrl);
            ui.Store.ApplyScan(ui.Source.Id,new("Повторная подписка",
                ui.SourceOrder.Append(slowUrl).Select(url=>new DiscoveredMedia(url,ui.Title(url))).ToArray(),"",""),[]);
            ui.Store.Skip(ui.Source.Id,[slowUrl]);
            var skipped=new DownloadItem{Url=slowUrl,Title="Existing ordinary task"};
            ui.Subject.SessionState.AddBatch([completed,skipped],ui.Subject.ReadOptions());ui.Subject.Reindex();
            ui.ClickMain("StartButton");
            RequeueUi.Until(()=>skipped.Status=="Running" && File.Exists(gate+".started"),
                "A real native worker must own the retained attempt before its Skip.");
            ui.Main<DataGrid>("QueueGrid").SelectedItem=skipped;ui.ClickMain("SkipQueueItemsButton");
            RequeueUi.Until(()=>skipped.Status=="Skipped" && skipped.AttemptFinished && ui.Subject.SessionState.Coordinator is null &&
                ui.Subject.SessionState.RunningTask?.IsCompleted==true,"Skip must join its actual native attempt and finish the old session.");
            string stage=skipped.StageDir;
            string retained=JsonSerializer.Serialize(skipped.ResumeState,AppPersistence.Json);
            double progress=skipped.Progress;long transferred=skipped.TransferredBytes;
            RequeueUi.Require(stage.Length>0 && Directory.Exists(stage) && skipped.ResumeState is not null &&
                !ui.Main<Button>("StartButton").IsEnabled,"Unrequested Completed and Skipped rows must remain ineligible for ordinary Start.");
            byte[] history=File.ReadAllBytes(ui.Data.HistoryPath);
            var oldRun=ui.Subject.SessionState.RunningTask;

            ui.Refresh();ui.Select(ui.Processed);int repeats=ui.DuplicateCount;ui.Queue();
            RequeueUi.Require(ui.DuplicateCount==repeats && completed.RequeueRequested && completed.Status=="Completed" && completed.State=="Done" &&
                completed.Path==oldMedia && completed.OwnedPaths.SequenceEqual(new[]{oldMedia,oldSidecar}) && completed.Progress==100 &&
                completed.TransferredBytes==1234 && ReferenceEquals(completed.Selection,selection) && ReferenceEquals(completed.CompletedSelection,selection) &&
                completed.SubscriptionId=="earlier-source" && completed.SubscriptionName=="Старый каталог" &&
                completed.StateLabel.Contains("повтор",StringComparison.OrdinalIgnoreCase) && ui.Main<Button>("StartButton").IsEnabled,
                "Historical requeue must mark an actionable explicit repeat while preserving Completed, old ownership, selections, counters and routing.");

            ui.Main<DataGrid>("QueueGrid").SelectedItem=completed;ui.RemovalAnswer=7;
            int removals=ui.RemovalCount;ui.ClickMain("RemoveQueueButton");ui.RemovalAnswer=null;
            RequeueUi.Require(ui.RemovalCount==removals+1 && ui.Subject.SessionState.Queue.Contains(completed) && completed.RequeueRequested &&
                File.ReadAllText(oldMedia)=="previous owned media" && File.ReadAllText(oldSidecar)=="previous owned subtitles",
                "Requeue must preserve the real destructive-removal confirmation; declining it keeps the old media and repeat request.");

            ui.Select(slowUrl);ui.Queue();
            RequeueUi.Require(skipped.RequeueRequested && skipped.Status=="Skipped" && skipped.StageDir==stage && Directory.Exists(stage) &&
                JsonSerializer.Serialize(skipped.ResumeState,AppPersistence.Json)==retained && skipped.Progress==progress && skipped.TransferredBytes==transferred &&
                skipped.SubscriptionId.Length==0 && skipped.SubscriptionName.Length==0,
                "Explicit subscription admission of an existing skipped task must preserve its owned retained plan, counters and original ordinary routing.");
            var checkpoint=ui.Data.Restore()!.Queue;
            RequeueUi.Require(checkpoint.Single(row=>row.Url==completed.Url).RequeueRequested &&
                checkpoint.Single(row=>row.Url==completed.Url).Status=="Completed" &&
                checkpoint.Single(row=>row.Url==slowUrl).RequeueRequested && checkpoint.Single(row=>row.Url==slowUrl).Status=="Skipped" &&
                ui.Subject.SessionState.Queue.Count==2 && ui.Subject.SessionState.Coordinator is null &&
                ReferenceEquals(oldRun,ui.Subject.SessionState.RunningTask) && File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
                "Repeat/resume requests must survive the real checkpoint without duplicating rows, starting a session or rewriting history.");

            ui.HistoryAnswer="CancelButton";repeats=ui.DuplicateCount;ui.ClickMain("StartButton");
            RequeueUi.Until(()=>ui.DuplicateCount==repeats+1 && ui.Main<Button>("StartButton").IsEnabled,
                "The real ordinary Start must finish cancelling its repeat-history decision.");
            RequeueUi.Require(completed.RequeueRequested && skipped.RequeueRequested && completed.Status=="Completed" && skipped.Status=="Skipped" &&
                completed.Path==oldMedia && Directory.Exists(stage) && ReferenceEquals(oldRun,ui.Subject.SessionState.RunningTask) &&
                File.ReadAllBytes(ui.Data.HistoryPath).SequenceEqual(history),
                "Cancelling Start must retain both explicit requests and their data for the next user decision.");

            ui.HistoryAnswer="YesAllButton";File.WriteAllText(gate,"release");ui.ClickMain("StartButton");
            RequeueUi.Until(()=>completed.Status=="Completed" && skipped.Status=="Completed" && !completed.RequeueRequested && !skipped.RequeueRequested &&
                ui.Subject.SessionState.Coordinator is null && ui.Subject.SessionState.RunningTask?.IsCompleted==true &&
                !ReferenceEquals(oldRun,ui.Subject.SessionState.RunningTask) && ui.Item(slowUrl).State=="Processed",
                "A single ordinary Start must actually repeat/resume both explicitly requeued rows and deliver its subscription success acknowledgement.");
            RequeueUi.Require(completed.Path!=oldMedia && completed.Path.EndsWith("fixture (2).video.mp4",StringComparison.Ordinal) &&
                File.Exists(completed.Path) && File.Exists(skipped.Path) && !Directory.Exists(stage) &&
                File.ReadAllText(oldMedia)=="previous owned media" && File.ReadAllText(oldSidecar)=="previous owned subtitles" &&
                ui.Data.ReadHistory().ContainsKey(slowUrl) && File.ReadAllBytes(ui.Data.HistoryPath).Take(history.Length).SequenceEqual(history),
                "A real accepted repeat must reserve a new media/sidecar name, preserve the previous publication, consume retained staging and record genuine success.");
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH",previousPath);
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",previousGate);
        }
    }

    private sealed class RequeueUi:IDisposable
    {
        internal readonly Window Window;
        internal readonly NativeWindow Subject;
        internal readonly AppPersistence Data;
        internal readonly SubscriptionScenarioDriver Driver;
        internal SubscriptionStore Store=>Driver.TestStore;
        internal readonly SubscriptionDefinition Source;
        internal readonly string[] SourceOrder;
        internal string Baseline=>SourceOrder[0];
        internal string NewUpper=>SourceOrder[1];
        internal string NewLower=>SourceOrder[2];
        internal string Skipped=>SourceOrder[3];
        internal string Queued=>SourceOrder[4];
        internal string Processed=>SourceOrder[5];
        internal string Duplicate=>SourceOrder[6];
        internal string HistoryAnswer="YesAllButton";
        internal int ChooserCount,DuplicateCount,RemovalCount;
        internal int ExpectedDuplicateNotices,DuplicateNotices;
        internal int? RemovalAnswer;
        private readonly string _root;
        private readonly DispatcherTimer _responses=new(){Interval=TimeSpan.FromMilliseconds(20)};
        private readonly HashSet<Window> _seen=new();
        private string? _answer;
        private Action<Window>? _inspect;
        private Exception? _responseError;
        private readonly uint _thread=GetCurrentThreadId();
        private IntPtr _mainHandle;
        private readonly CancellationTokenSource _diagnosticStop=new();
        private Task? _diagnosticTask;
        private string _stage="constructing";
        private long _stageAt=Stopwatch.GetTimestamp();
        private readonly HashSet<IntPtr> _nativeReplies=[];

        internal RequeueUi(string root,string fixture)
        {
            RequeueStage(Path.GetFileName(root)+": constructor before main");
            _root=root;Directory.CreateDirectory(root);Data=new AppPersistence(Path.Combine(root,"profile"));
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(input);
            Window=(Window)XamlReader.Load(reader);
            Subject=new NativeWindow(Window,Data,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
                localPosterFfmpegPath:Path.Combine(root,"missing-poster-tool.exe"));
            Subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",Container="MP4"});
            Driver=new SubscriptionScenarioDriver(Subject.Subscriptions);
            try
            {
            Trace("constructor seeding");
            SourceOrder=[Url("baseline"),Url("Video"),Url("video"),Url("skipped"),Url("queued"),Url("processed"),Url("duplicate")];
            var origin=Store.Add(Url("origin-source"),false,shortName:"Источник");
            Store.ApplyScan(origin.Id,new("Источник",[new(Duplicate,Title(Duplicate))],"",""),[]);
            Store.MarkQueued(origin.Id,[Duplicate]);Store.MarkSuccessful(Duplicate);
            Source=Store.Add(Url("source"),false,shortName:"Повторы");
            Store.ApplyScan(Source.Id,new("Повторная подписка",[new(Baseline,Title(Baseline))],"",""),[]);
            Store.ApplyScan(Source.Id,new("Повторная подписка",SourceOrder.Select(url=>new DiscoveredMedia(url,Title(url))).ToArray(),"",""),[]);
            Store.Skip(Source.Id,[Skipped]);Store.MarkQueued(Source.Id,[Queued,Processed]);Store.MarkSuccessful(Processed);
            Store.AcknowledgeDuplicates(Store.Read().Items.Where(item=>item.State=="AlreadyProcessed"));
            Data.AppendSuccess(Processed);Data.AppendSuccess(Duplicate);
            foreach(string url in SourceOrder)Cache(url);
            Window.Show();Subject.SaveCheckpoint();
            _mainHandle=new WindowInteropHelper(Window).Handle;
            _responses.Tick+=Respond;_responses.Start();
            _diagnosticTask=Task.Run(ObserveBlockedUiAsync);
            Trace("constructor before subscription Open");
            Driver.Open();Refresh();
            Trace("constructor after subscription Open");
            Require(Item(Processed).State=="Processed" && Item(Processed).SuccessAttributed &&
                Item(Duplicate).State=="AlreadyProcessed" && Item(Duplicate).DuplicateSubscriptionId==origin.Id && Item(Duplicate).DuplicateNotified,
                "The fixture must contain durable historical states with genuine cross-subscription provenance.");
            Window.UpdateLayout();Subject.SaveCheckpoint();
            Trace("constructor ready");
            }
            catch
            {
                try{Subject.Dispose();Window.Close();}
                finally{StopObserving();}
                throw;
            }
        }
        internal string Url(string suffix)=>"https://startup-download.invalid/"+Path.GetFileName(_root)+"/"+suffix;
        internal string Title(string url)=>"Материал "+new Uri(url).AbsolutePath.Split('/').Last();
        internal T Main<T>(string name) where T:class=>(T)(Window.FindName(name)??throw new InvalidOperationException("Missing actual main control: "+name));
        internal void ClickMain(string name)
        {
            Trace("main "+name+" before click");
            var operation=Window.Dispatcher.BeginInvoke(()=>Main<Button>(name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)));
            Until(()=>operation.Task.IsCompleted,"The actual main Dispatcher action must be delivered.");operation.Task.GetAwaiter().GetResult();ThrowResponse();
            Trace("main "+name+" delivered");
        }
        internal SubscriptionItem Item(string url)=>Store.Read().Items.Single(item=>item.SubscriptionId==Source.Id && item.Url==url);
        internal void Cache(string url)
        {
            string file=Path.Combine(Data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
            AppPersistence.AtomicWrite(file,JsonSerializer.Serialize(new{CacheVersion=1,PreviewFetchVersion=3,SavedAtUtc=DateTime.UtcNow,Url=url,
                Success=true,Title="",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",ThumbnailBase64=""},
                AppPersistence.Json));
        }
        internal void Refresh()
        {
            Driver.Refresh();
            Until(()=>Driver.TestWindow is not null && Driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Any(source=>source.Id==Source.Id) &&
                !Driver.TestIsMutating,"The actual subscription source view must finish loading.");
            Driver.TestSources.SelectedItem=Driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(source=>source.Id==Source.Id);
            Until(()=>Driver.TestInbox.Items.Count==Store.Read().Items.Count(item=>item.SubscriptionId==Source.Id),
                "The actual inbox must bind the source snapshot.");
        }
        internal void Select(params string[] urls)
        {
            var selected=urls.ToHashSet(StringComparer.Ordinal);
            Driver.TestInbox.SelectedItems.Clear();
            foreach(var row in Driver.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Where(row=>selected.Contains(row.Url)))
                Driver.TestInbox.SelectedItems.Add(row);
            Require(Driver.TestInbox.SelectedItems.Count==urls.Length,"Every requested exact fixture row must be visibly selected.");
        }
        internal Button DialogButton(Window dialog,string decision)=>(Button)(dialog.FindName(decision switch
        {
            "New"=>"SubscriptionQueueSelectionNewOnlyButton","All"=>"SubscriptionQueueSelectionAllSelectedButton",
            _=>"SubscriptionQueueSelectionCancelButton"
        })??throw new InvalidOperationException("Missing real subscription selection button."));
        internal void Queue(string? answer=null,Action<Window>? inspect=null)
        {
            Trace("queue "+(answer??"uniform")+" before click");
            _answer=answer;_inspect=inspect;int before=ChooserCount;
            var operation=Window.Dispatcher.BeginInvoke(()=>
            {
                var button=Driver.TestControl<Button>("QueueSubscriptionItemsButton");
                Require(button.IsEnabled,"Every selected subscription status must allow explicit queueing.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Until(()=>operation.Task.IsCompleted,"The actual Dispatcher must deliver the subscription queue click.");operation.Task.GetAwaiter().GetResult();
            Trace("queue click delivered; waiting for acknowledgement");
            Until(()=>!Driver.TestIsMutating,"The queue choice, history consent and durable acknowledgement must finish.");
            ThrowResponse();
            Require(answer is null?ChooserCount==before:ChooserCount==before+1,
                "Only a mixed captured selection must display exactly one choice.");
            _answer=null;_inspect=null;
            Trace("queue durable acknowledgement complete");
        }
        private void Respond(object? sender,EventArgs args)
        {
            var native=OwnedNativeWindows();
            _nativeReplies.IntersectWith(native.Select(item=>item.Handle));
            foreach(var item in native.Where(item=>item.Class=="#32770" && _nativeReplies.Add(item.Handle)))
            {
                if(ExpectedDuplicateNotices>0 && item.Caption=="Материалы уже обработаны")
                {
                    ExpectedDuplicateNotices--;DuplicateNotices++;
                    PostMessageW(item.Handle,0x111u,(IntPtr)1,IntPtr.Zero);
                }
                else if(RemovalAnswer is {} answer && item.Caption==ErrorMessages.Text("Удалить загрузки","Delete downloads"))
                {
                    RemovalCount++;Trace("native removal confirmation answered No");
                    PostMessageW(item.Handle,0x111u,(IntPtr)answer,IntPtr.Zero);
                }
                else
                {
                    UnexpectedDialog("native "+item.Caption);
                    // These are unexpected fixture-owned dialogs, never user
                    // approval. Cancel/No/OK only; never accept deletion/Start.
                    int safe=new[]{2,7,1}.FirstOrDefault(id=>RequeueGetDlgItem(item.Handle,id)!=IntPtr.Zero);
                    PostMessageW(item.Handle,safe==0?0x10u:0x111u,(IntPtr)safe,IntPtr.Zero);
                }
            }
            foreach(var dialog in Application.Current.Windows.Cast<Window>().ToArray())
            {
                if(!dialog.IsVisible || _seen.Contains(dialog))continue;
                bool chooser=dialog.Name=="SubscriptionQueueSelectionDialog" && ReferenceEquals(dialog.Owner,Driver.TestWindow);
                bool duplicate=dialog.Owner==Window && dialog.FindName("DuplicateUrl") is TextBox;
                if(!chooser && !duplicate)
                {
                    if(dialog!=Window && dialog!=Driver.TestWindow && OwnsWindow(dialog))
                    {
                        _seen.Add(dialog);UnexpectedDialog("WPF "+dialog.Title+" ("+dialog.Name+")");
                        dialog.Close();
                    }
                    continue;
                }
                _seen.Add(dialog);
                try
                {
                    if(chooser)
                    {
                        ChooserCount++;
                        if(_answer is not {} choice)
                            throw new InvalidOperationException("A uniform selection unexpectedly displayed a mixed-state choice.");
                        Trace("chooser "+choice);
                        Require(dialog.FindName("SubscriptionQueueSelectionSummary") is TextBlock summary && summary.Text.Length>0,
                            "The actual choice must explain its captured selection.");
                        Require(DialogButton(dialog,"Cancel").IsCancel && !DialogButton(dialog,"All").IsDefault &&
                            (DialogButton(dialog,"New").IsEnabled
                                ?DialogButton(dialog,"New").IsDefault && !DialogButton(dialog,"Cancel").IsDefault
                                :DialogButton(dialog,"Cancel").IsDefault),
                            "Enter and Escape must preserve the safe Only-new/Cancel defaults instead of selecting all historical results.");
                        _inspect?.Invoke(dialog);
                        Trace("chooser inspection returned; visible="+dialog.IsVisible);
                        if(!dialog.IsVisible)continue;
                        if(choice=="Close")dialog.Close();
                        else
                        {
                            var button=DialogButton(dialog,choice);
                            Require(button.IsEnabled,"The requested actual queue-choice button must be enabled.");
                            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        }
                    }
                    else
                    {
                        DuplicateCount++;Trace("history "+HistoryAnswer);
                        ((Button)dialog.FindName(HistoryAnswer)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    }
                }
                catch(Exception error){_responseError??=error;if(dialog.IsVisible)dialog.Close();}
            }
        }
        private void Trace(string stage)
        {
            Volatile.Write(ref _stage,stage);Volatile.Write(ref _stageAt,Stopwatch.GetTimestamp());
            RequeueStage(Path.GetFileName(_root)+": "+stage);
        }
        private void UnexpectedDialog(string description)
        {
            string message="Unexpected fixture-owned "+description+" during "+Volatile.Read(ref _stage);
            _responseError??=new InvalidOperationException(message);Trace(message);
        }
        private bool OwnsWindow(Window candidate)
        {
            for(Window? owner=candidate.Owner;owner is not null;owner=owner.Owner)
                if(ReferenceEquals(owner,Window))return true;
            return false;
        }
        private bool OwnsHandle(IntPtr handle)
        {
            var root=Volatile.Read(ref _mainHandle);
            for(int count=0;handle!=IntPtr.Zero && count<32;count++,handle=RequeueGetWindow(handle,4))
                if(root!=IntPtr.Zero && handle==root)return true;
            return false;
        }
        private List<(IntPtr Handle,string Caption,string Class)> OwnedNativeWindows()
        {
            var found=new List<(IntPtr,string,string)>();
            EnumThreadWindows(_thread,(handle,_)=>
            {
                if(!OwnsHandle(handle))return true;
                var caption=new StringBuilder(256);var type=new StringBuilder(128);
                // A ThreadPool diagnostic must not synchronously wait on a
                // blocked WPF thread while asking for its window caption.
                RequeueSendMessageTimeout(handle,0xDu,(IntPtr)caption.Capacity,caption,2,200,out _);
                RequeueGetClassName(handle,type,type.Capacity);
                found.Add((handle,caption.ToString(),type.ToString()));return true;
            },IntPtr.Zero);
            return found;
        }
        private async Task ObserveBlockedUiAsync()
        {
            try
            {
                while(true)
                {
                    await Task.Delay(TimeSpan.FromSeconds(15),_diagnosticStop.Token);
                    if(Stopwatch.GetElapsedTime(Volatile.Read(ref _stageAt))<TimeSpan.FromSeconds(15))continue;
                    string windows=string.Join("; ",OwnedNativeWindows().Select(item=>item.Class+" '"+item.Caption+"'"));
                    RequeueStage(Path.GetFileName(_root)+": watchdog during "+Volatile.Read(ref _stage)+"; owned windows: "+windows);
                }
            }
            catch(OperationCanceledException) when(_diagnosticStop.IsCancellationRequested){}
        }
        private void StopObserving()
        {
            _responses.Stop();_responses.Tick-=Respond;_diagnosticStop.Cancel();
            _diagnosticTask?.GetAwaiter().GetResult();_diagnosticStop.Dispose();
        }
        [DllImport("user32.dll",EntryPoint="GetWindow")] private static extern IntPtr RequeueGetWindow(IntPtr handle,uint command);
        [DllImport("user32.dll",EntryPoint="GetDlgItem")] private static extern IntPtr RequeueGetDlgItem(IntPtr handle,int id);
        [DllImport("user32.dll",EntryPoint="GetClassNameW",CharSet=CharSet.Unicode)] private static extern int RequeueGetClassName(IntPtr handle,StringBuilder text,int count);
        [DllImport("user32.dll",EntryPoint="SendMessageTimeoutW",CharSet=CharSet.Unicode)] private static extern IntPtr RequeueSendMessageTimeout(
            IntPtr handle,uint message,IntPtr wParam,StringBuilder text,uint flags,uint timeout,out IntPtr result);
        private void ThrowResponse()
        {if(_responseError is {} error)throw new InvalidOperationException("Subscription requeue modal automation failed.",error);}
        internal void RequireIdle(byte[] history)
        {
            Require(Subject.SessionState.Coordinator is null && Subject.SessionState.RunningTask is null && Subject.SessionState.StartedAt is null &&
                Subject.SessionState.Downloads.Count==0 && File.ReadAllBytes(Data.HistoryPath).SequenceEqual(history),
                "Subscription queue acceptance must not start native work, start clocks, publish downloads or alter successful history.");
        }
        internal static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Subscription requeue WPF test: "+message);}
        internal static void Until(Func<bool> ready,string message)
        {
            if(ready())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{try{if(ready() || watch.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;}
                catch(Exception error){failure=error;frame.Continue=false;}};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(failure is not null)throw new InvalidOperationException(message,failure);
            Require(ready(),message);
        }
        public void Dispose()
        {
            Trace("dispose before native join");
            try
            {
            if(Subject.SessionState.Coordinator is {} active)
            {active.Stop();Until(()=>Subject.SessionState.Coordinator is null,"The requeue fixture must join its remaining native workers.");}
            Trace("dispose before subscription close");Driver.TestWindow?.Close();
            Trace("dispose before main controller");Subject.Dispose();
            Trace("dispose before main window close");Window.Close();
            Trace("dispose complete");ThrowResponse();
            }
            finally {StopObserving();}
        }
    }
}
