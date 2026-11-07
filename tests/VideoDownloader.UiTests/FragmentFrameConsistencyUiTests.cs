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
            int scenario=0;
            foreach(var (firstSeconds,lastSeconds,expected,grid,steps) in new[]{
                (.042,.083,new[]{1},(double?)24,1),(.083,.125,new[]{2},(double?)24,2),
                (.042,.084,new[]{2},(double?)null,0),(.042,.126,new[]{2,3},(double?)null,0)})
            {
                scenario++;
                var row=new DownloadItem{Url=origin.Url,Title="Frame semantics "+scenario};
                string output=Path.Combine(root,"output-"+scenario);Directory.CreateDirectory(output);
                var options=new DownloadOptions{Output=output,Threads="1",Container="MP4",EmbedMetadata=false,EmbedChapters=false};
                var v24=new MediaFormat("fixture","mp4",160,90,24,"avc1","none","",null,false);
                var metadata=new PreviewData(row.Url,row.Title,"fixture","",4/24d,"fixture",90,24,"H264","SDR","",DateTime.UtcNow)
                    {FormatsLoaded=true,Formats=grid is null?[v24,v24 with{Id="v30",Fps=30}]:[v24]};
                MediaClipRequest? playedRequest=null;
                Task<MediaClipResult> Played(MediaClipRequest request,CancellationToken token)
                {
                    playedRequest=request;
                    return Task.FromResult(new MediaClipResult(request.Url,request.StartSeconds,request.EndSeconds,null,"Fixture playback observer"));
                }
                dialog=new MediaSelectionDialog(owner,row,()=>options,selection=>{row.Selection=selection;return null;},frames.GetFrameAsync,Played);
                dialog.Show();dialog.ReceiveMetadata(metadata);
                Control<TextBox>(dialog,"MediaSectionStartBox").Text=steps>0?"0":DownloadSection.FormatTimestamp(firstSeconds);
                Control<TextBox>(dialog,"MediaSectionEndBox").Text=DownloadSection.FormatTimestamp(lastSeconds);
                for(int step=0;step<steps;step++)Control<Button>(dialog,"MediaSectionStartNextFrameButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(Control<TextBox>(dialog,"MediaSectionStartBox").Text==DownloadSection.FormatTimestamp(firstSeconds),
                    "The actual editor must retain the stepped or raw millisecond label");
                if(grid is null)Require(Control<TextBlock>(dialog,"MediaSectionStepStatus").Text.Contains("0.1",StringComparison.Ordinal),
                    "The real mixed-FPS editor must disclose its seconds fallback");
                var start=Control<Image>(dialog,"MediaSectionStartFrame");var end=Control<Image>(dialog,"MediaSectionEndFrame");
                PumpUntil(()=>FrameId(start)==expected[0] && FrameId(end)==expected[^1],"Both actual decoded PNG cards must identify the selected source frame");
                Control<Button>(dialog,"PreviewMediaClipStartButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                PumpUntil(()=>playedRequest is not null,"The actual playback button must use the shared boundary conversion");
                double playedStart=grid is null?firstSeconds:expected[0]/24d;
                double playedEnd=grid is null?lastSeconds:(expected[^1]+1)/24d;
                Require(Math.Abs(playedRequest!.StartSeconds-playedStart)<1e-12 && Math.Abs(playedRequest.EndSeconds-playedEnd)<1e-12,
                    "Playback must retain raw boundaries without a common grid and restore only the explicit grid context");
                Control<Button>(dialog,"ApplyMediaSelectionButton").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Require(!dialog.IsVisible && row.Selection is not null && row.Selection.FrameRate==grid,"Apply must capture only the actual common frame grid");dialog=null;
                var persistence=new AppPersistence(Path.Combine(root,"profile-"+scenario));persistence.Save([row],options);
                var restored=persistence.Restore()!.Queue.Single();Require(restored.Selection?.FrameRate==grid,"Restart must retain frame semantics");
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
                Require(grid is null?actual.Length>0 && actual[0]==expected[0]:actual.SequenceEqual(expected),
                    "The actual export must preserve the raw start without a grid, and the exact selected frame with a grid");
            }
            Console.WriteLine("PASS: actual WPF common-grid/mixed-FPS fields, boundary PNGs, playback requests, Apply/restart and real yt-dlp publication");
        }
        finally
        {
            dialog?.Close();if(owner.IsVisible)owner.Close();
            var dispose=origin.DisposeAsync().AsTask();PumpUntil(()=>dispose.IsCompleted,"The local media origin did not join");dispose.GetAwaiter().GetResult();
        }
    }
}
