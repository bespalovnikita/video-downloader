using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestMediaClipPlayback(string testRoot)
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Clip playback WPF test: "+message);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)??
            throw new InvalidOperationException("Missing actual clip playback control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void PumpUntil(Func<bool> ready,string message)
        {
            if(ready())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(ready() || watch.Elapsed>TimeSpan.FromSeconds(5))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(ready(),message);
        }
        static void PumpFor(int milliseconds)
        {var watch=Stopwatch.StartNew();PumpUntil(()=>watch.ElapsedMilliseconds>=milliseconds,"Dispatcher interval did not complete.");}

        string root=Path.Combine(testRoot,"clip-ui");Directory.CreateDirectory(root);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var owner=(Window)XamlReader.Load(reader);
        string ownedDownload=Path.Combine(root,"existing-download.mp4");File.WriteAllText(ownedDownload,"existing published media");
        var completed=new DownloadSelection("https://clip-ui.invalid/Video?token=AbC",new(1,3));
        var row=new DownloadItem{Url=completed.Url,Title="Captured video",Status="Completed",Path=ownedDownload,
            CompletedSelection=completed,OwnedPaths=[ownedDownload]};
        var options=new DownloadOptions();int applyCalls=0;
        var metadata=new PreviewData(row.Url,row.Title,"fixture","",120,"fixture",360,25,"H264","SDR","",DateTime.UtcNow)
            {FormatsLoaded=true,Formats=[new("18","mp4",640,360,25,"avc1","mp4a","en",1000,false),
                new("137","mp4",640,360,25,"avc1","none","",1000,false),new("140","m4a",0,0,0,"none","mp4a","en",100,false)]};
        MediaSelectionDialog? dialog=null;var calls=new List<ClipUiCall>();var players=new List<ClipUiPlayer>();
        Task<MediaClipResult> Request(MediaClipRequest request,CancellationToken token)
        {var call=new ClipUiCall(request,token);calls.Add(call);return call.Completion.Task;}
        MediaSelectionDialog Open(ClipUiPlayer player,bool withMetadata=true)
        {
            players.Add(player);
            var result=new MediaSelectionDialog(owner,row,()=>options,selection=>{row.Selection=selection;applyCalls++;return null;},
                clipPreview:Request,clipPlayer:player);
            result.Show();if(withMetadata)result.ReceiveMetadata(metadata);return result;
        }
        var disposed=new List<string>();
        MediaClipPreview Clip(ClipUiPlayer player,double start,double end,bool audio=true,bool video=true)
        {
            string path=Path.Combine(root,Guid.NewGuid().ToString("N")+".mp4");File.WriteAllText(path,"controlled prepared clip");
            return new(path,start,end,video,audio,()=>
            {
                Require(player.Source?.LocalPath!=path,"Native source must detach before its owned clip cache is deleted.");
                disposed.Add(path);File.Delete(path);
            });
        }
        try
        {
            owner.Show();
            // The production player really is a WPF MediaElement, with audible
            // volume and manual graph ownership. Device-independent controls
            // below use a transport so CI need not have an audio endpoint.
            dialog=new MediaSelectionDialog(owner,row,()=>options,_=>null,clipPreview:Request);
            dialog.Show();dialog.ReceiveMetadata(metadata);
            var native=Control<MediaElement>(dialog,"MediaClipVideo");
            Require(native.Volume==1 && !native.IsMuted && native.LoadedBehavior==MediaState.Manual &&
                native.UnloadedBehavior==MediaState.Manual && native.Source is null,
                "Production playback must provide a real unmuted MediaElement whose graph is explicitly released.");
            dialog.Close();dialog=null;
            // Exercise the actual native graph, rather than only injected events.
            // A missing local source must report failure even without an audio
            // endpoint. Source-only + Manual/Closed never attempts this open.
            using(var nativePlayer=new WpfMediaClipPlayer())
            {
                bool nativeFailed=false;nativePlayer.Failed+=()=>nativeFailed=true;
                nativePlayer.Open(new Uri(Path.Combine(root,"missing-native-clip.mp4"),UriKind.Absolute));
                PumpUntil(()=>nativeFailed,"The real manual MediaElement never opened its source and raised MediaFailed; preroll must start before waiting for MediaOpened.");
                nativePlayer.Clear();Require(((MediaElement)nativePlayer.View).Source is null,"The actual failed native graph must release its source.");
            }

            var player=new ClipUiPlayer();dialog=Open(player);
            var startBox=Control<TextBox>(dialog,"MediaSectionStartBox");var endBox=Control<TextBox>(dialog,"MediaSectionEndBox");
            var status=Control<TextBlock>(dialog,"MediaClipPlaybackStatus");var range=Control<TextBlock>(dialog,"MediaClipRangeText");
            var position=Control<TextBlock>(dialog,"MediaClipPositionText");var seek=Control<Slider>(dialog,"MediaClipSeekSlider");
            startBox.Text="10";endBox.Text="100";PumpFor(30);
            Require(calls.Count==0,"Editing a range must not prepare or start playback automatically.");
            Require(Control<Button>(dialog,"PreviewMediaClipStartButton").IsEnabled &&
                Control<Button>(dialog,"PreviewMediaClipEndButton").IsEnabled,"A valid selected range must enable both bounded preview choices.");
            Click(dialog,"PreviewMediaClipStartButton");
            Require(calls.Count==1 && calls[0].Request.Url==row.Url && calls[0].Request.StartSeconds==10 &&
                calls[0].Request.EndSeconds==100 && !calls[0].Request.FromEnd &&
                Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled,
                "Preparation must retain the captured URL/range and must not block Apply.");
            var first=Clip(player,10,25);calls[0].Success(first);
            PumpUntil(()=>player.Source is not null,"Prepared local media was not attached to the actual playback view.");
            Require(range.Text.Contains(DownloadSection.FormatTimestamp(10),StringComparison.Ordinal) &&
                range.Text.Contains(DownloadSection.FormatTimestamp(25),StringComparison.Ordinal) &&
                range.Text.Contains("со звуком",StringComparison.Ordinal) && seek.Maximum==15,
                "A long selection must display its actual short preview range and audio state precisely.");
            player.RaiseOpened();Require(player.PlayCalls==1 && Control<Button>(dialog,"PauseMediaClipButton").IsEnabled,
                "The actual MediaOpened event path must start the prepared clip and enable transport controls.");
            player.Position=TimeSpan.FromSeconds(3);
            PumpUntil(()=>seek.Value==3,"Playback position timer must follow the player's actual position.");
            Require(position.Text.Contains(DownloadSection.FormatTimestamp(13),StringComparison.Ordinal),"Position must be shown in source time.");
            Click(dialog,"PauseMediaClipButton");Require(player.PauseCalls==1,"The actual pause button must pause playback.");
            Click(dialog,"PauseMediaClipButton");Require(player.PlayCalls==2,"The actual resume button must resume playback.");
            seek.Value=8.5;
            Require(player.Position.TotalSeconds==8.5 && position.Text.Contains(DownloadSection.FormatTimestamp(18.5),StringComparison.Ordinal),
                "The real slider must seek within the local clip while displaying source time.");
            Click(dialog,"StopMediaClipButton");Require(player.Position==TimeSpan.Zero && seek.Value==0 && player.StopCalls>0,
                "Stop must reset playback position without changing the selected download range.");

            Click(dialog,"PreviewMediaClipEndButton");
            Require(calls.Count==2 && calls[1].Request.FromEnd && disposed.Contains(first.Path) && player.Source is null,
                "Choosing the end must release the first clip before preparing a fresh end preview.");
            var last=Clip(player,85,100);calls[1].Success(last);
            PumpUntil(()=>player.Source is not null,"The end clip was not opened.");player.RaiseOpened();
            Require(range.Text.Contains(DownloadSection.FormatTimestamp(85),StringComparison.Ordinal) &&
                range.Text.Contains(DownloadSection.FormatTimestamp(100),StringComparison.Ordinal),"The final fifteen seconds must have an explicit range caption.");
            player.RaiseEnded();Require(seek.Value==0 && status.Text.Contains("завершён",StringComparison.Ordinal),
                "MediaEnded must stop the timer and reset the view's position.");
            startBox.Text="11";
            Require(player.Source is null && disposed.Contains(last.Path) && !seek.IsEnabled,
                "Time edits must immediately stop, detach and dispose the previous clip.");
            Require(row.Selection is null && row.CompletedSelection==completed && row.Status=="Completed" &&
                row.OwnedPaths.SequenceEqual(new[]{ownedDownload}) && File.ReadAllText(ownedDownload)=="existing published media",
                "Playback must leave the queue selection, completed ownership, published media and history unchanged.");

            Click(dialog,"PreviewMediaClipStartButton");var stale=calls[^1];startBox.Text="12";
            Require(stale.Token.IsCancellationRequested,"Editing a range must cancel its pending clip preparation.");
            Click(dialog,"PreviewMediaClipStartButton");var current=calls[^1];var currentClip=Clip(player,12,27);current.Success(currentClip);
            PumpUntil(()=>player.Source?.LocalPath==currentClip.Path,"The replacement clip was not accepted.");
            var staleClip=Clip(player,11,26);stale.Success(staleClip);
            PumpUntil(()=>disposed.Contains(staleClip.Path),"A late cancelled preparation must dispose its unused owned file.");
            Require(player.Source?.LocalPath==currentClip.Path,"Late completion must not replace current playback.");

            var mode=Control<ComboBox>(dialog,"MediaFormatModeCombo");var primary=Control<ComboBox>(dialog,"PrimaryMediaFormatCombo");
            var audio=Control<ComboBox>(dialog,"AudioMediaFormatCombo");
            mode.SelectedIndex=1;primary.SelectedValue="137";audio.SelectedValue="140";
            Require(player.Source is null && disposed.Contains(currentClip.Path),"Format changes must release the old source.");
            Click(dialog,"PreviewMediaClipStartButton");var manual=calls[^1];
            Require(manual.Request.VideoFormatId=="137" && manual.Request.AudioFormatId=="140",
                "Manual video and separate audio IDs must reach the single preview owner unchanged.");
            var manualClip=Clip(player,12,27);manual.Success(manualClip);
            PumpUntil(()=>player.Source is not null,"Manual AV preview was not attached.");player.RaiseOpened();player.RaiseFailed();
            Require(player.Source is null && disposed.Contains(manualClip.Path) && status.Text.Contains("аудиоустройство",StringComparison.Ordinal) &&
                Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled,
                "A real media failure must release the file and show an honest device error while preserving Apply.");

            Click(dialog,"PreviewMediaClipStartButton");var beforeReadonly=calls[^1];dialog.SetEditable(false);
            Require(beforeReadonly.Token.IsCancellationRequested && !Control<Button>(dialog,"PreviewMediaClipStartButton").IsEnabled,
                "A read-only row must cancel preparation and disable playback.");
            int guarded=calls.Count;Click(dialog,"PreviewMediaClipStartButton");Click(dialog,"PreviewMediaClipEndButton");
            Require(calls.Count==guarded,"Programmatic Click events must not bypass the read-only playback guard.");
            var readonlyClip=Clip(player,12,27);beforeReadonly.Success(readonlyClip);
            PumpUntil(()=>disposed.Contains(readonlyClip.Path),"A read-only late result must be deleted.");
            dialog.SetEditable(true);Click(dialog,"PreviewMediaClipStartButton");var atClose=calls[^1];dialog.Close();dialog=null;
            Require(atClose.Token.IsCancellationRequested && player.Disposed && player.Source is null,"Close must cancel and dispose the native graph.");
            var closedClip=Clip(player,12,27);atClose.Success(closedClip);
            PumpUntil(()=>disposed.Contains(closedClip.Path),"Closed-dialog late completion must clean its local cache.");
            player.RaiseOpened();player.RaiseFailed();Require(player.Source is null,"Closed player events must not restore a source.");

            // Failure and in-flight preparation are both optional to Apply.
            player=new ClipUiPlayer();dialog=Open(player,false);
            startBox=Control<TextBox>(dialog,"MediaSectionStartBox");endBox=Control<TextBox>(dialog,"MediaSectionEndBox");
            startBox.Text="2";endBox.Text="4";int before=calls.Count;Click(dialog,"PreviewMediaClipStartButton");
            Require(calls.Count==before,"Metadata must confirm an individual material before playback.");
            dialog.ReceiveMetadata(metadata with{IsCollection=true});Click(dialog,"PreviewMediaClipStartButton");
            Require(calls.Count==before,"Collections must not play the first entry as if it were the selected URL.");
            dialog.ReceiveMetadata(metadata);Click(dialog,"PreviewMediaClipStartButton");var optional=calls[^1];
            Click(dialog,"ApplyMediaSelectionButton");dialog=null;
            Require(applyCalls==1 && row.Selection?.Section==new DownloadSection(2,4) && optional.Token.IsCancellationRequested,
                "Apply must save a valid range and close/cancel playback even while preparation is pending.");
            optional.Completion.TrySetCanceled(optional.Token);
            Console.WriteLine("PASS: real WPF clip controls, native MediaElement availability, source positions, short-range captions, audio state, cancellation and file/queue lifetime");
        }
        finally
        {
            dialog?.Close();foreach(var call in calls)call.Completion.TrySetCanceled();
            foreach(var player in players)player.Dispose();owner.Close();
        }
    }

    private sealed class ClipUiCall(MediaClipRequest request,CancellationToken token)
    {
        internal MediaClipRequest Request {get;}=request;
        internal CancellationToken Token {get;}=token;
        internal TaskCompletionSource<MediaClipResult> Completion {get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Success(MediaClipPreview clip)=>Completion.TrySetResult(new(Request.Url,Request.StartSeconds,Request.EndSeconds,clip,""));
    }
    private sealed class ClipUiPlayer:IMediaClipPlayer
    {
        public FrameworkElement View {get;}=new MediaElement{Height=220};
        public event Action? Opened;
        public event Action? Ended;
        public event Action? Failed;
        public TimeSpan Position {get;set;}
        internal Uri? Source;
        internal int PlayCalls,PauseCalls,StopCalls;
        internal bool Disposed;
        public void Open(Uri source){Source=source;Position=TimeSpan.Zero;}
        public void Play()=>PlayCalls++;
        public void Pause()=>PauseCalls++;
        public void Stop(){StopCalls++;Position=TimeSpan.Zero;}
        public void Clear(){Source=null;Position=TimeSpan.Zero;}
        public void Dispose(){Disposed=true;Clear();Opened=null;Ended=null;Failed=null;}
        internal void RaiseOpened()=>Opened?.Invoke();
        internal void RaiseEnded()=>Ended?.Invoke();
        internal void RaiseFailed()=>Failed?.Invoke();
    }
}
