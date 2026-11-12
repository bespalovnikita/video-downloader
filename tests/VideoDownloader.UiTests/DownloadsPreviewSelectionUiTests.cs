using System.Diagnostics;
using System.IO;
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
    private static void TestDownloadsPreviewSelection(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Downloads preview WPF test: "+message);}
        static void Until(Func<bool> ready,string message)
        {
            if(ready())return;
            var watch=Stopwatch.StartNew();var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(10)};
            timer.Tick+=(_,_)=>{if(ready() || watch.Elapsed>TimeSpan.FromSeconds(5))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(ready(),message);
        }
        static void Drain()
        {
            var operation=Dispatcher.CurrentDispatcher.InvokeAsync(()=>{},DispatcherPriority.ApplicationIdle);
            Until(()=>operation.Task.IsCompleted,"The actual view changes must finish.");
            operation.Task.GetAwaiter().GetResult();
        }
        static string ImageBytes(byte blue)
        {
            byte[] pixels=Enumerable.Range(0,16).SelectMany(_=>new byte[]{blue,96,208,255}).ToArray();
            var bitmap=BitmapSource.Create(4,4,96,96,PixelFormats.Bgra32,null,pixels,16);
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var output=new MemoryStream();encoder.Save(output);return Convert.ToBase64String(output.ToArray());
        }
        static bool HasColor(Image image,byte blue)
        {
            if(image.Source is not BitmapSource source)return false;
            var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);var pixel=new byte[4];
            converted.CopyPixels(new Int32Rect(0,0,1,1),pixel,4,0);
            return pixel[0]==blue && pixel[1]==96 && pixel[2]==208;
        }

        string root=Path.Combine(testRoot,"downloads-preview-selection");Directory.CreateDirectory(root);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var window=(Window)XamlReader.Load(reader);
        NativeWindow? subject=null;
        try
        {
            subject=new NativeWindow(window,storage,"must-not-launch",automaticUpdates:false,automaticSubscriptionChecks:false);
            window.Show();window.UpdateLayout();
            var queue=(DataGrid)window.FindName("QueueGrid")!;
            var downloads=(DataGrid)window.FindName("DownloadGrid")!;
            var image=(Image)window.FindName("PreviewImage")!;
            var formats=(Button)window.FindName("MediaSelectionButton")!;
            var queued=new DownloadItem{Url="https://downloads-preview.invalid/queue",Title="В очереди"};
            var finished=new DownloadItem{Url="https://downloads-preview.invalid/download",Title="Скачанное видео",Status="Completed",AttemptFinished=true};
            var other=new DownloadItem{Url="https://downloads-preview.invalid/other",Title="Другая загрузка",Status="Completed",AttemptFinished=true};
            DownloadItem[] rows=[queued,finished,other];
            foreach(var (row,blue) in new[]{(queued,(byte)24),(finished,(byte)196),(other,(byte)88)})
            {
                SeedTitlePreviewCache(storage,new PreviewData(row.Url,row.Title,"","",1,"fixture",4,1,"","",ImageBytes(blue),DateTime.UtcNow));
                Require(subject.SessionState.TryAdd(row),"Every exact fixture URL must enter the normal queue.");
                subject.Preview.RequestRow(row);
            }
            subject.SessionState.Downloads.Add(finished);subject.SessionState.Downloads.Add(other);subject.Reindex();
            Until(()=>rows.All(row=>subject.Preview.GetCached(row.Url)?.ThumbnailBase64.Length>0),
                "The real worker must validate the disk PNGs before these presentation-only checks.");
            subject.SaveCheckpoint();
            string checkpoint=File.ReadAllText(storage.SessionPath);

            queue.SelectedItem=queued;
            Require(subject.Preview.SelectedUrl==queued.Url && HasColor(image,24),"Queue selection must retain its existing rendered preview.");
            downloads.SelectedItem=finished;
            Require(subject.Preview.SelectedUrl==finished.Url && HasColor(image,196) && ReferenceEquals(queue.SelectedItem,queued) && !formats.IsEnabled,
                "Selecting one Downloads row must show its own image without changing the queue selection.");
            formats.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(subject.MediaSelectionEditor is null,"A direct action must not edit another queue row while a Downloads preview is shown.");
            window.Activate();queue.Focus();
            Until(()=>queue.IsKeyboardFocusWithin && subject.Preview.SelectedUrl==queued.Url,
                "Returning to the already selected queue row must deliver real WPF focus and preview.");
            Require(HasColor(image,24) && formats.IsEnabled,
                "Returning to the queue must restore its selected image and the matching format action.");
            downloads.Focus();
            Until(()=>downloads.IsKeyboardFocusWithin && subject.Preview.SelectedUrl==finished.Url,
                "Returning to the already selected download must deliver real WPF focus and preview.");
            Require(HasColor(image,196) && !formats.IsEnabled,
                "Returning to Downloads must restore its selected image even without a selection change.");
            downloads.SelectedItems.Add(other);
            Require(subject.Preview.SelectedUrl==finished.Url && HasColor(image,196),"Multiselection must not replace the current preview arbitrarily.");
            downloads.SelectedItems.Clear();
            Require(subject.Preview.SelectedUrl==finished.Url && HasColor(image,196),"Clearing Downloads selection must preserve the current preview.");

            downloads.SelectedItem=finished;
            queue.Focus();
            Until(()=>queue.IsKeyboardFocusWithin && subject.Preview.SelectedUrl==queued.Url,
                "The background-sort check must start with the queue owning actual keyboard focus and preview.");
            finished.Status="Running";Drain();
            Require(downloads.Items[0]==finished && ReferenceEquals(downloads.SelectedItem,finished) &&
                subject.Preview.SelectedUrl==queued.Url && HasColor(image,24),
                "Background active-first sorting must not steal the chosen queue preview or change Downloads selection.");
            finished.Status="Completed";Drain();
            var late=new DownloadItem{Url="https://downloads-preview.invalid/late",Status="Starting"};
            subject.SessionState.Downloads.Add(late);Drain();
            Require(downloads.Items[0]==late && subject.Preview.SelectedUrl==queued.Url && HasColor(image,24),
                "A newly active download must not take over the current preview.");
            downloads.SelectedItem=other;
            Require(subject.Preview.SelectedUrl==other.Url && HasColor(image,88),"A later explicit Downloads selection must still update the image.");
            Require(subject.SessionState.Queue.SequenceEqual(rows) && rows.Select(row=>row.Order).SequenceEqual(new[]{1,2,3}) &&
                subject.SessionState.Coordinator is null && subject.SessionState.StartedAt is null && !File.Exists(storage.HistoryPath) &&
                File.ReadAllText(storage.SessionPath)==checkpoint,
                "Preview navigation must preserve queue order, checkpoint, successful history and the explicit Start boundary.");
        }
        finally{subject?.Dispose();window.Close();}
    }
}
