using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
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
        string? oldThumbnail=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL");
        using var thumbnails=new PreviewThumbnailServer();
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",callsFile);
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL",thumbnails.Url);
        UiScenario? controller=null;
        Window? window=null;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            Require(!Directory.Exists(storage.PreviewDir) || !Directory.EnumerateFileSystemEntries(storage.PreviewDir).Any(),
                "The profile must start with no preview cache records");
            controller=new UiScenario(window,storage,executable!);
            window.Show();
            var ui=controller;
            Require(ui.Q.SelectedItem==null && ui.D.SelectedItem==null && ui._preview.SelectedUrl.Length==0,
                "A new window must not have a selected video");
            int selections=0;
            ui.Q.SelectionChanged+=(_,_)=>selections++;
            ui.D.SelectionChanged+=(_,_)=>selections++;
            string originalPanel=ui.T("PreviewTitle").Text;
            string[] urls=["https://title-ingress.invalid/Video?token=AbC",
                "https://title-ingress.invalid/video?token=AbC",
                "https://title-ingress.invalid/Shortcut?token=XyZ",
                "https://title-ingress.invalid/Subscription?token=AbC",
                "https://title-ingress.invalid/SubscriptionPreview?token=AbC"];
            string[] titles=["Название из URL","Название из файла","Название из ярлыка","Название из подписки","Название успешной подписки"];
            int completedImages=0;

            void RequireIdle()
            {
                Require(ui.SessionState.Coordinator==null && ui.SessionState.RunningTask==null && ui.SessionState.StartedAt==null && ui.SessionState.Downloads.Count==0 &&
                    ui.SessionState.Queue.All(row=>row.Status=="Queued" && row.Path.Length==0 && row.OwnedPaths.Count==0),
                    "Background metadata must leave every row queued and never start a download");
                Require(selections==0 && ui.Q.SelectedItem==null && ui.D.SelectedItem==null && ui._preview.SelectedUrl.Length==0 &&
                    ui.T("PreviewTitle").Text==originalPanel && ui.F<Image>("PreviewImage").Source==null,
                    "Background metadata must never select a row or replace the preview panel");
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
                var row=ui.SessionState.Queue.Single(row=>string.Equals(row.Url,urls[index],StringComparison.Ordinal));
                PumpUntil(()=>row.Title==titles[index] && ui._preview.GetCached(row.Url) is { } ready &&
                    ready.ThumbnailBase64==thumbnails.Encoded && ready.ThumbnailsBase64.Length==1,
                    "The actual metadata child and HTTP worker must fetch the title and PNG before selection/Start: "+urls[index]);
                var cached=ui._preview.GetCached(row.Url);
                Require(cached?.Title==titles[index],"The real worker must persist its metadata result in the preview cache");
                Require(thumbnails.Requests==++completedImages && thumbnails.Errors.IsEmpty &&
                    PreviewImage.Decode(Convert.FromBase64String(cached!.ThumbnailBase64)) is {PixelWidth:PreviewThumbnailServer.Width,PixelHeight:PreviewThumbnailServer.Height},
                    "Every cold successful ingress must independently fetch and validate exactly one real HTTP image");
                VerifyRenderedTitle(row,titles[index]);
            }

            Require(ui._preview.GetCached(urls[0])==null,"URL ingress must be a cold cache miss");
            ui.B("UrlInput").Text=urls[0];
            ui.F<Button>("AddUrlButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(ui.B("UrlInput").Text.Length==0 && ui.SessionState.Queue.Single().Title.Length==0,
                "The real Add handler must enqueue the URL before supplying a title");
            VerifyTitle(0);

            string list=Path.Combine(root,"Видео [список].txt");
            File.WriteAllText(list,urls[1]+Environment.NewLine,Encoding.UTF8);
            Require(ui._preview.GetCached(urls[1])==null,"TXT ingress must be a distinct, case-sensitive cold cache miss");
            ui.ReceiveArguments([list]);
            Require(ui.SessionState.Queue.Single(row=>row.Url==urls[1]).Title.Length==0,
                "TXT ingress must not use the list filename as a video title");
            VerifyTitle(1);

            string shortcut=Path.Combine(root,"Видео [ярлык].url");
            File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL="+urls[2]+"\r\n",Encoding.UTF8);
            Require(ui._preview.GetCached(urls[2])==null,"InternetShortcut ingress must be a cold cache miss");
            ui.ReceiveArguments([shortcut]);
            Require(ui.SessionState.Queue.Single(row=>row.Url==urls[2]).Title.Length==0,
                "InternetShortcut ingress must enqueue its original URL before metadata arrives");
            VerifyTitle(2);

            Require(ui._preview.GetCached(urls[3])==null,"Subscription ingress must be a cold cache miss");
            var accepted=ui.QueueSubscriptionUrls([urls[3]],"cold-ingress-subscription","123",
                new Dictionary<string,string>(StringComparer.Ordinal){[urls[3]]=titles[3]});
            Require(accepted.SequenceEqual(new[]{urls[3]}) && ui.SessionState.Queue.Single(row=>row.Url==urls[3]).Title==titles[3],
                "Subscription discovery must supply its title immediately, before metadata completes");
            PumpUntil(()=>ui.B("LogBox").Text.Contains("Cold subscription fixture metadata unavailable",StringComparison.Ordinal),
                "The subscription preview must execute its metadata child and observe its real failure");
            Require(ui.SessionState.Queue.Single(row=>row.Url==urls[3]).Title==titles[3] && ui._preview.GetCached(urls[3])==null,
                "A failed cold metadata request must preserve the immediate subscription title");
            VerifyRenderedTitle(ui.SessionState.Queue.Single(row=>row.Url==urls[3]),titles[3]);
            Require(thumbnails.Requests==3,"Failed metadata must not manufacture or fetch an unrelated thumbnail");

            // Re-accepting an existing queue row must retry a failed cold
            // preview too; it must not depend on selecting that row.
            var failedRow=ui.SessionState.Queue.Single(row=>row.Url==urls[3]);
            File.WriteAllText(Path.Combine(root,"subscription-recovered"),"");
            var recovered=ui.QueueSubscriptionUrls([urls[3]],"cold-ingress-subscription","123",
                new Dictionary<string,string>(StringComparer.Ordinal){[urls[3]]=titles[3]});
            Require(recovered.SequenceEqual(new[]{urls[3]}) && ui.SessionState.Queue.Count==4 &&
                ReferenceEquals(ui.SessionState.Queue.Single(row=>row.Url==urls[3]),failedRow),
                "Subscription re-acceptance must keep the existing row while scheduling its failed preview again");
            VerifyTitle(3);

            Require(ui._preview.GetCached(urls[4])==null,"Successful subscription ingress must also start without cached metadata");
            accepted=ui.QueueSubscriptionUrls([urls[4]],"cold-success-subscription","456",
                new Dictionary<string,string>(StringComparer.Ordinal){[urls[4]]=titles[4]});
            Require(accepted.SequenceEqual(new[]{urls[4]}) && ui.SessionState.Queue.Single(row=>row.Url==urls[4]).Title==titles[4],
                "A fresh subscription must show its discovery title before its independent thumbnail fetch");
            VerifyTitle(4);

            ui.SaveCheckpoint();
            var saved=storage.Restore()!;
            Require(saved.Queue.Select(row=>row.Url).SequenceEqual(urls,StringComparer.Ordinal) &&
                saved.Queue.Select(row=>row.Title).SequenceEqual(titles,StringComparer.Ordinal),
                "All five exact URLs and their discovered titles must survive checkpoint persistence");

            // Only after every cold result is ready may selection render the
            // cached image. Neither selection nor Start was used to obtain it.
            foreach(int index in new[]{0,4})
            {
                ui.Q.SelectedItem=ui.SessionState.Queue.Single(row=>row.Url==urls[index]);
                PumpUntil(()=>ui._preview.SelectedUrl==urls[index] &&
                    ui.F<Image>("PreviewImage").Source is BitmapSource {PixelWidth:PreviewThumbnailServer.Width,PixelHeight:PreviewThumbnailServer.Height},
                    "Selecting a prefetched URL/subscription must render its actual cached PNG");
            }
            var calls=File.ReadAllLines(callsFile,Encoding.UTF8)
                .Where(line=>!string.IsNullOrWhiteSpace(line)).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(calls.Length==6 && calls.Select(call=>call.RootElement.GetProperty("Url").GetString())
                    .SequenceEqual(new[]{urls[0],urls[1],urls[2],urls[3],urls[3],urls[4]},StringComparer.Ordinal),
                    "Five URLs and one explicit failed-preview recovery must execute six children; cached selection must not repeat metadata");
                Require(calls.All(call=>call.RootElement.GetProperty("MetadataOnly").GetBoolean() &&
                    call.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "All six requests must run in actual metadata-only child processes");
            }
            finally { foreach(var call in calls)call.Dispose(); }
            Require(thumbnails.Requests==5 && thumbnails.Errors.IsEmpty && !File.Exists(storage.HistoryPath) &&
                ui.SessionState.Coordinator==null && ui.SessionState.Downloads.Count==0,
                "Cached display must not repeat HTTP acquisition, start downloads or record success");
            Console.WriteLine("PASS: cold URL/TXT/InternetShortcut/subscription title+HTTP PNG before selection/Start; existing subscription preview recovery; cached WPF images; 6 metadata-only children, 5 HTTP images, 0 downloads");
        }
        finally
        {
            controller?.Dispose();
            window?.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_THUMBNAIL",oldThumbnail);
        }
    }
}
