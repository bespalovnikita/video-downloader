using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Forms=System.Windows.Forms;
using Drawing=System.Drawing;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private void SetupTray()
    {
        string iconPath=Path.Combine(AppContext.BaseDirectory,"assets","app.ico");
        _tray.Icon=File.Exists(iconPath)?new Drawing.Icon(iconPath):Drawing.SystemIcons.Application;
        _tray.Text="Video Downloader | Ready";
        _tray.Visible=true;
        var menu=new Forms.ContextMenuStrip();
        menu.Items.Add("Open",null,(_,_)=>RestoreFromTray());
        menu.Items.Add("Open downloads",null,(_,_)=>OpenFolder(B("OutputBox").Text));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit",null,(_,_)=>
        {
            _exitFromTray=true;
            try {_window.Close();}
            finally {_exitFromTray=false;}
        });
        _tray.ContextMenuStrip=menu;
        _tray.DoubleClick+=(_,_)=>RestoreFromTray();
        _window.StateChanged+=(_,_)=>{if(_window.WindowState==WindowState.Minimized)_window.Hide();};
    }
    private void RestoreFromTray()
    {
        _window.Show();
        _window.WindowState=WindowState.Normal;
        _window.Activate();
    }
    private static void OpenFile(string path)
    {
        if(File.Exists(path)) Process.Start(new ProcessStartInfo(path){UseShellExecute=true});
    }
    private void OpenFolder(string path)
    {
        string folder=Directory.Exists(path)?path:Path.GetDirectoryName(path)??"";
        if(Directory.Exists(folder))Process.Start(new ProcessStartInfo("explorer.exe"){UseShellExecute=true,ArgumentList={folder}});
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
        F<Button>("ClearQueueButton").Click+=(_,_)=>
        {
            if (_session?.IsRunning==true)return;
            if (_queue.Count>0 && MessageBox.Show(_window,"Clear queue? This removes entries, not downloaded files.",
                    "Clear queue",MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes)
            { _queue.Clear();_downloads.Clear();Reindex(); }
        };
        F<Button>("MoveUpButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,_queue.IndexOf(item)-1);};
        F<Button>("MoveDownButton").Click+=(_,_)=>{if(Q.SelectedItem is DownloadItem item)MoveItem(item,_queue.IndexOf(item)+1);};
        F<Button>("RetryFailedButton").Click+=async(_,_)=>await StartAsync(_queue.Where(x=>x.Status=="Failed").ToArray());
        F<Button>("StartButton").Click+=async(_,_)=>await StartAsync();
        F<Button>("StopButton").Click+=(_,_)=>{_stopping=true;_session?.Stop();UpdateState();};
        F<Button>("PauseButton").Click+=(_,_)=>{_session?.Pause(!_session.IsPaused);UpdateState();};
        F<Button>("TrayButton").Click+=(_,_)=>_window.Hide();
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
    private void Paste(bool select)
    {
        try
        {
            if(!Clipboard.ContainsText())return;
            var urls=ParseUrls(Clipboard.GetText());
            AddUrls(urls,clipboard:true,selectNew:select);
        }
        catch(System.Runtime.InteropServices.COMException e){Log("Clipboard busy: "+e.Message);}
    }
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
        switch(name)
        {
            case "Universal":
                C("QualityCombo").SelectedItem="Best";C("CodecCombo").SelectedItem="Auto";
                C("ContainerCombo").SelectedItem="Auto";
                K("AudioOnlyCheck").IsChecked=false;K("EmbedMetadataCheck").IsChecked=true;
                K("EmbedChaptersCheck").IsChecked=true;K("EmbedThumbnailCheck").IsChecked=false;
                K("WriteSubsCheck").IsChecked=false;K("EmbedSubsCheck").IsChecked=false;
                break;
            case "4K archive":
                C("QualityCombo").SelectedItem="2160";C("CodecCombo").SelectedItem="Auto";
                C("ContainerCombo").SelectedItem="MKV";K("ArchiveCheck").IsChecked=true;
                K("EmbedMetadataCheck").IsChecked=true;K("EmbedChaptersCheck").IsChecked=true;
                break;
            case "MP4 compatibility":
                C("QualityCombo").SelectedItem="1080";C("CodecCombo").SelectedItem="H264";
                C("ContainerCombo").SelectedItem="MP4";K("AudioOnlyCheck").IsChecked=false;
                break;
            case "Music MP3":
                K("AudioOnlyCheck").IsChecked=true;K("EmbedMetadataCheck").IsChecked=true;
                K("EmbedThumbnailCheck").IsChecked=true;
                break;
            case "Subtitles archive":
                K("AudioOnlyCheck").IsChecked=false;K("WriteSubsCheck").IsChecked=true;
                K("AutoSubsCheck").IsChecked=true;K("EmbedSubsCheck").IsChecked=true;
                K("EmbedMetadataCheck").IsChecked=true;K("EmbedChaptersCheck").IsChecked=true;
                break;
        }
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
    private void OnClosing(object? sender,System.ComponentModel.CancelEventArgs e)
    {
        if (_disposed) return; // internal test shutdown, not a user window close
        if (!_exitFromTray)
        {
            var active=_session?.IsRunning==true;
            var answer=MessageBox.Show(_window,
                active
                    ? "A download is still running.\nYes: minimize to tray (downloads continue)\nNo: stop downloads and exit\nCancel: stay in the app"
                    : "Close Video Downloader?\nYes: minimize to tray\nNo: exit\nCancel: stay in the app",
                "Video Downloader",MessageBoxButton.YesNoCancel,MessageBoxImage.Question);
            if(answer==MessageBoxResult.Yes){e.Cancel=true;_window.Hide();return;}
            if(answer==MessageBoxResult.Cancel){e.Cancel=true;return;}
        }
        SaveCheckpoint();
        _session?.Stop();
        _tick.Stop();_clipboardTick.Stop();
        _tray.Visible=false;
        _tray.Dispose();
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _tick.Stop();_clipboardTick.Stop();
        _session?.Stop();
        _previews.Dispose();
        _tray.Dispose();
    }
}
