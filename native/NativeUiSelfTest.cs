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

            // Exercise the actual callback on a worker thread: reading a WPF
            // TextBox here used to throw before any metadata request could start.
            Stage("testing background preview cookies");
            var cookies=(TextBox)window.FindName("CookiesBox")!;
            cookies.Text=Path.Combine(root,"cookies.txt");
            var previews=(PreviewService)typeof(NativeWindow).GetField("_previews",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(controller)!;
            var readCookies=(Func<string>)typeof(PreviewService).GetField("_cookiePath",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!.GetValue(previews)!;
            Require(Task.Run(readCookies).GetAwaiter().GetResult()==cookies.Text,
                "Preview worker must read the current cookies without accessing WPF controls");
            cookies.Text="";
            Require(Task.Run(readCookies).GetAwaiter().GetResult()=="",
                "Preview worker must observe cookies being cleared");

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

            Stage("testing live clipboard preview selection");
            var addUrls=typeof(NativeWindow).GetMethod("AddUrls",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            addUrls.Invoke(controller,new object[]{new[]{"https://example.com/clipboard"},true,true});
            Require(queue.Items.Count==2,"Clipboard addition must enqueue one new URL");
            Require((queue.SelectedItem as DownloadItem)?.Url=="https://example.com/clipboard",
                "Manual clipboard Paste must immediately select its new preview");
            Require(((TextBlock)window.FindName("PreviewTitle")!).Text=="Preparing preview...",
                "New clipboard URL must show preview loading state immediately");

            Stage("testing Windows shortcut import");
            string shortcut=Path.Combine(root,"example.url");
            File.WriteAllText(shortcut,"[InternetShortcut]\r\nURL=https://example.com/shortcut\r\n");
            var readInput=typeof(NativeWindow).GetMethod("ReadInputFile",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Static)!;
            var imported=(IReadOnlyList<string>)readInput.Invoke(null,new object[]{shortcut})!;
            Require(imported.SequenceEqual(new[]{"https://example.com/shortcut"}),
                "Windows .url shortcut must import the URL= field");

            Stage("testing native tray field order");
            var startField=typeof(NativeWindow).GetField("_sessionStart",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            startField.SetValue(controller,DateTime.Now);
            var currentField=typeof(NativeWindow).GetField("_sessionUrls",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!;
            var sessionUrls=(HashSet<string>)currentField.GetValue(controller)!;
            sessionUrls.Add("https://example.com/clipboard");
            typeof(NativeWindow).GetMethod("UpdateState",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .Invoke(controller,null);
            Require(tray.Text.Contains("OK 0 Left 1 Err 0"),
                "Tray counts must be current-session only, ordered OK Left Err");

            Stage("testing shutdown");
            // Hold the worker completion task open, then exercise real window
            // Close events and dispatcher continuations. The first Close must
            // leave the app alive until the final result has been checkpointed.
            var cleanup=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            typeof(NativeWindow).GetField("_sessionTask",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .SetValue(controller,cleanup.Task);
            typeof(NativeWindow).GetField("_exitFromTray",
                System.Reflection.BindingFlags.NonPublic|System.Reflection.BindingFlags.Instance)!
                .SetValue(controller,true);
            bool closed=false;
            var frame=new System.Windows.Threading.DispatcherFrame();
            window.Closed+=(_,_)=>{closed=true;frame.Continue=false;};
            window.Close();
            Require(!closed && window.IsVisible && !window.IsEnabled,
                "Exit must keep the window alive while worker cleanup is pending");
            var finalRow=(DownloadItem)queue.Items[0];
            finalRow.Status="Interrupted";finalRow.State="Cancelled";
            cleanup.SetResult();
            var timeout=new System.Windows.Threading.DispatcherTimer {
                Interval=TimeSpan.FromSeconds(10)
            };
            timeout.Tick+=(_,_)=>frame.Continue=false;
            timeout.Start();
            try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
            finally { timeout.Stop(); }
            Require(closed,"Exit must close the window after cleanup completes");
            var finalSnapshot=new AppPersistence(Path.Combine(root,"profile")).Restore()!;
            Require(finalSnapshot.Queue[0].Status=="Interrupted" &&
                finalSnapshot.Queue[0].State=="Cancelled",
                "Exit must checkpoint the final cancelled result before closing");
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
