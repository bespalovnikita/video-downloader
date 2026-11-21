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
            ((Button)window.FindName("OpenQueueOptionsButton")!).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));window.UpdateLayout();
            var template=(TextBox)window.FindName("FilenameTemplateBox")!;
            var advanced=(Expander)window.FindName("QueueAdvancedOptionsExpander")!;
            var summary=(TextBlock)window.FindName("QueueAdvancedOptionsSummary")!;
            var reset=(Button)window.FindName("ResetQueueAdvancedOptionsButton")!;
            Require(!advanced.IsExpanded && !template.IsVisible && ((ComboBox)window.FindName("QualityCombo")!).IsVisible,
                "Queue settings must keep quality available while throughput and naming controls start collapsed");
            Require(summary.Visibility==Visibility.Collapsed && reset.Visibility==Visibility.Collapsed,
                "Default advanced settings must not add an empty summary or a redundant reset action.");
            subject.ApplyOptions(new DownloadOptions{RateLimit="5M",Threads="7",Fragments="5",FilenameTemplate="%(title)s.%(ext)s",
                UseHttpChunks=true,PreferAria2c=true,
                Output=Path.Combine(testRoot,"advanced-output"),Cookies=Path.Combine(testRoot,"advanced-cookies.txt")});
            summary.BringIntoView();window.UpdateLayout();
            Require(!advanced.IsExpanded && summary.IsVisible && summary.Text.Contains("5M") &&
                summary.Text.Contains("7") && summary.Text.Contains("5") && summary.Text.Contains("aria2c") &&
                summary.Text.Contains(ErrorMessages.Text("4 МиБ","4 MiB")) &&
                summary.Text.Contains(ErrorMessages.Text("шаблон","template")) &&
                System.Windows.Automation.Peers.UIElementAutomationPeer.CreatePeerForElement(advanced)?.GetName().Contains("5M")==true,
                "Restored nondefault limits and naming must remain visible and accessible while their fields are collapsed.");
            ((ComboBox)window.FindName("ProfileCombo")!).SelectedItem=ProfileCatalog.Display("4K archive");
            Require(summary.Text.Contains("5M") && subject.ReadOptions().RateLimit=="5M",
                "Switching profiles must keep the still-active advanced limit visible.");
            advanced.IsExpanded=true;
            advanced.IsExpanded=false;
            var hiddenOptions=subject.ReadOptions();
            Require(hiddenOptions.RateLimit=="5M" && hiddenOptions.Threads=="7" && hiddenOptions.Fragments=="5" &&
                    hiddenOptions.UseHttpChunks && hiddenOptions.PreferAria2c &&
                    hiddenOptions.FilenameTemplate=="%(title)s.%(ext)s",
                "Opening and closing advanced settings must preserve restored throughput and filename options");
            advanced.IsExpanded=true;
            reset.BringIntoView();window.UpdateLayout();
            reset.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            var defaults=new DownloadOptions();var resetOptions=subject.ReadOptions();
            Require(resetOptions.RateLimit==defaults.RateLimit && resetOptions.Threads==defaults.Threads &&
                resetOptions.Fragments==defaults.Fragments && resetOptions.FilenameTemplate==defaults.FilenameTemplate &&
                !resetOptions.UseHttpChunks && !resetOptions.PreferAria2c &&
                resetOptions.Output==hiddenOptions.Output && resetOptions.Cookies==hiddenOptions.Cookies &&
                resetOptions.Profile==hiddenOptions.Profile && resetOptions.Quality==hiddenOptions.Quality &&
                resetOptions.Container==hiddenOptions.Container && resetOptions.Archive==hiddenOptions.Archive &&
                summary.Visibility==Visibility.Collapsed && reset.Visibility==Visibility.Collapsed,
                "Group reset must clear only throughput/naming overrides and preserve profile, folder, cookies and primary selections.");
            var persisted=storage.Restore()?.Settings;
            Require(persisted is not null && persisted.RateLimit==defaults.RateLimit && persisted.Threads==defaults.Threads &&
                !persisted.UseHttpChunks && !persisted.PreferAria2c &&
                persisted.Output==hiddenOptions.Output && persisted.Cookies==hiddenOptions.Cookies,
                "An explicit group reset must checkpoint its values without resetting unrelated saved settings.");
            template.BringIntoView();window.UpdateLayout();
            Require(template.IsVisible,"The filename field must be reached through the opened queue settings tab");
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
                var help=(Button)window.FindName("FilenameTemplateHelpButton")!;
                help.BringIntoView();window.UpdateLayout();
                help.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            }
            finally { observer.Stop();watchdog.Stop(); }
            if(failure!=null)throw failure;
            Require(opened && template.Text==before && subject.ReadOptions().FilenameTemplate==before,
                "Opening and closing help must leave the user's actual template intact");
            Require(!File.Exists(storage.HistoryPath) && subject.SessionState.Queue.Count==0,
                "Filename help must not start work or add queue/history records");
            Console.WriteLine("PASS: filename-template tooltip and real scrollable modal help preserve options without downloads");
        }
        finally { subject.Dispose();window.Close(); }

        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Filename template WPF test failed: "+message);
        }
    }
}
