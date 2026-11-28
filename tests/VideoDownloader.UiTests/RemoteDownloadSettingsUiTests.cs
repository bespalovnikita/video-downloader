using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestRemoteDownloadSettings(string testRoot)
    {
        const string address="https://remote-settings-canary.invalid/path/";
        const string username="remote-user-canary";
        const string password="remote-password-canary";
        string directory=Path.Combine(testRoot,"remote-download-settings");
        var persistence=new AppPersistence(directory);
        var store=new RemoteDownloadSettingsStore(directory);
        string settingsFile=Path.Combine(directory,"remote-downloads.json");

        static void Require(bool condition,string message)
        {
            if(!condition)throw new InvalidOperationException("Remote download settings WPF test failed: "+message);
        }

        static Window OpenWindow()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var xml=XmlReader.Create(input);
            return (Window)XamlReader.Load(xml);
        }

        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing server settings control: "+name));

        static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

        static void WaitForButton(Button button)
        {
            if(button.IsEnabled)return;
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(button.IsEnabled || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            Require(button.IsEnabled,"The server settings action must finish without blocking the dispatcher.");
        }

        var initial=OpenWindow();
        using(var controller=new NativeWindow(initial,persistence,"unused-yt-dlp.exe",automaticUpdates:false,automaticSubscriptionChecks:false))
        {
            try
            {
                initial.Show();
                Click(Control<Button>(initial,"OpenQueueOptionsButton"));
                initial.UpdateLayout();
                var option=Control<CheckBox>(initial,"RemoteDownloadsEnabledCheck");
                var endpoint=Control<TextBox>(initial,"RemoteDownloadAddressBox");
                var login=Control<TextBox>(initial,"RemoteDownloadUsernameBox");
                var secret=Control<PasswordBox>(initial,"RemoteDownloadPasswordBox");
                var save=Control<Button>(initial,"SaveRemoteDownloadSettingsButton");
                var check=Control<Button>(initial,"CheckRemoteDownloadConnectionButton");
                var status=Control<TextBlock>(initial,"RemoteDownloadSettingsStatus");
                Require(option.IsVisible && option.IsChecked==false && !File.Exists(settingsFile),
                    "A new profile must show the unchecked opt-in without creating a server settings file.");
                option.IsChecked=true;
                initial.UpdateLayout();
                Require(secret.IsVisible && new PasswordBoxAutomationPeer(secret).IsPassword(),
                    "First enable must reveal a masked password field with a password automation peer.");
                option.IsChecked=false;
                using(var server=new TcpListener(IPAddress.Loopback,0))
                using(var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10)))
                {
                    server.Start();
                    var response=ReplyToCapabilityProbeAsync(server,username,password,deadline.Token,
                        "{\"version\":1,\"maxConcurrent\":3,\"freeDiskBytes\":6442450944}");
                    endpoint.Text=$"http://127.0.0.1:{((IPEndPoint)server.LocalEndpoint).Port}/bridge/";
                    login.Text=username;
                    secret.Password=password;
                    Click(check);
                    WaitForButton(check);
                    response.GetAwaiter().GetResult();
                    Require(status.Text.Contains(ErrorMessages.Text("Предел параллельных заданий — 3","Concurrent job limit is 3"),
                        StringComparison.Ordinal) && status.Text.Contains(ErrorMessages.Text("Свободно на VPS","Free space on the VPS"),
                        StringComparison.Ordinal) && status.Text.Contains(DisplayValueConverter.Bytes(6442450944),StringComparison.Ordinal) &&
                        option.IsChecked==false && controller.SessionState.Queue.Count==0 &&
                        controller.SessionState.Coordinator is null && !File.Exists(settingsFile) && !Directory.Exists(Path.Combine(directory,"remote-jobs")),
                        "The explicit check must show the slot limit and free space without changing settings or starting a queue.");
                    var queue=Control<DataGrid>(initial,"QueueGrid");
                    Require(queue.ContextMenu.Items.OfType<MenuItem>().Any(item=>Equals(item.Header,
                        ErrorMessages.Text("Качнуть через сервер","Download through server"))),
                        "Windows must offer the explicit server command in the queue context menu.");
                    const string page="{\"jobs\":[{\"id\":\"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa\",\"state\":\"received\",\"name\":\"fixture.mp4\",\"size\":1024,\"expiresAt\":2140000000,\"deleteAfter\":2140000000}],\"freeDiskBytes\":90000,\"unclaimedBytes\":0,\"unclaimedLimitBytes\":15000000000}";
                    var queueResponse=ReplyToCapabilityProbeAsync(server,username,password,deadline.Token,page,"/bridge/api/remote/jobs");
                    bool foundFile=false;
                    var openUntil=DateTime.UtcNow.AddSeconds(5);
                    var managerTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
                    managerTimer.Tick+=(_,_)=>
                    {
                        var dialog=initial.OwnedWindows.OfType<Window>()
                            .FirstOrDefault(window=>window.Title==ErrorMessages.Text("Файловый менеджер серверной очереди","Server queue file manager"));
                        if(dialog?.Content is DockPanel panel && panel.Children.OfType<DataGrid>().SingleOrDefault() is {} grid)
                        {
                            foundFile=grid.Items.Count==1;
                            if(foundFile || DateTime.UtcNow>=openUntil)
                            {
                                managerTimer.Stop();
                                dialog.Close();
                            }
                        }
                    };
                    managerTimer.Start();
                    try{Click(Control<Button>(initial,"OpenRemoteQueueButton"));}
                    finally{managerTimer.Stop();}
                    queueResponse.GetAwaiter().GetResult();
                    Require(foundFile && controller.SessionState.Queue.Count==0,
                        "The actual WPF manager must show retained remote files without touching the Windows queue.");
                    Click(save);
                    WaitForButton(save);
                    Require(secret.Password.Length==0 && File.Exists(settingsFile),"Saving the connection must clear the editable password field.");
                    string saved=File.ReadAllText(settingsFile);
                    response=ReplyToCapabilityProbeAsync(server,username,password,deadline.Token);
                    Click(check);
                    WaitForButton(check);
                    response.GetAwaiter().GetResult();
                    Require(status.Text.Contains(ErrorMessages.Text("Предел параллельных заданий — 3","Concurrent job limit is 3"),
                        StringComparison.Ordinal) && !status.Text.Contains(ErrorMessages.Text("Свободно на VPS","Free space on the VPS"),
                        StringComparison.Ordinal) && File.ReadAllText(settingsFile)==saved,
                        "A probe of an older server must reuse the saved credentials and display the slot limit without stale free space.");
                }
                string settingsBeforeInvalidAddress=File.ReadAllText(settingsFile);
                option.IsChecked=true;
                endpoint.Text="http://remote-settings-canary.invalid/";
                login.Text=username;
                secret.Password=password;
                Click(save);
                WaitForButton(save);
                Require(File.ReadAllText(settingsFile)==settingsBeforeInvalidAddress,
                    "Credentials must be rejected at an insecure non-loopback address without rewriting the saved connection.");
                endpoint.Text=address;
                Click(save);
                WaitForButton(save);
                var persisted=store.Read();
                Require(persisted.Enabled && persisted.Connection?.Address.AbsoluteUri==address && persisted.Connection.Username==username &&
                    persisted.Connection.Password==password && secret.Password.Length==0,
                    "An empty queue must allow saving and reading a complete user-protected connection.");
                string disk=File.ReadAllText(settingsFile);
                string checkpoint=JsonSerializer.Serialize(new DownloadOptions{RemoteConnection=persisted.Connection},AppPersistence.Json);
                Require(new[]{address,username,password}.All(value=>!disk.Contains(value,StringComparison.Ordinal) &&
                    !checkpoint.Contains(value,StringComparison.Ordinal)),"The endpoint and credentials must be encrypted at rest and excluded from queue serialization.");
            }
            finally{controller.Dispose();initial.Close();}
        }

        var restored=OpenWindow();
        using var reopened=new NativeWindow(restored,persistence,"unused-yt-dlp.exe",automaticUpdates:false,automaticSubscriptionChecks:false);
        try
        {
            restored.Show();
            var option=Control<CheckBox>(restored,"RemoteDownloadsEnabledCheck");
            var endpoint=Control<TextBox>(restored,"RemoteDownloadAddressBox");
            var secret=Control<PasswordBox>(restored,"RemoteDownloadPasswordBox");
            var save=Control<Button>(restored,"SaveRemoteDownloadSettingsButton");
            Require(option.IsChecked==true && endpoint.Text==address && secret.Password.Length==0,
                "A restarted window must restore the opt-in and address without placing the saved password in the textbox.");
            endpoint.Text="https://another-endpoint.invalid/";
            Click(save);
            WaitForButton(save);
            Require(store.Read().Connection?.Address.AbsoluteUri==address,
                "Changing the server address without a fresh password must preserve the existing binding.");
            endpoint.Text=address;
            option.IsChecked=false;
            Click(save);
            WaitForButton(save);
            var disabled=store.Read();
            Require(!disabled.Enabled && disabled.Connection?.Password==password,
                "Disabling shared downloads can preserve the encrypted connection for the next opt-in.");
        }
        finally{reopened.Dispose();restored.Close();}
    }

    private static async Task ReplyToCapabilityProbeAsync(TcpListener server,string username,string password,CancellationToken token,
        string body="{\"version\":1,\"maxConcurrent\":3}",string requestPath="/bridge/api/remote/capabilities")
    {
        using var client=await server.AcceptTcpClientAsync(token).ConfigureAwait(false);
        using var stream=client.GetStream();
        using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
        if(await reader.ReadLineAsync(token).ConfigureAwait(false)!=$"GET {requestPath} HTTP/1.1")
            throw new InvalidDataException("The server connection check must use a GET at the configured subpath.");
        bool authenticated=false;
        string expected=Convert.ToBase64String(Encoding.UTF8.GetBytes(username+":"+password));
        for(int count=0;await reader.ReadLineAsync(token).ConfigureAwait(false) is {Length:>0} header;count++)
        {
            if(count>32)throw new InvalidDataException("The capability probe sent too many headers.");
            authenticated|=header.StartsWith("Authorization: Basic ",StringComparison.OrdinalIgnoreCase) &&
                header["Authorization: Basic ".Length..]==expected;
        }
        if(!authenticated)throw new InvalidDataException("The capability probe must use the current form's credentials.");
        byte[] response=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: application/json\r\nConnection: close\r\n\r\n{body}");
        await stream.WriteAsync(response,token).ConfigureAwait(false);
    }
}
