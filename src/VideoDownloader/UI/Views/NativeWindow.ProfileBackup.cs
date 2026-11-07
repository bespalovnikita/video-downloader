using System.Windows;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private WindowsProfileBackup? _profileBackup;
    private ProfileBackupWindow? _profileBackupWindow;
    private bool _profileBackupBusy,_profileBackupRecoveryRequired,_applyingProfileBackup,_profileBackupClosePending;
    internal ProfileBackupWindow? ProfileBackupDialog=>_profileBackupWindow;
    internal bool ProfileBackupBusy=>_profileBackupBusy;
    internal ProfileBackupFilePickers? ProfileBackupPickers {get;set;}

    private void InitializeProfileBackup(WindowsProfileBackup? service)
    {
        _profileBackup=service??new WindowsProfileBackup(_storage);
        F<Button>("OpenProfileBackupButton").Click+=(_,_)=>OpenProfileBackup();
    }
    private bool CanUseProfileBackup()=>!_closing && !_disposed && !_profileBackupBusy && !_profileBackupRecoveryRequired &&
        !_checkpointReadOnly && !_legacyEngineBlocked && !_restoringSession && !_startingSession && !_appUpdateBusy && !_appUpdateHandoff &&
        !_dependencyOperationRunning && _subscriptions?.HasActiveWork!=true && SessionState.Coordinator?.IsRunning!=true &&
        SessionState.RunningTask?.IsCompleted!=false &&
        !SessionState.Queue.Concat(SessionState.Downloads).Any(row=>row.Status=="Running" || row.PendingRemoval || row.CleanupPaths.Count>0 || row.CleanupError.Length>0);
    private void OpenProfileBackup()
    {
        if(_profileBackupWindow is {} existing){existing.Activate();return;}
        if(!CanUseProfileBackup() || _profileBackup is null)
        {T("ClipboardHint").Text="Копирование профиля доступно после завершения загрузок, проверок и очистки, при исправных файлах настроек.";return;}
        var dialog=new ProfileBackupWindow(_window,_profileBackup,EnterProfileBackupAsync,ReadOptions,
            ()=>SessionState.Queue.ToArray(),ApplyRestoredProfile,ProfileBackupFailed,ProfileBackupPickers);
        _profileBackupWindow=dialog;
        dialog.Closed+=(_,_)=>{if(ReferenceEquals(_profileBackupWindow,dialog))_profileBackupWindow=null;RefreshAppUpdateControls();};
        dialog.Show();RefreshAppUpdateControls();
    }
    private Task<IDisposable> EnterProfileBackupAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!CanUseProfileBackup())throw new InvalidOperationException("Профиль занят или недоступен для записи. Завершите текущие операции и повторите действие.");
        SaveCheckpoint();
        if(_checkpointReadOnly)throw new IOException("Не удалось сохранить текущую очередь и настройки. Восстановление заблокировано.");
        // Set before the first await/background profile mutex acquisition. A
        // timer, metadata callback or nested dialog must never wait that mutex
        // synchronously on the dispatcher.
        _profileBackupBusy=true;_ingress.Stop();
        _mediaSelectionDialog?.Close();_mediaSelectionDialog=null;
        _historyWindow?.Close();_historyWindow=null;
        DisposeSubscriptions();_subscriptions=null;
        if(_window.Content is UIElement content)content.IsEnabled=false;
        UpdateState();
        return Task.FromResult<IDisposable>(new ProfileBackupBusyLease(this));
    }
    private sealed class ProfileBackupBusyLease(NativeWindow owner):IDisposable
    {
        private NativeWindow? _owner=owner;
        public void Dispose()=>Interlocked.Exchange(ref _owner,null)?.LeaveProfileBackup();
    }
    private void LeaveProfileBackup()
    {
        _profileBackupBusy=false;
        if(_disposed || _closing)return;
        if(_window.Content is UIElement content)content.IsEnabled=!_profileBackupRecoveryRequired;
        if(!_profileBackupRecoveryRequired)_ingress.Start();
        UpdateState();
    }
    private void ApplyRestoredProfile(DownloadOptions settings)
    {
        _applyingProfileBackup=true;
        try{ApplyOptions(settings);SessionState.SetOptions(settings);_lastSavedSnapshot="";}
        finally{_applyingProfileBackup=false;}
    }
    private void ProfileBackupFailed(Exception error)
    {
        Log("Резервная копия: "+JournalSanitizer.Redact(error.Message));
        if(error is ProfileRestoreException {RecoveryRequired:true} || _profileBackup?.RecoveryRequired==true)
        {
            _profileBackupRecoveryRequired=true;_checkpointReadOnly=true;_ingress.Stop();
            if(_window.Content is UIElement content)content.IsEnabled=false;
            RefreshProfileBackupControls();
        }
    }
    private bool RejectProfileBackupMutation()
    {
        if(!_profileBackupBusy && !_profileBackupRecoveryRequired)return false;
        T("ClipboardHint").Text=_profileBackupRecoveryRequired?"Перезапустите приложение для завершения восстановления профиля.":
            "Профиль занят резервным копированием. Дождитесь завершения и повторите действие.";
        return true;
    }
    private void RefreshProfileBackupControls()
    {
        F<Button>("OpenProfileBackupButton").IsEnabled=CanUseProfileBackup();
        if(_checkpointReadOnly || _legacyEngineBlocked)
        {F<Button>("StartButton").IsEnabled=false;F<Button>("RetryFailedButton").IsEnabled=false;}
        if(!_profileBackupBusy && !_profileBackupRecoveryRequired)return;
        foreach(string name in new[]{"StartButton","RetryFailedButton","MoveUpButton","MoveDownButton","ClearQueueButton","SubscriptionsButton",
            "UpdateYtDlpButton","InstallFfmpegButton","CheckAppUpdateButton","InstallAppUpdateButton"})F<Button>(name).IsEnabled=false;
        Q.AllowDrop=false;
    }
    private void DisposeProfileBackup()
    {_profileBackupWindow?.RequestCancellation();_profileBackupWindow?.Close();}
}
