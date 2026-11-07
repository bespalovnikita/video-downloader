using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestOrdinaryQueueCapacity(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Queue capacity WPF test failed: "+message);
        }
        string root=Path.Combine(testRoot,"ordinary-capacity");Directory.CreateDirectory(root);
        var data=new AppPersistence(Path.Combine(root,"profile"));
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        using var scenario=new UiScenario(window,data,"must-not-launch");
        var options=new DownloadOptions{Output=Path.Combine(root,"active-output"),WatchClipboard=true};
        var session=new DownloadCoordinator("must-not-launch",data,options,_=>{},_=>{});
        string media=Path.Combine(root,"published.mp4");File.WriteAllText(media,"owned sentinel");
        var known=new DownloadItem{Url="https://capacity.invalid/known",Status="Completed",State="Done",Progress=100,
            Path=media,OwnedPaths=[media],OutputRoot=root,AttemptFinished=true};
        var pending=new DownloadItem{Url="https://capacity.invalid/pending"};
        var rejectionResponder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
        try
        {
            window.Show();Clipboard.SetText("capacity fixture");scenario.ApplyOptions(options);
            scenario.SessionState.AddBatch([known,pending],scenario.ReadOptions());
            Require(session.Add(pending),"The fixture has one accepted active-session job before capacity rejection");
            scenario.SessionState.AttachCoordinator(session);scenario.SessionState.ReplaceUrls([pending.Url]);
            string repeated="https://capacity.invalid/history";data.AppendSuccess(repeated);
            // Leave less than one persisted row, measuring the same escaped
            // JSON representation and longest timestamp as the real preflight.
            int emptyBytes=JsonSerializer.SerializeToUtf8Bytes(new SessionSnapshot{Queue=scenario.SessionState.Queue.ToList(),
                Settings=scenario.ReadOptions(),SavedAtUtc=new DateTime(2000,1,1,0,0,0,DateTimeKind.Utc).AddTicks(1)},AppPersistence.Json).Length;
            known.Title=new string('Ж',(AppPersistence.MaxCheckpointBytes-emptyBytes-96)/6);
            scenario.Reindex();
            Require(!scenario.Subject.CheckpointReadOnly,"The initial near-capacity fixture remains savable");
            byte[] checkpoint=File.ReadAllBytes(data.SessionPath),history=File.ReadAllBytes(data.HistoryPath);
            var urls=scenario.SessionState.Urls.ToArray();int changes=0;
            scenario.SessionState.Queue.CollectionChanged+=(_,_)=>changes++;
            uint thread=GetCurrentThreadId();
            string warning=ErrorMessages.Text("Очередь не добавлена","Queue was not added");
            int warnings=0;
            rejectionResponder.Tick+=(_,_)=>{if(DismissDialog(thread,warning,1))warnings++;};
            rejectionResponder.Start();
            void Refuse(Action action)
            {
                int before=warnings;
                using var watchdog=new CancellationTokenSource();
                _=Task.Delay(TimeSpan.FromSeconds(8),watchdog.Token).ContinueWith(task=>
                {
                    if(task.IsCanceled)return;
                    DismissDialog(thread,warning,1);
                    _=window.Dispatcher.BeginInvoke(()=>
                    {
                        foreach(var dialog in Application.Current.Windows.Cast<Window>().Where(candidate=>candidate.Owner==window &&
                            candidate.FindName("YesButton") is Button).ToArray())dialog.Close();
                    });
                },TaskScheduler.Default);
                try{action();}finally{watchdog.Cancel();}
                Require(warnings>before,"Ordinary ingress must display its capacity refusal");
                Require(changes==0 && scenario.SessionState.Queue.SequenceEqual(new[]{known,pending}) && known.Order==1 && pending.Order==2 &&
                    known.Status=="Completed" && known.OwnedPaths.SequenceEqual(new[]{media}) && File.ReadAllText(media)=="owned sentinel" &&
                    ReferenceEquals(scenario.SessionState.Coordinator,session) && session.Outstanding==1 &&
                    scenario.SessionState.Urls.SequenceEqual(urls) && !scenario.Subject.CheckpointReadOnly &&
                    File.ReadAllBytes(data.SessionPath).SequenceEqual(checkpoint) && File.ReadAllBytes(data.HistoryPath).SequenceEqual(history),
                    "Refusal preserves queue/order/ownership, active ingress, history and checkpoint without entering read-only mode");
            }
            string first="https://capacity.invalid/Video?x=%2f",second="https://capacity.invalid/video?x=%2F";
            Refuse(()=>
            {
                scenario.B("UrlInput").Text=first+Environment.NewLine+second;
                scenario.F<Button>("AddUrlButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Refuse(()=>
            {
                Clipboard.SetText(first+Environment.NewLine+second);
                scenario._ingress.LastClipboard=Clipboard.GetText();
                scenario.F<Button>("PasteClipboardButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            });
            Refuse(()=>{Clipboard.SetText(first+"?watch=1");scenario.PollClipboard();});
            string text=Path.Combine(root,"large.txt");File.WriteAllLines(text,[first,second]);
            Refuse(()=>scenario.ReceiveArguments([text]));
            string shortcut=Path.Combine(root,"single.url");File.WriteAllText(shortcut,"[InternetShortcut]\nURL="+first);
            Refuse(()=>scenario.ReceiveArguments([shortcut]));
            Refuse(()=>scenario.ReceiveArguments(["videodownloader://"+Uri.EscapeDataString(first)]));

            // A history dialog pumps the dispatcher before preflight. A nested
            // failed Add must not mutate the queue or replace the active owner.
            bool nested=false;Exception? failure=null;
            var duplicateResponder=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            duplicateResponder.Tick+=(_,_)=>
            {
                var dialog=Application.Current.Windows.Cast<Window>().LastOrDefault(candidate=>candidate.Owner==window && candidate.FindName("YesButton") is Button);
                if(dialog==null)return;
                duplicateResponder.Stop();
                try{Refuse(()=>scenario.AddUrls([first]));nested=true;}
                catch(Exception error){failure=error;}
                ((Button)dialog.FindName("YesButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            };
            duplicateResponder.Start();
            try{Refuse(()=>scenario.AddUrls([repeated]));}finally{duplicateResponder.Stop();}
            if(failure!=null)throw failure;
            Require(nested,"A real nested duplicate dialog must complete before the final capacity check");

            scenario.SessionState.Forget(known);scenario.Reindex();
            scenario.AddUrls([first,second,first]);
            Require(!scenario.Subject.CheckpointReadOnly && session.Outstanding==3 && scenario.SessionState.Queue.Count==3 &&
                scenario.SessionState.Queue.Skip(1).Select(row=>row.Url).SequenceEqual(new[]{first,second}) &&
                scenario.SessionState.Queue.Skip(1).All(row=>!row.DeferredToNextStart) && data.Restore()!.Queue.Count==3,
                "After freeing space, exact URL variants join the same active session once and save normally");
            Console.WriteLine("PASS: real WPF manual/clipboard/watch/TXT/shortcut/protocol capacity refusal is atomic through nested history dispatch and preserves live Add");
        }
        finally
        {
            rejectionResponder.Stop();
            scenario.SessionState.AttachCoordinator(null);session.Stop();
            session.RunAsync().GetAwaiter().GetResult();session.DisposeAsync().AsTask().GetAwaiter().GetResult();
            scenario.Dispose();window.Close();
        }
    }
}
