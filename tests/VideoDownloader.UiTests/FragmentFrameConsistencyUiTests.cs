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
    private static void TestFragmentFrameConsistency(string testRoot)
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("WPF frame-grid roundtrip: "+message);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual frame-grid control: "+name));
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var clock=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || clock.Elapsed>TimeSpan.FromSeconds(20))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}Require(condition(),message);
        }
        static T Await<T>(Task<T> task)
        {PumpUntil(()=>task.IsCompleted,"Actual media operation did not finish");return task.GetAwaiter().GetResult();}
        static int? FrameId(Image image)
        {
            if(image.Source is not BitmapSource bitmap)return null;
            var converted=new FormatConvertedBitmap(bitmap,PixelFormats.Bgra32,null,0);int id=0;
            for(int bit=0;bit<10;bit++)
            {byte[] pixel=new byte[4];converted.CopyPixels(new Int32Rect(16+bit*12,20,1,1),pixel,4,0);if(pixel[0]>128)id|=1<<bit;}
            return id;
        }
        string root=Path.Combine(testRoot,"frame-grid-roundtrip");Directory.CreateDirectory(root);
        string source=Await(Task.Run(()=>FragmentFrameFixture.Source(root,"24",4,120)));
        var origin=new FragmentFrameFixture.Origin(File.ReadAllBytes(source));
        string ffmpeg=DependencyCenter.Executable("ffmpeg.exe");
        using var frames=new MediaFrameService((request,token)=>Task.FromResult(new MediaFrameSource(new Uri(origin.Url),"fixture",
            new Dictionary<string,string>(),"","","",24,4/24d,160,90)),ffmpeg);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));using var reader=XmlReader.Create(input);
        var owner=(Window)XamlReader.Load(reader);MediaSelectionDialog? dialog=null;
        try
        {
            owner.Show();
            foreach(int expected in new[]{1,2})
            {
                var row=new DownloadItem{Url=origin.Url,Title="Frame grid "+expected};
                string output=Path.Combine(root,"output-"+expected);Directory.CreateDirectory(output);
                var options=new DownloadOptions{Output=output,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=false};
                var metadata=new PreviewData(row.Url,row.Title,"fixture","",4/24d,"fixture",90,24,"H264","SDR","",DateTime.UtcNow)
                    {FormatsLoaded=true,Formats=[new MediaFormat("fixture","mp4",160,90,24,"avc1","none","",null,false)]};
                MediaClipRequest? playedRequest=null;
                Task<MediaClipResult> Played(MediaClipRequest request,CancellationToken token)
                {
                    playedRequest=request;
                    return Task.FromResult(new MediaClipResult(request.Url,request.StartSeconds,request.EndSeconds,null,"Fixture playback observer"));
                }
                dialog=new MediaSelectionDialog(owner,row,()=>options,selection=>{row.Selection=selection;return null;},frames.GetFrameAsync,Played);
                dialog.Show();dialog.ReceiveMetadata(metadata);
                Control<TextBox>(dialog,"MediaSectionStartBox").Text="0";
                Control<TextBox>(dialog,"MediaSectionEndBox").Text=expected==1?"0.083":"0.125";
                for(int step=0;step<expected;step++)Control<Button>(dialog,"MediaSectionStartNextFrameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Control<TextBox>(dialog,"MediaSectionStartBox").Text==DownloadSection.FormatTimestamp(expected/24d),
                    "The actual +frame control must produce the millisecond grid label");
                var start=Control<Image>(dialog,"MediaSectionStartFrame");var end=Control<Image>(dialog,"MediaSectionEndFrame");
                PumpUntil(()=>FrameId(start)==expected && FrameId(end)==expected,"Both actual decoded PNG cards must identify the selected source frame");
                Control<Button>(dialog,"PreviewMediaClipStartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>playedRequest is not null,"The actual playback button must use the shared boundary conversion");
                Require(Math.Abs(playedRequest!.StartSeconds-expected/24d)<1e-12 && Math.Abs(playedRequest.EndSeconds-(expected+1)/24d)<1e-12,
                    "Playback must receive the same restored grid boundaries as export; actual clip pixels are checked by native media tests");
                Control<Button>(dialog,"ApplyMediaSelectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!dialog.IsVisible && row.Selection?.FrameRate==24,"Apply must capture the inferred automatic frame grid");dialog=null;
                var persistence=new AppPersistence(Path.Combine(root,"profile-"+expected));persistence.Save([row],options);
                var restored=persistence.Restore()!.Queue.Single();Require(restored.Selection?.FrameRate==24,"Restart must retain frame semantics");
                var engine=new DownloadCoordinator(DependencyCenter.Executable("yt-dlp.exe"),persistence,options,_=>{},_=>{});
                try
                {
                    Require(engine.Add(restored),"The applied/restored frame plan must enter the actual download coordinator");
                    Task run=engine.RunAsync();PumpUntil(()=>run.IsCompleted,"The actual yt-dlp export did not finish");run.GetAwaiter().GetResult();
                }
                finally{var dispose=engine.DisposeAsync().AsTask();PumpUntil(()=>dispose.IsCompleted,"The coordinator did not join");dispose.GetAwaiter().GetResult();}
                Require(restored.Status=="Completed" && restored.IsPartialResult && persistence.ReadHistory().Count==0,
                    "Actual publication must remain partial and leave full-video history empty: "+restored.RawError);
                int[] actual=Await(Task.Run(()=>FragmentFrameFixture.Decode(ffmpeg,restored.Path)));
                Require(actual.SequenceEqual([expected]),"+frame → Apply → restart → actual export must contain exactly the frame shown by both cards");
            }
            Console.WriteLine("PASS: actual WPF +frame/Apply/restart and real yt-dlp publication decode source IDs 1 and 2 with matching actual boundary PNGs");
        }
        finally
        {
            dialog?.Close();if(owner.IsVisible)owner.Close();
            var dispose=origin.DisposeAsync().AsTask();PumpUntil(()=>dispose.IsCompleted,"The local media origin did not join");dispose.GetAwaiter().GetResult();
        }
    }
}
