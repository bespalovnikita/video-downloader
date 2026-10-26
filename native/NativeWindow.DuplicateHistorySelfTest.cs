using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Exercise the production planner and actual modal WPF buttons. No Start,
    // row selection or preview request is needed to decide a repeat download.
    internal static void TestDuplicateHistoryChoices(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Duplicate history WPF test failed: "+message);
        }
        var storage=new AppPersistence(Path.Combine(testRoot,"duplicate-history-profile"));
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        using var controller=new NativeWindow(window,storage);
        window.Show();
        try
        {
            const string upper="https://example.invalid/duplicate-history/Video?q=a%20b";
            const string lower="https://example.invalid/duplicate-history/video?q=a%20b";
            const string another="https://example.invalid/duplicate-history/another";
            const string fresh="https://example.invalid/duplicate-history/new";
            string[] urls=[fresh,upper,another,lower];
            List<DownloadItem> Rows()=>urls.Select(url=>new DownloadItem{Url=url}).ToList();

            (List<DownloadItem>? Approved,int Prompts) Decide(List<DownloadItem> candidates,bool removeRejected,
                string[] answers,string[] promptedUrls)
            {
                int prompts=0;
                Exception? failure=null;
                var observed=new HashSet<Window>();
                Window? Dialog()=>Application.Current.Windows.Cast<Window>().LastOrDefault(dialog=>
                    dialog.Owner==window && dialog.FindName("DuplicateUrl") is TextBox);
                void Answer(Window dialog,string button)
                {
                    if(button=="Close")dialog.Close();
                    else ((Button)dialog.FindName(button)!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                var watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
                observer.Tick+=(_,_)=>
                {
                    var dialog=Dialog();
                    if(dialog==null || !observed.Add(dialog))return;
                    int position=prompts++;
                    try
                    {
                        Require(position<answers.Length,"Unexpected duplicate prompt.");
                        Require(((TextBox)dialog.FindName("DuplicateUrl")!).Text==promptedUrls[position],
                            "The dialog must show the exact duplicate URL in input order.");
                        Require(((TextBlock)dialog.FindName("CounterLabel")!).Text==
                            $"Previously downloaded URL ({position+1}/2)",
                            "The dialog must report the duplicate position and total.");
                        Require(((TextBlock)dialog.FindName("CompletedAtLabel")!).Text.StartsWith("Last downloaded: ",StringComparison.Ordinal),
                            "Known success dates must reach the dialog.");
                        Answer(dialog,answers[position]);
                    }
                    catch(Exception error)
                    {
                        failure=error;
                        Answer(dialog,"CancelButton");
                    }
                };
                watchdog.Tick+=(_,_)=>
                {
                    watchdog.Stop();
                    failure=new TimeoutException("Duplicate history dialog did not finish.");
                    if(Dialog() is {} dialog)Answer(dialog,"CancelButton");
                };
                observer.Start();watchdog.Start();
                try
                {
                    var approved=controller.ConfirmDuplicates(candidates,removeRejected);
                    if(failure!=null)throw failure;
                    Require(prompts==answers.Length,"Each expected dialog must have been answered exactly once.");
                    return(approved,prompts);
                }
                finally{observer.Stop();watchdog.Stop();}
            }

            // An empty/missing history and an empty/nonmatching selection do not
            // open a dialog. A surprise prompt is cancelled and fails the test.
            foreach(bool emptyFile in new[]{false,true})
            {
                if(emptyFile)File.WriteAllText(storage.HistoryPath,"",new UTF8Encoding(false));
                var all=Rows();
                var result=Decide(all,false,[],[]);
                Require(result.Approved!=null && result.Approved.SequenceEqual(all),
                    "A fresh install must retain every candidate in order without a prompt.");
                Require(Decide([],false,[],[]).Approved is {Count:0},"An empty selection must remain empty.");
            }
            storage.AppendSuccess(upper);storage.AppendSuccess(another);
            byte[] historyBefore=File.ReadAllBytes(storage.HistoryPath);
            foreach(bool removeRejected in new[]{false,true})
            foreach(var scenario in new[]{"yes-no","yes-all","no-all","cancel-after-no","close"})
            {
                var candidates=Rows();
                controller._queue.Clear();
                foreach(var row in candidates)controller._queue.Add(row);
                controller.Reindex();
                byte[] checkpointBefore=File.ReadAllBytes(storage.SessionPath);
                string[] buttons=scenario switch{
                    "yes-no"=>["YesButton","NoButton"],
                    "yes-all"=>["YesAllButton"],
                    "no-all"=>["NoAllButton"],
                    "cancel-after-no"=>["NoButton","CancelButton"],
                    _=>["Close"]
                };
                var result=Decide(candidates,removeRejected,buttons,
                    buttons.Length==2?[upper,another]:[upper]);
                bool cancelled=scenario is "cancel-after-no" or "close";
                string[] accepted=scenario switch{
                    "yes-no"=>[fresh,upper,lower],
                    "yes-all"=>urls,
                    "no-all"=>[fresh,lower],
                    _=>[]
                };
                Require(cancelled?result.Approved==null:
                    result.Approved!=null && result.Approved.Select(row=>row.Url).SequenceEqual(accepted),
                    "Repeat choices must preserve the expected exact URLs: "+scenario);
                string[] expectedQueue=removeRejected && !cancelled?accepted:urls;
                Require(controller._queue.Select(row=>row.Url).SequenceEqual(expectedQueue),
                    "Only completed rejection decisions may remove queue rows: "+scenario);
                Require(storage.Restore()!.Queue.Select(row=>row.Url).SequenceEqual(expectedQueue),
                    "The checkpoint must preserve the committed queue decision: "+scenario);
                Require(candidates.Select(row=>row.Url).SequenceEqual(urls),
                    "The caller's candidate list must remain unchanged.");
                Require(File.ReadAllBytes(storage.HistoryPath).SequenceEqual(historyBefore),
                    "Confirmation must never add, replace or remove successful-history records.");
                if(cancelled)
                    Require(File.ReadAllBytes(storage.SessionPath).SequenceEqual(checkpointBefore),
                        "Cancel must roll back earlier No decisions without changing the checkpoint.");
                Require(controller._session==null && controller._sessionTask==null && controller._downloads.Count==0 &&
                    controller.Q.SelectedItem==null && !Directory.EnumerateFileSystemEntries(storage.PreviewDir).Any(),
                    "Confirmation must not select rows, fetch previews or start a download.");
            }
            var nonmatching=new List<DownloadItem>{new(){Url=lower},new(){Url=fresh}};
            Require(Decide(nonmatching,false,[],[]).Approved!.SequenceEqual(nonmatching),
                "A case-distinct URL and a fresh URL must not be treated as previous downloads.");
            Console.WriteLine("PASS: native duplicate dialogs Yes/No/YesAll/NoAll/Cancel/close, ordinal identity and transactional queue decisions");
        }
        finally{controller.Dispose();window.Close();}
    }
}
