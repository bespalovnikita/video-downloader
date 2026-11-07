using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms=System.Windows.Forms;

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
    private void SetDownloadActions()
    {
        var selected=D.SelectedItem as DownloadItem;
        F<Button>("OpenFileButton").IsEnabled=selected!=null && File.Exists(selected.Path);
        F<Button>("OpenFolderButton").IsEnabled=selected!=null && File.Exists(selected.Path);
        F<Button>("CopyPathButton").IsEnabled=selected!=null && selected.Path.Length>0;
        F<Button>("OpenUrlButton").IsEnabled=selected!=null;
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
        F<Button>("AddUrlButton").Click+=(_,_)=>{AddInputText(B("UrlInput").Text);B("UrlInput").Clear();};
        F<Button>("PasteClipboardButton").Click+=(_,_)=>Paste(true);
        B("UrlInput").KeyDown+=(_,e)=>{if(e.Key==Key.Enter){AddInputText(B("UrlInput").Text);B("UrlInput").Clear();e.Handled=true;}};
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
        F<Button>("ClearQueueButton").Click+=async(_,_)=>
        {
            if(_closing || _disposed || _appUpdateHandoff || _queueItemActionBusy || QueueRemovalBlocked || RejectProfileBackupMutation())return;
            if (SessionState.Coordinator?.IsRunning==true)return;
            var candidates=SessionState.ClearCandidates();
            if(candidates.Length>0 && ConfirmClearEntireQueue(candidates) && !_closing && !_disposed &&
                !_appUpdateHandoff && SessionState.Coordinator?.IsRunning!=true)
            {await ClearRowsAsync(candidates);}
        };
        F<Button>("MoveUpButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,SessionState.Queue.IndexOf(item)-1);};
        F<Button>("MoveDownButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,SessionState.Queue.IndexOf(item)+1);};
        F<Button>("RetryFailedButton").Click+=async(_,_)=>await StartAsync(SessionState.Queue.Where(x=>x.Status=="Failed").ToArray());
        F<Button>("OpenOutputDirectoryButton").Click+=(_,_)=>OpenConfiguredOutputFolder();
        F<Button>("StartButton").Click+=async(_,_)=>await StartAsync();
        F<Button>("StopButton").Click+=(_,_)=>{SessionState.Stop();UpdateState();};
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
        F<Button>("UpdateYtDlpButton").Click+=async(_,_)=>await DependencyOperation(ErrorMessages.Text("Обновление yt-dlp…","Updating yt-dlp..."),DependencyCenter.UpdateYtDlpAsync);
        F<Button>("InstallFfmpegButton").Click+=async(_,_)=>await DependencyOperation(ErrorMessages.Text("Установка FFmpeg…","Installing FFmpeg..."),
            async cancel=>{ await DependencyCenter.InstallFfmpegAsync(cancel);return ErrorMessages.Text("FFmpeg установлен рядом с VideoDownloader.exe.","FFmpeg installed next to VideoDownloader.exe.");});
        F<Button>("OpenAppSettingsButton").Click+=(_,_)=>OpenAppSettingsFolder();
        F<Button>("RegisterProtocolButton").Click+=(_,_)=>
        {
            try { DependencyCenter.RegisterProtocol();MessageBox.Show(_window,ErrorMessages.Text("Протокол videodownloader:// зарегистрирован для текущего пользователя Windows.","videodownloader:// registered for this Windows user."),ErrorMessages.Text("Протокол зарегистрирован","Protocol registered"));}
            catch(Exception e){MessageBox.Show(_window,DisplayError(e),ErrorMessages.Text("Не удалось зарегистрировать протокол","Protocol registration failed"));}
        };
        F<Button>("OpenFileButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)OpenFile(row.Path);};
        F<Button>("OpenFolderButton").Click+=(_,_)=>ShowFileInFolder(D.SelectedItem as DownloadItem);
        F<Button>("CopyPathButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row && row.Path.Length>0)Clipboard.SetText(row.Path);};
        F<Button>("OpenUrlButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)OpenUrl(row.Url);};
        Q.KeyDown+=async(_,e)=>
        {
            if (e.Key==Key.Delete){e.Handled=true;await RemoveRowsAsync(SelectedVisibleQueueRows());}
            else if(e.Key==Key.C && Keyboard.Modifiers==ModifierKeys.Control && Q.SelectedItem is DownloadItem row && IsVisibleQueueRow(row))
            {Clipboard.SetText(row.Url);e.Handled=true;}
        };
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
        QueueAction(ErrorMessages.Text("Открыть ссылку","Open URL"),row=>OpenUrl(row.Url));
        QueueAction(ErrorMessages.Text("Копировать ссылку","Copy URL"),row=>Clipboard.SetText(row.Url));
        var downloadSelected=new MenuItem{Header=ErrorMessages.Text("Скачать выбранные строки","Download selected rows")};
        downloadSelected.Click+=(_,_)=>{var rows=SelectedVisibleQueueRows();if(rows.Length>0)_ = StartAsync(rows);};
        queueMenu.Items.Add(downloadSelected);
        var skipSelected=new MenuItem{Header=ErrorMessages.Text("Пропустить выбранные строки","Skip selected rows")};
        skipSelected.Click+=async(_,_)=>await SkipRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(skipSelected);
        var resumeSelected=new MenuItem{Header=ErrorMessages.Text("Продолжить выбранные строки","Resume selected rows")};
        resumeSelected.Click+=async(_,_)=>await ResumeRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(resumeSelected);
        var removeSelected=new MenuItem{Header=ErrorMessages.Text("Удалить выбранные строки","Remove selected rows")};
        removeSelected.Click+=async(_,_)=>await RemoveRowsAsync(SelectedVisibleQueueRows());queueMenu.Items.Add(removeSelected);
        queueMenu.Opened+=(_,_)=>
        {
            bool selected=HasVisibleQueueSelection;
            downloadSelected.IsEnabled=selected && SessionState.Coordinator?.IsRunning!=true;
            skipSelected.IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanSkipQueueItem);
            resumeSelected.IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanContinueQueueItem);
            removeSelected.IsEnabled=selected;
        };
        Q.ContextMenu=queueMenu;

        var downloadMenu=new ContextMenu();
        void DownloadAction(string title,Action<DownloadItem> callback)
        {
            var entry=new MenuItem{Header=title};
            entry.Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)callback(row);};
            downloadMenu.Items.Add(entry);
        }
        DownloadAction(ErrorMessages.Text("Открыть файл","Open file"),row=>OpenFile(row.Path));
        DownloadAction(ErrorMessages.Text("Показать в папке","Show in folder"),row=>ShowFileInFolder(row));
        DownloadAction(ErrorMessages.Text("Копировать путь","Copy path"),row=>{if(row.Path.Length>0)Clipboard.SetText(row.Path);});
        DownloadAction(ErrorMessages.Text("Открыть ссылку","Open URL"),row=>OpenUrl(row.Url));
        DownloadAction(ErrorMessages.Text("Скачать повторно","Retry URL"),row=>{_ = StartAsync([row]);});
        var skipDownloads=new MenuItem{Header=ErrorMessages.Text("Пропустить выбранные загрузки","Skip selected downloads")};
        skipDownloads.Click+=async(_,_)=>await SkipRowsAsync(SelectedDownloadRows());downloadMenu.Items.Add(skipDownloads);
        var resumeDownloads=new MenuItem{Header=ErrorMessages.Text("Продолжить выбранные загрузки","Resume selected downloads")};
        resumeDownloads.Click+=async(_,_)=>await ResumeRowsAsync(SelectedDownloadRows());downloadMenu.Items.Add(resumeDownloads);
        downloadMenu.Opened+=(_,_)=>
        {
            skipDownloads.IsEnabled=!QueueItemActionsBlocked() && SelectedDownloadRows().Any(CanSkipQueueItem);
            resumeDownloads.IsEnabled=!QueueItemActionsBlocked() && SelectedDownloadRows().Any(CanContinueQueueItem);
        };
        DownloadAction(ErrorMessages.Text("Показать ошибку","Show error"),row=>MessageBox.Show(_window,row.ErrorMessage,
            ErrorMessages.Text("Ошибка скачивания","Download error")));
        DownloadAction(ErrorMessages.Text("Исходная диагностика","Show raw error"),row=>MessageBox.Show(_window,row.RawError,
            ErrorMessages.Text("Исходная диагностика yt-dlp","yt-dlp error")));
        D.ContextMenu=downloadMenu;
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
    private void RefreshDependencyLabel()=>T("DependencyStatus").Text=DependencyCenter.Inspect();
    private async Task DependencyOperation(string progress,Func<CancellationToken,Task<string>> task)
    {
        if(_closing || _disposed || _appUpdateHandoff || _dependencyOperationRunning || _queueItemActionBusy || RejectProfileBackupMutation())return;
        _dependencyOperationRunning=true;
        RefreshAppUpdateControls();
        T("DependencyStatus").Text=progress;
        F<Button>("UpdateYtDlpButton").IsEnabled=false;
        F<Button>("InstallFfmpegButton").IsEnabled=false;
        try
        {
            using var schedulingPause=await SuspendSubscriptionSchedulingAsync();
            await _preview.CancelLocalPostersAsync();
            if(_closing || _disposed || _appUpdateHandoff)throw new OperationCanceledException("Приложение закрывается или обновляется.");
            var response=await task(CancellationToken.None);
            Log(response);
            MessageBox.Show(_window,ErrorMessages.Text("Операция с компонентами завершена. Подробности доступны в журнале.",response),ErrorMessages.Text("Компоненты обновлены","Dependency operation complete"));
        }
        catch(Exception e)
        {
            Log("Dependency operation failed: "+e.Message);
            MessageBox.Show(_window,DisplayError(e),ErrorMessages.Text("Ошибка обновления компонентов","Dependency error"),MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            _dependencyOperationRunning=false;
            F<Button>("UpdateYtDlpButton").IsEnabled=true;
            F<Button>("InstallFfmpegButton").IsEnabled=true;
            RefreshDependencyLabel();
            RefreshAppUpdateControls();
            _preview.ResetLocalPosterAttempts();
            _preview.BackfillLocalPosters();
        }
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
                    ? ErrorMessages.Text("Загрузка ещё выполняется.\nДа: свернуть в трей и продолжить скачивание\nНет: остановить скачивание и выйти\nОтмена: остаться в приложении","A download is still running.\nYes: minimize to tray (downloads continue)\nNo: stop downloads and exit\nCancel: stay in the app")
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
        Task previewsStopped=_preview.StopAsync();
        Task diagnosticsStopped=StopJobDiagnosticsAsync();
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
            SessionState.Coordinator?.Stop();
            if (SessionState.RunningTask!=null) await SessionState.RunningTask;
            if (_queueItemActionTask!=null) await _queueItemActionTask;
            if (_queueClearTask!=null) await _queueClearTask;
        }
        catch(Exception error) { Log("Shutdown cleanup failed: "+error.Message); }
        await diagnosticsStopped;
        await previewsStopped;
        SaveCheckpoint();
        _journalCloseRequested=true;
        try { await _journal.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2)); }
        catch(TimeoutException) { UpdateJournalHint("Время ожидания записи журнала истекло. Приложение закрывается."); }
        FlushJournal();
        _panelLayout.Dispose();
        _lifecycle.Dispose();
        _closeReady=true;
        // Even an idle/completed task may finish synchronously inside Closing.
        // WPF forbids a recursive Close until the current event has returned.
        _=Dispatcher.BeginInvoke(()=>_window.Close());
    }
    public void Dispose()
    {
        if(_disposed)return;
        DisposeJobDiagnostics();
        DisposeProfileBackup();
        DisposeSubscriptions();
        FlushJournal();
        _disposed=true;
        _queueFilters?.Dispose();
        _downloadsView.Dispose();
        DisposeDiskSpace();
        DisposeMediaSelection();
        DisposeHistory();
        DisposeAppUpdates();
        _tick.Stop();_ingress.Dispose();
        SessionState.Coordinator?.Stop();
        _preview.Dispose();
        _panelLayout.Dispose();
        _lifecycle.Dispose();
        if(!_journalCloseRequested)_journal.Dispose();
    }
}
