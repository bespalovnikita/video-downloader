using System.IO;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly RemoteDownloadSettingsStore _remoteDownloadSettingsStore;
    private RemoteDownloadSettings _savedRemoteDownloadSettings=new(false,null);
    private bool _remoteDownloadSettingsUnreadable;
    private Task? _remoteDownloadSettingsWrite;
    private CancellationTokenSource? _remoteDownloadConnectionCheck;
    private Task? _remoteDownloadConnectionCheckTask;
    private RemoteDownloadConnection? _serverReconnectConnection;
    private DownloadItem[] _serverReconnectRows=[];
    private CancellationTokenSource? _serverReconnectProbe;
    private DateTime _serverReconnectAfter;

    private DateTime _nextServerNoticePoll=DateTime.UtcNow.AddMinutes(1);
    private bool _serverNoticePollRunning;
    private string? _serverNoticeConnection;
    private readonly HashSet<string> _notifiedExpiringJobs=new(StringComparer.Ordinal);
    private string? _lastServerWaitReason;

    private async void PollServerNotices()
    {
        if(_closing || _disposed || _appUpdateHandoff || _serverNoticePollRunning ||
            DateTime.UtcNow<_nextServerNoticePoll || _savedRemoteDownloadSettings.Connection is not {} connection)return;
        _nextServerNoticePoll=DateTime.UtcNow.AddMinutes(1);
        _serverNoticePollRunning=true;
        try
        {
            var notices=await RemoteDownloadClient.ReadNoticesAsync(connection,CancellationToken.None);
            if(_closing || _disposed || _savedRemoteDownloadSettings.Connection!=connection)return;
            string key=RemoteDownloadJournal.ConnectionKey(connection);
            if(_serverNoticeConnection!=key)
            {
                _serverNoticeConnection=key;
                _notifiedExpiringJobs.Clear();
                _lastServerWaitReason=null;
            }
            _notifiedExpiringJobs.IntersectWith(notices.Expiring);
            int expiring=notices.Expiring.Distinct().Count(_notifiedExpiringJobs.Add);
            string? waitReason=notices.WaitingReason is "quota" or "disk" ? notices.WaitingReason : null;
            bool changed=waitReason is not null && waitReason!=_lastServerWaitReason;
            _lastServerWaitReason=waitReason;
            if(expiring>0 || changed)
            {
                string expiry=expiring>0 ? ErrorMessages.Text(
                    $"Срок хранения неполученных файлов истекает в течение часа: {expiring}.",
                    $"Unclaimed files expiring within an hour: {expiring}.") : "";
                string space=changed ? RemoteQueueStatus.Waiting(waitReason) : "";
                _lifecycle.NotifyWarning("Video Downloader",string.Join(" ",new[]{expiry,space}.Where(text=>text.Length>0)));
            }
            using var client=new RemoteDownloadClient(connection,_storage,Log);
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Task.Run(()=>client.ReplayPickupReceiptsAsync(deadline.Token),deadline.Token);
        }
        catch(Exception error) when(RemoteDownloadClient.IsRemoteFailure(error) || error is UnauthorizedAccessException) { }
        finally { _serverNoticePollRunning=false; }
    }

    private void RememberServerDownloadsToReconnect()
    {
        if(_savedRemoteDownloadSettings.Connection is not {} connection)return;
        _serverReconnectConnection=connection;
        string key=RemoteDownloadJournal.ConnectionKey(connection);
        _serverReconnectRows=SessionState.Queue
            .Where(row=>row.ServerDownloadConnectionKey==key && IsPendingServerReconnect(row)).ToArray();
    }

    private static bool IsPendingServerReconnect(DownloadItem row)=>
        row.ServerDownloadRequested && row.CanResume && row.Status is ("Queued" or "Interrupted");

    private bool CanReconnectServerDownloads()=>
        !_startingSession && !_remoteQueueManagerOpen && !_closing && !_disposed && !_restoringSession && !_checkpointReadOnly &&
        !_dependencyOperationRunning && !_queueItemActionBusy && !_appUpdateHandoff && _speedDebugWindow is null &&
        !_profileBackupBusy && !_profileBackupRecoveryRequired && !_profileBackupReplaying &&
        _remoteDownloadSettingsWrite is null && SessionState.Coordinator?.IsRunning!=true;

    private void TryReconnectServerDownloads()
    {
        if(_serverReconnectConnection is not {} connection || _serverReconnectRows.Length==0 ||
            _serverReconnectProbe is not null || DateTime.UtcNow<_serverReconnectAfter || !CanReconnectServerDownloads())return;
        _=ReconnectServerDownloadsAsync(connection);
    }

    private async Task ReconnectServerDownloadsAsync(RemoteDownloadConnection connection)
    {
        _serverReconnectRows=_serverReconnectRows.Where(row=>SessionState.Queue.Contains(row) && IsPendingServerReconnect(row)).ToArray();
        if(_serverReconnectRows.Length==0)return;
        using var probe=new CancellationTokenSource();
        _serverReconnectProbe=probe;
        _serverReconnectAfter=DateTime.UtcNow.AddSeconds(20);
        try
        {
            var journal=new RemoteDownloadJournal(_storage,connection,Log);
            await Task.Run(()=>journal.Load(),probe.Token);
            if(probe.IsCancellationRequested || !CanReconnectServerDownloads() ||
                _savedRemoteDownloadSettings.Connection!=connection)return;
            _serverReconnectRows=_serverReconnectRows.Where(row=>journal.OwnsAssignment(row.Url)).ToArray();
            if(_serverReconnectRows.Length==0)return;
            await RemoteDownloadClient.CheckConnectionAsync(connection,probe.Token);
            if(probe.IsCancellationRequested || !CanReconnectServerDownloads() ||
                _savedRemoteDownloadSettings.Connection!=connection)return;
            var rows=_serverReconnectRows.Where(row=>SessionState.Queue.Contains(row) && IsPendingServerReconnect(row)).ToArray();
            _serverReconnectRows=[];
            // The original click granted consent. Server assignment IDs and
            // request parameters come from the original connection's journal.
            await StartWithConfirmationAsync(rows,rows.ToHashSet(),serverOnly:true,reconnect:connection);
        }
        catch(Exception error) when(RemoteDownloadClient.IsRemoteFailure(error)) { }
        finally { _serverReconnectProbe=null; }
    }

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
        F<Button>("CheckRemoteDownloadConnectionButton").Click+=async(_,_)=>await CheckRemoteDownloadConnectionFromControlsAsync();
        F<Button>("OpenRemoteQueueButton").Click+=(_,_)=>OpenRemoteQueueManager();
        void ClearConnectionStatus()=>T("RemoteDownloadSettingsStatus").Text="";
        B("RemoteDownloadAddressBox").TextChanged+=(_,_)=>ClearConnectionStatus();
        B("RemoteDownloadUsernameBox").TextChanged+=(_,_)=>ClearConnectionStatus();
        F<PasswordBox>("RemoteDownloadPasswordBox").PasswordChanged+=(_,_)=>ClearConnectionStatus();
    }

    private RemoteDownloadSettings CaptureRemoteDownloadSettings(bool forStart)
    {
        bool enabled=Check(K("RemoteDownloadsEnabledCheck"));
        if(forStart && !enabled)return _savedRemoteDownloadSettings with {Enabled=false};
        string password=F<PasswordBox>("RemoteDownloadPasswordBox").Password;
        if(!enabled && password.Length==0 && _savedRemoteDownloadSettings.Connection is null)return new(false,null);
        return new(enabled,CaptureRemoteDownloadConnection(password));
    }

    private RemoteDownloadConnection CaptureRemoteDownloadConnection(string password)
    {
        var address=RemoteDownloadConnection.ParseAddress(B("RemoteDownloadAddressBox").Text);
        string username=B("RemoteDownloadUsernameBox").Text;
        if(password.Length==0 && _savedRemoteDownloadSettings.Connection is { } previous &&
            address.AbsoluteUri==previous.Address.AbsoluteUri && username==previous.Username)
            return previous;
        return new RemoteDownloadConnection(address,username,password);
    }

    private async Task CheckRemoteDownloadConnectionFromControlsAsync()
    {
        if(_disposed || _closing || _appUpdateHandoff || RejectProfileBackupMutation() ||
            _remoteDownloadConnectionCheck is not null || _remoteDownloadSettingsWrite is not null)return;
        var status=T("RemoteDownloadSettingsStatus");
        RemoteDownloadConnection connection;
        try { connection=CaptureRemoteDownloadConnection(F<PasswordBox>("RemoteDownloadPasswordBox").Password); }
        catch(ArgumentException error) { status.Text=ErrorMessages.ForException(error);return; }

        using var cancellation=new CancellationTokenSource();
        _remoteDownloadConnectionCheck=cancellation;
        var fields=F<StackPanel>("RemoteDownloadSettingsFields");
        fields.IsEnabled=false;
        status.Text=ErrorMessages.Text("Проверяем подключение…","Checking the connection…");
        var probe=RemoteDownloadClient.CheckConnectionAsync(connection,cancellation.Token);
        _remoteDownloadConnectionCheckTask=probe;
        try
        {
            var capabilities=await probe;
            if(!_disposed && !_closing && !cancellation.IsCancellationRequested)
            {
                int slots=capabilities.MaxConcurrent;
                status.Text=ErrorMessages.Text($"Подключение подтверждено. Предел параллельных заданий — {slots}.",
                    $"Connection verified. Concurrent job limit is {slots}.");
                if(capabilities.FreeDiskBytes is long freeBytes && freeBytes>=0)
                {
                    string free=DisplayValueConverter.Bytes(freeBytes);
                    status.Text+=Environment.NewLine+ErrorMessages.Text($"Свободно на VPS — {free}.", $"Free space on the VPS — {free}.");
                }
            }
        }
        catch(Exception error) when(RemoteDownloadClient.IsRemoteFailure(error))
        {
            if(!_disposed && !_closing && !cancellation.IsCancellationRequested)
                status.Text=RemoteDownloadClient.DescribeConnectionFailure(error);
        }
        finally
        {
            _remoteDownloadConnectionCheck=null;
            _remoteDownloadConnectionCheckTask=null;
            if(!_disposed && !_closing)fields.IsEnabled=true;
        }
    }

    private async Task StopRemoteDownloadConnectionCheckAsync()
    {
        var probe=_remoteDownloadConnectionCheckTask;
        _remoteDownloadConnectionCheck?.Cancel();
        if(probe is null)return;
        try { await probe; }
        catch(Exception error) when(RemoteDownloadClient.IsRemoteFailure(error)) { }
    }

    private async Task SaveRemoteDownloadSettingsFromControlsAsync()
    {
        if(_disposed || _closing || (_startingSession && SessionState.Coordinator?.IsRunning!=true) || _appUpdateHandoff || RejectProfileBackupMutation() ||
            _remoteDownloadSettingsWrite is not null || _remoteDownloadConnectionCheck is not null)return;
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
