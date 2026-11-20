using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    internal static void TestOutOfOrderDownloads(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Out-of-order WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var elapsed=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || elapsed.Elapsed>TimeSpan.FromSeconds(15))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The real native fixture apphost is required.");
        string root=Path.Combine(testRoot,"out-of-order-ui");Directory.CreateDirectory(root);
        const string prefix="https://out-of-order.invalid/",variable="VD_OUT_OF_ORDER_CONTROL";
        string? previousControl=Environment.GetEnvironmentVariable(variable),previousPath=Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable(variable,root);
        string tools=Path.Combine(root,"tools");Directory.CreateDirectory(tools);
        File.WriteAllText(Path.Combine(tools,"ffmpeg.exe"),"preflight fixture");
        File.WriteAllText(Path.Combine(tools,"ffprobe.exe"),"preflight fixture");
        Environment.SetEnvironmentVariable("PATH",tools+Path.PathSeparator+previousPath);
        using var reader=XmlReader.Create(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));NativeWindow? subject=null;
        DispatcherTimer? observer=null;
        try
        {
            subject=new NativeWindow(window,storage,fixture,automaticUpdates:false,automaticSubscriptionChecks:false,
                localPosterFfmpegPath:Path.Combine(root,"missing-poster.exe"));
            window.Show();subject.ApplyOptions(new DownloadOptions{Output=Path.Combine(root,"output"),Threads="1",EmbedMetadata=false,EmbedChapters=false});
            var first=new DownloadItem{Url=prefix+"ui-first"};
            var second=new DownloadItem{Url=prefix+"ui-second",DeferredToNextStart=true};
            var third=new DownloadItem{Url=prefix+"ui-third",DeferredToNextStart=true};
            foreach(var row in new[]{first,second,third})subject.SessionState.TryAdd(row);
            subject.Reindex();
            var queue=(DataGrid)window.FindName("QueueGrid")!;
            MenuItem OpenAction(DownloadItem row)
            {
                queue.SelectedItems.Clear();queue.SelectedItem=row;queue.ScrollIntoView(row);
                var menu=queue.ContextMenu!;menu.PlacementTarget=queue;menu.IsOpen=true;window.UpdateLayout();
                var action=menu.Items.OfType<MenuItem>().Single(item=>item.Name=="DownloadOutOfOrderMenuItem");
                PumpUntil(()=>action.IsVisible,"The actual context menu must finish opening before its action is inspected.");
                Require(action.Header?.ToString()==ErrorMessages.Text("Скачать вне очереди","Download out of order"),
                    "The queue must expose the explicitly named production command.");
                return action;
            }
            void Click(MenuItem action)
            {action.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));queue.ContextMenu!.IsOpen=false;}
            bool Started(DownloadItem row)=>Directory.GetFiles(root,row.Url[prefix.Length..]+".*.starts").Length>0;
            var initial=OpenAction(first);Require(initial.IsEnabled && initial.IsVisible,"One ordinary waiting row must expose the command.");Click(initial);
            PumpUntil(()=>Started(first) && first.Status=="Running","Idle explicit action must run the selected row through ordinary Start preflight.");
            Require(second.Status=="Queued" && third.Status=="Queued" && !subject.SessionState.Coordinator!.IsPending(second) &&
                subject.SessionState.Urls.SequenceEqual(new[]{first.Url}),
                "An idle explicit request must not admit unrelated ordinary or imported waiting rows.");
            var extra=OpenAction(second);Require(extra.IsEnabled,"An explicitly selected deferred row must be allowed to join the current pool.");Click(extra);
            PumpUntil(()=>Started(second) && second.Status=="Running","The temporary second child must bypass the held normal worker.");
            Require(first.Status=="Running" && first.Slot=="T1" && second.Slot=="T2" &&
                subject.SessionState.Options.Threads=="1" && subject.SessionState.Urls.Contains(second.Url) && !second.DeferredToNextStart,
                "The new owner must participate in metrics while retaining the normal limit and captured session.");
            var duplicate=OpenAction(second);Require(!duplicate.IsEnabled,"A running row must not offer another temporary generation.");Click(duplicate);
            Require(Directory.GetFiles(root,"ui-second.*.starts").Length==1,"A forced repeated menu event must not create a duplicate child.");

            storage.AppendSuccess(third.Url);
            bool cancelled=false;
            observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            observer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate=>
                    candidate.FindName("DuplicateUrl") is TextBox text && text.Text==third.Url);
                if(dialog is null)return;
                cancelled=true;observer.Stop();
                ((Button)dialog.FindName("CancelButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            observer.Start();var history=OpenAction(third);Require(history.IsEnabled,"The deferred row must reach the usual history consent.");Click(history);
            PumpUntil(()=>cancelled && !subject.SessionState.Coordinator!.IsPending(third),"The real history dialog must allow cancelling this explicit request.");
            Require(third.Status=="Queued" && third.DeferredToNextStart && !Started(third),
                "Cancelling history consent must preserve the waiting row without starting a temporary worker.");

            bool stoppedDuringConsent=false;long stopVersion=subject.SessionState.StopVersion;
            observer.Stop();
            observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            observer.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.OfType<Window>().FirstOrDefault(candidate=>
                    candidate.FindName("DuplicateUrl") is TextBox text && text.Text==third.Url);
                if(dialog is null)return;
                observer.Stop();stoppedDuringConsent=true;subject.SessionState.Stop();
                ((Button)dialog.FindName("YesButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            observer.Start();var stale=OpenAction(third);Require(stale.IsEnabled,"A fresh explicit action must be admitted after the previous consent closed.");Click(stale);
            PumpUntil(()=>stoppedDuringConsent && subject.SessionState.Coordinator is null && subject.SessionState.RunningTask?.IsCompleted==true,
                "Stop inside the nested history dialog must join the original regular and temporary workers.");
            Require(subject.SessionState.StopVersion>stopVersion && third.Status=="Queued" && third.DeferredToNextStart && !Started(third) &&
                first.AttemptFinished && second.AttemptFinished && first.Status=="Interrupted" && second.Status=="Interrupted",
                "A late Yes must not create another session or publish cancelled work after the captured session stops.");
            var afterStop=OpenAction(third);
            Require(afterStop.IsEnabled,"A historical Stopping flag must not permanently disable the next explicit idle request.");
            queue.ContextMenu!.IsOpen=false;
            Console.WriteLine("PASS: actual out-of-order queue menu uses idle preflight, starts an extra owned child, rejects repeats, preserves history Cancel and refuses a late modal decision after Stop");
        }
        finally
        {
            observer?.Stop();
            if(subject?.SessionState.Coordinator is not null)
            {
                subject.SessionState.Stop();
                PumpUntil(()=>subject.SessionState.RunningTask?.IsCompleted!=false,"Final out-of-order UI cleanup must join owned workers.");
            }
            subject?.Dispose();window.Close();
            Environment.SetEnvironmentVariable(variable,previousControl);Environment.SetEnvironmentVariable("PATH",previousPath);
        }
    }
}
