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
    private static void TestProfileBackupSchedulingUi(string testRoot,string archive)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Backup scheduling WPF: "+message);}
        static void Pump(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{try{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(20))frame.Continue=false;}catch(Exception error){failure=error;frame.Continue=false;}};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            if(failure is not null)throw failure;Require(condition(),message);
        }
        static void Join(Task task)
        {Pump(()=>task.IsCompleted,"A real scheduling/profile task did not join.");task.GetAwaiter().GetResult();}
        static bool Alive(int pid)
        {try{using var process=Process.GetProcessById(pid);return !process.HasExited;}catch(ArgumentException){return false;}}
        static void Click(Window window,string name)=>((Button)window.FindName(name)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void Confirm(ProfileBackupWindow backup)
        {Click(backup,"RestoreProfileBackupButton");Click(backup,"ConfirmProfileRestoreButton");}

        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"The compiled scanner fixture is required for actual automatic-check cancellation.");
        string root=Path.Combine(testRoot,"scheduler-maintenance");Directory.CreateDirectory(root);
        string config=Path.Combine(root,"scan.json"),calls=Path.Combine(root,"calls.jsonl"),childFile=Path.Combine(root,"child.pid");
        string? previousTitleLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        const string baseline="https://subscription-scanner.invalid/backup-base",fresh="https://subscription-scanner.invalid/backup-fresh";
        var storage=new AppPersistence(Path.Combine(root,"profile"));storage.Save([],new DownloadOptions{Cookies=config});
        var store=new SubscriptionStore(Path.GetDirectoryName(storage.SessionPath)!);
        var source=store.Add("https://subscription-scanner.invalid/backup-schedule",false,shortName:"Местная");
        store.ApplyScan(source.Id,new("Backup schedule",[new(baseline,"Baseline")],"",""),[]);store.SetSchedule(source.Id,true,5);
        void Fixture(string mode,params string[] urls)=>File.WriteAllText(config,JsonSerializer.Serialize(new{Mode=mode,Response=JsonSerializer.Serialize(new
        {_type="playlist",title="Backup schedule",playlist_count=urls.Length,entries=urls.Select(url=>new{webpage_url=url,title="Backup fixture"}).ToArray()})}));
        int Calls(){try{return File.Exists(calls)?File.ReadAllLines(calls).Length:0;}catch(IOException){return 0;}}
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(input);
        var owner=(Window)XamlReader.Load(reader);var clock=new SubscriptionUiClock();using var held=new BackupUiCheckpoint();
        var service=new WindowsProfileBackup(storage,held.Visit);NativeWindow? main=null;ProfileBackupWindow? backup=null;
        var offers=SubscriptionQueueOfferResponder.Current!;int priorOffers=offers.Count;
        try
        {
            Fixture("hang",baseline,fresh);
            main=new NativeWindow(owner,storage,executable,automaticUpdates:false,subscriptionClock:clock,automaticSubscriptionChecks:false,profileBackup:service);
            main.ProfileBackupPickers=new(_=>archive,_=>null);owner.Show();
            var controller=main.Subscriptions;var driver=new SubscriptionScenarioDriver(controller);Join(controller.PollScheduledChecksAsync());
            Require(Calls()==0 && controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5),"Opted-in scheduling must wait its interval.");
            driver.Open();Pump(()=>driver.TestSources.Items.Count==1 && !driver.TestIsMutating,"The actual subscription view did not load.");
            Click(owner,"OpenProfileBackupButton");backup=main.ProfileBackupDialog??throw new InvalidOperationException("Backup view must open while idle.");
            byte[] before=File.ReadAllBytes(store.Path);clock.Advance(TimeSpan.FromMinutes(5));Task active=controller.PollScheduledChecksAsync();
            Pump(()=>File.Exists(childFile) && controller.IsAutomaticCheckActive,"The automatic scanner did not create its real child process.");
            int child=int.Parse(File.ReadAllText(childFile)),parent=int.Parse(File.ReadAllText(Path.Combine(root,"parent.pid"))),activeCalls=Calls();
            main.UpdateState();Require(((Button)owner.FindName("OpenProfileBackupButton")!).IsEnabled,"An automatic check must permit maintenance to cancel and join it.");
            bool barrierObserved=false;controller.ScheduleChanged+=()=>barrierObserved|=main.ProfileBackupBusy && controller.IsSchedulingSuspended;
            Click(backup,"OpenProfileBackupButton");
            Pump(()=>!backup.IsBusy,"Loading backup after cancelling the automatic scan did not join.");Join(active);
            Require(barrierObserved && !Alive(child) && !Alive(parent) && !controller.HasActiveWork && !controller.IsSchedulingSuspended &&
                ReferenceEquals(main.Subscriptions,controller) && driver.TestWindow is null && before.SequenceEqual(File.ReadAllBytes(store.Path)) &&
                ((Button)backup.FindName("RestoreProfileBackupButton")!).IsEnabled,
                "The same scheduler owner must survive maintenance; old view closes only after its real tree joins and keeps previous results.");
            Join(controller.PollScheduledChecksAsync());
            Require(Calls()==activeCalls && controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5),"Preview maintenance resumes with a full interval and no catch-up scan.");

            held.Arm("pointer");Confirm(backup);Pump(()=>held.Entered,"Restore did not enter the actual durable-pointer transaction.");
            Require(main.ProfileBackupBusy && controller.IsSchedulingSuspended,"The actual scheduling lease must span restore writes.");
            clock.Advance(TimeSpan.FromDays(1));Join(controller.PollScheduledChecksAsync());
            var watch=Stopwatch.StartNew();main.SaveCheckpoint();watch.Stop();
            Require(watch.Elapsed<TimeSpan.FromMilliseconds(250) && Calls()==activeCalls,"A held restore mutex must not block the dispatcher or admit an overdue scan.");
            driver.Open();Require(driver.TestWindow is null,"Manual subscription views and mutations stay suspended during restore.");
            held.Release();Pump(()=>!backup.IsBusy,"Restore did not complete after releasing its checkpoint.");Join(controller.PollScheduledChecksAsync());
            var restored=store.Read();var local=restored.Subscriptions.Single(definition=>definition.Id==source.Id);
            var imported=restored.Subscriptions.Single(definition=>definition.Url=="https://backup-ui.invalid/channel");
            Require(ReferenceEquals(main.Subscriptions,controller) && !controller.IsSchedulingSuspended && local.ScheduleEnabled && local.ScheduleIntervalMinutes==5 &&
                !imported.ScheduleEnabled && imported.ScheduleIntervalMinutes==11 && controller.NextScheduledCheckUtc(imported.Id) is null &&
                controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5) && Calls()==activeCalls,
                "Merge preserves existing local consent, imports new schedules off with their interval, and resumes only after the complete apply/checkpoint lease.");
            Fixture("normal",baseline,fresh);clock.Advance(TimeSpan.FromMinutes(5)-TimeSpan.FromSeconds(1));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==activeCalls,"Existing local schedules must wait the entire resumed interval.");
            clock.Advance(TimeSpan.FromSeconds(1));Join(controller.PollScheduledChecksAsync());
            Require(Calls()==activeCalls+1 && store.Read().Items.Single(item=>item.Url==fresh).State=="New" && main.SessionState.Queue.Count==0 &&
                main.SessionState.Coordinator is null && offers.Count==priorOffers && storage.ReadHistory().Count==1,
                "Only the retained local schedule checks after its interval; imports never enqueue, download or create a success.");

            // Manual checks remain an explicit conflicting action, rather than
            // being silently cancelled by profile maintenance.
            driver.Open();Pump(()=>driver.TestSources.Items.Count==2 && !driver.TestIsMutating,"The restored subscription view did not reopen.");
            driver.TestSources.SelectedItem=driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(row=>row.Id==source.Id);
            Fixture("hang",baseline,fresh);File.Delete(childFile);driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>File.Exists(childFile) && driver.TestIsMutating,"A manual check did not own its actual process.");int manualCalls=Calls();
            Click(backup,"OpenProfileBackupButton");Pump(()=>!backup.IsBusy,"The conflicting manual-check guard did not return.");
            Require(!controller.IsSchedulingSuspended && driver.TestIsMutating && Calls()==manualCalls &&
                ((TextBlock)backup.FindName("ProfileBackupStatus")!).Text.Contains("не выполнена",StringComparison.Ordinal),
                "Profile maintenance must reject a manual scan before changing its execution/consent.");
            driver.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump(()=>!driver.TestIsMutating,"The manual cancellation did not join.");

            Fixture("normal",baseline,fresh);Click(backup,"OpenProfileBackupButton");Pump(()=>!backup.IsBusy,"Backup reload did not join.");
            held.Arm("pointer");Confirm(backup);Pump(()=>held.Entered,"Close rollback did not reach the actual profile transaction.");
            var closing=backup;closing.Close();Require(closing.IsVisible && controller.IsSchedulingSuspended,"Closing must retain the scheduling lease until rollback joins.");
            clock.Advance(TimeSpan.FromDays(1));Join(controller.PollScheduledChecksAsync());held.Release();
            Pump(()=>!closing.IsVisible && !main.ProfileBackupBusy,"Closing did not release its joined transaction.");backup=null;Join(controller.PollScheduledChecksAsync());
            Require(ReferenceEquals(main.Subscriptions,controller) && !controller.IsSchedulingSuspended && !service.RecoveryRequired &&
                controller.NextScheduledCheckUtc(source.Id)==clock.GetUtcNow().AddMinutes(5) && Calls()==manualCalls,
                "Cancelled close resumes the same retained scheduler after a full interval without an immediate network request.");
            Require(main.SessionState.Queue.Count==0 && offers.Count==priorOffers,"Every maintenance path retains the explicit queue/Start boundary.");offers.ThrowIfFailed();
        }
        finally
        {
            held.Release();backup?.RequestCancellation();
            if(backup?.IsBusy==true)Join(backup.CancelAndWaitAsync());backup?.Close();
            if(main is not null){Join(main.Subscriptions.StopChecksAsync());main.Dispose();}
            owner.Close();Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousTitleLog);
        }
    }
}
