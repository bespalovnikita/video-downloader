using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestFragmentTimeline(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Fragment timeline WPF: "+message);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing real timeline control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();var watch=System.Diagnostics.Stopwatch.StartNew();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || watch.Elapsed>TimeSpan.FromSeconds(6))frame.Continue=false;};
            timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static void Drag(Thumb handle,double change,Window window)
        {
            handle.RaiseEvent(new DragStartedEventArgs(0,0){RoutedEvent=Thumb.DragStartedEvent});
            handle.RaiseEvent(new DragDeltaEventArgs(change,0){RoutedEvent=Thumb.DragDeltaEvent});
            handle.RaiseEvent(new DragCompletedEventArgs(change,0,false){RoutedEvent=Thumb.DragCompletedEvent});window.UpdateLayout();
        }
        static void KeyPress(Thumb handle,Key key,Window window)
        {
            var source=PresentationSource.FromVisual(window)??throw new InvalidOperationException("A shown WPF source is required.");
            handle.RaiseEvent(new KeyEventArgs(Keyboard.PrimaryDevice,source,Environment.TickCount,key){RoutedEvent=Keyboard.KeyDownEvent});
            window.UpdateLayout();
        }
        string root=Path.Combine(testRoot,"fragment-timeline");Directory.CreateDirectory(root);
        string resultPath=Path.Combine(root,"kept-result.mp4");File.WriteAllText(resultPath,"completed sentinel");
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var owner=(Window)XamlReader.Load(reader);
        var row=new DownloadItem{Url="https://timeline-ui.invalid/Video?token=AbC",Title="Timeline fixture",Status="Completed",State="Done",
            Path=resultPath,OwnedPaths=[resultPath],OutputRoot=root,AttemptFinished=true,
            CompletedSelection=new DownloadSelection("https://timeline-ui.invalid/Video?token=AbC",new DownloadSection(1,2))};
        var originalCompleted=row.CompletedSelection;
        var formats=new[]{new MediaFormat("v25","mp4",640,360,25,"avc1","none","",1000,false),
            new MediaFormat("audio","m4a",0,0,0,"none","mp4a","en",200,false)};
        var metadata=new PreviewData(row.Url,row.Title,"fixture","",12,"fixture",360,25,"H264","SDR","",DateTime.UtcNow)
            {FormatsLoaded=true,Formats=formats};
        var requests=new List<MediaFrameRequest>();
        byte[] png=SectionFramePng(Colors.Red);
        Task<MediaFrameResult> Frame(MediaFrameRequest request,CancellationToken token)
        {requests.Add(request);return Task.FromResult(new MediaFrameResult(request.Url,request.Seconds,request.Seconds,png,""));}
        int applied=0;MediaSelectionDialog? dialog=null;
        try
        {
            owner.Show();
            dialog=new MediaSelectionDialog(owner,row,()=>new DownloadOptions(),selection=>{row.Selection=selection;applied++;return null;},Frame);
            dialog.Show();dialog.UpdateLayout();
            var start=Control<TextBox>(dialog,"MediaSectionStartBox");var end=Control<TextBox>(dialog,"MediaSectionEndBox");
            var timeline=Control<FragmentTimeline>(dialog,"MediaSectionTimeline");
            var first=Control<Thumb>(dialog,"MediaSectionStartThumb");var last=Control<Thumb>(dialog,"MediaSectionEndThumb");
            Require(!first.IsEnabled && !last.IsEnabled,"A timeline cannot guess a duration before metadata arrives.");
            dialog.ReceiveMetadata(metadata);timeline.BringIntoView();dialog.UpdateLayout();
            Require(first.IsEnabled && last.IsEnabled && timeline.ActualWidth>120 &&
                AutomationProperties.GetName(first).Length>0 && AutomationProperties.GetName(last).Length>0,
                "Both real accessible handles must be usable after an individual video's metadata arrives.");
            Require(start.Text.Length==0 && end.Text.Length==0,"Metadata alone must preserve the implicit full-video selection.");
            double pixelsPerSecond=(timeline.ActualWidth-16)/12;
            Drag(first,2*pixelsPerSecond,dialog);Drag(last,-3*pixelsPerSecond,dialog);
            Require(DownloadSection.TryParse(start.Text,end.Text,12,out var range,out _) && range is {StartSeconds:2,EndSeconds:9},
                "Actual thumb drag events must update both existing timestamp fields and retain a valid range.");
            Require(Control<TextBlock>(dialog,"MediaSectionTimelineStatus").Text.Contains(DownloadSection.FormatTimestamp(7),StringComparison.Ordinal),
                "The selected duration must be visible after dragging both boundaries.");
            PumpUntil(()=>requests.Any(request=>request.Seconds==2 && !request.IsEnd) && requests.Any(request=>request.Seconds==9 && request.IsEnd),
                "Timeline edits must reach the existing two debounced frame previews with their exact boundary times.");
            Require(row.Selection==null && applied==0 && row.Status=="Completed" && row.CompletedSelection==originalCompleted &&
                row.Path==resultPath && File.ReadAllText(resultPath)=="completed sentinel",
                "Preview and range gestures must not apply, restart or replace an existing completed result.");

            start.Text="3.48";end.Text="8.48";dialog.UpdateLayout();
            Require(timeline.StartSeconds==3.48 && timeline.EndSeconds==8.48 && Canvas.GetLeft(last)>Canvas.GetLeft(first),
                "Typing exact times must reposition the visible timeline handles without mutating the row.");
            KeyPress(first,Key.Right,dialog);
            Require(start.Text==DownloadSection.FormatTimestamp(3.52),"The right arrow must advance the start by one 25-FPS source frame.");
            Click(dialog,"MediaSectionStartPreviousFrameButton");
            Require(start.Text==DownloadSection.FormatTimestamp(3.48),"The previous-frame button must return to the same source frame.");
            Click(dialog,"MediaSectionEndNextFrameButton");
            Require(end.Text==DownloadSection.FormatTimestamp(8.52),"The end boundary must have its own next-frame control.");
            KeyPress(last,Key.End,dialog);KeyPress(first,Key.Home,dialog);
            Require(start.Text==DownloadSection.FormatTimestamp(0) && end.Text==DownloadSection.FormatTimestamp(12),
                "Home/End on the real handles must navigate to legal source edges.");

            var mixed=metadata with{Formats=[formats[0],formats[0] with{Id="v50",Fps=50},formats[1]]};
            dialog.ReceiveMetadata(mixed);start.Text="1";end.Text="2";
            Require(Control<TextBlock>(dialog,"MediaSectionStepStatus").Text.Contains("0.1",StringComparison.Ordinal),
                "Automatic selection with different source FPS must disclose its seconds fallback.");
            Click(dialog,"MediaSectionStartNextFrameButton");
            Require(start.Text==DownloadSection.FormatTimestamp(1.1),"Unknown FPS must use the labelled 0.1-second step.");
            Control<ComboBox>(dialog,"MediaFormatModeCombo").SelectedIndex=1;
            Control<ComboBox>(dialog,"PrimaryMediaFormatCombo").SelectedValue="v25";
            start.Text="1";end.Text="2";Click(dialog,"MediaSectionStartNextFrameButton");
            Require(start.Text==DownloadSection.FormatTimestamp(1.04) && Control<TextBlock>(dialog,"MediaSectionStepStatus").Text.Contains("25 кадр/с",StringComparison.Ordinal),
                "An exact selected video format must restore its known frame grid.");
            Drag(first,100000,dialog);
            Require(DownloadSection.TryParse(start.Text,end.Text,12,out var clamped,out _) && clamped!.EndSeconds-clamped.StartSeconds>=.001-1e-9,
                "A large drag must clamp before the opposite boundary instead of crossing it.");

            string readonlyStart=start.Text,readonlyEnd=end.Text;dialog.SetEditable(false);
            Require(!first.IsEnabled && !last.IsEnabled && !Control<Button>(dialog,"MediaSectionStartNextFrameButton").IsEnabled,
                "Read-only rows must disable handles and frame-step controls.");
            Drag(first,-100,dialog);KeyPress(last,Key.Left,dialog);Click(dialog,"MediaSectionStartNextFrameButton");
            Require(start.Text==readonlyStart && end.Text==readonlyEnd,"Programmatically raised drag/key/button events cannot bypass read-only guards.");
            dialog.SetEditable(true);Control<ComboBox>(dialog,"MediaFormatModeCombo").SelectedIndex=0;
            start.Text="1";end.Text="2";dialog.ReceiveMetadata(metadata with{Duration=0,Formats=mixed.Formats});
            Require(!first.IsEnabled && Control<Button>(dialog,"MediaSectionEndNextFrameButton").IsEnabled &&
                Control<TextBlock>(dialog,"MediaSectionTimelineStatus").Text.Contains("не сообщил длительность",StringComparison.Ordinal),
                "An unknown duration disables the scale while valid text can still be stepped with an honest fallback.");
            Click(dialog,"MediaSectionEndNextFrameButton");Require(end.Text==DownloadSection.FormatTimestamp(2.1),"Unknown-duration text editing must remain usable.");
            start.Text="NaN";
            Require(start.Text=="NaN" && !Control<Button>(dialog,"MediaSectionEndNextFrameButton").IsEnabled,
                "Malformed typed input must remain visible for correction instead of being silently rewritten.");
            dialog.ReceiveMetadata(metadata with{IsCollection=true});start.Text="1";end.Text="4";
            Require(!first.IsEnabled && !last.IsEnabled && !Control<Button>(dialog,"MediaSectionEndNextFrameButton").IsEnabled,
                "A collection cannot acquire a misleading per-video timeline.");
            dialog.ReceiveMetadata(metadata with{Duration=12.34567});Click(dialog,"MediaSectionFocusEndButton");
            KeyPress(last,Key.End,dialog);
            Require(DownloadSection.TryParse(start.Text,end.Text,12.34567,out var precise,out _) && precise!.EndSeconds==12.345,
                "The source-end handle must use a legal millisecond before a fractional duration, never round beyond it.");
            start.Text="1";end.Text="4";Click(dialog,"ApplyMediaSelectionButton");
            Require(!dialog.IsVisible && applied==1 && row.Selection?.Section is {StartSeconds:1,EndSeconds:4} &&
                row.Status=="Completed" && row.CompletedSelection==originalCompleted && row.OwnedPaths.SequenceEqual([resultPath]),
                "Only explicit Apply stores the chosen range, preserving the earlier completed result and file ownership.");
            dialog=null;
            var persistence=new AppPersistence(Path.Combine(root,"profile"));persistence.Save([row],new DownloadOptions());
            var restored=persistence.Restore()!.Queue.Single();
            dialog=new MediaSelectionDialog(owner,restored,()=>new DownloadOptions(),_=>null,Frame);dialog.Show();dialog.ReceiveMetadata(metadata);
            Require(Control<FragmentTimeline>(dialog,"MediaSectionTimeline").StartSeconds==1 &&
                Control<FragmentTimeline>(dialog,"MediaSectionTimeline").EndSeconds==4,
                "A newly opened editor must reflect a real persisted/restored range without resetting it.");
            Console.WriteLine("PASS: real timeline thumb drags, keyboard/frame steps, text and frame synchronization, duration, readonly and restored range");
        }
        finally{dialog?.Close();if(owner.IsVisible)owner.Close();}
    }
}
