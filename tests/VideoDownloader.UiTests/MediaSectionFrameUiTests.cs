using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Color=System.Windows.Media.Color;
using Point=System.Windows.Point;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestMediaSectionFrames(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Section-frame WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var deadline=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || deadline.Elapsed>TimeSpan.FromSeconds(6))frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static void PumpFor(int milliseconds)
        {
            var watch=Stopwatch.StartNew();
            PumpUntil(()=>watch.ElapsedMilliseconds>=milliseconds,"The dispatcher did not process the observation interval.");
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual section-frame control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static Color? Pixel(Image image)
        {
            if(image.Source is not BitmapSource source)return null;
            var converted=new FormatConvertedBitmap(source,PixelFormats.Bgra32,null,0);
            byte[] pixels=new byte[4];
            converted.CopyPixels(new Int32Rect(0,0,1,1),pixels,4,0);
            return Color.FromArgb(pixels[3],pixels[2],pixels[1],pixels[0]);
        }

        byte[] red=SectionFramePng(Colors.Red),blue=SectionFramePng(Colors.Blue),green=SectionFramePng(Colors.Lime);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var owner=(Window)XamlReader.Load(reader);
        var row=new DownloadItem{Url="https://frame-ui.invalid/Video?token=AbC",Title="Selected source"};
        var other=new DownloadItem{Url="https://frame-ui.invalid/video?token=AbC",Title="Other source"};
        var rows=new ObservableCollection<DownloadItem>{row,other};
        Control<DataGrid>(owner,"QueueGrid").ItemsSource=rows;
        var formats=new[]{
            new MediaFormat("137","mp4",640,360,25,"avc1","none","",1000,false),
            new MediaFormat("18","mp4",320,180,25,"avc1","mp4a","en",900,false),
            new MediaFormat("140","m4a",0,0,0,"none","mp4a","en",200,false)};
        var metadata=new PreviewData(row.Url,row.Title,"fixture","",12,"fixture",360,25,"H264","SDR","",DateTime.UtcNow)
            {FormatsLoaded=true,Formats=formats};
        var options=new DownloadOptions();
        var probes=new List<SectionFrameProbe>();
        MediaSelectionDialog? dialog=null;
        int applyCalls=0;
        MediaSelectionDialog Open(SectionFrameProbe probe,bool withMetadata=true)
        {
            probes.Add(probe);
            var result=new MediaSelectionDialog(owner,row,()=>options,selection=>
            {row.Selection=selection;applyCalls++;return null;},probe.RequestAsync);
            result.Show();
            if(withMetadata)result.ReceiveMetadata(metadata);
            return result;
        }
        try
        {
            owner.Show();owner.Width=1280;owner.Height=760;owner.UpdateLayout();
            var probe=new SectionFrameProbe();dialog=Open(probe);
            var start=Control<TextBox>(dialog,"MediaSectionStartBox");
            var end=Control<TextBox>(dialog,"MediaSectionEndBox");
            var firstImage=Control<Image>(dialog,"MediaSectionStartFrame");
            var lastImage=Control<Image>(dialog,"MediaSectionEndFrame");
            var firstStatus=Control<TextBlock>(dialog,"MediaSectionStartFrameStatus");
            var lastStatus=Control<TextBlock>(dialog,"MediaSectionEndFrameStatus");
            Require(firstImage.Source==null && lastImage.Source==null && firstStatus.Text.Length>0 && lastStatus.Text.Length>0,
                "Both actual frame cards must start with a useful empty state.");
            start.BringIntoView();dialog.UpdateLayout();
            start.Text="1";start.Text="2";start.Text="00:00:03";
            Require(probe.Calls.Length==0,"Typing must not synchronously start a frame request on the UI thread.");
            PumpUntil(()=>probe.Calls.Length==1,"A valid start must request a frame even with an empty end.");
            var first=probe.Calls[0];
            Require(first.Request.Url==row.Url && first.Request.Seconds==3 && !first.Request.IsEnd && first.Request.DurationSeconds==12 && first.Request.FrameRate==25,
                "Debounced input must retain only the latest timestamp and the exact captured URL.");
            first.Success(red);
            PumpUntil(()=>Pixel(firstImage)==Colors.Red,"The start card must render the decoded red PNG pixels.");
            Require(firstImage.Source!.IsFrozen && firstStatus.Text.Contains(DownloadSection.FormatTimestamp(3),StringComparison.Ordinal),
                "The real Image must own a frozen bitmap with a visible source-time caption.");
            end.Text="00:00:12";
            PumpUntil(()=>probe.Calls.Length==2,"The end field must independently request its boundary frame.");
            var last=probe.Calls[1];
            Require(last.Request.IsEnd && last.Request.Seconds==12 && last.Request.DurationSeconds==12,
                "The source-end request must retain the exclusive-end and known-duration context.");
            last.Success(blue,11.96);
            PumpUntil(()=>Pixel(lastImage)==Colors.Blue,"The end card must display a different decoded frame.");
            Require(Pixel(firstImage)==Colors.Red && lastStatus.Text.Contains(DownloadSection.FormatTimestamp(11.96),StringComparison.Ordinal),
                "The final-frame caption must describe the sampled position just before the end, without replacing the start.");
            // A WPF Image without a Source can measure to zero width. Check
            // usable picture geometry after both decoded sources are assigned.
            firstImage.BringIntoView();lastImage.BringIntoView();dialog.UpdateLayout();
            double firstX=firstImage.TranslatePoint(new Point(),dialog).X;
            double lastX=lastImage.TranslatePoint(new Point(),dialog).X;
            string firstGeometry=FormattableString.Invariant($"start W={firstImage.ActualWidth:0.###},H={firstImage.ActualHeight:0.###},X={firstX:0.###}");
            string lastGeometry=FormattableString.Invariant($"end W={lastImage.ActualWidth:0.###},H={lastImage.ActualHeight:0.###},X={lastX:0.###}");
            Require(firstImage.ActualWidth>30 && lastImage.ActualWidth>30 && firstImage.ActualHeight>=100 && lastImage.ActualHeight>=100 && firstX<lastX,
                $"Both frame images must occupy distinct, usable side-by-side cards in the rendered dialog. {firstGeometry}; {lastGeometry}.");

            start.Text="5";
            Require(firstImage.Source==null,"Editing time must immediately remove the old picture.");
            PumpUntil(()=>probe.Calls.Length==3,"The changed start timestamp did not start its frame request.");
            var stale=probe.Calls[2];
            start.Text="6";
            Require(stale.Cancellation.IsCancellationRequested,"Editing again must cancel the previous start request.");
            PumpUntil(()=>probe.Calls.Length==4,"The latest start timestamp did not replace the cancelled request.");
            var current=probe.Calls[3];current.Success(green);
            PumpUntil(()=>Pixel(firstImage)==Colors.Lime,"The latest decoded frame did not render.");
            stale.Success(red);PumpFor(100);
            Require(Pixel(firstImage)==Colors.Lime && firstStatus.Text.Contains(DownloadSection.FormatTimestamp(6),StringComparison.Ordinal),
                "A late completion that ignores cancellation must never overwrite the current frame or caption.");

            // The owner can select another exact URL while this modeless editor
            // remains bound to the row captured when it opened.
            Control<DataGrid>(owner,"QueueGrid").SelectedItem=other;
            dialog.ReceiveMetadata(metadata with {Url=other.Url,Duration=2});
            start.Text="7";
            PumpUntil(()=>probe.Calls.Length==5,"The captured row must remain editable after another owner selection.");
            var scoped=probe.Calls[4];
            Require(scoped.Request.Url==row.Url && scoped.Request.DurationSeconds==12 &&
                Control<TextBox>(dialog,"MediaSelectionUrl").Text==row.Url,
                "Other-row selection and metadata must not change the source URL or duration used for frames.");
            scoped.Success(red);PumpUntil(()=>Pixel(firstImage)==Colors.Red,"The captured URL's next frame did not render.");
            int beforeInvalid=probe.Calls.Length;
            start.Text="NaN";end.Text="13";
            Require(firstImage.Source==null && lastImage.Source==null,"Invalid and out-of-duration inputs must immediately clear stale images.");
            PumpFor(450);
            Require(probe.Calls.Length==beforeInvalid && firstStatus.Text.Length>0 && lastStatus.Text.Length>0,
                "Invalid inputs must have visible explanations and must not perform frame I/O.");
            start.Text="";end.Text="";PumpFor(450);
            Require(firstImage.Source==null && lastImage.Source==null && probe.Calls.Length==beforeInvalid,
                "Blank fields mean the full video and must release the displayed boundary frames.");
            dialog.Close();dialog=null;

            // Selecting another source format must invalidate both pictures and
            // cancellation must remain effective after read-only/close events.
            probe=new SectionFrameProbe();dialog=Open(probe);
            start=Control<TextBox>(dialog,"MediaSectionStartBox");end=Control<TextBox>(dialog,"MediaSectionEndBox");
            firstImage=Control<Image>(dialog,"MediaSectionStartFrame");lastImage=Control<Image>(dialog,"MediaSectionEndFrame");
            var mode=Control<ComboBox>(dialog,"MediaFormatModeCombo");
            var primary=Control<ComboBox>(dialog,"PrimaryMediaFormatCombo");
            mode.SelectedIndex=1;primary.SelectedValue="137";start.Text="1";end.Text="2";
            PumpUntil(()=>probe.Calls.Length==2,"Both selected boundaries must request the manual source format.");
            var oldFormat=probe.Calls;
            Require(oldFormat.All(call=>call.Request.VideoFormatId=="137"),"Manual video IDs must reach the frame provider unchanged.");
            primary.SelectedValue="18";
            Require(oldFormat.All(call=>call.Cancellation.IsCancellationRequested),"Changing the video format must cancel both old frame requests.");
            PumpUntil(()=>probe.Calls.Length==4,"The two cards must refresh when the selected video format changes.");
            var selectedFormat=probe.Calls.Skip(2).ToArray();
            Require(selectedFormat.All(call=>call.Request.VideoFormatId=="18"),"Both refreshed requests must use the newly selected format ID.");
            selectedFormat.Single(call=>!call.Request.IsEnd).Success(red);
            selectedFormat.Single(call=>call.Request.IsEnd).Success(blue);
            PumpUntil(()=>Pixel(firstImage)==Colors.Red && Pixel(lastImage)==Colors.Blue,"The new-format pictures must render in both real cards.");
            Click(dialog,"RefreshMediaFramesButton");
            PumpUntil(()=>probe.Calls.Length==6,"The actual refresh button must retry both unchanged timestamps.");
            var active=probe.Calls.Skip(4).ToArray();
            dialog.SetEditable(false);
            Require(active.All(call=>call.Cancellation.IsCancellationRequested) && firstImage.Source==null && lastImage.Source==null &&
                !Control<Button>(dialog,"RefreshMediaFramesButton").IsEnabled,
                "A row becoming read-only must cancel work, release pictures and disable frame refresh.");
            int beforeReadonly=probe.Calls.Length;
            Click(dialog,"RefreshMediaFramesButton");start.Text="4";PumpFor(450);
            Require(probe.Calls.Length==beforeReadonly,"Programmatic events must not bypass read-only frame guards.");
            foreach(var call in oldFormat.Concat(active))call.Success(green);
            PumpFor(100);
            Require(firstImage.Source==null && lastImage.Source==null,"Late results must not repopulate a read-only editor.");
            dialog.SetEditable(true);Click(dialog,"RefreshMediaFramesButton");
            PumpUntil(()=>probe.Calls.Length>=beforeReadonly+2,"A re-enabled editor must allow an explicit retry.");
            var atClose=probe.Calls.Where(call=>!call.Completion.Task.IsCompleted).ToArray();
            dialog.Close();
            string closedFirst=Control<TextBlock>(dialog,"MediaSectionStartFrameStatus").Text;
            string closedLast=Control<TextBlock>(dialog,"MediaSectionEndFrameStatus").Text;
            Require(atClose.All(call=>call.Cancellation.IsCancellationRequested),"Closing must cancel both current and waiting frame work.");
            foreach(var call in atClose)call.Success(red);
            PumpFor(100);
            Require(firstImage.Source==null && lastImage.Source==null &&
                Control<TextBlock>(dialog,"MediaSectionStartFrameStatus").Text==closedFirst &&
                Control<TextBlock>(dialog,"MediaSectionEndFrameStatus").Text==closedLast,
                "No late frame callback may change images or statuses after closing.");
            dialog=null;

            // Missing metadata and collection responses never resolve a frame;
            // an optional frame failure must not prevent a valid range Apply.
            probe=new SectionFrameProbe();dialog=Open(probe,withMetadata:false);
            start=Control<TextBox>(dialog,"MediaSectionStartBox");end=Control<TextBox>(dialog,"MediaSectionEndBox");
            start.Text="4";end.Text="8";PumpFor(450);
            Require(probe.Calls.Length==0,"Frame requests must wait for metadata that confirms an individual source.");
            dialog.ReceiveMetadata(metadata with {IsCollection=true});PumpFor(450);
            Require(probe.Calls.Length==0,"Collection metadata must not produce frames from the first playlist item.");
            dialog.ReceiveMetadata(metadata);
            PumpUntil(()=>probe.Calls.Length==2,"Individual-video metadata must release the two pending frame requests.");
            const string frameFailure="Кадр тестового источника недоступен.";
            foreach(var call in probe.Calls)call.Fail(frameFailure);
            PumpUntil(()=>Control<TextBlock>(dialog,"MediaSectionStartFrameStatus").Text.Contains(frameFailure,StringComparison.Ordinal) &&
                Control<TextBlock>(dialog,"MediaSectionEndFrameStatus").Text.Contains(frameFailure,StringComparison.Ordinal),
                "Independent frame errors must be visible in both actual cards.");
            Require(Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled && row.Selection==null && applyCalls==0,
                "A failed preview must keep a valid range editable without applying or starting it.");
            Click(dialog,"ApplyMediaSelectionButton");
            Require(!dialog.IsVisible && applyCalls==1 && row.Selection?.Section is {StartSeconds:4,EndSeconds:8} && row.Status=="Queued" && other.Selection==null,
                "Apply must accept the valid captured range despite missing optional frames, without starting either row.");
            dialog=null;
            Console.WriteLine("PASS: actual WPF boundary images/pixels, independent times, debounce, exact URL, stale cancellation, format changes and nonblocking frame failures");
        }
        finally
        {
            dialog?.Close();
            foreach(var probe in probes)foreach(var call in probe.Calls)call.Fail("frame fixture finished");
            if(owner.IsVisible)owner.Close();
        }
    }

    private static byte[] SectionFramePng(Color color)
    {
        byte[] pixels=Enumerable.Range(0,4).SelectMany(_=>new[]{color.B,color.G,color.R,color.A}).ToArray();
        var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgra32,null,pixels,8);bitmap.Freeze();
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output=new MemoryStream();encoder.Save(output);return output.ToArray();
    }
    private sealed class SectionFrameProbe
    {
        private readonly ConcurrentQueue<SectionFrameCall> _calls=new();
        internal SectionFrameCall[] Calls=>_calls.ToArray();
        internal Task<MediaFrameResult> RequestAsync(MediaFrameRequest request,CancellationToken cancellation)
        {
            // Intentionally disregard cancellation when completing. This
            // exercises the view's generation guard, not just cooperative I/O.
            var call=new SectionFrameCall(request,cancellation);_calls.Enqueue(call);return call.Completion.Task;
        }
    }
    private sealed class SectionFrameCall(MediaFrameRequest request,CancellationToken cancellation)
    {
        internal MediaFrameRequest Request {get;}=request;
        internal CancellationToken Cancellation {get;}=cancellation;
        internal TaskCompletionSource<MediaFrameResult> Completion {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Success(byte[] png,double? sampled=null)=>Completion.TrySetResult(new(Request.Url,Request.Seconds,sampled??Request.Seconds,png,""));
        internal void Fail(string error)=>Completion.TrySetResult(new(Request.Url,Request.Seconds,null,[],error));
    }
}
