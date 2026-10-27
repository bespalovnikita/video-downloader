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
    // Full production scanner -> durable subscription store -> shared real
    // metadata worker. No queue row or Start is needed to populate the cache.
    private static void TestSubscriptionPreviewPrefetch(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if (!condition) throw new InvalidOperationException("Subscription preview WPF test failed: "+error);
        }
        static void PumpUntil(Func<bool> condition,string error)
        {
            if (condition()) return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(15);
            var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
            Exception? failure=null;
            timer.Tick+=(_,_)=>
            {
                try { if (condition() || DateTime.UtcNow>=deadline) frame.Continue=false; }
                catch(Exception exception) {failure=exception;frame.Continue=false;}
            };
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally {timer.Stop();}
            if(failure!=null) throw new InvalidOperationException(error,failure);
            Require(condition(),error);
        }
        static int Calls(string path)
        {
            try { return File.Exists(path)?File.ReadAllLines(path).Count(line=>!string.IsNullOrWhiteSpace(line)):0; }
            catch(IOException) { return 0; } // the fixture can be appending right now
        }

        string? executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE");
        Require(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable),"The native metadata/scanner fixture is required.");
        string root=Path.Combine(testRoot,"subscription-preview-prefetch-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string scanRoot=Path.Combine(root,"scan");
        string metadataRoot=Path.Combine(root,"metadata");
        Directory.CreateDirectory(scanRoot);
        Directory.CreateDirectory(metadataRoot);
        string scanConfig=Path.Combine(scanRoot,"scan.json");
        string metadataCookies=Path.Combine(metadataRoot,"fixture-control.txt");
        File.WriteAllText(metadataCookies,metadataRoot,Encoding.UTF8);
        foreach(string item in new[]{"first","A","B","C"}) File.WriteAllText(Path.Combine(metadataRoot,"release-"+item),"");
        string metadataCalls=Path.Combine(metadataRoot,"calls.jsonl");
        string scanCalls=Path.Combine(scanRoot,"calls.jsonl");
        string? previousTitleLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        NativeWindow? subject=null;
        SubscriptionController? subscriptions=null;
        Window? window=null;
        const string baseline="https://preview-scheduling.invalid/first";
        const string first="https://preview-scheduling.invalid/A";
        const string second="https://preview-scheduling.invalid/B";
        const string imported="https://preview-scheduling.invalid/C";
        var events=new List<string>();
        var logs=new List<string>();
        bool failPrefetch=false;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            subject=new NativeWindow(window,storage,executable!);
            window.Show();
            var ui=subject;
            ((TextBox)window.FindName("CookiesBox")!).Text=metadataCookies;
            var store=new SubscriptionStore(Path.GetDirectoryName(storage.SessionPath)!);
            subscriptions=new SubscriptionController(window,store,executable!,
                Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml"),storage.ReadHistory,()=>scanConfig,
                (urls,id,name,titles)=>
                {
                    var accepted=urls.ToArray();
                    events.AddRange(accepted.Select(url=>"queue:"+url));
                    return ui.QueueSubscriptionUrls(accepted,id,name,titles);
                },_=>{},logs.Add,urls=>
                {
                    var requested=urls.ToArray();
                    events.AddRange(requested.Select(url=>"prefetch:"+url));
                    if (failPrefetch) throw new IOException("Intentional optional preview prefetch failure");
                    ui.Preview.PrefetchUrls(requested);
                },(url,title)=>ui.Preview.Show(url,title));
            var driver=new SubscriptionScenarioDriver(subscriptions);
            var source=store.Add("https://subscription-scanner.invalid/ui-preview",false);

            void Response(string mode,params string[] urls)=>File.WriteAllText(scanConfig,
                JsonSerializer.Serialize(new {Mode=mode,Response=JsonSerializer.Serialize(new {
                    _type="playlist",title="Проверка превью подписки",uploader="Коллекция",
                    entries=urls.Select(url=>new {webpage_url=url,title="Обнаружено "+url.Split('/').Last()}).ToArray()
                })}),Encoding.UTF8);
            void SelectSource(string id)
            {
                driver.Refresh();
                PumpUntil(()=>driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Any(item=>item.Id==id),
                    "The actual subscription grid must show the source.");
                driver.TestSources.SelectedItem=driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(item=>item.Id==id);
            }
            void Check()
            {
                int before=Calls(scanCalls);
                var button=driver.TestControl<Button>("CheckSubscriptionButton");
                Require(button.IsEnabled,"Manual Check must be enabled before the scenario starts.");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>Calls(scanCalls)>before && button.IsEnabled,
                    "The actual manual full scan must finish and release its controls.");
            }
            void Idle()
            {
                Require(ui.SessionState.Coordinator==null && ui.SessionState.RunningTask==null && ui.SessionState.StartedAt==null &&
                    ui.SessionState.Downloads.Count==0 && !File.Exists(storage.HistoryPath),
                    "Scanning and metadata prefetch must not start a download or record success.");
            }

            Response("complete",baseline);
            driver.Open();
            PumpUntil(()=>driver.TestWindow!=null && driver.TestSources.Items.Count==1,"The real subscriptions window must bind its source.");
            SelectSource(source.Id);
            Check();
            Require(store.Read().Items.Single().State=="Baseline" && Calls(metadataCalls)==0 && events.Count==0,
                "The first baseline must not fetch metadata for old items.");

            ui.Preview.SelectedUrl="https://selected.invalid/current";
            ((TextBlock)window.FindName("PreviewTitle")!).Text="Оставить текущее превью";
            Response("complete",baseline,first);
            Check();
            PumpUntil(()=>ui.Preview.GetCached(first)?.Title=="Metadata A","New inbox material must fetch metadata immediately after the scan.");
            Require(ui.SessionState.Queue.Count==0 && Calls(metadataCalls)==1 &&
                store.Read().Items.Single(item=>item.Url==first).State=="New",
                "A cold preview must be ready before the item is accepted into the download queue.");
            Require(ui.Preview.SelectedUrl=="https://selected.invalid/current" &&
                ((TextBlock)window.FindName("PreviewTitle")!).Text=="Оставить текущее превью",
                "Background scan prefetch must not steal the main preview selection.");
            Idle();

            driver.TestInbox.SelectedItem=driver.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Single(item=>item.Url==first);
            Require(ui.Preview.SelectedUrl==first && ((TextBlock)window.FindName("PreviewTitle")!).Text=="Metadata A",
                "Selecting the inbox material must display its prefetched metadata without queueing it.");
            driver.TestControl<Button>("QueueSubscriptionItemsButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>!driver.TestIsMutating && store.Read().Items.Single(item=>item.Url==first).State=="Queued",
                "The explicit Queue action must retain its independent state transition.");
            Require(ui.SessionState.Queue.Count==1 && ui.SessionState.Queue.Single().Url==first && Calls(metadataCalls)==1,
                "Queueing an already prefetched URL must reuse the shared worker/cache.");
            Idle();

            Response("complete",baseline,first,second);
            Check();
            PumpUntil(()=>ui.Preview.GetCached(second)?.Title=="Metadata B","The next complete scan must prefetch a new URL.");
            Require(Calls(metadataCalls)==2 && ui.SessionState.Queue.Count==1 &&
                events.Contains("prefetch:"+first) && events.Contains("prefetch:"+second),
                "Queued and new source items share prefetch, with no duplicate metadata request for the queued URL.");

            failPrefetch=true;
            Check();
            failPrefetch=false;
            Require(store.Read().Subscriptions.Single(item=>item.Id==source.Id).LastError.Length==0 &&
                logs.Any(message=>message.Contains("Intentional optional preview prefetch failure",StringComparison.Ordinal)) &&
                driver.TestControl<TextBlock>("SubscriptionCheckStageText").Text=="Проверка завершена",
                "An optional preview scheduling failure must be logged without turning a durable full scan into failure.");

            // A stdout list accompanied by a nonzero exit is not a complete
            // scan and cannot add items or start their preview acquisition.
            Response("nonzero",baseline,first,second,imported);
            Check();
            Require(store.Read().Subscriptions.Single(item=>item.Id==source.Id).LastError.Length>0 &&
                store.Read().Items.All(item=>item.Url!=imported) && ui.Preview.GetCached(imported)==null && Calls(metadataCalls)==2,
                "A failed partial scan must preserve old results and must not prefetch its uncommitted URLs.");

            Response("hang",baseline,first,second,imported);
            string childPid=Path.Combine(scanRoot,"child.pid");
            if (File.Exists(childPid)) File.Delete(childPid);
            driver.TestControl<Button>("CheckSubscriptionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>File.Exists(childPid),"The cancellation scenario must start its real managed scan child.");
            driver.TestControl<Button>("CancelSubscriptionCheckButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>driver.TestControl<Button>("CheckSubscriptionButton").IsEnabled,
                "Cancellation must stop the incomplete scan and release its controls.");
            Require(store.Read().Items.All(item=>item.Url!=imported) && ui.Preview.GetCached(imported)==null && Calls(metadataCalls)==2,
                "A cancelled scan must not prefetch URLs from its incomplete result.");

            var importSource=store.Add("https://subscription-scanner.invalid/ui-preview-import",true);
            Response("complete",imported);
            SelectSource(importSource.Id);
            Check();
            PumpUntil(()=>ui.Preview.GetCached(imported)?.Title=="Metadata C","Download-existing must also warm previews after its first complete scan.");
            Require(events.IndexOf("prefetch:"+imported)>=0 && events.IndexOf("prefetch:"+imported)<events.IndexOf("queue:"+imported) &&
                Calls(metadataCalls)==3 && ui.SessionState.Queue.Count==2,
                "Initial imports must schedule preview acquisition before ordinary queue acceptance, without starting downloads.");
            Idle();

            var calls=File.ReadAllLines(metadataCalls).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(calls.Select(call=>call.RootElement.GetProperty("Url").GetString()).SequenceEqual(new[]{first,second,imported}) &&
                    calls.All(call=>call.RootElement.GetProperty("MetadataOnly").GetBoolean() &&
                        call.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "Only the three eligible URLs must execute one metadata-only managed child each.");
            }
            finally {foreach(var call in calls)call.Dispose();}
            Console.WriteLine("PASS: subscription full-scan prefetch before queue/Start, baseline and failed-scan exclusion, inbox selection, shared-worker dedup");
        }
        finally
        {
            subscriptions?.Dispose();
            subject?.Dispose();
            window?.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousTitleLog);
        }
    }
}
