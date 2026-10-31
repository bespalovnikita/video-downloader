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
    internal static void TestSubscriptionQueueOffer(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Subscription aggregate queue WPF test failed: "+message);
        }
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var deadline=DateTime.UtcNow.AddSeconds(15);
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try { Dispatcher.PushFrame(frame); } finally { timer.Stop(); }
            Require(condition(),message);
        }

        var responder=SubscriptionQueueOfferResponder.Current
            ??throw new InvalidOperationException("The WPF host must own the subscription-offer responder.");
        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"The native metadata/scanner fixture is required");
        string root=Path.Combine(testRoot,"subscription-queue-offer");Directory.CreateDirectory(root);
        string config=Path.Combine(root,"scan.json"),calls=Path.Combine(root,"calls.jsonl");
        string? previousTitleLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        NativeWindow? main=null;
        SubscriptionController? controller=null;
        SubscriptionScenarioDriver? driver=null;
        var queueCalls=new List<(string Id,string Name,string[] Urls)>();
        string acceptance="all";
        try
        {
            main=new NativeWindow(window,storage,executable);window.Show();
            var store=new SubscriptionStore(Path.GetDirectoryName(storage.SessionPath)!);
            SubscriptionDefinition Seed(string key)
            {
                var source=store.Add("https://subscription-scanner.invalid/offer-"+key,false,shortName:key);
                store.ApplyScan(source.Id,new SubscriptionScan("Коллекция "+key,[new(Base(key),"Исходный "+key)],"",""),[]);
                store.ApplyScan(source.Id,new SubscriptionScan("Коллекция "+key,[new(Base(key),"Исходный "+key),new(Old(key),"Пропущенный "+key)],"",""),[]);
                store.Skip(source.Id,[Old(key)]);
                return source;
            }
            static string Base(string key)=>"https://queue-offer.invalid/"+key+"/baseline";
            static string Old(string key)=>"https://queue-offer.invalid/"+key+"/old-skipped";
            static string New(string key,int index)=>"https://queue-offer.invalid/"+key+"/new-"+index;
            var alpha=Seed("Alpha");var beta=Seed("Beta");var failed=Seed("Failed");
            const string failedNew="https://queue-offer.invalid/Failed/unaccepted-new";
            store.ApplyScan(failed.Id,new SubscriptionScan("Коллекция Failed",
                [new(Base("Failed"),"Исходный Failed"),new(Old("Failed"),"Пропущенный Failed"),new(failedNew,"Неуспешный источник")],"",""),[]);
            string[] aNew=Enumerable.Range(1,50).Select(index=>New("Alpha",index)).ToArray();
            string[] bNew=Enumerable.Range(1,50).Select(index=>New("Beta",index)).ToArray();
            var aItems=new List<string>{Base("Alpha"),Old("Alpha")};aItems.AddRange(aNew);
            var bItems=new List<string>{Base("Beta"),Old("Beta")};bItems.AddRange(bNew);
            void Response(bool hangAlpha=false)
            {
                object Entry(string title,string mode,IEnumerable<string> urls)=>new
                {
                    Mode=mode,Response=JsonSerializer.Serialize(new
                    {
                        _type="playlist",title,
                        entries=urls.Select(url=>new{webpage_url=url,title="Название "+url.Split('/').Last()}).ToArray()
                    })
                };
                var responses=new Dictionary<string,object>(StringComparer.Ordinal)
                {
                    [alpha.Url]=Entry("Коллекция Alpha",hangAlpha?"hang":"complete",aItems),
                    [beta.Url]=Entry("Коллекция Beta","complete",bItems),
                    [failed.Url]=Entry("Коллекция Failed","nonzero",[failedNew])
                };
                File.WriteAllText(config,JsonSerializer.Serialize(new{Responses=responses}),Encoding.UTF8);
            }
            int ScanCalls()
            {
                try { return File.Exists(calls)?File.ReadAllLines(calls).Length:0; }
                catch(IOException){return 0;}
            }
            controller=new SubscriptionController(window,store,executable,
                Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml"),storage.ReadHistory,()=>config,
                (urls,id,name,titles)=>
                {
                    var requested=urls.ToArray();
                    Require(titles!=null && requested.All(url=>titles.TryGetValue(url,out var title) && title.Length>0),
                        "Bulk acceptance must preserve each discovered title");
                    Require(name==(id==alpha.Id?"Alpha":"Beta"),"Each source keeps its own destination folder");
                    if(acceptance=="throw-beta" && id==beta.Id)throw new IOException("Intentional later source acceptance failure");
                    if(acceptance=="reject-alpha" && id==alpha.Id)return Array.Empty<string>();
                    queueCalls.Add((id,name,requested));
                    if(acceptance=="close-alpha" && id==alpha.Id)driver!.TestWindow!.Close();
                    return requested;
                },_=>{},_=>{});
            driver=new SubscriptionScenarioDriver(controller);driver.Open();
            PumpUntil(()=>driver.TestWindow!=null && driver.TestSources.Items.Count==3,"Subscription sources must load");
            void CheckAll()
            {
                int before=ScanCalls();
                var button=driver.TestControl<Button>("CheckAllSubscriptionsButton");
                Require(button.IsEnabled,"A previous batch must release its controls before another check");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>ScanCalls()>before && !driver.TestIsMutating,"A complete batch must finish its scan, single modal and queue acknowledgement");
                responder.ThrowIfFailed();
                Require(main.SessionState.Coordinator==null && main.SessionState.RunningTask==null &&
                    main.SessionState.StartedAt==null && main.SessionState.Downloads.Count==0 && !File.Exists(storage.HistoryPath),
                    "Checking and accepting an offer must never start downloading or record success");
            }

            Response();int firstPrompt=responder.Count,firstScans=ScanCalls();
            using(responder.Respond("Yes",dialog=>
            {
                string text=((TextBlock)dialog.FindName("SubscriptionQueueOfferSummary")!).Text;
                Require(text.Contains("100 материалов") && text.Contains("Уникальных ссылок: 100") && text.Contains("Подписок: 2"),
                    "One summary must cover all 100 new materials from both successful sources and exclude the failed source");
                Require(ScanCalls()==firstScans+3 && !driver.TestProgress.IsActive && !driver.TestProgress.IsClockRunning &&
                    !driver.TestProgress.HasTimer && driver.TestIsMutating,
                    "Offer appears once after the whole batch and stopped progress, while UI mutations remain gated");
                Require(((Button)dialog.FindName("SubscriptionQueueOfferNoButton")!).IsDefault,
                    "Declining must be the safe keyboard default");
                driver.TestControl<Button>("CheckAllSubscriptionsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }))CheckAll();
            Require(responder.Count==firstPrompt+1 && ScanCalls()==firstScans+3 && queueCalls.Count==2 &&
                queueCalls[0].Urls.SequenceEqual(aNew) && queueCalls[1].Urls.SequenceEqual(bNew),
                "CheckAll presents exactly one offer, blocks reentrant scans and admits every source in source order");
            var state=store.Read();
            Require(state.Items.Count(item=>item.State=="Queued")==100 && state.Items.Single(item=>item.Url==failedNew).State=="New" &&
                state.Items.Where(item=>item.Url.EndsWith("old-skipped",StringComparison.Ordinal)).All(item=>item.State=="Skipped") &&
                state.Items.Where(item=>item.Url.EndsWith("baseline",StringComparison.Ordinal)).All(item=>item.State=="Baseline"),
                "Only accepted new materials become Queued; baseline, skipped and failed-source New remain intact");

            int noNewPrompts=responder.Count,noNewAccepts=queueCalls.Count;
            using(responder.Respond("Yes"))CheckAll();
            Require(responder.Count==noNewPrompts && queueCalls.Count==noNewAccepts,
                "A batch without eligible New must not offer already queued, baseline, skipped or failed-source materials");

            string aLater=New("Alpha",101),bLater=New("Beta",101);
            aItems.Add(aLater);bItems.Add(bLater);Response();
            int beforePrompts=responder.Count,beforeAccepts=queueCalls.Count;
            using(responder.Respond("No"))CheckAll();
            Require(responder.Count==beforePrompts+1 && queueCalls.Count==beforeAccepts &&
                store.Read().Items.Where(item=>item.Url==aLater || item.Url==bLater).All(item=>item.State=="New"),
                "Declining leaves all offered materials New without queue callbacks");
            using(responder.Respond("Close"))CheckAll();
            Require(responder.Count==beforePrompts+2 && queueCalls.Count==beforeAccepts,
                "Closing the offer is a decline, including previously unaccepted New");

            Response(hangAlpha:true);
            string child=Path.Combine(root,"child.pid");if(File.Exists(child))File.Delete(child);
            var check=driver.TestControl<Button>("CheckAllSubscriptionsButton");
            int cancelledPrompts=responder.Count;
            check.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>File.Exists(child),"The cancellable real scanner child must start");
            driver.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>!driver.TestIsMutating,"A cancelled batch must end without a queue offer");
            Require(responder.Count==cancelledPrompts && queueCalls.Count==beforeAccepts,
                "Cancellation never offers or automatically queues a partially checked batch");

            Response();string unseen=New("Alpha",102);
            using(responder.Respond("Yes",_=>
            {
                store.Skip(alpha.Id,[aLater]);store.MarkSuccessful(bLater);
                store.ApplyScan(alpha.Id,new SubscriptionScan("Коллекция Alpha",
                    aItems.Append(unseen).Select(url=>new DiscoveredMedia(url,"Название "+url.Split('/').Last())).ToArray(),"",""),[]);
            }))CheckAll();
            Require(queueCalls.Count==beforeAccepts && store.Read().Items.Single(item=>item.Url==unseen).State=="New" &&
                store.Read().Items.Single(item=>item.Url==aLater).State=="Skipped" &&
                store.Read().Items.Single(item=>item.Url==bLater).State=="Processed",
                "Modal reentrancy cannot queue a newly skipped/processed row or an unseen row added after the offer");

            aItems.Add(unseen);string bPartial=New("Beta",102);bItems.Add(bPartial);Response();
            acceptance="reject-alpha";
            using(responder.Respond("Yes"))CheckAll();
            Require(store.Read().Items.Single(item=>item.Url==unseen).State=="New" &&
                store.Read().Items.Single(item=>item.Url==bPartial).State=="Queued" && queueCalls.Count==beforeAccepts+1,
                "Only URLs returned by acceptance become Queued; rejected materials remain New");

            string bThrown=New("Beta",103);bItems.Add(bThrown);Response();acceptance="throw-beta";
            using(responder.Respond("Yes"))CheckAll();
            Require(store.Read().Items.Single(item=>item.Url==unseen).State=="Queued" &&
                store.Read().Items.Single(item=>item.Url==bThrown).State=="New" && queueCalls.Count==beforeAccepts+2 &&
                driver.TestControl<TextBlock>("SubscriptionStatusText").Text.Contains("Не удалось подтвердить очередь",StringComparison.Ordinal) &&
                driver.TestControl<TextBlock>("SubscriptionCheckDetailText").Text.Contains("добавлено в очередь: 1",StringComparison.Ordinal),
                "Acceptance of an earlier source is acknowledged durably even when a later source throws");

            string aClosed=New("Alpha",103);aItems.Add(aClosed);Response();acceptance="close-alpha";
            using(responder.Respond("Yes"))CheckAll();
            Require(driver.TestWindow==null && store.Read().Items.Single(item=>item.Url==aClosed).State=="Queued" &&
                store.Read().Items.Single(item=>item.Url==bThrown).State=="New" && queueCalls.Count==beforeAccepts+3,
                "Closing during acceptance stops later callbacks but preserves acknowledgement of already accepted URLs");
            Console.WriteLine("PASS: one real aggregate post-check offer for100new videos, partial scan failure, No/Close/cancel, reentrancy, rejection and durable partial acknowledgement; no downloads");
        }
        finally
        {
            controller?.Dispose();main?.Dispose();window.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousTitleLog);
        }
    }
}
