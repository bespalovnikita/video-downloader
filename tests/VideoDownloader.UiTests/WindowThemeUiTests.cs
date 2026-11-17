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
            Require(Same(window.Background,SystemColors.WindowBrush) && Same(input.Foreground,SystemColors.WindowTextBrush) &&
                Same(input.Background,SystemColors.WindowBrush) && Same(start.Background,SystemColors.HighlightBrush) &&
                Same(start.Foreground,SystemColors.HighlightTextBrush) && Same(pause.Foreground,SystemColors.HighlightTextBrush),
                "Existing window, fields and primary actions must switch to matched Windows system color pairs");
            Require(Same(state.Foreground,SystemColors.HighlightTextBrush) && state.Text==row.StateLabel,
                "A selected status cell must use HighlightText while keeping its bound status label");
            Require(Same(editor.Background,SystemColors.WindowBrush) && Same(editor.Foreground,SystemColors.WindowTextBrush) &&
                Same(editorNote.Foreground,SystemColors.WindowTextBrush) &&
                Same(Control<TextBox>(editor,"MediaSectionStartBox").Foreground,SystemColors.WindowTextBrush),
                "An already-open media editor must follow its owner's shared palette, including notes and editable fields");
            Require(Control<Button>(window,"MediaSelectionButton").Opacity==1,
                "Disabled guarded actions must retain readable system colors without translucent Contrast Theme text");

            contrast=false;theme.Refresh();Flush();
            Require(ColorOf(window.Background)==Color.FromRgb(17,19,24) &&
                ColorOf(start.Background)==Color.FromRgb(79,70,229) && ColorOf(state.Foreground)==Color.FromRgb(251,113,133) &&
                ColorOf(editorNote.Foreground)==Color.FromRgb(183,192,216),
                "Leaving Contrast Theme must restore the dark palette and normal bound state colors on existing controls");
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
