using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    internal static void TestQueuePlaybackAndTimer(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Queue playback/session clock WPF test failed: "+error);
        }
        static void PumpUntil(Func<bool> condition,string error)
        {
            var deadline=DateTime.UtcNow.AddSeconds(20);
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Require(condition(),error);
        }
        static void PumpFor(TimeSpan duration)
        {
            var elapsed=Stopwatch.StartNew();
            PumpUntil(()=>elapsed.Elapsed>=duration,"Dispatcher did not advance through the requested clock interval");
        }
        static void Click(Window window,string name)=>((Button)window.FindName(name)!)
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"The compiled native fixture host must be available");
        string root=Path.Combine(testRoot,"queue-playback-clock");
        Directory.CreateDirectory(root);
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        // These files satisfy the production preflight only. This fixture writes
        // its own media and never invokes either tool or contacts a remote site.
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"fixture preflight");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"fixture preflight");
        string? oldPath=Environment.GetEnvironmentVariable("PATH");
        string? oldGate=Environment.GetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE");
        string? oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+oldPath);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        NativeWindow? subject=null;
        Window? window=null;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            subject=new NativeWindow(window,storage,executable);
            window.Show();
            subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="2",Container="MP4"});
            var queue=(DataGrid)window.FindName("QueueGrid")!;
            var stats=(TextBlock)window.FindName("SessionStats")!;
            var fast=new DownloadItem{Url="https://startup-download.invalid/timer-fast"};
            var slow=new DownloadItem{Url="https://startup-download.invalid/timer-slow"};
            subject.SessionState.TryAdd(fast);subject.SessionState.TryAdd(slow);subject.Reindex();
            Require(subject.SessionState.Elapsed==TimeSpan.Zero && subject.SessionState.StartedAt==null,
                "Adding queued work must leave the session clock idle");
            string gate=Path.Combine(root,"release-success");
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            Click(window,"StartButton");
            PumpUntil(()=>fast.Status=="Completed" && slow.Status=="Running" && File.Exists(gate+".started"),
                "The real Start path must publish one media file while another worker remains active");
            var before=subject.SessionState.Elapsed;
            PumpFor(TimeSpan.FromMilliseconds(1100));
            Require(subject.SessionState.Elapsed>before+TimeSpan.FromSeconds(1),
                "The clock must continue after one success while another URL is still downloading");
            Click(window,"PauseButton");
            Require(subject.SessionState.ReadMetrics().Paused,"Pause must still control this session");
            Click(window,"PauseButton");
            File.WriteAllText(gate,"release");
            PumpUntil(()=>subject.SessionState.Coordinator==null && subject.SessionState.RunningTask?.IsCompleted==true,
                "All successful workers must finish through the normal Start continuation");
            Require(fast.Status=="Completed" && slow.Status=="Completed" && File.Exists(fast.Path) && File.Exists(slow.Path),
                "Both fixture media must be published before the session is complete");
            void RequireFrozen(string terminal)
            {
                subject.UpdateState();
                var frozen=subject.SessionState.Elapsed;
                string displayed=stats.Text;
                PumpFor(TimeSpan.FromMilliseconds(1100));
                Require(subject.SessionState.Elapsed==frozen && stats.Text==displayed,
                    terminal+" must freeze both elapsed duration and the live SessionStats text through later UI ticks");
            }
            RequireFrozen("Successful completion");

            var failed=new DownloadItem{Url="https://startup-download.invalid/wrong-container"};
            subject.SessionState.TryAdd(failed);subject.Reindex();
            Click(window,"StartButton");
            Require(subject.SessionState.Elapsed<TimeSpan.FromSeconds(1),"A new Start must reset the previous duration");
            PumpUntil(()=>failed.Status=="Failed" && subject.SessionState.Coordinator==null,
                "A container failure must finish normally without a start-error dialog");
            RequireFrozen("Failure completion");
            Click(window,"RetryFailedButton");
            Require(subject.SessionState.Elapsed<TimeSpan.FromSeconds(1),"Retry failed must begin a fresh session clock");
            PumpUntil(()=>failed.Status=="Failed" && subject.SessionState.Coordinator==null,"Retry must also terminate");
            RequireFrozen("Retried failure");
            // Keep the failed row for the next Start too; Stop must still wait
            // for the independently running gated worker and its cleanup.
            var stopped=new DownloadItem{Url="https://startup-download.invalid/stop-timer-slow"};
            subject.SessionState.Forget(slow);subject.SessionState.TryAdd(stopped);subject.Reindex();
            gate=Path.Combine(root,"release-stop");
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",gate);
            Click(window,"StartButton");
            PumpUntil(()=>stopped.Status=="Running" && File.Exists(gate+".started"),"Stop fixture must reach the worker");
            Click(window,"StopButton");
            PumpUntil(()=>subject.SessionState.Coordinator==null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Stop must await cancellation and cleanup before ending the session");
            Require(stopped.Status=="Interrupted" && !storage.ReadHistory().ContainsKey(stopped.Url),
                "The cancelled attempt must remain interrupted and never become a successful history entry");
            RequireFrozen("Stopped completion");

            // Open the actual executable fixture via the same Windows shell
            // path used for a media association, and observe its independent
            // child invocation. This needs no production test hook or player.
            string calls=Path.Combine(root,"shell-open.jsonl");
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",calls);
            var playable=new DownloadItem{Url="https://queue-playback.invalid/completed",Status="Completed",Path=executable};
            subject.SessionState.TryAdd(playable);subject.Reindex();
            queue.ScrollIntoView(playable);queue.UpdateLayout();
            var container=queue.ItemContainerGenerator.ContainerFromItem(playable) as DataGridRow;
            Require(container!=null,"The completed item must have a real rendered queue row");
            void DoubleClick(object source,MouseButton button=MouseButton.Left)=>queue.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,button)
            {RoutedEvent=System.Windows.Controls.Control.MouseDoubleClickEvent,Source=source});
            DoubleClick(queue); // Header/empty-space equivalent with a selected or visible item.
            DoubleClick(container!,MouseButton.Right);
            playable.Status="Running";DoubleClick(container!);
            playable.Status="Failed";DoubleClick(container!);
            playable.Status="Completed";playable.Path=Path.Combine(root,"missing.mp4");DoubleClick(container!);
            Require(!File.Exists(calls),"Header, other buttons, unfinished rows and missing files must never launch a child");
            playable.Path=executable;
            DoubleClick(container!);
            int OpenCalls()
            {
                try { return File.ReadLines(calls).Count(line=>
                {
                    using var record=JsonDocument.Parse(line);
                    return record.RootElement.GetProperty("Arguments").GetArrayLength()==0;
                }); }
                catch(Exception error) when(error is IOException or JsonException) { return 0; }
            }
            PumpUntil(()=>OpenCalls()==1,"Double-clicking a completed queue row must open its exact stored local path using Windows shell");
            PumpFor(TimeSpan.FromMilliseconds(500));
            Require(OpenCalls()==1 && subject.SessionState.Coordinator==null,
                "One double-click must open one local file and must not start a download or launch its URL");
            Console.WriteLine("PASS: actual Queue double-click shell open and guards; real Start with parallel success, pause/resume, failure, Retry and Stop freeze the displayed session clock");
        }
        finally
        {
            if(subject?.SessionState.Coordinator is { } active)
            {
                active.Stop();
                PumpUntil(()=>subject.SessionState.Coordinator==null,"Failed scenario cancellation did not finish");
            }
            subject?.Dispose();window?.Close();
            Environment.SetEnvironmentVariable("PATH",oldPath);
            Environment.SetEnvironmentVariable("VD_NATIVE_SESSION_TIMER_GATE",oldGate);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
        }
    }
}
