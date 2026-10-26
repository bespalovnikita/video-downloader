using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Real controls -> production ingress -> PreviewService -> managed native
    // child -> dispatcher -> rendered row. Every URL starts with an empty cache.
    internal static void TestColdTitleIngress(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if(!condition) throw new InvalidOperationException("Cold title ingress WPF test failed: "+error);
        }
        static void PumpUntil(Func<bool> condition,string error)
        {
            if(condition()) return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(20);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            Require(condition(),error);
        }
        static IEnumerable<TextBlock> Labels(DependencyObject parent)
        {
            if(parent is TextBlock text) yield return text;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++)
                foreach(var child in Labels(VisualTreeHelper.GetChild(parent,i)))yield return child;
        }
        string? executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE");
        Require(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable),
            "VD_NATIVE_TITLE_FIXTURE must identify the compiled native test host");
        string root=Path.Combine(testRoot,"cold-title-ingress-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string callsFile=Path.Combine(root,"metadata-calls.jsonl");
        string? oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",callsFile);
        NativeWindow? controller=null;
        Window? window=null;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            Require(!Directory.Exists(storage.PreviewDir) || !Directory.EnumerateFileSystemEntries(storage.PreviewDir).Any(),
                "The profile must start with no preview cache records");
            controller=new NativeWindow(window,storage,executable!);
            window.Show();
            var ui=controller;
            Require(ui.Q.SelectedItem==null && ui.D.SelectedItem==null && ui._previewUrl.Length==0,
                "A new window must not have a selected video");
            int selections=0;
            ui.Q.SelectionChanged+=(_,_)=>selections++;
            ui.D.SelectionChanged+=(_,_)=>selections++;
            string originalPanel=ui.T("PreviewTitle").Text;
            string[] urls=["https://title-ingress.invalid/Video?token=AbC",
                "https://title-ingress.invalid/video?token=AbC",
                "https://title-ingress.invalid/Shortcut?token=XyZ",
                "https://title-ingress.invalid/Subscription?token=AbC"];
            string[] titles=["Название из URL","Название из файла","Название из ярлыка","Название из подписки"];

            void RequireIdle()
            {
                Require(ui._session==null && ui._sessionTask==null && ui._sessionStart==null && ui._downloads.Count==0 &&
                    ui._queue.All(row=>row.Status=="Queued" && row.Path.Length==0 && row.OwnedPaths.Count==0),
                    "Background metadata must leave every row queued and never start a download");
                Require(selections==0 && ui.Q.SelectedItem==null && ui.D.SelectedItem==null && ui._previewUrl.Length==0 &&
                    ui.T("PreviewTitle").Text==originalPanel,"Background metadata must never select a row or replace the preview panel");
            }
            void VerifyRenderedTitle(DownloadItem row,string title)
            {
                foreach(bool showTitle in new[]{false,true})
                {
                    ui.K("ShowVideoTitlesCheck").IsChecked=showTitle;
                    string expected=showTitle?title:row.Url;
                    ui.Q.ScrollIntoView(row);
                    PumpUntil(()=>
                    {
                        ui.Q.UpdateLayout();
                        var column=ui.Q.Columns.OfType<DataGridTemplateColumn>().First();
                        var content=column.GetCellContent(row);
                        return content!=null && Labels(content).Any(label=>label.Text==expected &&
                            string.Equals(label.ToolTip as string,row.Url,StringComparison.Ordinal));
                    },"The actual row binding must render "+expected);
                }
                RequireIdle();
            }
            void VerifyTitle(int index)
            {
                var row=ui._queue.Single(row=>string.Equals(row.Url,urls[index],StringComparison.Ordinal));
                PumpUntil(()=>row.Title==titles[index],"The actual metadata worker must fill the title for "+urls[index]);
                var cached=ui._previews.Get(row.Url);
                Require(cached?.Title==titles[index],"The real worker must persist its metadata result in the preview cache");
                VerifyRenderedTitle(row,titles[index]);
            }

            Require(ui._previews.Get(urls[0])==null,"URL ingress must be a cold cache miss");
            ui.B("UrlInput").Text=urls[0];
            ui.F<Button>("AddUrlButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ui.B("UrlInput").Text.Length==0 && ui._queue.Single().Title.Length==0,
                "The real Add handler must enqueue the URL before supplying a title");
            VerifyTitle(0);

            string list=Path.Combine(root,"Видео [список].txt");
            File.WriteAllText(list,urls[1]+Environment.NewLine,Encoding.UTF8);
            Require(ui._previews.Get(urls[1])==null,"TXT ingress must be a distinct, case-sensitive cold cache miss");
            ui.ReceiveArguments([list]);
            Require(ui._queue.Single(row=>row.Url==urls[1]).Title.Length==0,
                "TXT ingress must not use the list filename as a video title");
            VerifyTitle(1);

            string shortcut=Path.Combine(root,"Видео [ярлык].url");
            File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL="+urls[2]+"\r\n",Encoding.UTF8);
            Require(ui._previews.Get(urls[2])==null,"InternetShortcut ingress must be a cold cache miss");
            ui.ReceiveArguments([shortcut]);
            Require(ui._queue.Single(row=>row.Url==urls[2]).Title.Length==0,
                "InternetShortcut ingress must enqueue its original URL before metadata arrives");
            VerifyTitle(2);

            Require(ui._previews.Get(urls[3])==null,"Subscription ingress must be a cold cache miss");
            var accepted=ui.QueueSubscriptionUrls([urls[3]],"cold-ingress-subscription","123",
                new Dictionary<string,string>(StringComparer.Ordinal){[urls[3]]=titles[3]});
            Require(accepted.SequenceEqual(new[]{urls[3]}) && ui._queue.Single(row=>row.Url==urls[3]).Title==titles[3],
                "Subscription discovery must supply its title immediately, before metadata completes");
            PumpUntil(()=>ui.B("LogBox").Text.Contains("Cold subscription fixture metadata unavailable",StringComparison.Ordinal),
                "The subscription preview must execute its metadata child and observe its real failure");
            Require(ui._queue.Single(row=>row.Url==urls[3]).Title==titles[3] && ui._previews.Get(urls[3])==null,
                "A failed cold metadata request must preserve the immediate subscription title");
            VerifyRenderedTitle(ui._queue.Single(row=>row.Url==urls[3]),titles[3]);

            ui.SaveCheckpoint();
            var saved=storage.Restore()!;
            Require(saved.Queue.Select(row=>row.Url).SequenceEqual(urls,StringComparer.Ordinal) &&
                saved.Queue.Select(row=>row.Title).SequenceEqual(titles,StringComparer.Ordinal),
                "All four exact URLs and their discovered titles must survive checkpoint persistence");
            var calls=File.ReadAllLines(callsFile,Encoding.UTF8)
                .Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(calls.Length==4 && calls.Select(call=>call.RootElement.GetProperty("Url").GetString())
                    .SequenceEqual(urls,StringComparer.Ordinal),"Each exact URL must execute exactly one metadata child, in ingress order");
                Require(calls.All(call=>call.RootElement.GetProperty("MetadataOnly").GetBoolean() &&
                    call.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "All four requests must run in actual metadata-only child processes");
            }
            finally { foreach(var call in calls)call.Dispose(); }
            Require(!File.Exists(storage.HistoryPath),"Metadata prefetch must never record a successful download");
            Console.WriteLine("PASS: native cold title ingress URL, TXT, InternetShortcut, subscription; 4 exact URLs, 4 metadata-only children, 0 downloads, 0 selections");
        }
        finally
        {
            controller?.Dispose();
            window?.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
        }
    }
}
