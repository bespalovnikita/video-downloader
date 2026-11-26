using System.IO;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly RemoteDownloadSettingsStore _remoteDownloadSettingsStore;
    private RemoteDownloadSettings _savedRemoteDownloadSettings=new(false,null);
    private bool _remoteDownloadSettingsUnreadable;
    private Task? _remoteDownloadSettingsWrite;

    private void InitializeRemoteDownloadSettings()
    {
        try
        {
            _savedRemoteDownloadSettings=_remoteDownloadSettingsStore.Read();
            if(_savedRemoteDownloadSettings.Connection is { } connection)
            {
                B("RemoteDownloadAddressBox").Text=connection.Address.AbsoluteUri;
                B("RemoteDownloadUsernameBox").Text=connection.Username;
            }
            K("RemoteDownloadsEnabledCheck").IsChecked=_savedRemoteDownloadSettings.Enabled;
        }
        catch(Exception error) when(error is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or FormatException or
            System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            _remoteDownloadSettingsUnreadable=true;
            Log("Could not read the download server settings ("+error.GetType().Name+").");
            T("RemoteDownloadSettingsStatus").Text=ErrorMessages.Text(
                "Не удалось прочитать сохранённое подключение. Укажите настройки сервера заново.",
                "Could not read the saved connection. Enter the server settings again.");
        }
        K("RemoteDownloadsEnabledCheck").Checked+=(_,_)=>
        {
            if(_savedRemoteDownloadSettings.Connection is null && F<PasswordBox>("RemoteDownloadPasswordBox").Password.Length==0)
                F<Expander>("RemoteDownloadSettingsExpander").IsExpanded=true;
        };
        F<Button>("SaveRemoteDownloadSettingsButton").Click+=async(_,_)=>await SaveRemoteDownloadSettingsFromControlsAsync();
    }

    private RemoteDownloadSettings CaptureRemoteDownloadSettings(bool forStart)
    {
        bool enabled=Check(K("RemoteDownloadsEnabledCheck"));
        if(forStart && !enabled)return _savedRemoteDownloadSettings with {Enabled=false};
        string password=F<PasswordBox>("RemoteDownloadPasswordBox").Password;
        if(!enabled && password.Length==0 && _savedRemoteDownloadSettings.Connection is null)return new(false,null);
        var address=RemoteDownloadConnection.ParseAddress(B("RemoteDownloadAddressBox").Text);
        string username=B("RemoteDownloadUsernameBox").Text;
        if(password.Length==0 && _savedRemoteDownloadSettings.Connection is { } previous &&
            address.AbsoluteUri==previous.Address.AbsoluteUri && username==previous.Username)
            return new(enabled,previous);
        return new(enabled,new RemoteDownloadConnection(address,username,password));
    }

    private async Task SaveRemoteDownloadSettingsFromControlsAsync()
    {
        if(_disposed || _closing || (_startingSession && SessionState.Coordinator?.IsRunning!=true) || _appUpdateHandoff || RejectProfileBackupMutation() ||
            _remoteDownloadSettingsWrite is not null)return;
        var fields=F<StackPanel>("RemoteDownloadSettingsFields");
        var enabled=K("RemoteDownloadsEnabledCheck");
        var status=T("RemoteDownloadSettingsStatus");
        fields.IsEnabled=false;
        enabled.IsEnabled=false;
        try
        {
            await PersistRemoteDownloadSettingsAsync(CaptureRemoteDownloadSettings(forStart:false),explicitSave:true);
            if(!_disposed && !_closing)
            {
                F<PasswordBox>("RemoteDownloadPasswordBox").Clear();
                status.Text=ErrorMessages.Text("Настройки сохранены для следующего запуска.","Settings saved for the next start.");
            }
        }
        catch(Exception error) when(error is ArgumentException or IOException or UnauthorizedAccessException)
        {
            if(!_disposed && !_closing)status.Text=ErrorMessages.ForException(error);
        }
        finally
        {
            if(!_disposed && !_closing){fields.IsEnabled=true;enabled.IsEnabled=true;}
        }
    }

    private async Task PersistRemoteDownloadSettingsAsync(RemoteDownloadSettings settings,bool explicitSave=false)
    {
        if(!_remoteDownloadSettingsUnreadable && _savedRemoteDownloadSettings==settings)return;
        // Do not erase an unreadable local settings file on an ordinary local start.
        if(_remoteDownloadSettingsUnreadable && !explicitSave && !settings.Enabled)return;
        var write=Task.Run(()=>_remoteDownloadSettingsStore.Save(settings));
        _remoteDownloadSettingsWrite=write;
        try
        {
            await write;
            _savedRemoteDownloadSettings=settings;
            _remoteDownloadSettingsUnreadable=false;
        }
        catch(Exception error) when(error is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Log("Could not save the download server settings ("+error.GetType().Name+").");
            throw new IOException(ErrorMessages.Text("Не удалось сохранить настройки сервера.","Could not save the server settings."));
        }
        finally{_remoteDownloadSettingsWrite=null;}
    }

    private void ClearSavedRemoteDownloadPassword(RemoteDownloadConnection connection)
    {
        var password=F<PasswordBox>("RemoteDownloadPasswordBox");
        if(password.Password!=connection.Password || B("RemoteDownloadUsernameBox").Text!=connection.Username ||
            !Uri.TryCreate(B("RemoteDownloadAddressBox").Text.Trim(),UriKind.Absolute,out var address))return;
        string current=address.AbsoluteUri.EndsWith('/')?address.AbsoluteUri:address.AbsoluteUri+"/";
        if(current==connection.Address.AbsoluteUri)password.Clear();
    }
}
