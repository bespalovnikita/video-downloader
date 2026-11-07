using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;
using ListBox=System.Windows.Controls.ListBox;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestFragmentMergeSelection(string testRoot)
    {
        static void Require(bool value,string message)
        {if(!value)throw new InvalidOperationException("Fragment-merge WPF test: "+message);}
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing actual fragment output control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        string root=Path.Combine(testRoot,"merge-selection-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(root);
        string oldFile=Path.Combine(root,"prior-result.mp4");File.WriteAllText(oldFile,"PRIOR OWNED RESULT");
        const string url="https://merge-selection.invalid/Video?token=AbC";
        var prior=new DownloadSelection(url,new DownloadSection(0,1),DurationSeconds:12);
        DownloadSection[] ordered=[new(5,7),new(1,2)];
        var initial=new DownloadSelection(url,ordered[0],DurationSeconds:12){Sections=new DownloadSectionSet(ordered)};
        var row=new DownloadItem{Url=url,Title="Merge selection",Status="Completed",State="Done",Path=oldFile,
            OutputRoot=root,OwnedPaths=[oldFile],CompletedSelection=prior,Selection=initial,AttemptFinished=true};
        var options=new DownloadOptions{Output=root,Container="MP4"};
        var storage=new AppPersistence(Path.Combine(root,"profile"));int applied=0;
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);var owner=(Window)XamlReader.Load(reader);
        MediaSelectionDialog? editor=null;
        MediaSelectionDialog Open()
        {
            var result=new MediaSelectionDialog(owner,row,()=>options,selection=>
            {row.Selection=selection;storage.Save(new[]{row},options);applied++;return null;},clipPlayer:new ClipUiPlayer());
            result.Show();result.ReceiveMetadata(new PreviewData(url,row.Title,"fixture","",12,"fixture",360,25,
                "H264","SDR","",DateTime.UtcNow){FormatsLoaded=true});return result;
        }
        void PriorUntouched()
        {
            Require(row.CompletedSelection==prior && row.Status=="Completed" && row.Path==oldFile &&
                row.OwnedPaths.SequenceEqual(new[]{oldFile}) && File.ReadAllText(oldFile)=="PRIOR OWNED RESULT" &&
                storage.ReadHistory().Count==0,"Changing output mode must preserve the previous result and full-video history.");
        }
        try
        {
            owner.Show();editor=Open();
            var merge=Control<CheckBox>(editor,"MergeMediaSectionsCheck");
            var encode=Control<CheckBox>(editor,"AllowMergeReencodeCheck");
            var note=Control<TextBlock>(editor,"MediaMergeCompatibilityText");
            Require(merge.IsChecked==false && merge.IsEnabled && encode.IsChecked==false && !encode.IsEnabled,
                "An existing ordered multi-range plan must default to separate files without reencoding consent.");
            merge.IsChecked=true;encode.IsChecked=true;
            Require(encode.IsEnabled && note.Text.Contains("проверяется после",StringComparison.Ordinal) &&
                note.Text.Contains("порядке списка",StringComparison.Ordinal) && row.Selection==initial && applied==0,
                "The actual controls must keep a local draft and explain compatibility checks before Start.");
            merge.IsChecked=false;
            Require(encode.IsChecked==false && !encode.IsEnabled,"Returning to separate files must clear reencoding consent.");
            merge.IsChecked=true;editor.SetEditable(false);
            merge.IsChecked=false;encode.IsChecked=true;Click(editor,"ApplyMediaSelectionButton");
            Require(merge.IsChecked==true && encode.IsChecked==false && !merge.IsEnabled && !encode.IsEnabled &&
                applied==0 && row.Selection==initial,"Programmatic events must not bypass read-only output-mode guards.");
            editor.SetEditable(true);Click(editor,"ApplyMediaSelectionButton");
            Require(!editor.IsVisible && applied==1 && row.Selection is {IsMerged:true,AllowMergeReencode:false} &&
                row.Selection.EffectiveSections.SequenceEqual(ordered),
                "Apply must capture one-file mode, strict stream-copy policy and the user's range order.");
            PriorUntouched();
            Require(storage.Restore()!.Queue.Single().Selection==row.Selection,
                "The actual checkpoint must round-trip output mode and all immutable ranges.");editor=null;

            editor=Open();merge=Control<CheckBox>(editor,"MergeMediaSectionsCheck");
            encode=Control<CheckBox>(editor,"AllowMergeReencodeCheck");
            Require(merge.IsChecked==true && encode.IsChecked==false,"Reopening must restore merged mode without inventing consent.");
            encode.IsChecked=true;Click(editor,"ApplyMediaSelectionButton");
            Require(applied==2 && row.Selection is {IsMerged:true,AllowMergeReencode:true} &&
                storage.Restore()!.Queue.Single().Selection==row.Selection,
                "Only an explicit checkbox choice may persist fallback reencoding consent.");editor=null;

            editor=Open();var saved=row.Selection;
            merge=Control<CheckBox>(editor,"MergeMediaSectionsCheck");merge.IsChecked=false;editor.Close();editor=null;
            Require(row.Selection==saved && applied==2,"Closing an edited output-mode draft must not apply it.");

            editor=Open();var multiple=Control<CheckBox>(editor,"MultipleMediaSectionsCheck");
            var list=Control<ListBox>(editor,"MediaSectionsList");list.SelectedIndex=1;multiple.IsChecked=false;
            merge=Control<CheckBox>(editor,"MergeMediaSectionsCheck");encode=Control<CheckBox>(editor,"AllowMergeReencodeCheck");
            Require(merge.IsChecked==false && encode.IsChecked==false && !merge.IsEnabled && !encode.IsEnabled,
                "Switching to a single range must restore the legacy separate-file mode.");
            merge.IsChecked=true;Require(merge.IsChecked==false,"A disabled one-range merge control must reject direct events.");
            Click(editor,"ApplyMediaSelectionButton");
            Require(applied==3 && row.Selection is {FragmentOutputMode:"Separate",AllowMergeReencode:false,Sections:null} &&
                row.Selection.Section==new DownloadSection(1,2),"Single-range Apply must keep the selected range and legacy output behavior.");
            PriorUntouched();editor=null;

            editor=Open();multiple=Control<CheckBox>(editor,"MultipleMediaSectionsCheck");multiple.IsChecked=true;
            merge=Control<CheckBox>(editor,"MergeMediaSectionsCheck");merge.IsChecked=true;
            Click(editor,"ApplyMediaSelectionButton");
            Require(editor.IsVisible && applied==3 &&
                Control<TextBlock>(editor,"MediaSelectionError").Text.Contains("не менее двух",StringComparison.Ordinal),
                "A one-range draft must explain why it cannot be saved as a merged result.");
            Click(editor,"AddMediaSectionButton");Control<TextBox>(editor,"MediaSectionStartBox").Text="8";
            Control<TextBox>(editor,"MediaSectionEndBox").Text="9";
            options.WriteSubs=true;Click(editor,"ApplyMediaSelectionButton");
            Require(editor.IsVisible && applied==3 &&
                Control<TextBlock>(editor,"MediaSelectionError").Text.Contains("субтит",StringComparison.Ordinal),
                "Unavailable subtitle retiming must be explained without discarding tracks or applying the draft.");
            options.WriteSubs=false;Control<CheckBox>(editor,"AllowMergeReencodeCheck").IsChecked=true;
            Click(editor,"ApplyMediaSelectionButton");
            Require(applied==4 && row.Selection is {IsMerged:true,AllowMergeReencode:true} &&
                row.Selection.EffectiveSections.SequenceEqual(new[]{new DownloadSection(1,2),new DownloadSection(8,9)}),
                "A corrected draft must retain explicit consent and both ranges.");editor=null;PriorUntouched();

            editor=Open();Click(editor,"ResetMediaSelectionButton");
            Require(row.Selection is null && applied==5,"The existing reset action must clear merged-mode planning too.");
            editor=null;PriorUntouched();
            Console.WriteLine("PASS: actual WPF fragment output choice, explicit reencode consent, compatibility explanation, range order, checkpoint and Completed ownership");
        }
        finally{editor?.Close();owner.Close();}
    }
}
