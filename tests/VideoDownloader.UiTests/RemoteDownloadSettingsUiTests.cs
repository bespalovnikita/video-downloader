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

        static void WaitForSave(Button button)
        {
            if(button.IsEnabled)return;
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var frame=new DispatcherFrame();
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(20)};
            timer.Tick+=(_,_)=>{if(button.IsEnabled || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{Dispatcher.PushFrame(frame);}
            finally{timer.Stop();}
            Require(button.IsEnabled,"The settings writer must finish without blocking the dispatcher.");
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
                Require(option.IsVisible && option.IsChecked==false && !File.Exists(settingsFile),
                    "A new profile must show the unchecked opt-in without creating a server settings file.");
                option.IsChecked=true;
                initial.UpdateLayout();
                Require(secret.IsVisible && new PasswordBoxAutomationPeer(secret).IsPassword(),
                    "First enable must reveal a masked password field with a password automation peer.");
                endpoint.Text="http://remote-settings-canary.invalid/";
                login.Text=username;
                secret.Password=password;
                Click(save);
                WaitForSave(save);
                Require(!File.Exists(settingsFile),"Credentials must be rejected at an insecure non-loopback address.");
                endpoint.Text=address;
                Click(save);
                WaitForSave(save);
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
            WaitForSave(save);
            Require(store.Read().Connection?.Address.AbsoluteUri==address,
                "Changing the server address without a fresh password must preserve the existing binding.");
            endpoint.Text=address;
            option.IsChecked=false;
            Click(save);
            WaitForSave(save);
            var disabled=store.Read();
            Require(!disabled.Enabled && disabled.Connection?.Password==password,
                "Disabling shared downloads can preserve the encrypted connection for the next opt-in.");
        }
        finally{reopened.Dispose();restored.Close();}
    }
}
