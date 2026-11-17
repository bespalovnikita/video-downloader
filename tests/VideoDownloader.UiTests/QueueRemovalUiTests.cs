using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    internal static void TestQueueRecordRemoval(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Queue record removal WPF test: "+message);}
        static void Until(Func<bool> condition,string message)
        {
            if(condition())return;
            var elapsed=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || elapsed.Elapsed>TimeSpan.FromSeconds(10))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing queue-removal control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Answer(string title,int answer,Action action,Action? beforeAnswer=null)
        {
            uint thread=GetCurrentThreadId();bool seen=false;Exception? failure=null;
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
                try{beforeAnswer?.Invoke();beforeAnswer=null;}
                catch(Exception error){failure=error;}
                if(DismissDialog(thread,title,failure is null?answer:7)){seen=true;timer.Stop();}
            };
            using var watchdog=new CancellationTokenSource();
            _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
            {if(!task.IsCanceled)DismissDialog(thread,title,7);},TaskScheduler.Default);
            timer.Start();try{action();}finally{watchdog.Cancel();timer.Stop();}
            if(failure is not null)throw failure;
            Require(seen,"The explicit confirmation was not displayed: "+title);
        }

        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The compiled native metadata fixture is required.");
        string root=Path.Combine(testRoot,"queue-record-removal");Directory.CreateDirectory(root);
        var culture=CultureInfo.CurrentUICulture;CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        var data=new AppPersistence(Path.Combine(root,"profile"));NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,data,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
                localPosterFfmpegPath:Path.Combine(root,"missing-poster-tool.exe"));
            window.Show();subject.ApplyOptions(new DownloadOptions{Output=root});
            var queue=Control<DataGrid>(window,"QueueGrid");
            DownloadItem Row(string id,string status="Queued")=>new(){Url="https://title-ingress.invalid/queue-record-"+id,
                Title=id,Status=status,State=status,AttemptFinished=status!="Queued",OutputRoot=root};
            DownloadItem Completed(string id,bool owned=true)
            {
                var row=Row(id,"Completed");row.Path=Path.Combine(root,id+".mp4");
                File.WriteAllText(row.Path,id+" media sentinel");row.OwnedPaths=owned?[row.Path]:[];return row;
            }
            DownloadItem Retained(string id)
            {
                var row=Row(id,"Skipped");row.RunDir=Path.Combine(root,"vd-native-"+Guid.NewGuid().ToString("N"));
                row.StageDir=Path.Combine(row.RunDir,Guid.NewGuid().ToString("N"));Directory.CreateDirectory(row.StageDir);
                File.WriteAllText(Path.Combine(row.StageDir,"video.part"),"retained partial sentinel");
                DownloadResumeStore.Create(row,new DownloadOptions{Output=root},null,"Best");return row;
            }
            void Add(params DownloadItem[] rows)
            {foreach(var row in rows)subject.SessionState.TryAdd(row);subject.Reindex();subject.UpdateState();}
            void Select(params DownloadItem[] rows)
            {queue.SelectedItems.Clear();foreach(var row in rows)queue.SelectedItems.Add(row);subject.UpdateState();}
            string clearTitle=ErrorMessages.Text("Очистить очередь","Clear queue");
            string completedClearTitle=ErrorMessages.Text("Убрать завершённые записи","Remove completed entries");

            var safe=Completed("safe-owned");var legacy=Completed("safe-legacy",owned:false);var hidden=Row("hidden");
            data.AppendSuccess(safe);byte[] history=File.ReadAllBytes(data.HistoryPath);
            Add(safe,legacy,hidden);Select(safe,legacy);
            Click(window,"ForgetQueueButton");
            Until(()=>!subject.SessionState.Queue.Contains(safe) && !subject.SessionState.Queue.Contains(legacy),
                "Safe removal must remove the selected records.");
            Require(subject.SessionState.Queue.SequenceEqual(new[]{hidden}) && File.Exists(safe.Path) && File.Exists(legacy.Path) &&
                File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                "Safe record removal must preserve both owned and legacy media, history and unselected records.");

            var skipped=Retained("selected-retained");string stage=skipped.StageDir;Add(skipped);Select(skipped);
            Answer("Убрать записи из очереди",7,()=>Click(window,"ForgetQueueButton"));
            Require(subject.SessionState.Queue.Contains(skipped) && Directory.Exists(stage),
                "Declining temporary-data discard must preserve the skipped row and stage.");
            Answer("Убрать записи из очереди",6,()=>Click(window,"ForgetQueueButton"));
            Until(()=>!subject.SessionState.Queue.Contains(skipped),"Selected retained record removal must finish.");
            Require(!Directory.Exists(stage) && !File.Exists(stage+".resume.json") && File.Exists(safe.Path),
                "Discarding retained data must remove only its proof-bound stage and preserve published media.");

            var completed=Completed("completed-only");var changed=Completed("changed-after-question");var queued=Row("queued");
            var beforeAll=Retained("all-retained");string allStage=beforeAll.StageDir;Add(completed,changed,queued,beforeAll);
            var late=Row("late-completed-dialog");
            Answer(completedClearTitle,6,()=>Click(window,"ClearQueueButton"),()=>
            {changed.Status="Queued";Add(late);});
            Until(()=>!subject.SessionState.Queue.Contains(completed),"Completed-only action must remove its captured eligible record.");
            Require(subject.SessionState.Queue.Contains(changed) && subject.SessionState.Queue.Contains(queued) &&
                subject.SessionState.Queue.Contains(beforeAll) && subject.SessionState.Queue.Contains(late) &&
                File.Exists(completed.Path) && File.Exists(changed.Path) && Directory.Exists(allStage),
                "Completed-only must recheck state and preserve unfinished/skipped/late rows and all media.");
            int before=subject.SessionState.Queue.Count;
            Click(window,"ClearQueueButton");
            Require(subject.SessionState.Queue.Count==before,
                "With no completed rows, the same command must never fall through to unfinished records.");

            Answer(clearTitle,7,()=>Click(window,"ClearAllQueueButton"));
            Require(subject.SessionState.Queue.Count==before && Directory.Exists(allStage),
                "Declining explicit full clear must preserve retained data and every record.");
            var afterQuestion=Row("late-full-clear");
            Answer(clearTitle,6,()=>Click(window,"ClearAllQueueButton"),()=>Add(afterQuestion));
            Until(()=>subject.SessionState.Queue.SequenceEqual(new[]{afterQuestion}),
                "Explicit full clear must include skipped rows but exclude a late arrival.");
            Require(!Directory.Exists(allStage) && File.Exists(completed.Path) && File.Exists(changed.Path) &&
                File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                "Full clear must discard retained temporary data while preserving completed files and history.");

            var destructive=Completed("destructive-owned");string ownPath=destructive.Path;
            string foreign=Path.Combine(root,"unowned-user-file.mp4");File.WriteAllText(foreign,"foreign sentinel");
            Add(destructive);Select(destructive);
            Answer(ErrorMessages.Text("Удалить загрузки","Delete downloads"),7,()=>Click(window,"RemoveQueueButton"));
            Require(subject.SessionState.Queue.Contains(destructive) && File.Exists(ownPath),
                "Declining destructive removal must preserve owned files and the queue entry.");
            Answer(ErrorMessages.Text("Удалить загрузки","Delete downloads"),6,()=>Click(window,"RemoveQueueButton"));
            Until(()=>!subject.SessionState.Queue.Contains(destructive),"Confirmed destructive removal must finish.");
            Require(!File.Exists(ownPath) && File.ReadAllText(foreign)=="foreign sentinel" && File.Exists(safe.Path) &&
                File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                "The explicit destructive command must delete only proven owned files and preserve history/unrelated files.");

            var active=new DownloadCoordinator("must-not-launch",data,new DownloadOptions{Output=root},_=>{},_=>{});
            try
            {
                subject.SessionState.AttachCoordinator(active);Select(afterQuestion);
                Require(!Control<Button>(window,"ForgetQueueButton").IsEnabled && !Control<Button>(window,"ClearAllQueueButton").IsEnabled,
                    "Safe record removal and full clear must explain and enforce their idle-only availability.");
                Click(window,"ForgetQueueButton");Click(window,"ClearAllQueueButton");
                Require(subject.SessionState.Queue.SequenceEqual(new[]{afterQuestion}),
                    "Raised events must not bypass active-session safety guards.");
                Require(afterQuestion.Status=="Queued" && afterQuestion.OwnedPaths.Count==0 && afterQuestion.StageDir.Length==0,
                    "The active-session confirmation case must begin with a plain queued row and no files.");
                Answer(ErrorMessages.Text("Удалить загрузки","Delete downloads"),7,()=>Click(window,"RemoveQueueButton"));
                Require(subject.SessionState.Queue.SequenceEqual(new[]{afterQuestion}) && !afterQuestion.PendingRemoval,
                    "An active session must ask before mutating even a queued row, which a worker could start after classification.");
                Answer(ErrorMessages.Text("Удалить загрузки","Delete downloads"),6,()=>Click(window,"RemoveQueueButton"));
                Until(()=>!subject.SessionState.Queue.Contains(afterQuestion),
                    "The explicitly confirmed queued record must be removable during an active session.");
                Require(File.Exists(safe.Path) && File.Exists(legacy.Path) && File.Exists(foreign) &&
                    File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                    "Confirming an active queued removal must preserve unrelated published files and history.");
            }
            finally
            {
                subject.SessionState.AttachCoordinator(null);active.Stop();active.RunAsync().GetAwaiter().GetResult();
                active.DisposeAsync().AsTask().GetAwaiter().GetResult();
            }
            Console.WriteLine("PASS: safe queue record removal preserves media/history, explicit completed/full scopes recheck captured rows, retained data has proof-bound discard and destructive removal affects only owned files");
        }
        finally
        {
            subject?.Dispose();window.Close();CultureInfo.CurrentUICulture=culture;
        }
    }
}
