using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Xml;

namespace VideoDownloader.Native;

// Real WPF dispatcher/control test, isolated from the user's persistent data.
// Invoked only by the internal --ui-self-test CI switch.
internal static class NativeUiSelfTest
{
    public static void Run()
    {
        static void Stage(string status)
        {
            var location=Environment.GetEnvironmentVariable("VD_NATIVE_UI_TEST_RESULT");
            if(!string.IsNullOrWhiteSpace(location))File.WriteAllText(location,status);
        }
        Stage("creating temp folder");
        string root=Path.Combine(Path.GetTempPath(),"vd-native-ui-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Stage("creating WPF application");
        var app=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        NativeWindow? controller=null;
        Window? window=null;
        try
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            window=(Window)XamlReader.Load(reader);
            Stage("XAML loaded");
            controller=new NativeWindow(window,new AppPersistence(Path.Combine(root,"profile")));
            Stage("controller constructed");
            window.Show();
            Stage("window shown");
            TextBox urlField=(TextBox)window.FindName("UrlInput")!;
            Button add=(Button)window.FindName("AddUrlButton")!;
            Button remove=(Button)window.FindName("RemoveQueueButton")!;
            DataGrid queue=(DataGrid)window.FindName("QueueGrid")!;

            static void Require(bool condition,string error)
            {
                if(!condition) throw new InvalidOperationException("WPF UI parity test failed: "+error);
            }

            urlField.Text="https://example.com/one https://example.com/two";
            add.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Stage("Add clicked");
            Require(queue.Items.Count==2,"Add button must place both pasted URLs in the queue");
            Require(urlField.Text=="","Add button must clear the URL text field");
            Require((queue.Items[0] as DownloadItem)?.Url=="https://example.com/one","First URL must retain its order");
            Require((queue.Items[1] as DownloadItem)?.Url=="https://example.com/two","Second URL must retain its order");
            Require(window.Resources.Contains("QueueStatusTextStyle"),"Queue state colors must be present");
            Require(window.Resources.Contains("DownloadStatusTextStyle"),"Download state colors must be present");

            queue.SelectedItem=queue.Items[0];
            remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Stage("Remove clicked");
            Require(queue.Items.Count==1,"Remove button must delete only the selected queued URL");
            Require((queue.Items[0] as DownloadItem)?.Url=="https://example.com/two","Remove must preserve other entries");

            Stage("testing tray");
            var trayField=typeof(NativeWindow).GetField("_tray",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance);
            var tray=(System.Windows.Forms.NotifyIcon?)trayField?.GetValue(controller);
            Require(tray!=null && tray.Visible,"Native WPF UI must expose a functioning tray icon");
            Require(tray!.Text.Length<=63,"Windows tray caption must respect the 63-character limit");

            // Checkpoint must be stored in the isolated test profile, never in
            // the user's actual %LOCALAPPDATA% directory.
            string checkpoint=Path.Combine(root,"profile","session.json");
            Require(File.Exists(checkpoint),"Queue actions must write a native v1 checkpoint");
            Stage("testing checkpoint");
            var restored=new AppPersistence(Path.Combine(root,"profile")).Restore();
            Require(restored?.Queue.Count==1,"New session checkpoint must restore the surviving queue row");

            Stage("testing shutdown");
            // PASS is set after the finally cleanup to catch shutdown crashes.
        }
        finally
        {
            Stage("dispose controller");
            controller?.Dispose();
            Stage("close WPF window");
            window?.Close();
            Stage("shutdown dispatcher");
            app.Shutdown();
            Stage("delete isolated test profile");
            try {Directory.Delete(root,true);}catch(IOException){}
            Stage("PASS");
        }
    }
}
