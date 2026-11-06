using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using ListBox=System.Windows.Controls.ListBox;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestMultipleFragments(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Multiple-fragment WPF test: "+message);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual multi-fragment control: "+name));
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
        static IEnumerable<TextBlock> Texts(DependencyObject item)
        {
            if(item is TextBlock text)yield return text;
            for(int index=0;index<VisualTreeHelper.GetChildrenCount(item);index++)
                foreach(var child in Texts(VisualTreeHelper.GetChild(item,index)))yield return child;
        }
        string root=Path.Combine(testRoot,"multiple-fragments-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string completed=Path.Combine(root,"existing.mp4");File.WriteAllText(completed,"EXISTING RESULT");
        var prior=new DownloadSelection("https://multiple-ui.invalid/Video",new DownloadSection(0,1),DurationSeconds:12);
        var row=new DownloadItem{Url=prior.Url,Title="Multiple clips",Status="Completed",State="Done",Path=completed,OutputRoot=root,
            OwnedPaths=new(){completed},CompletedSelection=prior,AttemptFinished=true};
        var options=new DownloadOptions{Output=root};
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var owner=(Window)XamlReader.Load(reader);
        MediaSelectionDialog? dialog=null;
        ClipUiPlayer? player=null;var playbackCalls=new List<ClipUiCall>();
        int applied=0;
        string? Apply(DownloadSelection? selection)
        {row.Selection=selection;storage.Save(new[]{row},options);applied++;return null;}
        MediaSelectionDialog Open()
        {
            player=new ClipUiPlayer();
            var editor=new MediaSelectionDialog(owner,row,()=>options,Apply,
                (request,_)=>Task.FromResult(new MediaFrameResult(request.Url,request.Seconds,null,[],"Optional fixture frame unavailable")),
                (request,token)=>{var call=new ClipUiCall(request,token);playbackCalls.Add(call);return call.Completion.Task;},player);
            editor.Show();editor.ReceiveMetadata(new PreviewData(row.Url,row.Title,"fixture","",12,"fixture",360,25,"H264","SDR","",DateTime.UtcNow)
                {FormatsLoaded=true});return editor;
        }
        try
        {
            owner.Show();owner.Width=1280;owner.Height=760;owner.UpdateLayout();dialog=Open();
            var multiple=Control<CheckBox>(dialog,"MultipleMediaSectionsCheck");
            var list=Control<ListBox>(dialog,"MediaSectionsList");
            var start=Control<TextBox>(dialog,"MediaSectionStartBox");var end=Control<TextBox>(dialog,"MediaSectionEndBox");
            start.Text="1";end.Text="2";multiple.IsChecked=true;
            Require(list.Items.Count==1 && start.Text=="1" && end.Text=="2","Enabling the list must retain the currently edited single range");
            Click(dialog,"AddMediaSectionButton");
            Require(list.Items.Count==2 && list.SelectedIndex==1 && start.Text=="" && end.Text=="", "Adding a range must select a new independently editable draft");
            start.Text="1.5";end.Text="3";Click(dialog,"ApplyMediaSelectionButton");
            Require(dialog.IsVisible && applied==0 && Control<TextBlock>(dialog,"MediaSelectionError").Text.Contains("пересек",StringComparison.Ordinal),
                "Overlapping drafts must explain the refusal without committing or closing");
            start.Text="5";end.Text="7";
            list.BringIntoView();dialog.UpdateLayout();
            Require(Texts(list).Any(text=>text.Text.Contains("00:00:05",StringComparison.Ordinal)),"The actual list must render the edited range label");
            Click(dialog,"MoveMediaSectionUpButton");
            Require(list.SelectedIndex==0 && start.Text=="5" && end.Text=="7","Reordering must preserve selection and the current range's editor");
            list.SelectedIndex=1;Require(start.Text=="1" && end.Text=="2","Selecting another range must update the existing time/frame editor");
            Click(dialog,"PreviewMediaClipStartButton");var pending=playbackCalls.Single();
            Require(pending.Request.StartSeconds==1 && pending.Request.EndSeconds==2,
                "Playback must request the currently selected draft rather than the first range in the list");
            list.SelectedIndex=0;
            Require(pending.Token.IsCancellationRequested && row.Selection is null && applied==0,
                "Switching the selected draft must cancel pending playback without applying the download plan");
            string stalePath=Path.Combine(root,"stale-preview.mp4");File.WriteAllText(stalePath,"late owned preview");
            pending.Success(new(stalePath,1,2,true,true,()=>File.Delete(stalePath)));
            PumpUntil(()=>!File.Exists(stalePath),"The previous draft's late preview must be disposed without attaching to the player");
            Require(player!.Source is null,"The late previous range must never replace the selected draft's player");
            Click(dialog,"PreviewMediaClipStartButton");var prepared=playbackCalls.Last();
            Require(prepared.Request.StartSeconds==5 && prepared.Request.EndSeconds==7,
                "The newly selected draft must supply both source boundaries to playback");
            string previewPath=Path.Combine(root,"selected-preview.mp4");File.WriteAllText(previewPath,"prepared owned preview");
            prepared.Success(new(previewPath,5,7,true,true,()=>
            {
                Require(player!.Source is null,"Switching ranges must detach the native graph before deleting its prepared file");
                File.Delete(previewPath);
            }));
            PumpUntil(()=>player!.Source?.LocalPath==previewPath,"The current draft's prepared preview must attach to its player");
            player.RaiseOpened();list.SelectedIndex=1;
            Require(player.Source is null && !File.Exists(previewPath) && row.Selection is null && applied==0 &&
                row.CompletedSelection==prior && row.OwnedPaths.SequenceEqual(new[]{completed}),
                "Switching away from a playing draft must release its preview while preserving the unapplied plan and published result");
            var timeline=Control<FragmentTimeline>(dialog,"MediaSectionTimeline");
            Require(timeline.StartSeconds==1 && timeline.EndSeconds==2,"Selecting a range must synchronize the existing timeline");
            Click(dialog,"MediaSectionEndNextFrameButton");
            Require(timeline.EndSeconds==2.1,"A timeline step must update the selected draft through the shared fields");
            list.SelectedIndex=0;
            Require(timeline.StartSeconds==5 && timeline.EndSeconds==7,"Stepping one draft must preserve other ranges");
            list.SelectedIndex=1;
            Require(timeline.EndSeconds==2.1 && end.Text==DownloadSection.FormatTimestamp(2.1),"Reselecting must restore the stepped draft without losing its edit");
            start.Text="2";end.Text="3"; // No separate Save action: Apply must use the latest draft text.
            Click(dialog,"AddMediaSectionButton");start.Text="3";end.Text="4";
            Click(dialog,"RemoveMediaSectionButton");
            Require(list.Items.Count==2 && start.Text=="2" && end.Text=="3","Removing a draft must retain the neighbouring draft and its latest values");
            Click(dialog,"ApplyMediaSelectionButton");
            Require(!dialog.IsVisible && applied==1 && row.Selection?.EffectiveSections.SequenceEqual(new[]{new DownloadSection(5,7),new DownloadSection(2,3)})==true,
                "Apply must commit every range in the visible user order, including unsaved field edits");
            Require(row.Status=="Completed" && row.Path==completed && row.OwnedPaths.SequenceEqual(new[]{completed}) &&
                row.CompletedSelection==prior && File.ReadAllText(completed)=="EXISTING RESULT",
                "Editing multiple planned fragments must preserve the previous Completed result and its ownership");
            Require(storage.Restore()!.Queue.Single().Selection==row.Selection && storage.ReadHistory().Count==0,
                "The real checkpoint must restore all ranges without recording full-video success");
            dialog=null;

            dialog=Open();multiple=Control<CheckBox>(dialog,"MultipleMediaSectionsCheck");list=Control<ListBox>(dialog,"MediaSectionsList");
            start=Control<TextBox>(dialog,"MediaSectionStartBox");end=Control<TextBox>(dialog,"MediaSectionEndBox");
            Require(multiple.IsChecked==true && list.Items.Count==2 && start.Text==DownloadSection.FormatTimestamp(5),
                "Reopening must recover the complete stored range list and select its first entry");
            var saved=row.Selection;dialog.SetEditable(false);
            Require(!Control<Button>(dialog,"AddMediaSectionButton").IsEnabled && !Control<Button>(dialog,"RemoveMediaSectionButton").IsEnabled,
                "Read-only/current-session guards must disable range mutations");
            Click(dialog,"RemoveMediaSectionButton");Click(dialog,"AddMediaSectionButton");multiple.IsChecked=false;
            Require(list.Items.Count==2 && multiple.IsChecked==true && row.Selection==saved,
                "Programmatic clicks and checkbox events must not bypass the read-only guard");
            dialog.SetEditable(true);list.SelectedIndex=1;multiple.IsChecked=false;
            Require(start.Text==DownloadSection.FormatTimestamp(2) && end.Text==DownloadSection.FormatTimestamp(3),
                "Turning off the list must retain the currently selected single range");
            start.Text="";end.Text="";Click(dialog,"ApplyMediaSelectionButton");
            Require(row.Selection==null && applied==2 && File.Exists(completed),"Full-video reset must clear only the planned ranges without deleting the previous result");
            dialog=null;
            Console.WriteLine("PASS: real WPF multi-range list, draft text/selection/order, overlap validation, checkpoint, Completed ownership and readonly guards");
        }
        finally{dialog?.Close();foreach(var call in playbackCalls)call.Completion.TrySetCanceled();if(owner.IsVisible)owner.Close();}
    }
}
