using System.Diagnostics;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using System.Xml;
using Color=System.Windows.Media.Color;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using ListBox=System.Windows.Controls.ListBox;
using Point=System.Windows.Point;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestMediaWaveform(string testRoot)
    {
        static void Require(bool value,string reason)
        {if(!value)throw new InvalidOperationException("Audio waveform WPF: "+reason);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing waveform control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void PumpUntil(Func<bool> condition,string reason)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(6))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),reason);
        }
        static void FlushDispatcher()
        {
            bool finished=false;
            _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>finished=true));
            PumpUntil(()=>finished,"The dispatcher did not finish its queued callbacks.");
        }
        static void Drag(Thumb thumb,double pixels)
        {
            thumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            thumb.RaiseEvent(new DragDeltaEventArgs(pixels,0){RoutedEvent=Thumb.DragDeltaEvent});
            thumb.RaiseEvent(new DragCompletedEventArgs(pixels,0,false){RoutedEvent=Thumb.DragCompletedEvent});
        }
        static void KeyPress(Thumb thumb,Key key,Window window)
        {
            var source=PresentationSource.FromVisual(window)??throw new InvalidOperationException("Shown WPF source required.");
            thumb.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
        }
        static Color Pixel(AudioWaveformView view,int bin)
        {
            view.UpdateLayout();var bitmap=new RenderTargetBitmap((int)Math.Ceiling(view.ActualWidth),112,96,96,PixelFormats.Pbgra32);
            bitmap.Render(view);
            int x=(int)Math.Round(8+(bin+.5)*(view.ActualWidth-16)/view.Data!.Bins.Count);
            byte[] pixel=new byte[4];bitmap.CopyPixels(new Int32Rect(x,36,1,1),pixel,4,0);
            return Color.FromArgb(pixel[3],pixel[2],pixel[1],pixel[0]);
        }
        static AudioWaveform Wave(double start,double end,double? decodedEnd=null)=>new(start,end,
            Enumerable.Range(0,10).Select(index=>index is >=3 and <=7?new WaveformBin(.8,.4):new WaveformBin(0,0)),decodedEnd);

        string root=Path.Combine(testRoot,"waveform-ui");Directory.CreateDirectory(root);
        string media=Path.Combine(root,"previous-result.mp4");File.WriteAllText(media,"old completed media sentinel");
        string url="https://waveform-ui.invalid/Video?token=AbC";
        var completed=new DownloadSelection(url,new(1,2));
        var row=new DownloadItem{Url=url,Title="Waveform UI",Status="Completed",State="Done",Path=media,
            OutputRoot=root,OwnedPaths=[media],CompletedSelection=completed,AttemptFinished=true};
        var storage=new AppPersistence(Path.Combine(root,"profile"));storage.Save([row],new DownloadOptions());
        string checkpoint=File.ReadAllText(storage.SessionPath);
        var metadata=new PreviewData(url,row.Title,"fixture","",80,"fixture",360,25,"H264","SDR","",DateTime.UtcNow)
        {FormatsLoaded=true,Formats=[new("18","mp4",640,360,25,"avc1","mp4a","en",1000,false),
            new("137","mp4",640,360,25,"avc1","none","",900,false),new("140","m4a",0,0,0,"none","mp4a","en",100,false)]};
        var options=new DownloadOptions();int clipCalls=0,applyCalls=0;
        TaskCompletionSource<MediaClipResult>? pendingPlayback=null;
        var calls=new List<WaveformUiCall>();
        Task<MediaWaveformResult> Request(MediaWaveformRequest request,CancellationToken token)
        {var call=new WaveformUiCall(request,token);calls.Add(call);return call.Completion.Task;}
        Task<MediaClipResult> NoPlayback(MediaClipRequest request,CancellationToken token)
        {clipCalls++;return pendingPlayback?.Task??Task.FromResult(new MediaClipResult(request.Url,request.StartSeconds,request.EndSeconds,null,"not requested"));}
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var owner=(Window)XamlReader.Load(reader);
        MediaSelectionDialog? dialog=null;
        MediaSelectionDialog Open(bool withMetadata=true)
        {
            var result=new MediaSelectionDialog(owner,row,()=>options,selection=>{row.Selection=selection;applyCalls++;return null;},
                clipPreview:NoPlayback,waveformPreview:Request);
            result.Show();if(withMetadata)result.ReceiveMetadata(metadata);return result;
        }
        var oldCursor=System.Windows.Forms.Cursor.Position;
        try
        {
            owner.Show();dialog=Open(false);
            var start=Control<TextBox>(dialog,"MediaSectionStartBox");var end=Control<TextBox>(dialog,"MediaSectionEndBox");
            var first=Control<AudioWaveformView>(dialog,"MediaWaveformStartView");
            var last=Control<AudioWaveformView>(dialog,"MediaWaveformEndView");
            var firstThumb=Control<Thumb>(dialog,"MediaWaveformStartThumb");var lastThumb=Control<Thumb>(dialog,"MediaWaveformEndThumb");
            start.Text="10";end.Text="30";FlushDispatcher();
            Click(dialog,"PreviewMediaWaveformStartButton");
            Require(calls.Count==0 && !Control<Button>(dialog,"PreviewMediaWaveformStartButton").IsEnabled,
                "Waveform preparation must wait for individual source metadata.");
            dialog.ReceiveMetadata(metadata);
            Require(calls.Count==0 && clipCalls==0 && first.Data is null && last.Data is null,
                "Metadata, typed ranges and selection must not start waveform media or playback automatically.");
            Click(dialog,"PreviewMediaWaveformStartButton");
            Require(calls.Count==1 && !calls[0].Request.IsEnd && calls[0].Request.ClipRequest.Url==url &&
                calls[0].Request.ClipRequest.StartSeconds==10 && calls[0].Request.ClipRequest.EndSeconds==30,
                "The explicit start action must retain the captured exact URL and draft range.");
            var measured=Wave(5,20);calls[0].Success(measured);
            PumpUntil(()=>ReferenceEquals(first.Data,measured),"The actual waveform view did not accept its completed data.");
            first.BringIntoView();dialog.UpdateLayout();FlushDispatcher();
            Require(first.ActualWidth>180 && firstThumb.IsEnabled && AutomationProperties.GetName(firstThumb).Length>0 &&
                Control<TextBlock>(dialog,"MediaWaveformStartRange").Text.Contains(DownloadSection.FormatTimestamp(5),StringComparison.Ordinal) &&
                Control<TextBlock>(dialog,"MediaWaveformStartRange").Text.Contains(DownloadSection.FormatTimestamp(20),StringComparison.Ordinal),
                "The real view must expose its source-time window and accessible boundary handle.");
            var quiet=Pixel(first,1);var loud=Pixel(first,6);
            Require(loud.G>quiet.G+60,"Rendered PCM peaks must draw visibly above the silent-bin background.");
            Require(firstThumb.CaptureMouse() && firstThumb.IsMouseCaptured,"The shown waveform Thumb must capture the real WPF mouse.");
            try
            {
                double secondPixels=(first.ActualWidth-16)/15;
                firstThumb.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
                firstThumb.RaiseEvent(new DragDeltaEventArgs(secondPixels,0){RoutedEvent=Thumb.DragDeltaEvent});
                Require(firstThumb.IsMouseCaptured && firstThumb.IsEnabled && start.Text==DownloadSection.FormatTimestamp(11),
                    "The first field update must not disable the active Thumb or lose its real mouse capture.");
                firstThumb.RaiseEvent(new DragDeltaEventArgs(secondPixels/2,0){RoutedEvent=Thumb.DragDeltaEvent});
                Require(firstThumb.IsMouseCaptured && start.Text==DownloadSection.FormatTimestamp(11.5) &&
                    ReferenceEquals(first.Data,measured) && first.BoundarySeconds==11.5 && calls.Count==1,
                    "A continuous captured drag must retain its source window and accumulate changes without another request.");
                firstThumb.RaiseEvent(new DragCompletedEventArgs(secondPixels*1.5,0,false){RoutedEvent=Thumb.DragCompletedEvent});
            }
            finally{firstThumb.ReleaseMouseCapture();}
            KeyPress(firstThumb,Key.Home,dialog);Require(start.Text==DownloadSection.FormatTimestamp(5),"Home must use this waveform's source start.");
            KeyPress(firstThumb,Key.End,dialog);Require(start.Text==DownloadSection.FormatTimestamp(20),"End must use this waveform's source end.");

            start.Text="10";end.Text="30";Require(first.Data is null,"External typed edits must invalidate the previous waveform.");
            Click(dialog,"PreviewMediaWaveformStartButton");calls[^1].Success(measured);
            PumpUntil(()=>ReferenceEquals(first.Data,measured),"Replacement start waveform was not rendered.");
            first.BringIntoView();dialog.UpdateLayout();dialog.Activate();
            var target=first.PointToScreen(new Point(8+(first.ActualWidth-16)/2,53));
            System.Windows.Forms.Cursor.Position=new System.Drawing.Point((int)Math.Round(target.X),(int)Math.Round(target.Y));FlushDispatcher();
            first.RaiseEvent(new MouseButtonEventArgs(Mouse.PrimaryDevice,Environment.TickCount,MouseButton.Left)
                {RoutedEvent=UIElement.MouseLeftButtonDownEvent,Source=first});
            Require(DownloadSection.TryParseTimestamp(start.Text,out double clicked) && Math.Abs(clicked-12.5)<.05,
                "A real track-click event must map the device position onto absolute source time.");

            start.Text="12.5";end.Text="13";Click(dialog,"PreviewMediaWaveformStartButton");calls[^1].Success(measured);
            PumpUntil(()=>ReferenceEquals(first.Data,measured),"The short valid draft did not accept its waveform context.");
            KeyPress(firstThumb,Key.End,dialog);
            Require(start.Text==DownloadSection.FormatTimestamp(12.999),"The start marker must stop one millisecond before the other boundary.");
            start.Text="12.5";Click(dialog,"PreviewMediaWaveformEndButton");calls[^1].Success(measured);
            PumpUntil(()=>ReferenceEquals(last.Data,measured),"The short draft's end waveform was not rendered.");
            KeyPress(lastThumb,Key.Home,dialog);
            Require(end.Text==DownloadSection.FormatTimestamp(12.501),"The end marker must remain strictly after the start boundary.");
            start.Text="12.5";end.Text="30";Click(dialog,"PreviewMediaWaveformStartButton");calls[^1].Success(measured);
            PumpUntil(()=>ReferenceEquals(first.Data,measured),"The restored draft's start waveform was not rendered.");

            Click(dialog,"PreviewMediaWaveformEndButton");Require(calls[^1].Request.IsEnd,"The second card must request the end boundary.");
            var ending=Wave(22.5,37.5,35);calls[^1].Success(ending);
            PumpUntil(()=>ReferenceEquals(last.Data,ending),"The end waveform did not accept its actual source window.");
            Require(Control<TextBlock>(dialog,"MediaWaveformEndRange").Text.Contains(DownloadSection.FormatTimestamp(35),StringComparison.Ordinal),
                "An audio track that ends early must disclose its decoded end instead of stretching the waveform.");
            last.BringIntoView();dialog.UpdateLayout();Drag(lastThumb,-(last.ActualWidth-16)/30);
            Require(end.Text==DownloadSection.FormatTimestamp(29.5) && first.Data is null && ReferenceEquals(last.Data,ending),
                "Dragging the end edits only that field and invalidates the other card.");
            Require(row.Selection is null && row.CompletedSelection==completed && row.Status=="Completed" &&
                row.OwnedPaths.SequenceEqual(new[]{media}) && File.ReadAllText(media)=="old completed media sentinel" &&
                File.ReadAllText(storage.SessionPath)==checkpoint && !File.Exists(storage.HistoryPath) && applyCalls==0 && clipCalls==0,
                "Waveform preparation and gestures must preserve row selection, old ownership, checkpoint, history and playback.");

            start.Text="13";Click(dialog,"PreviewMediaWaveformStartButton");var stale=calls[^1];end.Text="31";
            Require(stale.Token.IsCancellationRequested,"Changing another field must cancel the pending captured range.");
            Click(dialog,"PreviewMediaWaveformStartButton");var current=calls[^1];var currentData=Wave(5.5,20.5);current.Success(currentData);
            PumpUntil(()=>ReferenceEquals(first.Data,currentData),"The current waveform was not accepted.");
            stale.Success(Wave(5.5,20.5));FlushDispatcher();
            Require(ReferenceEquals(first.Data,currentData),"A late response from the previous range must not replace the new waveform.");

            Click(dialog,"PreviewMediaWaveformEndButton");var cancelled=calls[^1];Click(dialog,"CancelMediaWaveformEndButton");
            Require(cancelled.Token.IsCancellationRequested && last.Data is null,"The explicit cancel button must cancel preparation and clear its card.");
            cancelled.Success(Wave(23.5,38.5));FlushDispatcher();Require(last.Data is null,"A cancelled card must ignore its late result.");
            Click(dialog,"PreviewMediaWaveformStartButton");var readonlyCall=calls[^1];dialog.SetEditable(false);
            string readonlyStart=start.Text,readonlyEnd=end.Text;int guarded=calls.Count;
            Click(dialog,"PreviewMediaWaveformStartButton");Drag(firstThumb,100);KeyPress(lastThumb,Key.Left,dialog);
            Require(readonlyCall.Token.IsCancellationRequested && calls.Count==guarded && start.Text==readonlyStart && end.Text==readonlyEnd &&
                first.Data is null && !firstThumb.IsEnabled && !Control<Button>(dialog,"PreviewMediaWaveformStartButton").IsEnabled,
                "Read-only guards must cancel preparation and reject even directly raised actions.");
            readonlyCall.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"A read-only late result must stay discarded.");
            dialog.SetEditable(true);start.Text="NaN";guarded=calls.Count;Click(dialog,"PreviewMediaWaveformStartButton");
            Require(calls.Count==guarded,"Invalid time fields must not start media preparation.");
            start.Text="13";end.Text="31";dialog.ReceiveMetadata(metadata with{IsCollection=true});Click(dialog,"PreviewMediaWaveformEndButton");
            Require(calls.Count==guarded,"A collection must not masquerade as one video's sound.");
            dialog.ReceiveMetadata(metadata);

            Control<ComboBox>(dialog,"MediaFormatModeCombo").SelectedIndex=1;
            Control<ComboBox>(dialog,"PrimaryMediaFormatCombo").SelectedValue="137";
            Control<ComboBox>(dialog,"AudioMediaFormatCombo").SelectedValue="140";
            Click(dialog,"PreviewMediaWaveformStartButton");var manual=calls[^1];
            Require(manual.Request.ClipRequest.VideoFormatId=="137" && manual.Request.ClipRequest.AudioFormatId=="140",
                "Manual video and separate audio IDs must reach the shared media owner unchanged.");
            dialog.ReceiveMetadata(metadata with{Formats=metadata.Formats.Where(format=>format.Id!="137").ToArray()});
            Require(manual.Token.IsCancellationRequested,"A selected ID that disappears from the confirmed catalogue must invalidate its pending waveform.");
            manual.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"An unconfirmed format cannot receive its old waveform.");
            dialog.ReceiveMetadata(metadata);Click(dialog,"PreviewMediaWaveformStartButton");manual=calls[^1];
            Control<ComboBox>(dialog,"PrimaryMediaFormatCombo").SelectedValue="18";
            Require(manual.Token.IsCancellationRequested,"Format changes must cancel their old audio source.");
            manual.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"A stale format's waveform cannot be applied.");
            Control<ComboBox>(dialog,"MediaFormatModeCombo").SelectedIndex=0;
            options.AudioOnly=true;options.AudioTrack="Languages";options.AudioLanguages="ru";dialog.SetEditable(true);
            Click(dialog,"PreviewMediaWaveformStartButton");var language=calls[^1];
            Require(language.Request.ClipRequest.AudioOnly && language.Request.ClipRequest.AudioTrack=="Languages" &&
                language.Request.ClipRequest.AudioLanguages=="ru","Audio-only and language settings must be captured explicitly.");
            options.AudioOnly=false;options.AudioLanguages="ru,en";dialog.SetEditable(true);
            Require(language.Token.IsCancellationRequested,"Changing audio-only or language intent must cancel the prior source.");
            language.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"An old language result must stay discarded.");
            Click(dialog,"PreviewMediaWaveformStartButton");language=calls[^1];
            Require(!language.Request.ClipRequest.AudioOnly && language.Request.ClipRequest.AudioLanguages=="ru,en",
                "The current multi-language request must retain the language order for the shared media owner.");
            options.Cookies="new-cookie-context";dialog.SetEditable(true);
            Require(language.Token.IsCancellationRequested,"A changed cookies selection must invalidate the old request.");
            language.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"Old authentication-context results must stay discarded.");
            Click(dialog,"PreviewMediaWaveformStartButton");var freshMetadata=calls[^1];
            dialog.ReceiveMetadata(metadata with{SavedAtUtc=metadata.SavedAtUtc.AddMinutes(1),Formats=metadata.Formats.ToArray()});
            Require(!freshMetadata.Token.IsCancellationRequested,"A private source refresh with unchanged intent must not cancel its own waveform request.");
            var refreshedWave=Wave(5.5,20.5);freshMetadata.Success(refreshedWave);
            PumpUntil(()=>ReferenceEquals(first.Data,refreshedWave),"The waveform must survive a same-intent metadata refresh.");
            Click(dialog,"PreviewMediaWaveformStartButton");var changedDuration=calls[^1];dialog.ReceiveMetadata(metadata with{Duration=70});
            Require(changedDuration.Token.IsCancellationRequested,"A changed source duration must invalidate its old waveform window.");
            changedDuration.Success(Wave(5.5,20.5));FlushDispatcher();Require(first.Data is null,"An old-duration result must remain discarded.");
            dialog.ReceiveMetadata(metadata with{Duration=0});Click(dialog,"PreviewMediaWaveformStartButton");var unknownDuration=calls[^1];
            Require(unknownDuration.Request.ClipRequest.DurationSeconds is null,"Unknown total duration must stay unknown in the captured request.");
            var unknownWave=Wave(5.5,20.5);unknownDuration.Success(unknownWave);
            PumpUntil(()=>ReferenceEquals(first.Data,unknownWave),"A bounded source-time waveform must remain usable without a known total duration.");
            dialog.ReceiveMetadata(metadata);
            Click(dialog,"PreviewMediaWaveformStartButton");calls[^1].Failure("Аудиодорожка отсутствует");
            PumpUntil(()=>Control<TextBlock>(dialog,"MediaWaveformStartStatus").Text.Contains("отсутствует",StringComparison.Ordinal),
                "A missing audio track must show an honest explanation.");
            Require(first.Data is null && Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled,"No-audio failure must preserve Apply.");
            Click(dialog,"PreviewMediaWaveformStartButton");var wrongUrl=calls[^1];
            wrongUrl.Completion.TrySetResult(new(url.Replace("/Video","/video",StringComparison.Ordinal),13,Wave(5.5,20.5),""));
            PumpUntil(()=>Control<TextBlock>(dialog,"MediaWaveformStartStatus").Text.Contains("другую",StringComparison.Ordinal),
                "A case-distinct URL must not replace the captured source's waveform.");
            Require(first.Data is null,"Wrong-source data must stay rejected.");
            Click(dialog,"PreviewMediaWaveformStartButton");var wrongTime=calls[^1];
            wrongTime.Completion.TrySetResult(new(url,14,Wave(5.5,20.5),""));
            PumpUntil(()=>Control<TextBlock>(dialog,"MediaWaveformStartStatus").Text.Contains("другую",StringComparison.Ordinal),
                "A result for another requested boundary must stay rejected.");
            Require(first.Data is null,"Wrong-boundary data must stay rejected.");
            Click(dialog,"PreviewMediaWaveformStartButton");var silent=new AudioWaveform(5.5,20.5,Enumerable.Repeat(new WaveformBin(0,0),10));
            calls[^1].Success(silent);PumpUntil(()=>ReferenceEquals(first.Data,silent),"A real silent envelope must remain usable.");
            Require(Control<TextBlock>(dialog,"MediaWaveformStartStatus").Text.Contains("тишина",StringComparison.Ordinal) && firstThumb.IsEnabled,
                "A silent audio track must be distinguished from a missing audio track.");
            Click(dialog,"PreviewMediaWaveformStartButton");var atStop=calls[^1];
            pendingPlayback=new(TaskCreationOptions.RunContinuationsAsynchronously);
            Click(dialog,"PreviewMediaClipStartButton");
            Require(Control<Button>(dialog,"StopMediaClipButton").IsEnabled,"An actual pending playback request must enable its Stop control.");
            Click(dialog,"StopMediaClipButton");
            Require(atStop.Token.IsCancellationRequested && first.Data is null && last.Data is null,
                "The real enabled playback Stop button must also revoke and clear waveform generations.");
            atStop.Success(Wave(5.5,20.5));pendingPlayback.TrySetResult(new(url,13,31,null,"stopped"));pendingPlayback=null;
            FlushDispatcher();Require(first.Data is null,"A late waveform result cannot return after playback Stop.");
            Click(dialog,"PreviewMediaWaveformStartButton");var atClose=calls[^1];dialog.Close();dialog=null;
            Require(atClose.Token.IsCancellationRequested,"Closing the editor must cancel its owned waveform work.");
            atClose.Success(Wave(5.5,20.5));FlushDispatcher();

            options=new DownloadOptions();
            var originalPlan=new DownloadSelection(url,new(10,20),DurationSeconds:80)
                {Sections=new DownloadSectionSet([new(10,20),new(30,40)])};row.Selection=originalPlan;
            dialog=Open();start=Control<TextBox>(dialog,"MediaSectionStartBox");end=Control<TextBox>(dialog,"MediaSectionEndBox");
            first=Control<AudioWaveformView>(dialog,"MediaWaveformStartView");last=Control<AudioWaveformView>(dialog,"MediaWaveformEndView");
            lastThumb=Control<Thumb>(dialog,"MediaWaveformEndThumb");var list=Control<ListBox>(dialog,"MediaSectionsList");
            Click(dialog,"PreviewMediaWaveformStartButton");var oldSection=calls[^1];list.SelectedIndex=1;
            Require(oldSection.Token.IsCancellationRequested && start.Text==DownloadSection.FormatTimestamp(30) && end.Text==DownloadSection.FormatTimestamp(40),
                "Selecting another draft must cancel the first range and use its own fields.");
            oldSection.Success(Wave(5,20));FlushDispatcher();Require(first.Data is null,"An old selected draft cannot receive a late waveform.");
            Click(dialog,"PreviewMediaWaveformEndButton");var secondWave=Wave(32.5,47.5);calls[^1].Success(secondWave);
            PumpUntil(()=>ReferenceEquals(last.Data,secondWave),"The selected second draft's end waveform was not shown.");
            last.BringIntoView();dialog.UpdateLayout();Drag(lastThumb,-(last.ActualWidth-16)/15);
            Require(end.Text==DownloadSection.FormatTimestamp(39) && ReferenceEquals(row.Selection,originalPlan),
                "A waveform gesture must edit only the selected local draft until Apply.");
            Click(dialog,"ApplyMediaSelectionButton");dialog=null;
            Require(applyCalls==1 && row.Selection?.EffectiveSections.SequenceEqual(new[]{new DownloadSection(10,20),new DownloadSection(30,39)})==true &&
                row.CompletedSelection==completed && File.ReadAllText(media)=="old completed media sentinel",
                "Only Apply commits both ordered drafts while preserving the prior published result.");
            Console.WriteLine("PASS: rendered audio waveform bins/source axis, real thumb/track/keyboard boundary edits, explicit requests, generation cancellation, formats, readonly and multi-range draft ownership");
        }
        finally
        {
            dialog?.Close();foreach(var call in calls)call.Completion.TrySetCanceled();
            System.Windows.Forms.Cursor.Position=oldCursor;owner.Close();
        }
    }
    private sealed class WaveformUiCall(MediaWaveformRequest request,CancellationToken token)
    {
        internal readonly MediaWaveformRequest Request=request;
        internal readonly CancellationToken Token=token;
        internal readonly TaskCompletionSource<MediaWaveformResult> Completion=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal void Success(AudioWaveform waveform)=>Completion.TrySetResult(new(Request.ClipRequest.Url,
            Request.IsEnd?Request.ClipRequest.EndSeconds:Request.ClipRequest.StartSeconds,waveform,""));
        internal void Failure(string error)=>Completion.TrySetResult(new(Request.ClipRequest.Url,
            Request.IsEnd?Request.ClipRequest.EndSeconds:Request.ClipRequest.StartSeconds,null,error));
    }
}
