using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Brush=System.Windows.Media.Brush;
using SystemColors=System.Windows.SystemColors;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestOwnedWindowContrast(Window owner,Action<bool> setContrast,Action flush)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Owned Windows Contrast Theme WPF test: "+message);}
        static bool Same(Brush actual,Brush expected)=>actual is SolidColorBrush a && expected is SolidColorBrush b && a.Color==b.Color;
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing themed owned control: "+name));
        static Window View(string name,Window owner)
        {
            using var reader=XmlReader.Create(Path.Combine(AppContext.BaseDirectory,"ui",name));
            var window=(Window)XamlReader.Load(reader);window.Owner=owner;WindowThemeController.Inherit(window,owner);return window;
        }
        static IEnumerable<DependencyObject> Children(DependencyObject root)
        {
            yield return root;
            for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++)
                foreach(var child in Children(VisualTreeHelper.GetChild(root,i)))yield return child;
        }

        string root=Path.Combine(Path.GetTempPath(),"vd-owned-theme-"+Guid.NewGuid().ToString("N"));
        var storage=new AppPersistence(root);
        using var journal=new DiagnosticJournal(Path.Combine(root,"logs"),JournalProfile.Errors);
        var target=JobDiagnosticTarget.Capture(new DownloadItem{Url="https://theme.invalid/job",Title="Проверка",Status="Failed"});
        var diagnostics=new JobDiagnosticsWindow(owner,target,new JobDiagnosticsService(journal,"must-not-launch"),()=>journal.Sequence);
        var backup=new ProfileBackupWindow(owner,new WindowsProfileBackup(storage),
            _=>Task.FromResult<IAsyncDisposable>(new ThemeMaintenanceLease()),()=>new DownloadOptions(),()=>[],
            _=>Task.CompletedTask,_=>{});
        var history=new DownloadHistoryWindow(owner,storage);
        var subscriptions=View("SubscriptionsWindow.xaml",owner);
        var duplicate=View("DuplicateDownloadDialog.xaml",owner);
        var help=NativeWindow.CreateFilenameTemplateHelp(owner);
        var dialogs=new[]{subscriptions,duplicate,history,diagnostics,backup,help};
        try
        {
            var row=new{Title="Одинаковое имя",Url="https://theme.invalid/source",StatusLabel="Активна",ScheduleLabel="Вручную",Message="Предупреждение"};
            var grid=Control<DataGrid>(subscriptions,"SubscriptionGrid");grid.ItemsSource=new[]{row};grid.SelectedItem=row;
            foreach(var dialog in dialogs){dialog.Show();dialog.UpdateLayout();}
            flush();
            var search=Control<TextBox>(subscriptions,"SubscriptionInboxSearchBox");
            var yes=Control<Button>(duplicate,"YesButton");
            var cancel=Control<Button>(duplicate,"CancelButton");
            var previewError=Control<TextBlock>(subscriptions,"SubscriptionPreviewError");
            previewError.Text="Ошибка метаданных";previewError.Visibility=Visibility.Visible;
            Require(!((WindowThemeState)owner.FindResource("WindowThemeState")).HighContrast &&
                Same(subscriptions.Background,(Brush)owner.FindResource("WindowBackgroundBrush")) &&
                Same(yes.Background,(Brush)owner.FindResource("AccentBrush")),
                "Secondary windows must start with their semantic dark palette before the owner changes theme.");

            setContrast(true);flush();foreach(var dialog in dialogs)dialog.UpdateLayout();
            Require(dialogs.All(dialog=>Same(dialog.Background,SystemColors.WindowBrush) && Same(dialog.Foreground,SystemColors.WindowTextBrush)),
                "Already-open subscriptions, duplicate, history, diagnostics, backup and help must follow their owner's system surfaces and text.");
            Require(Same(search.Background,SystemColors.WindowBrush) && Same(search.Foreground,SystemColors.WindowTextBrush) &&
                Same(yes.Background,SystemColors.HighlightBrush) && Same(yes.Foreground,SystemColors.HighlightTextBrush) &&
                Same(previewError.Foreground,SystemColors.WindowTextBrush) &&
                Same(Control<TextBlock>(history,"HistoryStatusText").Foreground,SystemColors.WindowTextBrush) &&
                Same(Control<TextBlock>(diagnostics,"JobDiagnosticsPurpose").Foreground,SystemColors.WindowTextBrush),
                "Fields, primary confirmation, validation and explanatory text must use matching system color pairs.");
            var content=grid.Columns[0].GetCellContent(row)??throw new InvalidOperationException("The selected source must render its title cell.");
            var identity=Children(content).OfType<TextBlock>().Single(label=>label.Name=="SubscriptionSourceIdentity");
            var cell=Children(grid).OfType<DataGridCell>().First(candidate=>ReferenceEquals(candidate.Column,grid.Columns[0]) && candidate.IsSelected);
            Require(identity.IsVisible && Same(cell.Background,SystemColors.HighlightBrush) &&
                Same(identity.Foreground,SystemColors.HighlightTextBrush),
                "A selected subscription's title and secondary URL must stay readable on the system highlight.");
            duplicate.Activate();cancel.Focus();flush();duplicate.UpdateLayout();
            var chrome=(Border)cancel.Template.FindName("DuplicateButtonChrome",cancel);
            Require(cancel.IsKeyboardFocused && Same(chrome.BorderBrush,SystemColors.WindowTextBrush),
                "Keyboard focus in the duplicate decision must have a visible system-color border.");
            var check=Control<Button>(subscriptions,"CheckAllSubscriptionsButton");subscriptions.Activate();check.Focus();flush();
            var subscriptionChrome=(Border)check.Template.FindName("Chrome",check);
            Require(check.IsKeyboardFocused && Same(subscriptionChrome.BorderBrush,SystemColors.WindowTextBrush),
                "Subscription command focus must remain visible in Contrast Theme.");

            Control<Expander>(subscriptions,"AddSubscriptionExpander").IsExpanded=true;
            var add=Control<Button>(subscriptions,"AddSubscriptionButton");
            subscriptions.UpdateLayout();add.BringIntoView();flush();subscriptions.UpdateLayout();
            var cursor=System.Windows.Forms.Cursor.Position;
            try
            {
                var hoverPoint=add.PointToScreen(new System.Windows.Point(add.ActualWidth/2,add.ActualHeight/2));
                var client=new WaveformMouseInput.DevicePoint{X=(int)Math.Round(hoverPoint.X),Y=(int)Math.Round(hoverPoint.Y)};
                var hwnd=new WindowInteropHelper(subscriptions).Handle;
                Require(WaveformMouseInput.ScreenToClient(hwnd,ref client) &&
                    client.X is >=0 and <=short.MaxValue && client.Y is >=0 and <=short.MaxValue,
                    "The visible primary command must provide a valid native mouse target.");
                System.Windows.Forms.Cursor.Position=new System.Drawing.Point((int)Math.Round(hoverPoint.X),(int)Math.Round(hoverPoint.Y));
                // Native input updates WPF's MouseDevice and the actual IsMouseOver trigger.
                _=WaveformMouseInput.SendMessage(hwnd,0x0200,0,(client.Y<<16)|(client.X&0xffff));
                var frame=new DispatcherFrame();var watch=System.Diagnostics.Stopwatch.StartNew();
                var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
                timer.Tick+=(_,_)=>{if(add.IsMouseOver || watch.Elapsed>TimeSpan.FromSeconds(3))frame.Continue=false;};
                timer.Start();try{Dispatcher.PushFrame(frame);}finally{timer.Stop();}
                var hoveredChrome=(Border)add.Template.FindName("Chrome",add);
                Require(add.IsMouseOver && Same(hoveredChrome.Background,SystemColors.HighlightBrush) &&
                    Same(add.Foreground,SystemColors.HighlightTextBrush) &&
                    Same(hoveredChrome.BorderBrush,SystemColors.WindowTextBrush),
                    "A genuinely hovered primary subscription command must retain the system highlight/text pair and show its hover border.");
            }
            finally{System.Windows.Forms.Cursor.Position=cursor;}

            setContrast(false);flush();foreach(var dialog in dialogs)dialog.UpdateLayout();
            Require(dialogs.All(dialog=>Same(dialog.Background,(Brush)owner.FindResource("WindowBackgroundBrush"))) &&
                Same(search.Background,(Brush)owner.FindResource("FieldBrush")) &&
                Same(yes.Background,(Brush)owner.FindResource("AccentBrush")) &&
                Same(previewError.Foreground,(Brush)owner.FindResource("ValidationBrush")),
                "Leaving Contrast Theme must restore the dark semantic palette in the same already-open windows.");
        }
        finally
        {
            foreach(var dialog in dialogs)dialog.Close();setContrast(false);flush();
            try{Directory.Delete(root,true);}catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }

    private sealed class ThemeMaintenanceLease:IAsyncDisposable
    {public ValueTask DisposeAsync()=>ValueTask.CompletedTask;}
}
