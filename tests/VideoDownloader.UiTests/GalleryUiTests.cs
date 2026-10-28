using System.Windows;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestPreviewGallery(string testRoot)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Preview gallery WPF test failed: "+error);
        }
        static string Picture(byte value)
        {
            byte[] pixels=[value,30,90,255,value,30,90,255,value,30,90,255,value,30,90,255];
            var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,pixels,8);
            var encoder=new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream=new MemoryStream();encoder.Save(stream);
            return Convert.ToBase64String(stream.ToArray());
        }
        static void Pump(TimeSpan duration,Func<bool>? stop=null)
        {
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow+duration;
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if((stop?.Invoke()??false) || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
        }
        static void Hover(FrameworkElement host,bool enter)=>host.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice,0)
            {RoutedEvent=enter?Mouse.MouseEnterEvent:Mouse.MouseLeaveEvent});
        static MouseWheelEventArgs Wheel(FrameworkElement host,int delta)
        {
            var args=new MouseWheelEventArgs(Mouse.PrimaryDevice,0,delta){RoutedEvent=Mouse.PreviewMouseWheelEvent};
            host.RaiseEvent(args);return args;
        }

        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        NativeWindow? subject=null;
        const string firstUrl="https://example.invalid/gallery-first";
        const string secondUrl="https://example.invalid/gallery-second";
        string[] images=Enumerable.Range(1,16).Select(value=>Picture((byte)value)).ToArray();
        PreviewData Data(string url,string title,string[] gallery)=>new(url,title,"Автор","",30,"fixture",1080,30,"H264","",
            gallery.FirstOrDefault()??"",DateTime.UtcNow){ThumbnailsBase64=gallery};
        try
        {
            subject=new NativeWindow(window,new AppPersistence(Path.Combine(testRoot,"preview-gallery")),"must-not-launch");
            window.Show();
            window.UpdateLayout();
            var preview=subject.Preview;
            var host=(FrameworkElement)window.FindName("PreviewImageHost")!;
            var image=(Image)window.FindName("PreviewImage")!;
            var count=(TextBlock)window.FindName("PreviewImageCounter")!;
            var previous=(Button)window.FindName("PreviewPreviousButton")!;
            var next=(Button)window.FindName("PreviewNextButton")!;
            var title=(TextBlock)window.FindName("PreviewTitle")!;
            Hover(host,false);

            preview.SelectedUrl=firstUrl;
            preview.ReceiveReady(Data(firstUrl,"Название галереи",images));
            Require(count.Text=="1 / 15" && image.Source!=null && previous.IsEnabled && next.IsEnabled,
                "The actual preview controls must display a bounded 15-image gallery.");
            var firstSource=image.Source;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(count.Text=="2 / 15" && image.Source!=firstSource,"Next must replace the displayed bitmap.");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(count.Text=="1 / 15" && image.Source==firstSource,"Previous must reuse the already decoded displayed image.");
            previous.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(count.Text=="15 / 15","Backward navigation must wrap to the last image.");
            Hover(host,true);
            for(int update=0;update<5 && count.Text=="15 / 15";update++)
            {
                Pump(TimeSpan.FromMilliseconds(300),()=>count.Text=="1 / 15");
                preview.ReceiveReady(Data(firstUrl,"Название галереи",images));
            }
            Require(count.Text=="1 / 15",
                "Hovering must automatically wrap even while incremental metadata/gallery updates arrive.");
            Hover(host,false);
            Pump(TimeSpan.FromMilliseconds(1150));
            Require(count.Text=="1 / 15","Leaving the image must pause automatic navigation.");
            Require(Wheel(host,-120).Handled && count.Text=="2 / 15","The wheel must move forward and consume the gallery event.");
            Require(Wheel(host,120).Handled && count.Text=="1 / 15","The wheel must move backward.");
            Hover(host,true);
            Require(Wheel(host,-120).Handled && count.Text=="2 / 15","Wheel navigation must work while automatic hover is active.");
            Pump(TimeSpan.FromMilliseconds(300));
            Require(count.Text=="2 / 15","Manual navigation must restart the hover interval rather than immediately skip another image.");
            Hover(host,false);

            preview.ReceiveReady(Data(firstUrl,"Название галереи обновлено",images.Take(10).ToArray()));
            Require(count.Text=="2 / 10" && title.Text=="Название галереи обновлено",
                "Progressive data for the selected URL must retain the current gallery image.");
            preview.ReceiveReady(Data(secondUrl,"Другой URL",images.Take(2).ToArray()));
            preview.ReceiveFailure(secondUrl,"Stale failure");
            Require(count.Text=="2 / 10" && title.Text=="Название галереи обновлено",
                "Ready/failure events for a different exact URL must leave the visible gallery alone.");

            preview.SelectedUrl=secondUrl;
            Require(image.Source==null && !previous.IsEnabled && !next.IsEnabled,
                "Changing URL must immediately clear old images and stop the timer.");
            preview.ReceiveReady(new PreviewData(secondUrl,"Старый кеш","","",0,"",0,0,"","",images[0],DateTime.UtcNow));
            Require(count.Text=="1 / 1" && image.Source!=null && !previous.IsEnabled && !next.IsEnabled,
                "Legacy one-image cache records must still render without enabling a gallery.");
            Require(!Wheel(host,-120).Handled,"A single image must leave ordinary scrolling available.");
            preview.ReceiveReady(Data(secondUrl,"Повреждённый первый кандидат",["not-base64",images[1],images[2]]));
            Require(count.Text=="1 / 2" && image.Source!=null,
                "An undecodable image must be skipped while usable gallery images remain available.");
            Hover(host,true);
            preview.ReceiveFailure(secondUrl,"ERROR: HTTP Error 403: Forbidden");
            Require(image.Source!=null && next.IsEnabled && previous.IsEnabled &&
                    title.Text=="Повреждённый первый кандидат" &&
                    ((TextBlock)window.FindName("PreviewMeta")!).ToolTip as string=="ERROR: HTTP Error 403: Forbidden",
                "A failed gallery refresh must keep valid images and the known title, with the original diagnostic available.");
            preview.SelectedUrl=secondUrl+"/cold-failure";
            preview.ReceiveFailure(preview.SelectedUrl,"ERROR: HTTP Error 404: Not Found");
            Require(image.Source==null && !next.IsEnabled && !previous.IsEnabled,
                "A cold failure must leave the gallery empty and disable navigation.");
            Pump(TimeSpan.FromMilliseconds(1000));
            Require(image.Source==null,"A timer must not restore images after a failure.");

            preview.SelectedUrl=secondUrl;
            preview.ReceiveReady(Data(secondUrl,"После повтора",images.Take(3).ToArray()));
            Hover(host,true);
            window.Hide();
            string hiddenCount=count.Text;
            Pump(TimeSpan.FromMilliseconds(1000));
            Require(count.Text==hiddenCount,"Hiding the preview window must stop automatic navigation.");
            window.Show();
            Hover(host,true);
            preview.Dispose();
            var disposedImage=image.Source;
            string disposedCount=count.Text;
            next.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Wheel(host,-120);
            Hover(host,true);
            preview.ReceiveReady(Data(secondUrl,"Запоздалое обновление",images.Take(2).ToArray()));
            Pump(TimeSpan.FromMilliseconds(1000));
            Require(image.Source==disposedImage && count.Text==disposedCount,
                "Disposal must detach hover, wheel, buttons, timer and late service events.");
            Console.WriteLine("PASS: preview gallery hover, wheel, controls, progressive updates, selection, legacy images, failures and disposal");
        }
        finally
        {
            subject?.Dispose();
            window.Close();
        }
        TestGalleryCacheAndKnownTitle(testRoot,images.Take(15).ToArray());
    }

    private static void TestGalleryCacheAndKnownTitle(string testRoot,string[] images)
    {
        static void Require(bool condition,string error)
        {
            if(!condition)throw new InvalidOperationException("Preview cache WPF test failed: "+error);
        }
        static void PumpUntil(Func<bool> complete)
        {
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(complete() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            Require(complete(),"The shared worker must load a valid disk gallery without a metadata process.");
        }
        const string cachedUrl="https://example.invalid/gallery-disk-cache";
        const string discoveredUrl="https://example.invalid/gallery-discovery-title";
        var storage=new AppPersistence(Path.Combine(testRoot,"gallery-cache-title"));
        Directory.CreateDirectory(storage.PreviewDir);
        var saved=new PreviewData(cachedUrl,"Название из дискового кеша","","",1,"",0,0,"","",images[0],DateTime.UtcNow)
            {ThumbnailsBase64=images};
        void WriteCache(PreviewData data)
        {
            string cacheName=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(data.Url))).ToLowerInvariant()+".json";
            File.WriteAllText(Path.Combine(storage.PreviewDir,cacheName),JsonSerializer.Serialize(new{
                CacheVersion=1,PreviewFetchVersion=3,Success=true,ThumbnailError="",data.Url,data.Title,data.Uploader,
                data.PlaylistTitle,data.Duration,data.Extractor,data.MaxHeight,data.MaxFps,data.Codecs,data.DynamicRange,
                data.ThumbnailBase64,data.ThumbnailsBase64,data.SavedAtUtc
            },AppPersistence.Json));
        }
        WriteCache(saved);
        WriteCache(saved with {Url=discoveredUrl,Title="",ThumbnailBase64="",ThumbnailsBase64=[]});
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var rows=new[]{new DownloadItem{Url=cachedUrl}};
        int scans=0,saves=0;
        using var preview=new PreviewController(window,storage,"must-not-launch",()=>{scans++;return rows;},()=>saves++,_=>{});
        try
        {
            window.Show();
            var title=(TextBlock)window.FindName("PreviewTitle")!;
            var count=(TextBlock)window.FindName("PreviewImageCounter")!;
            Require(preview.GetCached(cachedUrl)==null,
                "UI cache inspection must remain memory-only even when a valid gallery exists on disk.");
            preview.Show(cachedUrl,"Название из обнаружения");
            Require(title.Text=="Название из обнаружения" && count.Text!= "1 / 15",
                "Show must return with discovery metadata before the worker loads and decodes the disk cache.");
            bool heartbeat=false;
            _=window.Dispatcher.BeginInvoke(()=>heartbeat=true);
            PumpUntil(()=>heartbeat && count.Text=="1 / 15" && title.Text==saved.Title);
            Require(rows[0].Title==saved.Title && saves==1,
                "The worker's cached title must enrich the existing row and checkpoint it once.");
            int previousScans=scans,previousSaves=saves;
            for(int update=0;update<15;update++)preview.ReceiveReady(saved);
            Require(scans==previousScans && saves==previousSaves,
                "Image-only updates with the same URL/title must neither rescan the queue nor save another checkpoint.");

            preview.Show(discoveredUrl,"Название из подписки до очереди");
            preview.ReceiveReady(new PreviewData(discoveredUrl,"","","",0,"",0,0,"","","",DateTime.UtcNow));
            Require(title.Text=="Название из подписки до очереди",
                "An inbox discovery title must survive empty metadata before the URL joins the ordinary queue.");
            preview.ReceiveFailure(discoveredUrl,"ERROR: HTTP Error 403: Forbidden");
            Require(title.Text=="Название из подписки до очереди" && saves==1,
                "A failed inbox preview must retain its discovery title without checkpointing a nonqueued URL.");
            Console.WriteLine("PASS: disk gallery loads on worker, UI remains responsive, repeated image updates avoid queue scans, and inbox titles survive failures");
        }
        finally
        {
            preview.Dispose();
            window.Close();
        }
    }
}
