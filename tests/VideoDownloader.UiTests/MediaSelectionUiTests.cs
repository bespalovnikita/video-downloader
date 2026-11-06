using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestMediaSelection(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Media-selection WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(20);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing real media control: "+name));
        static void Click(Window window,string name)=>Control<Button>(window,name).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        string fixture=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE")??"";
        Require(File.Exists(fixture),"The real native metadata fixture must be provided.");
        string root=Path.Combine(testRoot,"media-selection-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string log=Path.Combine(root,"metadata-calls.jsonl");
        string? oldLog=Environment.GetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG");
        Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",log);
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        using var subject=new NativeWindow(window,storage,fixture);
        try
        {
            window.Show();window.Width=1280;window.Height=760;window.UpdateLayout();
            var queue=Control<DataGrid>(window,"QueueGrid");
            var edit=Control<Button>(window,"MediaSelectionButton");
            Require(!edit.IsEnabled,"An empty selection must not enable per-item edits.");
            const string firstUrl="https://title-ingress.invalid/Video?token=AbC";
            const string secondUrl="https://title-ingress.invalid/video?token=AbC";
            Control<TextBox>(window,"UrlInput").Text=firstUrl+" "+secondUrl;
            Click(window,"AddUrlButton");
            PumpUntil(()=>subject.Preview.GetCached(firstUrl) is {FormatsLoaded:true,Formats.Length:>0} &&
                subject.Preview.GetCached(secondUrl) is {FormatsLoaded:true,Formats.Length:>0},
                "Cold metadata must provide actual format catalogs through the one production preview worker.");
            var first=subject.SessionState.Queue.Single(row=>row.Url==firstUrl);
            var second=subject.SessionState.Queue.Single(row=>row.Url==secondUrl);
            queue.SelectedItem=first;
            Require(edit.IsEnabled,"A single idle queue row enables the editor.");
            Click(window,"MediaSelectionButton");
            var dialog=subject.MediaSelectionEditor??throw new InvalidOperationException("The actual format editor did not open.");
            Require(dialog.IsVisible,"The actual format editor must open.");
            var catalog=Control<DataGrid>(dialog,"MediaFormatsGrid");
            Require(catalog.IsReadOnly && catalog.Items.Count==3 && catalog.Columns.Count==9,
                "The actual parsed catalog must be read-only and expose all format details.");
            string Cell(int row,int column)
            {
                catalog.ScrollIntoView(catalog.Items[row],catalog.Columns[column]);catalog.UpdateLayout();
                return (catalog.Columns[column].GetCellContent(catalog.Items[row]) as TextBlock)?.Text??"";
            }
            Require(Cell(0,0)=="137" && Cell(0,2)=="1920×1080" && Cell(0,3)=="30" && Cell(0,4)=="SDR",
                "Rendered format cells must show the selected source's ID, resolution, FPS and dynamic range.");
            Require(Cell(1,8)=="Неизвестно" && Cell(2,8).StartsWith("≈",StringComparison.Ordinal),
                "Unknown and approximate sizes must remain visibly different from exact sizes.");

            var mode=Control<ComboBox>(dialog,"MediaFormatModeCombo");
            var primary=Control<ComboBox>(dialog,"PrimaryMediaFormatCombo");
            var audio=Control<ComboBox>(dialog,"AudioMediaFormatCombo");
            var start=Control<TextBox>(dialog,"MediaSectionStartBox");
            var end=Control<TextBox>(dialog,"MediaSectionEndBox");
            mode.SelectedIndex=1;primary.SelectedValue="137";audio.SelectedValue="140";
            Require(Equals(primary.SelectedValue,"137") && Equals(audio.SelectedValue,"140"),
                "Exact source IDs must be selectable through real controls.");
            start.Text="00:00:20";end.Text="00:00:10";Click(dialog,"ApplyMediaSelectionButton");
            Require(dialog.IsVisible && first.Selection==null && Control<TextBlock>(dialog,"MediaSelectionError").Text.Length>0,
                "An inverted interval must leave the dialog open and the stored row unchanged.");
            start.Text="00:00:05";end.Text="00:00:10";audio.SelectedValue="";Click(dialog,"ApplyMediaSelectionButton");
            Require(first.Selection==null && Control<TextBlock>(dialog,"MediaSelectionError").Text.Length>0,
                "Video-only selection must not silently guess or substitute an audio format.");
            audio.SelectedValue="140";

            // The modeless editor remains bound to its captured URL even if
            // selection and a late callback now describe another source.
            queue.SelectedItem=second;
            subject.Preview.ReceiveReady(subject.Preview.GetCached(secondUrl)!);
            Require(Control<TextBox>(dialog,"MediaSelectionUrl").Text==firstUrl && Cell(0,0)=="137",
                "Another URL's selection or metadata must not replace the open editor's catalog.");
            Click(dialog,"ApplyMediaSelectionButton");
            Require(subject.MediaSelectionEditor==null && first.Selection is {Format.Id:"137",AudioFormat.Id:"140",Section.StartSeconds:5,Section.EndSeconds:10} &&
                first.Selection.Url==firstUrl && second.Selection==null && first.Status=="Queued" && second.Status=="Queued",
                "Apply changes only the captured row, without changing status or starting work.");
            var saved=storage.Restore()!;
            var restored=saved.Queue.Single(row=>row.Url==firstUrl);
            Require(restored.Selection==first.Selection,"The complete per-item selection must survive a real checkpoint write and restore.");
            queue.SelectedItem=first;
            Require(Control<TextBlock>(window,"MediaSelectionSummary").Text.Contains(first.Selection!.Summary,StringComparison.Ordinal),
                "The main summary must follow the selected row's stored choice.");

            // Reopen using data restored from disk, rather than reusing form state.
            first.Selection=restored.Selection;
            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            Require(Equals(Control<ComboBox>(dialog,"PrimaryMediaFormatCombo").SelectedValue,"137") &&
                Equals(Control<ComboBox>(dialog,"AudioMediaFormatCombo").SelectedValue,"140") &&
                Control<TextBox>(dialog,"MediaSectionStartBox").Text==DownloadSection.FormatTimestamp(5) &&
                Control<TextBox>(dialog,"MediaSectionEndBox").Text==DownloadSection.FormatTimestamp(10),
                "Restored source IDs and interval must populate the actual reopened controls.");
            Click(dialog,"ResetMediaSelectionButton");
            Require(first.Selection==null && second.Selection==null,"Reset clears only this row's planned selection.");

            string completedPath=Path.Combine(root,"kept-result.mp4");File.WriteAllText(completedPath,"kept media sentinel");
            first.Status="Completed";first.State="Done";first.AttemptFinished=true;first.Path=completedPath;
            first.OwnedPaths=[completedPath];first.OutputRoot=root;first.CompletedSelection=restored.Selection;
            subject.UpdateState();Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            dialog.ReceiveMetadata(subject.Preview.GetCached(firstUrl)!);
            Control<TextBox>(dialog,"MediaSectionStartBox").Text="00:00:10";
            Control<TextBox>(dialog,"MediaSectionEndBox").Text="00:00:15";Click(dialog,"ApplyMediaSelectionButton");
            Require(first.Status=="Completed" && first.Path==completedPath && first.OwnedPaths.SequenceEqual([completedPath]) &&
                first.CompletedSelection==restored.Selection && first.Selection?.Section is {StartSeconds:10,EndSeconds:15} &&
                File.ReadAllText(completedPath)=="kept media sentinel",
                "Planning a different fragment must preserve an already completed result and its ownership.");

            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            var previous=first.Selection;first.Status="Running";subject.UpdateState();
            Require(!edit.IsEnabled && !Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled &&
                !Control<Button>(dialog,"ResetMediaSelectionButton").IsEnabled,
                "A row that becomes active must make its open editor read-only.");
            Click(dialog,"ResetMediaSelectionButton");
            Require(first.Selection==previous,"A programmatically raised click cannot bypass the active-row guard.");
            first.Status="Completed";subject.UpdateState();Click(dialog,"CloseMediaSelectionButton");
            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            dialog.ReceiveMetadata(subject.Preview.GetCached(firstUrl)! with {IsCollection=true,Formats=[]});
            Control<ComboBox>(dialog,"MediaFormatModeCombo").SelectedIndex=0;
            Control<TextBox>(dialog,"MediaSectionStartBox").Text="00:00:01";
            Control<TextBox>(dialog,"MediaSectionEndBox").Text="00:00:02";Click(dialog,"ApplyMediaSelectionButton");
            Require(first.Selection==previous && Control<TextBlock>(dialog,"MediaSelectionError").Text.Length>0,
                "A collection cannot acquire a misleading per-video interval.");
            Click(dialog,"CloseMediaSelectionButton");
            Require(subject.SessionState.Coordinator==null && subject.SessionState.RunningTask==null && !File.Exists(storage.HistoryPath),
                "Editing and restoring selections must never start transfers or add successful history.");
            foreach(string line in File.ReadAllLines(log).Where(line=>!string.IsNullOrWhiteSpace(line)))
            {
                using var call=JsonDocument.Parse(line);
                Require(call.RootElement.GetProperty("MetadataOnly").GetBoolean(),"The cold fixture must only execute metadata requests.");
            }
            Console.WriteLine("PASS: cold actual formats, scoped row selection, interval errors, persisted editor, completed ownership and active guards through real WPF controls");
        }
        finally
        {
            subject.Dispose();if(window.IsVisible)window.Close();
            Environment.SetEnvironmentVariable("VD_NATIVE_TITLE_FIXTURE_LOG",oldLog);
        }
    }
}
