using System.Windows;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private WindowsProfileBackup? _profileBackup;
    private ProfileBackupWindow? _profileBackupWindow;
    private bool _profileBackupBusy,_profileBackupRecoveryRequired,_applyingProfileBackup,_profileBackupClosePending,_profileBackupReplaying,_profileBackupIngressDispatch;
    internal const int MaxDeferredProfileArgumentGroups=64,MaxDeferredProfileArguments=4096;
    private sealed class DeferredProfileArguments(string[] arguments)
    {internal readonly string[] Arguments=arguments;internal int Next;}
    private readonly Queue<DeferredProfileArguments> _deferredProfileArguments=new();
    private int _deferredProfileArgumentGroups,_deferredProfileArgumentCount,_deferredProfileArgumentChars;
    private string _profileBackupIngressWarning="",_profileBackupOverflowWarning="";
    internal ProfileBackupWindow? ProfileBackupDialog=>_profileBackupWindow;
    internal bool ProfileBackupBusy=>_profileBackupBusy;
    internal ProfileBackupFilePickers? ProfileBackupPickers {get;set;}

    private void InitializeProfileBackup(WindowsProfileBackup? service)
    {
        _profileBackup=service??new WindowsProfileBackup(_storage);
        F<Button>("OpenProfileBackupButton").Click+=(_,_)=>OpenProfileBackup();
    }
    private bool CanUseProfileBackup(bool ownBarrier=false,bool allowAutomatic=true)=>!_closing && !_disposed &&
        (!_profileBackupBusy || ownBarrier) && !_profileBackupRecoveryRequired && !_profileBackupReplaying &&
        !_checkpointReadOnly && !_legacyEngineBlocked && !_restoringSession && !_startingSession && !_queueItemActionBusy && !_appUpdateBusy && !_appUpdateHandoff &&
        !_dependencyOperationRunning && (_subscriptions?.HasActiveWork!=true || allowAutomatic && _subscriptions.IsAutomaticCheckActive) &&
        SessionState.Coordinator?.IsRunning!=true &&
        SessionState.RunningTask?.IsCompleted!=false &&
        !SessionState.Queue.Concat(SessionState.Downloads).Any(row=>row.Status=="Running" || row.PendingRemoval || row.CleanupPaths.Count>0 || row.CleanupError.Length>0);
    private void OpenProfileBackup()
    {
        if(_profileBackupWindow is {} existing){existing.Activate();return;}
        if(!CanUseProfileBackup() || _profileBackup is null)
        {T("ClipboardHint").Text="Копирование профиля доступно после завершения загрузок, проверок и очистки, при исправных файлах настроек.";return;}
        var dialog=new ProfileBackupWindow(_window,_profileBackup,EnterProfileBackupAsync,ReadOptions,
            ()=>SessionState.Queue.ToArray(),ApplyRestoredProfile,ProfileBackupFailed,ProfileBackupPickers,()=>_profileBackupIngressWarning);
        _profileBackupWindow=dialog;
        dialog.Closed+=(_,_)=>{if(ReferenceEquals(_profileBackupWindow,dialog))_profileBackupWindow=null;RefreshAppUpdateControls();};
        dialog.Show();RefreshAppUpdateControls();
    }
    private async Task<IAsyncDisposable> EnterProfileBackupAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if(!CanUseProfileBackup())throw new InvalidOperationException("Профиль занят или недоступен для записи. Завершите текущие операции и повторите действие.");
        // Set before the first await/background profile mutex acquisition. A
        // timer, metadata callback or nested dialog must never wait that mutex
        // synchronously on the dispatcher.
        if(_deferredProfileArguments.Count==0)
        {_profileBackupIngressWarning=_profileBackupOverflowWarning="";_deferredProfileArgumentGroups=_deferredProfileArgumentCount=_deferredProfileArgumentChars=0;}
        _profileBackupBusy=true;_ingress.Stop();
        _mediaSelectionDialog?.Close();_mediaSelectionDialog=null;
        _historyWindow?.Close();_historyWindow=null;
        if(_window.Content is UIElement content)content.IsEnabled=false;
        UpdateState();
        IDisposable? scheduling=null;
        try
        {
            await _preview.CancelLocalPostersAsync();token.ThrowIfCancellationRequested();
            scheduling=await SuspendSubscriptionSchedulingAsync();token.ThrowIfCancellationRequested();
            if(!CanUseProfileBackup(ownBarrier:true,allowAutomatic:false))
                throw new InvalidOperationException("Состояние профиля изменилось. Завершите текущие операции и повторите действие.");
            // Both validation and the controlled checkpoint flush stay off the
            // dispatcher. Public timer/preview SaveCheckpoint remains blocked.
            bool legacy;
            try
            {
                legacy=await Task.Run(()=>
                {
                    var previous=_storage.Restore();
                    return previous?.LegacyEngineWasActive==true || AppPersistence.LegacyEngineIsAlive(previous?.Engine);
                },token);
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested){throw;}
            catch{_checkpointReadOnly=true;throw;}
            if(legacy){_legacyEngineBlocked=true;_checkpointReadOnly=true;throw new IOException("Работник прежней версии ещё использует профиль.");}
            token.ThrowIfCancellationRequested();
            await SaveProfileBackupCheckpointAsync(token);token.ThrowIfCancellationRequested();
            if(!CanUseProfileBackup(ownBarrier:true,allowAutomatic:false))
                throw new InvalidOperationException("Состояние профиля изменилось во время подготовки. Повторите действие.");
            CloseSubscriptionsForMaintenance();
            return new ProfileBackupBusyLease(this,scheduling);
        }
        catch
        {
            try{scheduling?.Dispose();}finally{await LeaveProfileBackupAsync();}
            throw;
        }
    }
    private sealed class ProfileBackupBusyLease(NativeWindow owner,IDisposable scheduling):IAsyncDisposable
    {
        private NativeWindow? _owner=owner;
        public async ValueTask DisposeAsync()
        {
            var current=Interlocked.Exchange(ref _owner,null);if(current is null)return;
            try{scheduling.Dispose();}finally{await current.LeaveProfileBackupAsync();}
        }
    }
    private async Task LeaveProfileBackupAsync()
    {
        _profileBackupBusy=false;
        _profileBackupReplaying=_deferredProfileArguments.Count>0;
        try
        {
            if(_profileBackupReplaying)await ReplayProfileArgumentsAsync();
        }
        finally
        {
            _profileBackupReplaying=false;
            if(!_disposed && !_closing)
            {
                if(_window.Content is UIElement content)content.IsEnabled=!_profileBackupRecoveryRequired;
                if(!_profileBackupRecoveryRequired && !_checkpointReadOnly && !_legacyEngineBlocked)_ingress.Start();UpdateState();
                _preview.BackfillLocalPosters();
            }
        }
    }
    private async Task SaveProfileBackupCheckpointAsync(CancellationToken token)
    {
        var rows=SessionState.Queue.ToArray();var options=ReadOptions();
        await Task.Run(()=>SaveCheckpointCore(rows,options),token);
        if(_checkpointReadOnly)throw new IOException("Не удалось сохранить текущую очередь и настройки. Работа с профилем заблокирована.");
    }
    private async Task ApplyRestoredProfile(DownloadOptions settings)
    {
        _applyingProfileBackup=true;
        try{ApplyOptions(settings);SessionState.SetOptions(settings);_lastSavedSnapshot="";}
        finally{_applyingProfileBackup=false;}
        // Restore already committed. Complete its local UI/layout checkpoint
        // under the same scheduling lease even if Close requested cancellation.
        try{await SaveProfileBackupCheckpointAsync(CancellationToken.None);}
        catch(Exception error){throw new ProfileRestoreException("Профиль восстановлен, но сохранение текущих настроек не завершено. Перезапустите приложение.",false,true,error);}
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
        if(!_profileBackupBusy && !_profileBackupRecoveryRequired && (!_profileBackupReplaying || _profileBackupIngressDispatch))return false;
        T("ClipboardHint").Text=_profileBackupRecoveryRequired?"Перезапустите приложение для завершения восстановления профиля.":
            "Профиль занят резервным копированием. Дождитесь завершения и повторите действие.";
        return true;
    }
    private void RefreshProfileBackupControls()
    {
        F<Button>("OpenProfileBackupButton").IsEnabled=CanUseProfileBackup();
        if(_checkpointReadOnly || _legacyEngineBlocked)
        {F<Button>("StartButton").IsEnabled=false;F<Button>("RetryFailedButton").IsEnabled=false;}
        foreach(string name in new[]{"AddUrlButton","PasteClipboardButton","OpenListButton"})
            F<Button>(name).IsEnabled=!_checkpointReadOnly && !_restoringSession;
        if(!_profileBackupBusy && !_profileBackupRecoveryRequired && !_profileBackupReplaying)return;
        foreach(string name in new[]{"StartButton","RetryFailedButton","MoveUpButton","MoveDownButton","ClearQueueButton","SubscriptionsButton",
            "UpdateYtDlpButton","InstallFfmpegButton","CheckAppUpdateButton","InstallAppUpdateButton"})F<Button>(name).IsEnabled=false;
        Q.AllowDrop=false;
    }
    private void DisposeProfileBackup()
    {_profileBackupWindow?.RequestCancellation();_profileBackupWindow?.Close();}

    private void DeferProfileArguments(IEnumerable<string> arguments)
    {
        var group=new List<string>();int chars=0;
        foreach(string argument in arguments)
        {
            if(argument is null || _deferredProfileArgumentGroups>=MaxDeferredProfileArgumentGroups ||
                group.Count>=MaxDeferredProfileArguments-_deferredProfileArgumentCount ||
                argument.Length>PortableQueueCodec.ClipboardTextLimit-_deferredProfileArgumentChars-chars)
            {
                _profileBackupOverflowWarning="Лимит отложенных аргументов достигнут. Последняя передача не принята; повторите её после завершения работы с профилем.";
                WarnProfileArguments(_profileBackupOverflowWarning);return;
            }
            group.Add(argument);chars+=argument.Length;
        }
        if(group.Count==0)return;
        _deferredProfileArguments.Enqueue(new(group.ToArray()));_deferredProfileArgumentGroups++;
        _deferredProfileArgumentCount+=group.Count;_deferredProfileArgumentChars+=chars;
        T("ClipboardHint").Text="Аргументы второго экземпляра отложены до завершения работы с профилем. Скачивание не начнётся автоматически.";
    }
    private bool CanReplayProfileArguments()=>!_disposed && !_closing && !_appUpdateHandoff && !_checkpointReadOnly &&
        !_legacyEngineBlocked && !_profileBackupBusy && !_profileBackupRecoveryRequired && !_startingSession &&
        SessionState.Coordinator?.IsRunning!=true && SessionState.RunningTask?.IsCompleted!=false;
    private async Task ReplayProfileArgumentsAsync()
    {
        bool validated=false;
        try
        {
            if(!CanReplayProfileArguments())throw new InvalidOperationException("Профиль недоступен для приёма отложенных аргументов.");
            bool legacy;
            try{legacy=await Task.Run(()=>
            {
                if(_profileBackup?.RecoveryRequired==true)throw new IOException("Восстановление профиля ещё не завершено.");
                var previous=_storage.Restore();new SubscriptionStore(Path.GetDirectoryName(_storage.SessionPath)!).Read();_storage.ReadHistory();
                return previous?.LegacyEngineWasActive==true || AppPersistence.LegacyEngineIsAlive(previous?.Engine);
            });}
            catch{_checkpointReadOnly=true;throw;}
            if(legacy || !CanReplayProfileArguments())throw new InvalidOperationException("Состояние профиля изменилось до приёма аргументов.");
            validated=true;
            do
            {
                while(_deferredProfileArguments.TryPeek(out var group))
                {
                    while(group.Next<group.Arguments.Length)
                    {
                        string argument=group.Arguments[group.Next];
                        if(!CanReplayProfileArguments())throw new InvalidOperationException("Приём отложенных аргументов приостановлен.");
                        if(PortableQueueCodec.IsPortable(argument))await ImportPortableTextAsync(argument,profileReplay:true);
                        else if(!argument.StartsWith("videodownloader://",StringComparison.OrdinalIgnoreCase) && !AppPersistence.ValidUrl(argument) &&
                            Path.GetExtension(argument).Equals(".vdqueue",StringComparison.OrdinalIgnoreCase))
                            await ImportPortableFileAsync(argument,profileReplay:true);
                        else
                        {
                            IReadOnlyList<string>? urls=null;
                            bool protocol=argument.StartsWith("videodownloader://",StringComparison.OrdinalIgnoreCase);
                            string extension=Path.GetExtension(argument);
                            if(!protocol)
                                urls=!AppPersistence.ValidUrl(argument) && (extension.Equals(".txt",StringComparison.OrdinalIgnoreCase) || extension.Equals(".url",StringComparison.OrdinalIgnoreCase))
                                    ?await Task.Run(()=>ReadInputFile(argument)):ParseUrls(argument);
                            if(!CanReplayProfileArguments())throw new InvalidOperationException("Состояние профиля изменилось во время чтения аргументов.");
                            _profileBackupIngressDispatch=true;
                            try{if(protocol)_ingress.ReceiveArguments([argument]);else AddUrls(urls!);}
                            finally{_profileBackupIngressDispatch=false;}
                        }
                        group.Next++;
                    }
                    _deferredProfileArguments.Dequeue();
                }
                await SaveProfileBackupCheckpointAsync(CancellationToken.None);
            }while(_deferredProfileArguments.Count>0);
            _deferredProfileArgumentGroups=_deferredProfileArgumentCount=_deferredProfileArgumentChars=0;
            _profileBackupIngressWarning=_profileBackupOverflowWarning;
        }
        catch(Exception error)
        {
            WarnProfileArguments("Отложенные аргументы не удалось полностью принять. Непринятая часть сохранена до следующей операции с копией; при выходе повторите её передачу. "+
                JournalSanitizer.Redact(ErrorMessages.ForException(error),multiline:false));
            if(validated && CanReplayProfileArguments())
                try{await SaveProfileBackupCheckpointAsync(CancellationToken.None);}
                catch(Exception save){WarnProfileArguments("Принятые ссылки не удалось сохранить: "+JournalSanitizer.Redact(ErrorMessages.ForException(save),multiline:false));}
        }
        if(_profileBackupIngressWarning.Length>0)T("ClipboardHint").Text=_profileBackupIngressWarning;
    }
    private void WarnProfileArguments(string warning)
    {
        _profileBackupIngressWarning+=(_profileBackupIngressWarning.Length>0?"\n":"")+warning;
        if(_profileBackupIngressWarning.Length>1600)_profileBackupIngressWarning=_profileBackupIngressWarning[..1600];
        T("ClipboardHint").Text=_profileBackupIngressWarning;
    }
}
