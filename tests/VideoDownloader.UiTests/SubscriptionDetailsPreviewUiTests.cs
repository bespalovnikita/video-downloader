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
    // Actual inbox selection -> shared metadata process -> two real HTTP PNGs
    // -> right-hand controls. No queue row, Start or main-preview selection.
    internal static void TestSubscriptionDetailsPreview(string testRoot)
    {
        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Subscription details WPF test failed: "+message);
        }
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(12);
            Exception? failure=null;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>
            {
                try { if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false; }
                catch(Exception error) {failure=error;frame.Continue=false;}
            };
            timer.Start();
            try { Dispatcher.PushFrame(frame); }
            finally { timer.Stop(); }
            if(failure!=null)throw new InvalidOperationException(message,failure);
            Require(condition(),message);
        }
        static int Calls(string path)
        {
            try {return File.Exists(path)?File.ReadAllLines(path).Count(line=>!string.IsNullOrWhiteSpace(line)):0;}
            catch(IOException) {return 0;}
        }
        static byte FirstBlue(Image image)
        {
            var source=(BitmapSource)image.Source;
            var decoded=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            byte[] pixels=new byte[decoded.PixelWidth*decoded.PixelHeight*4];
            decoded.CopyPixels(pixels,decoded.PixelWidth*4,0);
            return pixels[0];
        }

        string executable=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(executable),"A compiled native metadata fixture is required");
        string root=Path.Combine(testRoot,"subscription-details-"+Guid.NewGuid().ToString("N"));
        string metadata=Path.Combine(root,"metadata");
        Directory.CreateDirectory(metadata);
        string cookies=Path.Combine(root,"fixture-control.txt");
        File.WriteAllText(cookies,metadata,Encoding.UTF8);
        string calls=Path.Combine(metadata,"calls.jsonl");
        using var firstImage=new PreviewThumbnailServer(24);
        using var secondImage=new PreviewThumbnailServer(144);
        File.WriteAllText(Path.Combine(metadata,"thumbnail-url.txt"),firstImage.Url,Encoding.UTF8);
        File.WriteAllText(Path.Combine(metadata,"thumbnails-urls.json"),
            JsonSerializer.Serialize(new[]{firstImage.Url,secondImage.Url}),Encoding.UTF8);
        string? previousLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",null);
        const string first="https://preview-scheduling.invalid/A";
        const string second="https://preview-scheduling.invalid/B";
        const string third="https://preview-scheduling.invalid/C";
        const string mainSelection="https://outside.invalid/main-preview";
        NativeWindow? subject=null;
        Window? window=null;
        try
        {
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            var store=new SubscriptionStore(Path.GetDirectoryName(storage.SessionPath)!);
            var source=store.Add("https://subscription-details.invalid/collection",false);
            store.ApplyScan(source.Id,new SubscriptionScan("Коллекция превью",new[]{
                new DiscoveredMedia(first,"Снимок A"),new DiscoveredMedia(second,"Снимок B"),new DiscoveredMedia(third,"Снимок C")
            },"",""),[]);
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            subject=new NativeWindow(window,storage,executable);
            window.Show();
            ((TextBox)window.FindName("CookiesBox")!).Text=cookies;
            subject.Preview.SelectedUrl=mainSelection;
            ((TextBlock)window.FindName("PreviewTitle")!).Text="Главное превью остаётся прежним";
            var subscriptions=subject.Subscriptions;
            var driver=new SubscriptionScenarioDriver(subscriptions);
            driver.Open();
            void Bound()=>PumpUntil(()=>driver.TestWindow?.IsVisible==true && driver.TestInbox.Items.Count==3,
                "The actual subscriptions window must show the three saved baseline items");
            Bound();
            driver.TestSources.SelectedItem=driver.TestSources.Items.Cast<SubscriptionController.SubscriptionSourceView>().Single(item=>item.Id==source.Id);
            Bound();
            T Control<T>(string name) where T:class=>driver.TestControl<T>(name);
            TextBlock Title()=>Control<TextBlock>("SubscriptionPreviewTitle");
            TextBox Url()=>Control<TextBox>("SubscriptionPreviewUrlBox");
            Image First()=>Control<Image>("SubscriptionPreviewImage");
            Image Second()=>Control<Image>("SubscriptionPreviewSecondImage");
            SubscriptionController.SubscriptionItemView Row(string url)=>driver.TestInbox.Items.Cast<SubscriptionController.SubscriptionItemView>().Single(item=>item.Url==url);
            void Select(string url)
            {
                driver.TestInbox.SelectedItems.Clear();
                driver.TestInbox.SelectedItem=Row(url);
            }
            void Idle()
            {
                Require(subject.SessionState.Queue.Count==0 && subject.SessionState.Downloads.Count==0 &&
                    subject.SessionState.Coordinator==null && subject.SessionState.RunningTask==null &&
                    subject.SessionState.StartedAt==null && !File.Exists(storage.HistoryPath),
                    "Selecting subscription metadata must not queue, start or record a download");
                Require(subject.Preview.SelectedUrl==mainSelection &&
                    ((TextBlock)window.FindName("PreviewTitle")!).Text=="Главное превью остаётся прежним" &&
                    ((Image)window.FindName("PreviewImage")!).Source==null,
                    "The right-hand panel must not replace the independent main-window preview");
            }
            bool Complete(string url)=>subject.Preview.GetCached(url) is {} data && data.ThumbnailsBase64.Length==2 &&
                data.ThumbnailsBase64.Contains(firstImage.Encoded) && data.ThumbnailsBase64.Contains(secondImage.Encoded);
            void Pictures()
            {
                Require(First().Source is BitmapSource {PixelWidth:PreviewThumbnailServer.Width,PixelHeight:PreviewThumbnailServer.Height} &&
                    Second().Source is BitmapSource {PixelWidth:PreviewThumbnailServer.Width,PixelHeight:PreviewThumbnailServer.Height} &&
                    First().Visibility==Visibility.Visible && Second().Visibility==Visibility.Visible &&
                    new[]{FirstBlue(First()),FirstBlue(Second())}.Order().SequenceEqual(new byte[]{24,144}),
                    "Both distinct real PNGs must be decoded and rendered in the subscription panel");
            }
            void Empty()
            {
                Require(Url().Text.Length==0 && First().Source==null && Second().Source==null &&
                    !Control<Button>("SubscriptionOpenVideoButton").IsEnabled &&
                    !Control<Button>("SubscriptionCopyVideoUrlButton").IsEnabled,
                    "No single selected video must clear its images, URL and actions");
            }

            Empty();
            Require(Calls(calls)==0 && firstImage.Requests==0 && secondImage.Requests==0,
                "Opening the subscriptions list must not fetch the whole old baseline");
            Select(first);
            Require(Title().Text=="Снимок A" && Url().Text==first && Url().IsReadOnly &&
                Control<TextBlock>("SubscriptionPreviewInfo").Text.Contains("Исходный снимок"),
                "The known title, exact copyable URL and saved state must appear before network metadata is ready");
            PumpUntil(()=>File.Exists(Path.Combine(metadata,"started-A")),"Selecting A must start its actual metadata child");
            Select(second);
            Require(Title().Text=="Снимок B" && Url().Text==second && First().Source==null && Second().Source==null,
                "Changing video must immediately clear images from the preceding selection");
            File.WriteAllText(Path.Combine(metadata,"release-A"),"");
            PumpUntil(()=>Complete(first) && File.Exists(Path.Combine(metadata,"started-B")),
                "The shared single worker must finish A before starting the selected B");
            Require(Title().Text=="Снимок B" && Url().Text==second && First().Source==null && Second().Source==null,
                "A late result for A must not overwrite B while its request is still pending");
            File.WriteAllText(Path.Combine(metadata,"release-B"),"");
            PumpUntil(()=>Complete(second) && Title().Text=="Metadata B" && First().Source!=null && Second().Source!=null,
                "B must display its real title and two HTTP thumbnails before Start");
            Pictures();
            Require(Control<TextBlock>("SubscriptionPreviewInfo").Text.Contains("Scheduling fixture") &&
                Control<Button>("SubscriptionOpenVideoButton").IsEnabled &&
                Control<Button>("SubscriptionCopyVideoUrlButton").IsEnabled,
                "Ready metadata must expose uploader information and the selected video actions");
            Control<Button>("SubscriptionCopyVideoUrlButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(Clipboard.GetText()==second,"Copy video URL must copy the exact selected material address");
            Idle();

            Select(first);Pictures();
            Select(second);Pictures();
            Require(Title().Text=="Metadata B" && Calls(calls)==2 && firstImage.Requests==2 && secondImage.Requests==2,
                "Cached reselection must render immediately with no extra metadata process or image request");
            subject.Preview.ReceiveFailure(first,"Stale unrelated thumbnail failure");
            Require(Title().Text=="Metadata B" && Control<TextBlock>("SubscriptionPreviewError").Text.Length==0,
                "An unrelated failure must leave the selected panel unchanged");
            subject.Preview.ReceiveFailure(second,"ERROR: HTTP Error 403: Forbidden");
            Require(Title().Text=="Metadata B" && Url().Text==second && First().Source!=null &&
                Control<TextBlock>("SubscriptionPreviewError").Text.Length>0,
                "A refresh failure must remain visible while preserving the selected video's known title and usable image");

            driver.TestInbox.SelectedItems.Add(Row(first));
            Empty();
            driver.TestInbox.SelectedItems.Clear();Empty();
            Select(third);
            PumpUntil(()=>File.Exists(Path.Combine(metadata,"started-C")),"C must start a request for the close/reopen scenario");
            var previousWindow=driver.TestWindow!;
            previousWindow.Close();
            driver.Open();Bound();
            Require(!ReferenceEquals(previousWindow,driver.TestWindow),"Reopening subscriptions must create a fresh window");
            Empty();
            File.WriteAllText(Path.Combine(metadata,"release-C"),"");
            PumpUntil(()=>Complete(third),"The independent shared worker must finish C after the old subscription window closes");
            Empty();
            Select(third);Pictures();
            Require(Title().Text=="Metadata C" && Calls(calls)==3 && firstImage.Requests==3 && secondImage.Requests==3 &&
                firstImage.Errors.IsEmpty && secondImage.Errors.IsEmpty && !Directory.EnumerateFiles(metadata,"overlap-*").Any(),
                "Reopening and cached selection must neither replay requests nor overlap metadata workers");
            Idle();

            var records=File.ReadAllLines(calls).Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(records.Length==3 && records.Select(record=>record.RootElement.GetProperty("Url").GetString()).SequenceEqual(new[]{first,second,third}) &&
                    records.All(record=>record.RootElement.GetProperty("MetadataOnly").GetBoolean() &&
                        record.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "Exactly three managed metadata-only children must serve all panel selections");
            }
            finally {foreach(var record in records)record.Dispose();}
            Console.WriteLine("PASS: subscription right-panel known metadata, two cold HTTP PNGs, stale-selection guards, cache reuse, failure preservation, multi-selection and close/reopen; 0 downloads");
        }
        finally
        {
            foreach(string name in new[]{"A","B","C"})File.WriteAllText(Path.Combine(metadata,"release-"+name),"");
            subject?.Dispose();window?.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",previousLog);
        }
    }
}
