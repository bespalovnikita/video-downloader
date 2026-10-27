using System.Windows;
using Drawing=System.Drawing;
using Forms=System.Windows.Forms;

namespace VideoDownloader.Native;

// Owns shell visibility and its resources; download shutdown remains with the
// session owner, which can delay Close until publication/cleanup is complete.
internal sealed class WindowLifecycleController : IDisposable
{
    private readonly Window _window;
    private readonly Forms.NotifyIcon _tray;
    private readonly Forms.ContextMenuStrip _menu;
    private readonly Drawing.Icon? _ownedIcon;
    private bool _disposed;
    internal bool ExitRequested { get; private set; }
    internal Forms.NotifyIcon TrayIcon=>_tray;

    internal WindowLifecycleController(Window window,Action openDownloads)
    {
        _window=window;
        string iconPath=Path.Combine(AppContext.BaseDirectory,"assets","app.ico");
        if(File.Exists(iconPath))_ownedIcon=new Drawing.Icon(iconPath);
        _tray=new Forms.NotifyIcon {
            Icon=_ownedIcon??Drawing.SystemIcons.Application,
            Text="Video Downloader | Ready",Visible=true
        };
        _menu=new Forms.ContextMenuStrip();
        _menu.Items.Add("Open",null,(_,_)=>Restore());
        _menu.Items.Add("Open downloads",null,(_,_)=>openDownloads());
        _menu.Items.Add(new Forms.ToolStripSeparator());
        _menu.Items.Add("Exit",null,(_,_)=>RequestExit());
        _tray.ContextMenuStrip=_menu;
        _tray.DoubleClick+=OnTrayDoubleClick;
        _window.StateChanged+=OnWindowStateChanged;
    }

    internal void Hide()
    {
        if(!_disposed)_window.Hide();
    }
    internal void Restore()
    {
        if(_disposed)return;
        _window.Show();
        _window.WindowState=WindowState.Normal;
        _window.Activate();
    }
    internal void Activate()
    {
        if(_disposed)return;
        if(!_window.IsVisible)_window.Show();
        if(_window.WindowState==WindowState.Minimized)_window.WindowState=WindowState.Normal;
        _window.Activate();
    }
    internal void RequestExit()
    {
        if(_disposed)return;
        ExitRequested=true;
        try{_window.Close();}
        finally{ExitRequested=false;}
    }
    internal void UpdateStatus(string status)
    {
        if(!_disposed && _tray.Text!=status)_tray.Text=status;
    }
    internal void NotifyCompletion(int completed,int remaining,int failed)
    {
        if(_disposed)return;
        try
        {
            (failed>0?System.Media.SystemSounds.Hand:System.Media.SystemSounds.Asterisk).Play();
            _tray.ShowBalloonTip(4000,"Video Downloader",$"OK {completed} · Left {remaining} · Err {failed}",
                failed>0?Forms.ToolTipIcon.Warning:Forms.ToolTipIcon.Info);
        }
        catch(InvalidOperationException){}
    }
    private void OnTrayDoubleClick(object? sender,EventArgs args)=>Restore();
    private void OnWindowStateChanged(object? sender,EventArgs args)
    {
        if(_window.WindowState==WindowState.Minimized)Hide();
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _window.StateChanged-=OnWindowStateChanged;
        _tray.DoubleClick-=OnTrayDoubleClick;
        _tray.Visible=false;
        _tray.Dispose();
        _menu.Dispose();
        _ownedIcon?.Dispose();
    }
}
