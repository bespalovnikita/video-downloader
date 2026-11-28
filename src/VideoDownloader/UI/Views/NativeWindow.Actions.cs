using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms=System.Windows.Forms;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private bool _dependencyOperationRunning;
    private static void OpenFile(string path)
    {
        if(File.Exists(path)) Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    private void OpenCompletedQueueFile(object? sender,MouseButtonEventArgs e)
    {
        if(e.ChangedButton!=MouseButton.Left || e.OriginalSource is not DependencyObject source ||
            ItemsControl.ContainerFromElement(Q,source) is not DataGridRow container ||
            container.Item is not DownloadItem row || row.Status!="Completed" || !File.Exists(row.Path))return;
        e.Handled=true;
        try { OpenFile(row.Path); }
        catch(Exception error) when(error is IOException or ArgumentException or
                                   System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(_window,DisplayError(error),ErrorMessages.Text("Не удалось открыть файл","Cannot open file"),
                MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
    private void OpenFolder(string path)
    {
        string folder=Directory.Exists(path)?path:Path.GetDirectoryName(path)??"";
        if(Directory.Exists(folder))Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={folder}});
    }
    internal string ConfiguredOutputDirectory()
    {
        // When downloading, use the pinned output path of the actual session
        // (important for Archive and midnight rollover). Otherwise use the UI.
        return SessionState.Coordinator?.IsRunning==true
            ? SessionState.Options.OutputDirectory
            : ReadOptions().OutputDirectory;
    }
    private void OpenConfiguredOutputFolder()
    {
        try
        {
            string folder=ConfiguredOutputDirectory();
            if (!Directory.Exists(folder))
            {
                MessageBox.Show(_window, "Папка загрузок ещё не существует:\n" + folder,
                    "Открыть папку загрузок", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute=true,
                ArgumentList={folder}
            });
        }
        catch(Exception e) when (e is IOException or ArgumentException or
                                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(_window, "Не удалось открыть папку загрузок:\n" + DisplayError(e),
                "Открыть папку загрузок",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
    private void OpenAppSettingsFolder()
    {
        try
        {
            // Use the actual persistence directory rather than guessing AppData
            // or depending on whether the user has changed their downloads path.
            var path=Path.GetDirectoryName(_storage.SessionPath)!;
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe")
            {
                UseShellExecute=true,
                ArgumentList={path}
            });
        }
        catch(Exception e) when (e is IOException or ArgumentException or
                                System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            MessageBox.Show(_window, "Не удалось открыть папку настроек:\n" + DisplayError(e),
                "Открыть папку настроек",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
    }
    private void ShowFileInFolder(DownloadItem? row)
    {
        if(row==null || row.Path.Length==0 || !File.Exists(row.Path))return;
        Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={"/select,",row.Path}});
    }
    private DownloadItem? _downloadActionsRow;
    private string _downloadActionsPath="";
    private void RefreshDownloadActions()
    {
        var selected=D.SelectedItems.Count==1?D.SelectedItem as DownloadItem:null;
        if(!ReferenceEquals(selected,_downloadActionsRow) ||
            !string.Equals(selected?.Path??"",_downloadActionsPath,StringComparison.Ordinal))SetDownloadActions();
    }
    private void SetDownloadActions()
    {
        var selected=D.SelectedItems.Count==1?D.SelectedItem as DownloadItem:null;
        _downloadActionsRow=selected;_downloadActionsPath=selected?.Path??"";
        bool exists=selected!=null && File.Exists(selected.Path);
        F<Button>("OpenFileButton").IsEnabled=exists;
        F<Button>("OpenFolderButton").IsEnabled=exists;
        F<Button>("CopyPathButton").IsEnabled=selected!=null && selected.Path.Length>0;
        F<Button>("OpenUrlButton").IsEnabled=selected!=null;
    }
    private static void SetMenuAvailability(MenuItem item,bool available,string unavailableReason,string? purpose=null)
    {
        item.IsEnabled=available;
        item.ToolTip=available?purpose:unavailableReason;
        ToolTipService.SetShowOnDisabled(item,true);
    }
    private string? RetryDownloadUnavailable(DownloadItem row)
    {
        if(SessionState.Coordinator?.IsRunning==true || SessionState.RunningTask?.IsCompleted==false)
            return ErrorMessages.Text("Дождитесь завершения текущего сеанса. Для пропущенных заданий доступно «Продолжить выбранные загрузки».",
                "Wait for the current session to finish. Use Resume selected downloads for skipped tasks.");
        if(_checkpointReadOnly || _profileBackupRecoveryRequired)
            return ErrorMessages.Text("Сначала восстановите доступ к профилю: сейчас скачивание заблокировано.",
                "Restore access to the profile first: downloading is currently blocked.");
        if(_legacyEngineBlocked)
            return ErrorMessages.Text("Завершите прежний загрузчик и перезапустите приложение.",
                "Stop the previous downloader and restart the application.");
        if(_restoringSession || _startingSession || _queueItemActionBusy || _dependencyOperationRunning ||
            _profileBackupBusy || _profileBackupReplaying)
            return ErrorMessages.Text("Дождитесь завершения текущей операции.","Wait for the current operation to finish.");
        if(!SessionState.Queue.Contains(row))
            return ErrorMessages.Text("Запись уже убрана из очереди. Добавьте ссылку заново.",
                "This entry is no longer in the queue. Add its URL again.");
        if(!row.CanResume)
            return ErrorMessages.Text("Сначала завершите удаление или устраните ошибку очистки этого задания.",
                "Finish removal or resolve this task's cleanup error first.");
        return null;
    }
    private void CopyGridUrlFromKeyboard(DataGrid grid,KeyEventArgs e)
    {
        if(e.Key!=Key.C || Keyboard.Modifiers!=ModifierKeys.Control || grid.SelectedItems.Count!=1 ||
            grid.SelectedItem is not DownloadItem row || !grid.Items.Contains(row) ||
            ReferenceEquals(grid,Q) && !IsVisibleQueueRow(row))return;
        Clipboard.SetText(row.DisplayUrl);
        // Handle before DataGrid's Copy command: its template column has no
        // tabular clipboard value and must not replace the explicit URL copy.
        e.Handled=true;
    }
    internal static Forms.OpenFileDialog CreateOpenListDialog() => new()
    {
        Title=ErrorMessages.Text("Открыть список ссылок","Open URL list"),
        Filter=ErrorMessages.Text("Файлы очереди и ссылок|*.vdqueue;*.txt;*.url|Все файлы|*.*",
            "Queue and link files|*.vdqueue;*.txt;*.url|All files|*.*")
    };

    private void BindActions()
    {
        BindFilenameTemplateHelp();
        Q.MouseDoubleClick+=OpenCompletedQueueFile;
        F<Button>("AddUrlButton").Click+=(_,_)=>SubmitInputText();
        F<Button>("PasteClipboardButton").Click+=(_,_)=>Paste(true);
        B("UrlInput").KeyDown+=(_,e)=>{if(e.Key==Key.Enter){SubmitInputText();e.Handled=true;}};
        F<Button>("PortableQueueButton").Click+=(_,_)=>OpenPortableQueueMenu();
        F<Button>("OpenListButton").Click+=(_,_)=>
        {
            using var picker=CreateOpenListDialog();
            if(picker.ShowDialog()==Forms.DialogResult.OK)
            {
                if(Path.GetExtension(picker.FileName).Equals(".vdqueue",StringComparison.OrdinalIgnoreCase))
                    _ = ImportPortableFileAsync(picker.FileName);
                else AddUrls(ReadInputFile(picker.FileName));
            }
        };
        F<Button>("RemoveQueueButton").Click+=async(_,_)=>await RemoveRowsAsync(SelectedVisibleQueueRows());
        F<Button>("SubscriptionsButton").Click+=(_,_)=>OpenSubscriptions();
        F<Button>("ClearQueueButton").Click+=async(_,_)=>await ClearQueueEntriesAsync(completedOnly:true);
        F<Button>("MoveUpButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,SessionState.Queue.IndexOf(item)-1);};
        F<Button>("MoveDownButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,SessionState.Queue.IndexOf(item)+1);};
        F<Button>("RetryFailedButton").Click+=async(_,_)=>await StartAsync(SessionState.Queue.Where(x=>x.Status=="Failed").ToArray());
        F<Button>("OpenOutputDirectoryButton").Click+=(_,_)=>OpenConfiguredOutputFolder();
        F<Button>("StartButton").Click+=async(_,_)=>await StartAsync();
        F<Button>("StopButton").Click+=(_,_)=>{SessionState.Stop();UpdateState();};
        F<Button>("SpeedDebugButton").Click+=(_,_)=>OpenSpeedDebug();
        F<Button>("PauseButton").Click+=(_,_)=>{SessionState.Pause();UpdateState();};
        F<Button>("TrayButton").Click+=(_,_)=>_lifecycle.Hide();
        F<Button>("PickOutputButton").Click+=(_,_)=>
        {
            if(RejectProfileBackupMutation())return;
            using var picker=new Forms.FolderBrowserDialog{Description=ErrorMessages.Text("Папка загрузок","Download folder")};
            if(picker.ShowDialog()==Forms.DialogResult.OK && !RejectProfileBackupMutation())B("OutputBox").Text=picker.SelectedPath;
        };
        F<Button>("PickCookiesButton").Click+=(_,_)=>
        {
            if(RejectProfileBackupMutation())return;
            using var picker=new Forms.OpenFileDialog{Title=ErrorMessages.Text("Выбрать файл cookies","Choose cookies file"),Filter=ErrorMessages.Text("Текстовые файлы cookies|*.txt|Все файлы|*.*","Cookie text files|*.txt|All files|*.*")};
            if(picker.ShowDialog()==Forms.DialogResult.OK && !RejectProfileBackupMutation())B("CookiesBox").Text=picker.FileName;
        };
        C("ProfileCombo").SelectionChanged+=(_,_)=>ApplyProfile(Pick(C("ProfileCombo")));
        F<Button>("RefreshDepsButton").Click+=(_,_)=>RefreshDependencyLabel();
        F<Button>("UpdateYtDlpButton").Click+=async(_,_)=>await UpdateYtDlpFromUiAsync();
        F<Button>("InstallFfmpegButton").Click+=async(_,_)=>await InstallFfmpegFromUiAsync();
        F<Button>("OpenAppSettingsButton").Click+=(_,_)=>OpenAppSettingsFolder();
        F<Button>("RegisterProtocolButton").Click+=(_,_)=>
        {
            try { DependencyCenter.RegisterProtocol();MessageBox.Show(_window,ErrorMessages.Text("Протокол videodownloader:// зарегистрирован для текущего пользователя Windows.","videodownloader:// registered for this Windows user."),ErrorMessages.Text("Протокол зарегистрирован","Protocol registered"));}
            catch(Exception e){MessageBox.Show(_window,DisplayError(e),ErrorMessages.Text("Не удалось зарегистрировать протокол","Protocol registration failed"));}
        };
        F<Button>("OpenFileButton").Click+=(_,_)=>{if(D.SelectedItems.Count==1 && D.SelectedItem is DownloadItem row)OpenFile(row.Path);};
        F<Button>("OpenFolderButton").Click+=(_,_)=>ShowFileInFolder(D.SelectedItems.Count==1?D.SelectedItem as DownloadItem:null);
        F<Button>("CopyPathButton").Click+=(_,_)=>{if(D.SelectedItems.Count==1 && D.SelectedItem is DownloadItem row && row.Path.Length>0)Clipboard.SetText(row.Path);};
        F<Button>("OpenUrlButton").Click+=(_,_)=>{if(D.SelectedItems.Count==1 && D.SelectedItem is DownloadItem row)OpenUrl(row.DisplayUrl);};
        // Own the queue shortcut before DataGrid's class command binding can
        // consume Delete, even though row deletion is disabled on the grid.
        Q.PreviewKeyDown+=async(_,e)=>
        {
            if (e.Key==Key.Delete){e.Handled=true;await ForgetRowsAsync(SelectedVisibleQueueRows());}
        };
        Q.PreviewKeyDown+=(_,e)=>CopyGridUrlFromKeyboard(Q,e);
        D.PreviewKeyDown+=(_,e)=>CopyGridUrlFromKeyboard(D,e);
        SetupContextMenus();
    }
    private void Paste(bool select)=>_ingress.Paste(select);
    private void SetupContextMenus()
    {
        var queueMenu=new ContextMenu();
        void QueueAction(string title,Action<DownloadItem> callback)
        {
            var entry=new MenuItem{Header=title};
            entry.Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem row && IsVisibleQueueRow(row))callback(row);};
            queueMenu.Opened+=(_,_)=>entry.IsEnabled=Q.SelectedItem is DownloadItem row && IsVisibleQueueRow(row);
            queueMenu.Items.Add(entry);
        }
        QueueAction(ErrorMessages.Text("Открыть ссылку","Open URL"),row=>OpenUrl(row.DisplayUrl));
        QueueAction(ErrorMessages.Text("Копировать ссылку","Copy URL"),row=>Clipboard.SetText(row.DisplayUrl));
        var downloadSelected=new MenuItem{Header=ErrorMessages.Text("Скачать выбранные строки","Download selected rows")};
        downloadSelected.Click+=(_,_)=>{var rows=SelectedVisibleQueueRows();if(rows.Length>0)_ = StartAsync(rows);};
        queueMenu.Items.Add(downloadSelected);
        var downloadOnServer=new MenuItem{Header=ErrorMessages.Text("Качнуть через сервер","Download through server")};
        downloadOnServer.Click+=(_,_)=>_ = StartWithConfirmationAsync(SelectedVisibleQueueRows(),null,serverOnly:true);
        queueMenu.Items.Add(downloadOnServer);
        ConfigureOutOfOrderAction(queueMenu);
        var skipSelected=new MenuItem{Header=ErrorMessages.Text("Пропустить выбранные строки","Skip selected rows")};
        skipSelected.Click+=async(_,_)=>await SkipRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(skipSelected);
        var resumeSelected=new MenuItem{Header=ErrorMessages.Text("Продолжить выбранные строки","Resume selected rows")};
        resumeSelected.Click+=async(_,_)=>await ResumeRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(resumeSelected);
        var resumeFailed=AddFailedResumeAction(queueMenu,SelectedVisibleQueueRows);
        var forgetSelected=new MenuItem{Header=ErrorMessages.Text("Убрать записи из очереди","Remove queue entries")};
        forgetSelected.Click+=async(_,_)=>await ForgetRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(forgetSelected);
        var removeSelected=new MenuItem{Header=ErrorMessages.Text("Удалить загрузки и файлы…","Delete downloads and files…")};
        removeSelected.Click+=async(_,_)=>await RemoveRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(removeSelected);
        queueMenu.Opened+=(_,_)=>
        {
            bool selected=HasVisibleQueueSelection;
            downloadSelected.IsEnabled=selected && SessionState.Coordinator?.IsRunning!=true;
            downloadOnServer.IsEnabled=downloadSelected.IsEnabled && !_startingSession;
            skipSelected.IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanSkipQueueItem);
            resumeSelected.IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanContinueQueueItem);
            resumeFailed.IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanResumeFailedQueueItem);
            removeSelected.IsEnabled=selected;
            forgetSelected.IsEnabled=!QueueRecordMutationBlocked() && SelectedVisibleQueueRows().Any(CanForgetQueueRecord);
        };
        Q.ContextMenu=queueMenu;

        var downloadMenu=new ContextMenu();
        // An external move/delete may leave Path unchanged. Refresh its
        // availability when requested instead of polling disk on every tick.
        downloadMenu.Opened+=(_,_)=>SetDownloadActions();
        void DownloadAction(string title,Action<DownloadItem> callback,Func<DownloadItem,string?>? unavailable=null)
        {
            var entry=new MenuItem{Header=title};
            string? Unavailable()
            {
                if(_closing || _disposed || _appUpdateHandoff)
                    return ErrorMessages.Text("Дождитесь завершения текущей операции приложения.","Wait for the application operation to finish.");
                if(D.SelectedItems.Count!=1 || D.SelectedItem is not DownloadItem row)
                    return ErrorMessages.Text("Выберите одну загрузку.","Select one download.");
                return unavailable?.Invoke(row);
            }
            entry.Click+=(_,_)=>{if(Unavailable() is null && D.SelectedItem is DownloadItem row)callback(row);};
            downloadMenu.Opened+=(_,_)=>
            {
                string? reason=Unavailable();
                SetMenuAvailability(entry,reason is null,reason??"");
            };
            downloadMenu.Items.Add(entry);
        }
        string? MissingFile(DownloadItem row)=>File.Exists(row.Path)?null:ErrorMessages.Text(
            "Сохранённый файл ещё не создан или недоступен.","The saved file has not been created or is unavailable.");
        DownloadAction(ErrorMessages.Text("Открыть файл","Open file"),row=>OpenFile(row.Path),MissingFile);
        DownloadAction(ErrorMessages.Text("Показать в папке","Show in folder"),row=>ShowFileInFolder(row),MissingFile);
        DownloadAction(ErrorMessages.Text("Копировать путь","Copy path"),row=>Clipboard.SetText(row.Path),row=>row.Path.Length>0?null:
            ErrorMessages.Text("У этой загрузки пока нет сохранённого пути.","This download does not have a saved path yet."));
        DownloadAction(ErrorMessages.Text("Открыть ссылку","Open URL"),row=>OpenUrl(row.DisplayUrl));
        DownloadAction(ErrorMessages.Text("Копировать ссылку","Copy URL"),row=>Clipboard.SetText(row.DisplayUrl));
        DownloadAction(ErrorMessages.Text("Скачать повторно","Retry URL"),row=>{_ = StartAsync([row]);},RetryDownloadUnavailable);
        var skipDownloads=new MenuItem{Header=ErrorMessages.Text("Пропустить выбранные загрузки","Skip selected downloads")};
        skipDownloads.Click+=async(_,_)=>await SkipRowsAsync(SelectedDownloadRows());downloadMenu.Items.Add(skipDownloads);
        var resumeDownloads=new MenuItem{Header=ErrorMessages.Text("Продолжить выбранные загрузки","Resume selected downloads")};
        resumeDownloads.Click+=async(_,_)=>await ResumeRowsAsync(SelectedDownloadRows());downloadMenu.Items.Add(resumeDownloads);
        var resumeFailedDownloads=AddFailedResumeAction(downloadMenu,SelectedDownloadRows);
        downloadMenu.Opened+=(_,_)=>
        {
            var selected=SelectedDownloadRows();
            string reason=selected.Length==0?ErrorMessages.Text("Выберите загрузки.","Select downloads."):
                QueueItemActionsBlocked()?ErrorMessages.Text("Дождитесь завершения текущей операции или восстановления доступа к профилю.",
                    "Wait for the current operation to finish or restore access to the profile."):
                ErrorMessages.Text("Среди выбранных загрузок нет подходящих заданий.","None of the selected downloads are eligible for this action.");
            SetMenuAvailability(skipDownloads,!QueueItemActionsBlocked() && selected.Any(CanSkipQueueItem),reason);
            SetMenuAvailability(resumeDownloads,!QueueItemActionsBlocked() && selected.Any(CanContinueQueueItem),reason);
            SetMenuAvailability(resumeFailedDownloads,!QueueItemActionsBlocked() && selected.Any(CanResumeFailedQueueItem),reason);
        };
        DownloadAction(ErrorMessages.Text("Показать ошибку","Show error"),row=>MessageBox.Show(_window,row.ErrorMessage,
            ErrorMessages.Text("Ошибка скачивания","Download error")),row=>row.ErrorMessage.Length>0?null:
            ErrorMessages.Text("У этой загрузки нет сообщения об ошибке.","This download has no error message."));
        DownloadAction(ErrorMessages.Text("Исходная диагностика","Show raw error"),row=>MessageBox.Show(_window,row.RawError,
            ErrorMessages.Text("Исходная диагностика yt-dlp","yt-dlp error")),row=>row.RawError.Length>0?null:
            ErrorMessages.Text("У этой загрузки нет исходного сообщения об ошибке.","This download has no original error message."));
        D.ContextMenu=downloadMenu;
    }
    private MenuItem AddFailedResumeAction(ContextMenu menu,Func<DownloadItem[]> getRows)
    {
        var action=new MenuItem{Header=ErrorMessages.Text("Докачать после ошибки","Resume after error")};
        action.Click+=async(_,_)=>await ResumeRowsAsync(getRows(),failed:true);
        menu.Items.Add(action);
        return action;
    }

    private static void OpenUrl(string url)
    {
        if(AppPersistence.ValidUrl(url))Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
    }
    private void ApplyProfile(string name)
    {
        if(!_applyingProfileBackup && RejectProfileBackupMutation())return;
        string id=ProfileCatalog.ToId(name);
        if (name!=id && name!=ProfileCatalog.Display(id)) return;
        var options=ReadOptions();
        ProfileCatalog.Apply(options,name);
        ApplyOptions(options);
    }
    private async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if (_disposed) return; // internal test shutdown, not a user window close
        if(_profileBackupWindow is {IsBusy:true} backup)
        {
            e.Cancel=true;if(_profileBackupClosePending)return;
            _profileBackupClosePending=true;bool exitRequested=_lifecycle.ExitRequested;
            try{await backup.CancelAndWaitAsync();}
            finally{_profileBackupClosePending=false;}
            if(!_disposed)_=Dispatcher.BeginInvoke(()=>{if(exitRequested)_lifecycle.RequestExit();else _window.Close();});return;
        }
        if (_closeReady) return;
        if (_closing) { e.Cancel=true; return; }
        if (!_lifecycle.ExitRequested)
        {
            var active=SessionState.Coordinator?.IsRunning==true;
            var answer=MessageBox.Show(_window,
                active
                    ? ErrorMessages.Text("Загрузка ещё выполняется.\nДа: свернуть в трей и продолжить скачивание\nНет: выйти (задания «Качнуть через сервер» продолжатся на сервере)\nОтмена: остаться в приложении","A download is still running.\nYes: minimize to tray (downloads continue)\nNo: exit (explicit server jobs continue on the server)\nCancel: stay in the app")
                    : ErrorMessages.Text("Закрыть Video Downloader?\nДа: свернуть в трей\nНет: выйти\nОтмена: остаться в приложении","Close Video Downloader?\nYes: minimize to tray\nNo: exit\nCancel: stay in the app"),
                "Video Downloader",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
            if(answer==MessageBoxResult.Yes){e.Cancel=true;_lifecycle.Hide();return;}
            if(answer==MessageBoxResult.Cancel){e.Cancel=true;return;}
        }
        // Keep the dispatcher and process alive until cancellation has rolled
        // back publication and removed staging files. A second Close is issued
        // only after the final ownership/status checkpoint has been written.
        e.Cancel=true;
        _closing=true;
        _serverReconnectProbe?.Cancel();
        Task remoteConnectionStopped=StopRemoteDownloadConnectionCheckAsync();
        await StopDependencyOperationAsync();
        Task previewsStopped=_preview.StopAsync();
        Task diagnosticsStopped=StopJobDiagnosticsAsync();
        Task speedDebugStopped=StopSpeedDebugAsync();
        DisposeProfileBackup();
        DisposeDiskSpace();
        SessionState.MarkStopping();
        _tick.Stop();_ingress.Stop();
        _window.IsEnabled=false;
        try{if(_subscriptions is not null)await _subscriptions.StopChecksAsync();}
        catch(Exception error){Log("Subscription shutdown failed: "+ErrorMessages.ForException(error));}
        DisposeSubscriptions();
        try
        {
            SessionState.Coordinator?.Stop(preserveRemoteAssignments:true);
            if (SessionState.RunningTask!=null) await SessionState.RunningTask;
            if (_queueItemActionTask!=null) await _queueItemActionTask;
            if (_queueClearTask!=null) await _queueClearTask;
        }
        catch(Exception error) { Log("Shutdown cleanup failed: "+error.Message); }
        if(_remoteDownloadSettingsWrite is { } settingsWrite)
        {
            try{await settingsWrite;}
            catch(Exception error){Log("Server settings save did not finish ("+error.GetType().Name+").");}
        }
        await diagnosticsStopped;
        await speedDebugStopped;
        await previewsStopped;
        await remoteConnectionStopped;
        SaveCheckpoint();
        _journalCloseRequested=true;
        try { await _journal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch(TimeoutException) { UpdateJournalHint("Время ожидания записи журнала истекло. Приложение закрывается."); }
        FlushJournal();
        foreach(var column in _subscriptionColumns)column.Dispose();
        _panelLayout.Dispose();
        _journalLayout.Dispose();
        _theme.Dispose();
        _lifecycle.Dispose();
        _closeReady=true;
        // Even an idle/completed task may finish synchronously inside Closing.
        // WPF forbids a recursive Close until the current event has returned.
        _=Dispatcher.BeginInvoke(()=>_window.Close());
    }
    public void Dispose()
    {
        if(_disposed)return;
        _remoteDownloadConnectionCheck?.Cancel();
        _serverReconnectProbe?.Cancel();
        DisposeDependencyControls();
        DisposeJobDiagnostics();
        _speedDebugWindow?.CancelRun();
        DisposeProfileBackup();
        DisposeSubscriptions();
        FlushJournal();
        _disposed=true;
        _queueFilters?.Dispose();
        foreach(var column in _subscriptionColumns)column.Dispose();
        _downloadsView.Dispose();
        DisposeDiskSpace();
        DisposeMediaSelection();
        DisposeHistory();
        DisposeAppUpdates();
        _tick.Stop();_ingress.Dispose();
        SessionState.Coordinator?.Stop();
        _preview.Dispose();
        _panelLayout.Dispose();
        _journalLayout.Dispose();
        _theme.Dispose();
        _lifecycle.Dispose();
        if(!_journalCloseRequested)_journal.Dispose();
    }
}
