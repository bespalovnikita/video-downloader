using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using SystemColors=System.Windows.SystemColors;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestWindowContrastTheme()
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Windows Contrast Theme WPF test failed: "+message);}
        static bool Same(Brush actual,Brush expected)=>actual is SolidColorBrush a && expected is SolidColorBrush b && a.Color==b.Color;
        static Color ColorOf(Brush brush)=>((SolidColorBrush)brush).Color;
        static T Control<T>(Window owner,string name) where T:class=>(T)(owner.FindName(name)
            ??throw new InvalidOperationException("Missing actual Contrast Theme control: "+name));
        static void Flush()
        {
            var frame=new DispatcherFrame();
            _=Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background,new Action(()=>frame.Continue=false));
            Dispatcher.PushFrame(frame);
        }
        using var stream=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(stream);
        var window=(Window)XamlReader.Load(reader);
        bool contrast=false;
        using var theme=new WindowThemeController(window,()=>contrast);
        MediaSelectionDialog? editor=null;
        try
        {
            window.Show();window.Width=1500;window.Height=900;window.UpdateLayout();
            var start=Control<Button>(window,"StartButton");start.IsEnabled=true;
            var pause=Control<Button>(window,"PauseButton");pause.IsEnabled=true;
            var input=Control<TextBox>(window,"UrlInput");
            var queue=Control<DataGrid>(window,"QueueGrid");
            var row=new DownloadItem{Url="https://theme.invalid/video",Title="Название",Status="Failed",State="Failed"};
            queue.ItemsSource=new[]{row};queue.SelectedItem=row;window.UpdateLayout();Flush();
            var state=(TextBlock)queue.Columns[2].GetCellContent(row);
            var originalState=state;
            string StateDetails()
            {
                DataGridCell? cell=null;
                for(DependencyObject? ancestor=state;ancestor is not null;ancestor=VisualTreeHelper.GetParent(ancestor))
                    if(ancestor is DataGridCell found){cell=found;break;}
                var container=queue.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
                var paletteState=window.TryFindResource("WindowThemeState") as WindowThemeState;
                static string ColorText(Brush? value)=>value is SolidColorBrush solid?solid.Color.ToString():value?.ToString()??"null";
                var source=DependencyPropertyHelper.GetValueSource(state,TextBlock.ForegroundProperty);
                return $" actual={ColorText(state.Foreground)}, expected={ColorText(SystemColors.HighlightTextBrush)}, text=[{state.Text}], expectedText=[{row.StateLabel}],"+
                    $" rowSelected={container?.IsSelected}, cellSelected={cell?.IsSelected}, cellFocus={cell?.IsKeyboardFocusWithin},"+
                    $" cellForeground={ColorText(cell?.Foreground)}, cellBackground={ColorText(cell?.Background)}, paletteContrast={paletteState?.HighContrast},"+
                    $" queueSelected={ReferenceEquals(queue.SelectedItem,row)}, sameTextElement={ReferenceEquals(state,originalState)}, foregroundSource={source.BaseValueSource}/{source.IsExpression}.";
            }
            Require(ColorOf(start.Background)==Color.FromRgb(79,70,229) &&
                ColorOf(start.Foreground)==Color.FromRgb(238,241,248) &&
                ColorOf(pause.Background)==Color.FromRgb(245,158,11) &&
                ColorOf(pause.Foreground)==Color.FromRgb(17,19,24) &&
                ColorOf(state.Foreground)==Color.FromRgb(251,113,133),
                "Normal mode must preserve the existing CTA contrast pairs and the selected row's status color");

            editor=new MediaSelectionDialog(window,row,()=>new DownloadOptions(),_=>null);
            editor.Show();editor.UpdateLayout();
            var editorNote=Control<TextBlock>(editor,"MediaCatalogStatus");
            contrast=true;theme.Refresh();Flush();window.UpdateLayout();editor.UpdateLayout();
            // Resource invalidation may regenerate the column's TextBlock.
            // Inspect the element currently presented by the actual cell.
            state=(TextBlock)queue.Columns[2].GetCellContent(row);
            Require(Same(window.Background,SystemColors.WindowBrush) && Same(input.Foreground,SystemColors.WindowTextBrush) &&
                Same(input.Background,SystemColors.WindowBrush) && Same(start.Background,SystemColors.HighlightBrush) &&
                Same(start.Foreground,SystemColors.HighlightTextBrush) && Same(pause.Foreground,SystemColors.HighlightTextBrush),
                "Existing window, fields and primary actions must switch to matched Windows system color pairs");
            var selectedRow=queue.ItemContainerGenerator.ContainerFromItem(row) as DataGridRow;
            DataGridCell? selectedCell=null;
            for(DependencyObject? ancestor=state;ancestor is not null;ancestor=VisualTreeHelper.GetParent(ancestor))
                if(ancestor is DataGridCell found){selectedCell=found;break;}
            Require(state.IsVisible && selectedRow?.IsSelected==true && selectedCell?.IsSelected==true,
                "The selection assertion must inspect a live visible status in an actually selected row and cell."+StateDetails());
            Require(Same(state.Foreground,SystemColors.HighlightTextBrush) && state.Text==row.StateLabel,
                "A selected status cell must use HighlightText while keeping its bound status label."+StateDetails());
            Require(Same(editor.Background,SystemColors.WindowBrush) && Same(editor.Foreground,SystemColors.WindowTextBrush) &&
                Same(editorNote.Foreground,SystemColors.WindowTextBrush) &&
                Same(Control<TextBox>(editor,"MediaSectionStartBox").Foreground,SystemColors.WindowTextBrush),
                "An already-open media editor must follow its owner's shared palette, including notes and editable fields");
            Require(Control<Button>(window,"MediaSelectionButton").Opacity==1,
                "Disabled guarded actions must retain readable system colors without translucent Contrast Theme text");

            // Keyboard focus can remain in a cell after Ctrl+click or an
            // explicit selection clear removes its row from the selection.
            // Its highlight background still needs the paired system text.
            DataGridCell? stateCell=null;
            for(DependencyObject? ancestor=state;ancestor is not null;ancestor=VisualTreeHelper.GetParent(ancestor))
                if(ancestor is DataGridCell cell){stateCell=cell;break;}
            Require(stateCell is not null,"The bound status must belong to an actual DataGridCell");
            window.Activate();queue.CurrentCell=new DataGridCellInfo(row,queue.Columns[2]);
            System.Windows.Input.Keyboard.Focus(stateCell!);queue.UnselectAll();Flush();window.UpdateLayout();
            var rowContainer=(DataGridRow)queue.ItemContainerGenerator.ContainerFromItem(row);
            Require(stateCell!.IsKeyboardFocusWithin && !rowContainer.IsSelected,
                "The focus test must exercise a genuinely focused cell in an unselected row");
            Require(Same(stateCell.Background,SystemColors.HighlightBrush) &&
                Same(state.Foreground,SystemColors.HighlightTextBrush) && state.Text==row.StateLabel,
                "A focused unselected status cell must keep the bound label readable on its system highlight background."+StateDetails());
            queue.SelectedItem=row;Flush();

            contrast=false;theme.Refresh();Flush();
            window.UpdateLayout();editor.UpdateLayout();
            state=(TextBlock)queue.Columns[2].GetCellContent(row);
            Require(ColorOf(window.Background)==Color.FromRgb(17,19,24) &&
                ColorOf(start.Background)==Color.FromRgb(79,70,229) && ColorOf(state.Foreground)==Color.FromRgb(251,113,133) &&
                ColorOf(editorNote.Foreground)==Color.FromRgb(183,192,216),
                "Leaving Contrast Theme must restore the dark palette and normal bound state colors on existing controls."+
                $" window={ColorOf(window.Background)}, start={ColorOf(start.Background)}, editorNote={ColorOf(editorNote.Foreground)};"+StateDetails());
            theme.Dispose();contrast=true;theme.Refresh();Flush();
            Require(ColorOf(start.Background)==Color.FromRgb(79,70,229),
                "A late theme callback after disposal must not mutate the closed owner's resource palette");

            var released=DisposedThemeReference(window);
            for(int attempt=0;attempt<3 && released.IsAlive;attempt++)
            {GC.Collect();GC.WaitForPendingFinalizers();GC.Collect();}
            Require(!released.IsAlive,"Disposal must release the static SystemParameters event subscription");
            Console.WriteLine("PASS: actual window/editor controls follow Windows Contrast Theme resources, preserve status bindings, restore dark colors and release static subscriptions");
        }
        finally{editor?.Close();window.Close();}
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference DisposedThemeReference(Window window)
    {
        var theme=new WindowThemeController(window);
        var reference=new WeakReference(theme);theme.Dispose();return reference;
    }
}
