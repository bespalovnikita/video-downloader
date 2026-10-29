using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestPortableQueueTransfer(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Portable queue WPF test failed: "+message);
        }
        static void PumpUntil(Func<bool> condition)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var deadline=DateTime.UtcNow.AddSeconds(12);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),"Timed out waiting for an asynchronous import or clipboard export");
        }
        string root=Path.Combine(testRoot,"portable-queue");Directory.CreateDirectory(root);
        var data=new AppPersistence(Path.Combine(root,"profile"));
        string media=Path.Combine(root,"existing.mp4");File.WriteAllText(media,"existing publication sentinel");
        string existingUrl="https://portable.invalid/already",first="https://portable.invalid/watch?v=AbC&Token=%2f",second="https://portable.invalid/watch?v=abc&Token=%2F";
        data.AppendSuccess(existingUrl);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        using var scenario=new UiScenario(window,data,"must-not-launch");
        var session=new DownloadCoordinator("must-not-launch",data,new DownloadOptions{Output=Path.Combine(root,"output")},_=>{},_=>{});
        try
        {
            window.Show();
            var known=new DownloadItem{Url=existingUrl,Status="Completed",State="Done",Progress=100,Path=media,OwnedPaths=[media]};
            scenario.SessionState.TryAdd(known);
            scenario.SessionState.AttachCoordinator(session);
            int historyCount=data.ReadHistory().Count;
            var entries=new[]{new PortableQueueEntry(existingUrl,"Заголовок из переноса"),new PortableQueueEntry(first,"Точное видео 🎬"),new PortableQueueEntry(second,"другой регистр")};
            Clipboard.SetText(PortableQueueCodec.Encode(entries));
            ((Button)window.FindName("PasteClipboardButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>scenario.SessionState.Queue.Count==3);
            var rows=scenario.SessionState.Queue.ToArray();
            Require(rows.Select(row=>row.Url).SequenceEqual(new[]{existingUrl,first,second}),"Paste preserves order and exact URL identity");
            Require(ReferenceEquals(rows[0],known) && known.Status=="Completed" && known.Progress==100 && known.Path==media && known.Title==entries[0].Title,
                "An existing completed row keeps its state and owned publication while an empty title may be enriched");
            Require(rows.Skip(1).All(row=>row.Status=="Queued" && row.DeferredToNextStart && row.Progress==0 && row.OwnedPaths.Count==0 && row.StageDir.Length==0 && row.SubscriptionId.Length==0),
                "Imported rows contain display data only and wait for explicit Start");
            Require(session.Outstanding==0 && data.ReadHistory().Count==historyCount && File.ReadAllText(media)=="existing publication sentinel",
                "Import into an active session never live-admits URLs, writes successes or touches existing files");
            var restored=data.Restore()!;
            Require(restored.Queue.Select(row=>row.Url).SequenceEqual(rows.Select(row=>row.Url)) && restored.Queue[1].DeferredToNextStart,
                "The complete imported queue is checkpointed with its deferred state");

            // Exercise the actual Queue menu action, asynchronous compression and clipboard.
            Clipboard.SetText("not a portable queue");
            var transfer=(Button)window.FindName("PortableQueueButton")!;
            transfer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var copy=transfer.ContextMenu!.Items.OfType<MenuItem>().Single(item=>Equals(item.Header,"Копировать очередь"));
            copy.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            PumpUntil(()=>Clipboard.ContainsText() && PortableQueueCodec.IsPortable(Clipboard.GetText()));
            var exported=PortableQueueCodec.Decode(Clipboard.GetText());
            Require(exported.SequenceEqual(entries),"Copy exports all rows including completed ones with names and order");
            transfer.ContextMenu.IsOpen=false;

            string fileUrl="https://portable.invalid/from-file";
            string file=Path.Combine(root,"queue.vdqueue");
            File.WriteAllText(file,PortableQueueCodec.Encode([new PortableQueueEntry(fileUrl,"Из файла")]),new UTF8Encoding(false));
            scenario.ReceiveArguments([file]);
            PumpUntil(()=>scenario.SessionState.Queue.Count==4);
            Require(scenario.SessionState.Queue.Last().Url==fileUrl && scenario.SessionState.Queue.Last().Title=="Из файла" && session.Outstanding==0,
                "A .vdqueue argument uses the same safe deferred import as clipboard paste");

            string watched="https://portable.invalid/watch-must-not-import";
            ((CheckBox)window.FindName("WatchClipboardCheck")!).IsChecked=true;
            Clipboard.SetText(PortableQueueCodec.Encode([new PortableQueueEntry(watched,"Только явная вставка")]));
            scenario.PollClipboard();
            Require(scenario.SessionState.Queue.All(row=>row.Url!=watched),"Clipboard watching must not import a portable queue automatically");
            Clipboard.SetText("VDQUEUE2:invalid "+watched);
            scenario.PollClipboard();
            Require(scenario.SessionState.Queue.All(row=>row.Url!=watched),"Clipboard watching cannot partially extract URLs from an unsupported portable queue");
            Console.WriteLine("PASS: portable queue WPF paste/copy/file transfer preserves state and waits for Start during active sessions");
        }
        finally
        {
            scenario.SessionState.AttachCoordinator(null);session.Stop();
            session.RunAsync().GetAwaiter().GetResult();session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            scenario.Dispose();window.Close();
        }
    }
}
