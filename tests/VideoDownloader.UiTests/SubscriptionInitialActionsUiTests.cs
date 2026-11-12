using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestSubscriptionInitialActions(string testRoot)
    {
        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        RequeueUi.Require(File.Exists(fixture),"The actual scanner fixture must exist.");
        var culture=CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo("ru-RU");
        string? previousLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        try
        {
            string root=Path.Combine(testRoot,"subscription-initial-actions");
            using var ui=new RequeueUi(root,fixture);
            var driver=ui.Driver;
            byte[] history=File.ReadAllBytes(ui.Data.HistoryPath);
            string config=Path.Combine(root,"scan.json");
            ui.Main<TextBox>("CookiesBox").Text=config;
            var combo=driver.TestControl<ComboBox>("SubscriptionInitialActionCombo");
            string[] labels=
            [
                "Добавить все материалы в текущую очередь скачивания",
                "Добавить все материалы в подписку без скачивания в статусе \"пропущено\"",
                "Добавить все материалы в подписку без скачивания в статусе \"готово\""
            ];
            RequeueUi.Require(combo.Items.Cast<ComboBoxItem>().Select(item=>(string)item.Content).SequenceEqual(labels) &&
                combo.SelectedItem is ComboBoxItem {Tag:SubscriptionInitialActions.Skip} && combo.IsEnabled,
                "The real dropdown has exactly the three requested actions and defaults to skipped.");

            void Response(string mode,params string[] urls)=>File.WriteAllText(config,JsonSerializer.Serialize(new
            {
                Mode=mode,Response=JsonSerializer.Serialize(new
                {
                    _type="playlist",title="Начальная подписка",
                    entries=urls.Select(url=>new{webpage_url=url,title=ui.Title(url)}).ToArray()
                })
            }),Encoding.UTF8);
            SubscriptionDefinition Add(string key,string action,string mode,params string[] urls)
            {
                string source="https://subscription-scanner.invalid/initial-"+key;
                foreach(string url in urls)ui.Cache(url);
                Response(mode,urls);
                combo.SelectedItem=combo.Items.Cast<ComboBoxItem>().Single(item=>(string)item.Tag==action);
                driver.TestControl<TextBox>("SubscriptionUrlBox").Text=source;
                driver.TestControl<TextBox>("NewSubscriptionNameBox").Text="Начало "+key;
                driver.TestControl<Button>("AddSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                RequeueUi.Until(()=>!driver.TestIsMutating && ui.Store.Read().Subscriptions.Any(item=>item.Url==source &&
                    (mode=="complete"?item.Initialized:item.LastError.Length>0)),"The actual Add and first complete/failed scan must finish.");
                var added=ui.Store.Read().Subscriptions.Single(item=>item.Url==source);
                RequeueUi.Require(added.InitialAction==action,"The dropdown choice must be captured before asynchronous addition.");
                ui.RequireIdle(history);
                return added;
            }
            void SelectSource(string id)
            {
                driver.Refresh();
                RequeueUi.Until(()=>driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Any(row=>row.Id==id),
                    "The source projection must refresh.");
                driver.TestSources.SelectedItem=driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(row=>row.Id==id);
                RequeueUi.Until(()=>driver.TestInbox.Items.Count==ui.Store.Read().Items.Count(item=>item.SubscriptionId==id),
                    "The selected initial source must bind all persisted rows.");
            }

            string skippedUrl=ui.Url("initial-skipped"),manualUrl=ui.Url("initial-manual"),mixedSkipped=ui.Url("initial-mixed-skipped");
            var skipped=Add("skip",SubscriptionInitialActions.Skip,"complete",skippedUrl);
            RequeueUi.Require(ui.Store.Read().Items.Single(item=>item.SubscriptionId==skipped.Id) is {State:"Skipped",ManuallyCompleted:false} &&
                ui.Subject.SessionState.Queue.Count==0,"Skip initializes a visible subscription without queue admission.");
            var manual=Add("manual",SubscriptionInitialActions.ManualDone,"complete",manualUrl);
            SelectSource(manual.Id);
            var row=driver.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Single();
            driver.TestInbox.ScrollIntoView(row);driver.TestWindow!.UpdateLayout();
            RequeueUi.Require(row.Item is {State:"Skipped",ManuallyCompleted:true,SuccessAttributed:false} && row.StateLabel=="Готово" &&
                driver.TestInbox.Columns[1].GetCellContent(row) is TextBlock state && state.Text=="Готово" &&
                state.ToolTip is string tip && tip.Contains("вручную") && tip.Contains("не скачивался") && ui.Subject.SessionState.Queue.Count==0,
                "The actual ready cell and tooltip must explain the manual no-download designation.");

            // Raw Skipped and manual ready are different visible decisions and
            // must still offer the real mixed-status chooser.
            ui.Store.ApplyScan(manual.Id,new("Начальная подписка",[new(manualUrl,ui.Title(manualUrl)),new(mixedSkipped,ui.Title(mixedSkipped))],"",""),[]);
            ui.Store.Skip(manual.Id,[mixedSkipped]);ui.Cache(mixedSkipped);
            SelectSource(manual.Id);ui.Select(manualUrl,mixedSkipped);
            ui.Queue("Cancel",dialog=>
            {
                string summary=string.Join("\n",((StackPanel)dialog.Content).Children.OfType<TextBlock>().Select(text=>text.Text));
                RequeueUi.Require(summary.Contains("Готово: 1") && summary.Contains("Пропущено: 1"),
                    "The real mixed chooser distinguishes manual ready from skipped.");
            });
            RequeueUi.Require(ui.Store.Read().Items.Single(item=>item.Url==manualUrl).ManuallyCompleted && ui.Subject.SessionState.Queue.Count==0,
                "Cancelling a mixed choice retains the manual designation and queue.");
            ui.Select(manualUrl,mixedSkipped);ui.Queue("All");
            RequeueUi.Require(ui.Store.Read().Items.Where(item=>item.SubscriptionId==manual.Id).All(item=>item.State=="Queued" && !item.ManuallyCompleted),
                "Actual explicit admission resets manual ready only after queue acknowledgement.");
            ui.RequireIdle(history);

            // Retry must retain the originally captured ManualDone choice even
            // if the visible dropdown is changed after a failed enumeration.
            string retryUrl=ui.Url("initial-retry");
            var failed=Add("retry",SubscriptionInitialActions.ManualDone,"nonzero",retryUrl);
            RequeueUi.Require(!failed.Initialized && ui.Store.Read().Items.All(item=>item.SubscriptionId!=failed.Id),
                "A failed first scan cannot commit a partial manual-ready snapshot.");
            combo.SelectedIndex=0;Response("complete",retryUrl);SelectSource(failed.Id);
            driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequeueUi.Until(()=>!driver.TestIsMutating && ui.Store.Read().Items.Any(item=>item.SubscriptionId==failed.Id && item.ManuallyCompleted),
                "A successful retry must use its persisted initial choice instead of the current form input.");

            string queuedUrl=ui.Url("initial-queued");
            int repeats=ui.DuplicateCount,notices=ui.DuplicateNotices;
            ui.HistoryAnswer="CancelButton";ui.ExpectedDuplicateNotices=1;
            var all=Add("all",SubscriptionInitialActions.Queue,"complete",ui.Processed,ui.Duplicate,queuedUrl);
            var pending=ui.Store.Read().Items.Where(item=>item.SubscriptionId==all.Id).ToArray();
            RequeueUi.Require(ui.DuplicateCount==repeats+1 && ui.DuplicateNotices==notices+1 && pending.Length==3 &&
                pending.All(item=>item.InitialQueuePending) && pending.Count(item=>item.State=="AlreadyProcessed")==2 &&
                ui.Subject.SessionState.Queue.Count==2,
                "Queue-all must offer existing history consent, preserve every pending URL on Cancel and retain real successful provenance.");
            ui.HistoryAnswer="YesAllButton";SelectSource(all.Id);
            driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            RequeueUi.Until(()=>!driver.TestIsMutating && ui.Store.Read().Items.Where(item=>item.SubscriptionId==all.Id).All(item=>!item.InitialQueuePending),
                "Retry must acknowledge all accepted URLs, including historical successful rows.");
            RequeueUi.Require(ui.DuplicateCount==repeats+2 && ui.Subject.SessionState.Queue.Count==5 &&
                new[]{ui.Processed,ui.Duplicate,queuedUrl}.All(url=>ui.Subject.SessionState.Queue.Any(item=>item.Url==url)) &&
                ui.Store.Read().Items.Count(item=>item.SubscriptionId==all.Id && item.State=="AlreadyProcessed")==2,
                "Accepting queue-all adds every initial URL while historical subscription states keep their genuine success.");
            ui.RequireIdle(history);
            Console.WriteLine("PASS: actual initial-action dropdown/default, manual-ready tooltip/mixed choice, failed first-scan retry, history cancellation and complete durable queue-all admission without Start");
        }
        finally
        {
            CultureInfo.CurrentUICulture=culture;
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousLog);
        }
    }
}
