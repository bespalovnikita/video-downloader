using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;
using System.Windows.Input;
using System.Windows.Interop;

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
        static void NativeEditorKey(Window dialog,Key key,Func<bool> ready,string message)
        {
            int observed=0;
            PreProcessInputEventHandler observe=(_,e)=>
            {
                if(e.StagingItem.Input is System.Windows.Input.KeyEventArgs input && input.Key==key &&
                    input.RoutedEvent==Keyboard.PreviewKeyDownEvent)observed++;
            };
            InputManager.Current.PreProcessInput+=observe;
            try
            {
                nint target=MediaEditorKeyboardInput.GetFocus();
                if(target==0)target=new WindowInteropHelper(dialog).Handle;
                nint virtualKey=KeyInterop.VirtualKeyFromKey(key);
                int down=((key==Key.Escape?1:0x1c)<<16)|1;
                Require(PostMessageW(target,0x0100,virtualKey,(nint)down) &&
                    PostMessageW(target,0x0101,virtualKey,(nint)(down|unchecked((int)0xc0000000))),
                    "The actual HWND keyboard messages must be queued.");
                PumpUntil(()=>observed>0 && ready(),message);
            }
            finally{InputManager.Current.PreProcessInput-=observe;}
        }

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
            dialog.UpdateLayout();
            var expert=Control<Expander>(dialog,"MediaFormatsExpander");
            var catalog=Control<DataGrid>(dialog,"MediaFormatsGrid");
            var scroll=Control<ScrollViewer>(dialog,"MediaSelectionScrollViewer");
            var simpleStart=Control<TextBox>(dialog,"MediaSectionStartBox");
            var simpleEnd=Control<TextBox>(dialog,"MediaSectionEndBox");
            Rect VisibleBounds(FrameworkElement control)=>control.TransformToAncestor(scroll)
                .TransformBounds(new Rect(new System.Windows.Point(),control.RenderSize));
            var rangeBounds=VisibleBounds(simpleStart);
            Require(!expert.IsExpanded && !catalog.IsVisible && simpleStart.IsVisible &&
                rangeBounds.Top>=0 && rangeBounds.Bottom<=scroll.ActualHeight,
                "An automatic fragment must open at its visible time fields, with the expert catalog collapsed.");
            var unchangedMode=Control<CheckBox>(dialog,"MultipleMediaSectionsCheck");
            unchangedMode.IsChecked=true;unchangedMode.IsChecked=false;
            dialog.Activate();simpleStart.Focus();
            PumpUntil(()=>simpleStart.IsKeyboardFocused,"The actual empty editor must accept keyboard focus before Escape.");
            NativeEditorKey(dialog,Key.Escape,()=>subject.MediaSelectionEditor is null,
                "Escape must close an unchanged editor immediately even after visiting multiple-range mode without editing.");
            Require(first.Selection is null,"Closing an unchanged editor must not apply a plan.");
            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            expert=Control<Expander>(dialog,"MediaFormatsExpander");catalog=Control<DataGrid>(dialog,"MediaFormatsGrid");
            scroll=Control<ScrollViewer>(dialog,"MediaSelectionScrollViewer");
            simpleStart=Control<TextBox>(dialog,"MediaSectionStartBox");simpleEnd=Control<TextBox>(dialog,"MediaSectionEndBox");
            simpleStart.Text="2";simpleEnd.Text="4";
            var formatMode=Control<ComboBox>(dialog,"MediaFormatModeCombo");
            dialog.Activate();formatMode.BringIntoView();formatMode.Focus();
            PumpUntil(()=>formatMode.IsKeyboardFocused,"The real format ComboBox must own keyboard focus before opening.");
            formatMode.IsDropDownOpen=true;
            PumpUntil(()=>formatMode.IsDropDownOpen,"The actual format ComboBox popup must open before native Escape.");
            NativeEditorKey(dialog,Key.Escape,()=>!formatMode.IsDropDownOpen,
                "Escape must reach the ComboBox and close its popup before the editor handles it.");
            Require(dialog.IsVisible && ReferenceEquals(subject.MediaSelectionEditor,dialog) &&
                Control<StackPanel>(dialog,"MediaDiscardDraftPanel").Visibility==Visibility.Collapsed &&
                simpleStart.Text=="2" && simpleEnd.Text=="4" && first.Selection is null,
                "Dismissing the popup must preserve the unapplied range and avoid a discard prompt.");
            simpleStart.BringIntoView();simpleStart.Focus();
            PumpUntil(()=>simpleStart.IsKeyboardFocused,"The edited range must own keyboard focus before editor Escape.");
            NativeEditorKey(dialog,Key.Escape,()=>Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "Escape from the editor must protect an edited draft instead of closing it.");
            Require(dialog.IsVisible && simpleStart.Text=="2" && simpleEnd.Text=="4" && first.Selection is null,
                "The pinned discard choice must preserve the current input and stored plan.");
            Require(Control<Button>(dialog,"ContinueMediaEditingButton").IsDefault &&
                !Control<Button>(dialog,"ApplyMediaSelectionButton").IsDefault,
                "The dirty-draft prompt must use Continue editing as its safe Enter default.");
            NativeEditorKey(dialog,Key.Return,()=>!Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "Native Enter on the discard choice must continue editing rather than apply the draft.");
            PumpUntil(()=>simpleStart.IsKeyboardFocused,"Continuing must restore focus to the original time field.");
            Require(Control<StackPanel>(dialog,"MediaDiscardDraftPanel").Visibility==Visibility.Collapsed &&
                Control<Button>(dialog,"ApplyMediaSelectionButton").IsDefault && first.Selection is null,
                "Continuing must remove the confirmation without changing the draft.");
            NativeEditorKey(dialog,Key.Escape,()=>Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "The edited range must open its discard choice before the row becomes active.");
            first.Status="Running";subject.UpdateState();
            Require(!simpleStart.IsEnabled && !Control<Button>(dialog,"ApplyMediaSelectionButton").IsEnabled,
                "A row becoming active while the discard choice is open must disable editing and Apply.");
            NativeEditorKey(dialog,Key.Return,()=>!Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible &&
                Control<Button>(dialog,"CloseMediaSelectionButton").IsKeyboardFocused,
                "Native Enter must continue with focus on Close when the original field has become read-only.");
            Require(dialog.IsVisible && simpleStart.Text=="2" && simpleEnd.Text=="4" && first.Selection is null,
                "Continuing a now read-only draft must preserve it without applying or closing the editor.");
            first.Status="Queued";subject.UpdateState();
            Require(PostMessageW(new WindowInteropHelper(dialog).Handle,0x0112,(nint)0xf060,0),
                "The native chrome Close command must be queued.");
            PumpUntil(()=>Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "Chrome Close and Alt+F4 must use the same dirty-draft protection.");
            Click(dialog,"ContinueMediaEditingButton");
            Click(dialog,"ApplyMediaSelectionButton");
            Require(subject.MediaSelectionEditor==null && first.Selection is {Format:null,Section.StartSeconds:2,Section.EndSeconds:4} &&
                first.Status=="Queued" && subject.SessionState.Coordinator==null,
                "A normal fragment must apply through the default automatic mode without opening or choosing technical formats.");
            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            var savedSingle=first.Selection;
            var hiddenMultiple=Control<CheckBox>(dialog,"MultipleMediaSectionsCheck");
            var retainedList=Control<System.Windows.Controls.ListBox>(dialog,"MediaSectionsList");
            var retainedStart=Control<TextBox>(dialog,"MediaSectionStartBox");
            var retainedEnd=Control<TextBox>(dialog,"MediaSectionEndBox");
            hiddenMultiple.IsChecked=true;Click(dialog,"AddMediaSectionButton");
            retainedStart.Text="6";retainedEnd.Text="8";retainedList.SelectedIndex=0;hiddenMultiple.IsChecked=false;
            Require(retainedList.Items.Count==2 && !retainedList.IsVisible &&
                retainedStart.Text==DownloadSection.FormatTimestamp(2) && retainedEnd.Text==DownloadSection.FormatTimestamp(4) &&
                first.Selection==savedSingle,
                "The saved single range must remain unchanged while an additional unapplied draft is hidden.");
            dialog.Activate();retainedStart.BringIntoView();retainedStart.Focus();
            PumpUntil(()=>retainedStart.IsKeyboardFocused,"The unchanged active range must own focus before Escape with hidden drafts.");
            NativeEditorKey(dialog,Key.Escape,()=>Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "Native Escape must protect retained extra drafts even when the visible single range matches the saved plan.");
            Require(dialog.IsVisible && ReferenceEquals(subject.MediaSelectionEditor,dialog) && first.Selection==savedSingle,
                "Protecting hidden drafts must not close the editor or apply its inactive ranges.");
            Click(dialog,"ContinueMediaEditingButton");hiddenMultiple.IsChecked=true;retainedList.SelectedIndex=1;
            Require(retainedStart.Text=="6" && retainedEnd.Text=="8" && first.Selection==savedSingle,
                "Continuing after Escape must restore the additional draft without persisting it.");
            Click(dialog,"ResetMediaSelectionButton");
            Require(first.Selection==null,"Reset must restore the full automatic selection after the simple-fragment probe.");
            Click(window,"MediaSelectionButton");dialog=subject.MediaSelectionEditor!;
            expert=Control<Expander>(dialog,"MediaFormatsExpander");
            catalog=Control<DataGrid>(dialog,"MediaFormatsGrid");
            scroll=Control<ScrollViewer>(dialog,"MediaSelectionScrollViewer");
            expert.IsExpanded=true;catalog.BringIntoView();dialog.UpdateLayout();
            Require(catalog.IsReadOnly && catalog.Items.Count==3 && catalog.Columns.Count==9,
                "The expert section must expose the actual parsed, read-only catalog and all format details.");
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
            expert.IsExpanded=false;
            mode.SelectedIndex=1;primary.SelectedValue="137";audio.SelectedValue="140";
            Require(expert.IsExpanded && primary.IsVisible && audio.IsVisible,
                "Choosing exact formats must reveal their actual source selectors.");
            Require(Equals(primary.SelectedValue,"137") && Equals(audio.SelectedValue,"140"),
                "Exact source IDs must be selectable through real controls.");
            start.Text="00:00:20";end.Text="00:00:10";Click(dialog,"ApplyMediaSelectionButton");
            Require(dialog.IsVisible && first.Selection==null && Control<TextBlock>(dialog,"MediaSelectionError").Text.Length>0,
                "An inverted interval must leave the dialog open and the stored row unchanged.");
            PumpUntil(()=>end.IsKeyboardFocused,"Apply must move keyboard focus to the inverted interval's end field.");
            Require(Control<TextBlock>(dialog,"MediaSectionEndError").Visibility==Visibility.Visible &&
                Control<TextBlock>(dialog,"MediaSectionEndError").Text.Length>0 &&
                VisibleBounds(end).Top>=0 && VisibleBounds(end).Bottom<=scroll.ActualHeight &&
                Control<TextBlock>(dialog,"MediaSelectionError").IsVisible,
                "A range error must appear beside its field and remain visible in the pinned footer after scrolling from expert formats.");
            start.Text="00:00:05";end.Text="00:00:10";audio.SelectedValue="";Click(dialog,"ApplyMediaSelectionButton");
            Require(first.Selection==null && Control<TextBlock>(dialog,"MediaSelectionError").Text.Length>0,
                "Video-only selection must not silently guess or substitute an audio format.");
            PumpUntil(()=>audio.IsKeyboardFocused,"A missing separate audio stream must focus its source selector.");
            Require(Control<TextBlock>(dialog,"MediaFormatError").Visibility==Visibility.Visible && expert.IsExpanded,
                "Format compatibility feedback must be beside the revealed format selectors.");
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
            Require(Control<Expander>(dialog,"MediaFormatsExpander").IsExpanded &&
                Equals(Control<ComboBox>(dialog,"PrimaryMediaFormatCombo").SelectedValue,"137") &&
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
            PumpUntil(()=>Control<TextBox>(dialog,"MediaSectionStartBox").IsKeyboardFocused,
                "A collection restriction must reveal and focus the range that needs to be cleared.");
            Require(Control<TextBlock>(dialog,"MediaSectionStartError").Visibility==Visibility.Visible,
                "The unsupported collection range must have its explanation beside the time field.");
            Click(dialog,"CloseMediaSelectionButton");
            Require(dialog.IsVisible && first.Selection==previous &&
                Control<StackPanel>(dialog,"MediaDiscardDraftPanel").IsVisible,
                "The explicit Close button must protect a changed collection draft as well as Escape and chrome Close.");
            Click(dialog,"DiscardMediaDraftButton");
            Require(subject.MediaSelectionEditor is null && first.Selection==previous,
                "Only an explicit discard may close the edited draft without applying it.");
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

    private static class MediaEditorKeyboardInput
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint GetFocus();
    }
}
