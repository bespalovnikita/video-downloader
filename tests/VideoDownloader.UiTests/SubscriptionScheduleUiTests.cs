using System.Diagnostics;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestSubscriptionSchedule(string testRoot)
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Subscription schedule WPF test failed: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(20);Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{try{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;}catch(Exception error){failure=error;frame.Continue=false;}};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(failure is not null)throw failure;Require(condition(),message);
        }
        static void Join(Task task)
        {PumpUntil(()=>task.IsCompleted,"The scheduled task must join its native worker");task.GetAwaiter().GetResult();}
        static bool Alive(int pid)
        {try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"The real compiled metadata/scanner fixture is required");
        string root=Path.Combine(testRoot,"subscription-schedule");Directory.CreateDirectory(root);
        string config=Path.Combine(root,"scan.json"),calls=Path.Combine(root,"calls.jsonl"),childPath=Path.Combine(root,"child.pid");
        string? titleLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        var store=new SubscriptionStore(Path.GetDirectoryName(storage.SessionPath)!);
        const string baseline="https://subscription-scanner.invalid/schedule-base";
        const string fresh="https://subscription-scanner.invalid/schedule-new";
        const string later="https://subscription-scanner.invalid/schedule-later";
        var source=store.Add("https://subscription-scanner.invalid/schedule-ui",false);
        store.ApplyScan(source.Id,new("Scheduled collection",[new(baseline,"Baseline")],"",""),[]);
        var clock=new SubscriptionUiClock();NativeWindow? main=null;IDisposable? lease=null;
        var notices=new List<string>();
        var responder=SubscriptionQueueOfferResponder.Current!;int prompts=responder.Count;
        void Fixture(string mode,params string[] urls)=>File.WriteAllText(config,JsonSerializer.Serialize(new{Mode=mode,Response=JsonSerializer.Serialize(new
        {_type="playlist",title="Scheduled collection",playlist_count=urls.Length,entries=urls.Select(url=>new{webpage_url=url,title="Scheduled title"}).ToArray()})}));
        int Calls(){try{return File.Exists(calls)?File.ReadAllLines(calls).Length:0;}catch(IOException){return 0;}}
        try
        {
            Fixture("normal",baseline,fresh);
            main=new NativeWindow(window,storage,executable,automaticUpdates:false,subscriptionClock:clock,automaticSubscriptionChecks:false);
            window.Show();((TextBox)window.FindName("CookiesBox")!).Text=config;
            var controller=main.Subscriptions;var driver=new SubscriptionScenarioDriver(controller);
            typeof(SubscriptionController).GetField("_scheduleNotice",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!
                .SetValue(controller,(Action<string>)notices.Add);
            Join(controller.PollScheduledChecksAsync());Require(Calls()==0,"Startup does not scan opt-out subscriptions");
            driver.Open();PumpUntil(()=>driver.TestSources.Items.Count==1 && !driver.TestIsMutating,"Real subscription controls must load");
            var enabled=driver.TestControl<CheckBox>("SubscriptionScheduleEnabledCheck");
            var interval=driver.TestControl<TextBox>("SubscriptionScheduleIntervalBox");
            var save=driver.TestControl<Button>("SaveSubscriptionScheduleButton");
            Require(enabled.IsChecked==false && interval.Text=="30","New and existing sources default to schedule off and thirty minutes");
            interval.Text="4";Require(!save.IsEnabled,"Too frequent polling cannot be saved through WPF");
            interval.Text="5";enabled.IsChecked=true;save.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>store.Read().Subscriptions.Single().ScheduleEnabled && !driver.TestIsMutating,"The opt-in and valid interval must persist");
            Join(controller.PollScheduledChecksAsync());
            Require(Calls()==0 && controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5),"Opt-in waits the chosen interval instead of checking immediately");
            clock.Advance(TimeSpan.FromMinutes(5));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==1 && store.Read().Items.Single(item=>item.Url==fresh).State=="New" &&
                driver.TestControl<TextBlock>("SubscriptionScheduleStatusText").Text.Length>0 &&
                driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single().NextCheckLabel!="—",
                "The real scanner updates inbox, last attempt and next-check controls");
            Require(notices.Count==1 && responder.Count==prompts && main.SessionState.Queue.Count==0 && main.SessionState.Coordinator is null &&
                main.SessionState.RunningTask is null && !File.Exists(storage.HistoryPath),
                "An automatic discovery never offers a modal, enqueues, starts or records success");
            clock.Advance(TimeSpan.FromMinutes(5));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==2 && notices.Count==1 && responder.Count==prompts,"An unchanged unaccepted inbox creates neither a repeated alert nor an automatic queue offer");

            driver.TestWindow!.Close();main.Lifecycle.Hide();Fixture("normal",baseline,fresh,later);
            clock.Advance(TimeSpan.FromDays(2));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==3 && notices.Count==2 && store.Read().Items.Single(item=>item.Url==later).State=="New" && !window.IsVisible,
                "Checks survive a closed subscription window, run in tray and coalesce sleep without stealing focus");
            Join(controller.PollScheduledChecksAsync());Require(Calls()==3,"A resume does not replay every missed interval");

            main.Lifecycle.Restore();driver.Open();PumpUntil(()=>driver.TestSources.Items.Count==1,"The subscription window reopens");
            Fixture("hang",baseline,fresh,later);File.Delete(childPath);clock.Advance(TimeSpan.FromMinutes(5));
            byte[] before=File.ReadAllBytes(store.Path);Task running=controller.PollScheduledChecksAsync();
            PumpUntil(()=>File.Exists(childPath) && controller.IsAutomaticCheckActive,"Automatic scanner must own its real child process");
            int child=int.Parse(File.ReadAllText(childPath)),parent=int.Parse(File.ReadAllText(Path.Combine(root,"parent.pid")));
            Require(controller.HasActiveWork && !driver.TestControl<Button>("CheckSubscriptionButton").IsEnabled,
                "Manual and automatic checks share one execution guard");
            int activeCalls=Calls();driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Task reentrantPoll=controller.PollScheduledChecksAsync();
            Require(Calls()==activeCalls,"A reentrant manual or automatic check cannot create another scanner");
            driver.TestWindow!.Close();main.Lifecycle.Hide();
            Require(!running.IsCompleted && Alive(child),"Closing only the subscription window keeps an automatic check alive");
            Task<IDisposable> maintenance=main.SuspendSubscriptionSchedulingAsync();Join(maintenance);lease=maintenance.Result;Join(running);Join(reentrantPoll);
            Require(!Alive(child) && !Alive(parent) && !controller.HasActiveWork && controller.IsSchedulingSuspended &&
                before.SequenceEqual(File.ReadAllBytes(store.Path)),"Profile maintenance cancels and joins the native tree before returning its lease");
            clock.Advance(TimeSpan.FromDays(1));Join(controller.PollScheduledChecksAsync());Require(Calls()==activeCalls,"The maintenance lease blocks new checks");
            Fixture("normal",baseline,fresh,later);lease.Dispose();lease=null;Join(controller.PollScheduledChecksAsync());
            Require(Calls()==activeCalls && controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5),
                "Maintenance resume waits a full interval and revalidates saved configuration");
            clock.Advance(TimeSpan.FromMinutes(5));Join(controller.PollScheduledChecksAsync());Require(Calls()==activeCalls+1,"A joined check leaves scheduling usable");

            main.Lifecycle.Restore();driver.Open();PumpUntil(()=>driver.TestSources.Items.Count==1 && !driver.TestIsMutating,"Manual check controls reopen");
            Fixture("hang",baseline,fresh,later);File.Delete(childPath);
            driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>File.Exists(childPath) && driver.TestIsMutating,"A manual scanner owns the common guard");
            int manualCalls=Calls();clock.Advance(TimeSpan.FromDays(1));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==manualCalls && !controller.IsAutomaticCheckActive,"Scheduling never overlaps a manual check");
            driver.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>!driver.TestIsMutating,"The ordinary Cancel button still joins a manual check");

            // Actual native owner close, including the asynchronous OnClosing path.
            Fixture("hang",baseline,fresh,later);File.Delete(childPath);
            Join(controller.PollScheduledChecksAsync());clock.Advance(TimeSpan.FromMinutes(5));
            Task closingCheck=controller.PollScheduledChecksAsync();PumpUntil(()=>File.Exists(childPath),"A final automatic worker starts");
            child=int.Parse(File.ReadAllText(childPath));parent=int.Parse(File.ReadAllText(Path.Combine(root,"parent.pid")));
            main.Lifecycle.RequestExit();Join(closingCheck);
            PumpUntil(()=>!window.IsVisible && !Alive(child) && !Alive(parent),"Application exit joins its scheduled child tree");
            Require(responder.Count==prompts && main.SessionState.Queue.Count==0 && !File.Exists(storage.HistoryPath),"Every automatic path preserves the explicit queue/Start boundary");
            responder.ThrowIfFailed();
        }
        finally
        {
            lease?.Dispose();
            if(main is not null){Join(main.Subscriptions.StopChecksAsync());main.Dispose();}
            if(window.IsVisible)window.Close();Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",titleLog);
        }
    }
    private sealed class SubscriptionUiClock:TimeProvider
    {
        private DateTimeOffset _now=new(2030,1,1,0,0,0,TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow()=>_now;
        internal void Advance(TimeSpan value)=>_now+=value;
    }
}
