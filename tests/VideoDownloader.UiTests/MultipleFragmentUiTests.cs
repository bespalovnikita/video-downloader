using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
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
        int applied=0;
        string? Apply(DownloadSelection? selection)
        {row.Selection=selection;storage.Save(new[]{row},options);applied++;return null;}
        MediaSelectionDialog Open()
        {
            var editor=new MediaSelectionDialog(owner,row,()=>options,Apply,
                (request,_)=>Task.FromResult(new MediaFrameResult(request.Url,request.Seconds,null,[],"Optional fixture frame unavailable")));
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
        finally{dialog?.Close();if(owner.IsVisible)owner.Close();}
    }
}
