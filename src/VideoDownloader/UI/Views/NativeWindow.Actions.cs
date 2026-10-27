using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms=System.Windows.Forms;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private static void OpenFile(string path)
    {
        if(File.Exists(path)) Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
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
            MessageBox.Show(_window, "Не удалось открыть папку загрузок:\n" + e.Message,
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
            MessageBox.Show(_window, "Не удалось открыть папку настроек:\n" + e.Message,
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
    private void BindActions()
    {
        F<Button>("AddUrlButton").Click+=(_,_)=>{AddUrls(ParseUrls(B("UrlInput").Text));B("UrlInput").Clear();};
        F<Button>("PasteClipboardButton").Click+=(_,_)=>Paste(true);
        B("UrlInput").KeyDown+=(_,e)=>{if(e.Key==Key.Enter){AddUrls(ParseUrls(B("UrlInput").Text));B("UrlInput").Clear();e.Handled=true;}};
        F<Button>("OpenListButton").Click+=(_,_)=>
        {
            using var picker=new Forms.OpenFileDialog
            {
                Title="Open URL list",Filter="Link files|*.txt;*.url|All files|*.*"
            };
            if(picker.ShowDialog()==Forms.DialogResult.OK)AddUrls(ReadInputFile(picker.FileName));
        };
        F<Button>("RemoveQueueButton").Click+=(_,_)=>RemoveRows(Q.SelectedItems.Cast<DownloadItem>().ToArray());
        F<Button>("SubscriptionsButton").Click+=(_,_)=>OpenSubscriptions();
        F<Button>("ClearQueueButton").Click+=(_,_)=>
        {
            if (SessionState.Coordinator?.IsRunning==true)return;
            if (SessionState.Queue.Count>0 && MessageBox.Show(_window,"Clear queue? This removes entries, not downloaded files.",
                    "Clear queue",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            { ClearRows(); }
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
            using var picker=new Forms.FolderBrowserDialog{Description="Download folder"};
            if(picker.ShowDialog()==Forms.DialogResult.OK)B("OutputBox").Text=picker.SelectedPath;
        };
        F<Button>("PickCookiesButton").Click+=(_,_)=>
        {
            using var picker=new Forms.OpenFileDialog{Filter="Cookie text files|*.txt|All files|*.*"};
            if(picker.ShowDialog()==Forms.DialogResult.OK)B("CookiesBox").Text=picker.FileName;
        };
        C("ProfileCombo").SelectionChanged+=(_,_)=>ApplyProfile(Pick(C("ProfileCombo")));
        F<Button>("RefreshDepsButton").Click+=(_,_)=>RefreshDependencyLabel();
        F<Button>("UpdateYtDlpButton").Click+=async(_,_)=>await DependencyOperation("Updating yt-dlp...",DependencyCenter.UpdateYtDlpAsync);
        F<Button>("InstallFfmpegButton").Click+=async(_,_)=>await DependencyOperation("Installing FFmpeg...",
            async cancel=>{ await DependencyCenter.InstallFfmpegAsync(cancel);return "FFmpeg installed next to VideoDownloader.exe.";});
        F<Button>("OpenAppSettingsButton").Click+=(_,_)=>OpenAppSettingsFolder();
        F<Button>("RegisterProtocolButton").Click+=(_,_)=>
        {
            try { DependencyCenter.RegisterProtocol();MessageBox.Show(_window,"videodownloader:// registered for this Windows user.","Protocol registered");}
            catch(Exception e){MessageBox.Show(_window,e.Message,"Protocol registration failed");}
        };
        F<Button>("OpenFileButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)OpenFile(row.Path);};
        F<Button>("OpenFolderButton").Click+=(_,_)=>ShowFileInFolder(D.SelectedItem as DownloadItem);
        F<Button>("CopyPathButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row && row.Path.Length>0)Clipboard.SetText(row.Path);};
        F<Button>("OpenUrlButton").Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)OpenUrl(row.Url);};
        Q.KeyDown+=(_,e)=>
        {
            if (e.Key==Key.Delete){RemoveRows(Q.SelectedItems.Cast<DownloadItem>().ToArray());e.Handled=true;}
            else if(e.Key==Key.C && Keyboard.Modifiers==ModifierKeys.Control && Q.SelectedItem is DownloadItem row)
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
            entry.Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem row)callback(row);};
            queueMenu.Items.Add(entry);
        }
        QueueAction("Open URL",row=>OpenUrl(row.Url));
        QueueAction("Copy URL",row=>Clipboard.SetText(row.Url));
        QueueAction("Download selected",row=>{_ = StartAsync([row]);});
        QueueAction("Remove",row=>RemoveRows([row]));
        Q.ContextMenu=queueMenu;

        var downloadMenu=new ContextMenu();
        void DownloadAction(string title,Action<DownloadItem> callback)
        {
            var entry=new MenuItem{Header=title};
            entry.Click+=(_,_)=>{if(D.SelectedItem is DownloadItem row)callback(row);};
            downloadMenu.Items.Add(entry);
        }
        DownloadAction("Open file",row=>OpenFile(row.Path));
        DownloadAction("Show in folder",row=>ShowFileInFolder(row));
        DownloadAction("Copy path",row=>{if(row.Path.Length>0)Clipboard.SetText(row.Path);});
        DownloadAction("Open URL",row=>OpenUrl(row.Url));
        DownloadAction("Retry URL",row=>{_ = StartAsync([row]);});
        DownloadAction("Show raw error",row=>MessageBox.Show(_window,row.RawError,"yt-dlp error"));
        D.ContextMenu=downloadMenu;
    }
    private static void OpenUrl(string url)
    {
        if(AppPersistence.ValidUrl(url))Process.Start(new ProcessStartInfo(url){UseShellExecute=true});
    }
    private void ApplyProfile(string name)
    {
        string id=ProfileCatalog.ToId(name);
        if (name!=id && name!=ProfileCatalog.Display(id)) return;
        var options=ReadOptions();
        ProfileCatalog.Apply(options,name);
        ApplyOptions(options);
    }
    private void RefreshDependencyLabel()=>T("DependencyStatus").Text=DependencyCenter.Inspect();
    private async Task DependencyOperation(string progress,Func<CancellationToken,Task<string>> task)
    {
        T("DependencyStatus").Text=progress;
        F<Button>("UpdateYtDlpButton").IsEnabled=false;
        F<Button>("InstallFfmpegButton").IsEnabled=false;
        try
        {
            var response=await task(CancellationToken.None);
            Log(response);
            MessageBox.Show(_window,response,"Dependency operation complete");
        }
        catch(Exception e)
        {
            Log("Dependency operation failed: "+e.Message);
            MessageBox.Show(_window,e.Message,"Dependency error",MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            F<Button>("UpdateYtDlpButton").IsEnabled=true;
            F<Button>("InstallFfmpegButton").IsEnabled=true;
            RefreshDependencyLabel();
        }
    }
    private async void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if (_disposed) return; // internal test shutdown, not a user window close
        if (_closeReady) return;
        if (_closing) { e.Cancel=true; return; }
        if (!_lifecycle.ExitRequested)
        {
            var active=SessionState.Coordinator?.IsRunning==true;
            var answer=MessageBox.Show(_window,
                active
                    ? "A download is still running.\nYes: minimize to tray (downloads continue)\nNo: stop downloads and exit\nCancel: stay in the app"
                    : "Close Video Downloader?\nYes: minimize to tray\nNo: exit\nCancel: stay in the app",
                "Video Downloader",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
            if(answer==MessageBoxResult.Yes){e.Cancel=true;_lifecycle.Hide();return;}
            if(answer==MessageBoxResult.Cancel){e.Cancel=true;return;}
        }
        // Keep the dispatcher and process alive until cancellation has rolled
        // back publication and removed staging files. A second Close is issued
        // only after the final ownership/status checkpoint has been written.
        e.Cancel=true;
        _closing=true;
        DisposeSubscriptions();
        SessionState.MarkStopping();
        _tick.Stop();_ingress.Stop();
        _window.IsEnabled=false;
        try
        {
            SessionState.Coordinator?.Stop();
            if (SessionState.RunningTask!=null) await SessionState.RunningTask;
        }
        catch(Exception error) { Log("Shutdown cleanup failed: "+error.Message); }
        SaveCheckpoint();
        _lifecycle.Dispose();
        _closeReady=true;
        // Even an idle/completed task may finish synchronously inside Closing.
        // WPF forbids a recursive Close until the current event has returned.
        _=Dispatcher.BeginInvoke(()=>_window.Close());
    }
    public void Dispose()
    {
        if(_disposed)return;
        DisposeSubscriptions();
        _disposed=true;
        _tick.Stop();_ingress.Dispose();
        SessionState.Coordinator?.Stop();
        _preview.Dispose();
        _lifecycle.Dispose();
    }
}
