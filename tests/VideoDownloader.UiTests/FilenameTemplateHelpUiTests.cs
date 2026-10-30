using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestFilenameTemplateHelp(string testRoot)
    {
        using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
        using var reader=XmlReader.Create(input);
        var window=(Window)XamlReader.Load(reader);
        var storage=new AppPersistence(Path.Combine(testRoot,"filename-help"));
        using var subject=new NativeWindow(window,storage,"must-not-launch");
        try
        {
            window.Show();
            var template=(TextBox)window.FindName("FilenameTemplateBox")!;
            template.Text="%(upload_date)s - %(title)s [%(id)s].%(ext)s";
            string before=template.Text;
            Require(template.ToolTip?.ToString()?.Contains("%(title)s")==true &&
                Equals(((TextBlock)window.FindName("FilenameTemplateLabel")!).ToolTip,template.ToolTip),
                "Hovering the filename field must explain template syntax");
            Exception? failure=null;
            bool opened=false;
            Window? Dialog()=>Application.Current.Windows.Cast<Window>().LastOrDefault(candidate=>candidate.Owner==window);
            var observer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            var watchdog=new DispatcherTimer{Interval=TimeSpan.FromSeconds(5)};
            observer.Tick+=(_,_)=>
            {
                var dialog=Dialog();
                // ShowDialog can pump the dispatcher before making its window
                // visible. Keep observing instead of consuming a one-shot callback.
                if(dialog?.IsVisible!=true)return;
                observer.Stop();
                try
                {
                    opened=true;
                    var panel=(DockPanel)dialog.Content;
                    var text=panel.Children.OfType<TextBox>().Single();
                    Require(text.IsReadOnly && text.VerticalScrollBarVisibility==ScrollBarVisibility.Auto &&
                        text.Text.Contains("%(playlist_index)03d") && text.Text.Contains("#output-template"),
                        "Long reference must be selectable, read-only, scrollable and include examples and the full reference");
                    panel.Children.OfType<Button>().Single().RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                }
                catch(Exception error) { failure=error; }
                finally { if(dialog.IsVisible)dialog.Close(); }
            };
            watchdog.Tick+=(_,_)=>
            {
                watchdog.Stop();observer.Stop();
                failure=new TimeoutException("Filename template help did not become visible or close.");
                Dialog()?.Close();
            };
            observer.Start();watchdog.Start();
            try
            {
                ((Button)window.FindName("FilenameTemplateHelpButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { observer.Stop();watchdog.Stop(); }
            if(failure!=null)throw failure;
            Require(opened && template.Text==before && subject.ReadOptions().FilenameTemplate==before,
                "Opening and closing help must leave the user's actual template intact");
            Require(!File.Exists(storage.HistoryPath) && subject.SessionState.Queue.Count==0,
                "Filename help must not start work or add queue/history records");
            Console.WriteLine("PASS: filename-template tooltip and real scrollable modal help preserve options without downloads");
        }
        finally { window.Close(); }

        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Filename template WPF test failed: "+message);
        }
    }
}
