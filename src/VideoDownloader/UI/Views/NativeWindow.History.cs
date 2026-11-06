using System.Windows;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private DownloadHistoryWindow? _historyWindow;
    internal DownloadHistoryWindow? HistoryLibrary=>_historyWindow;
    private void InitializeHistoryControls()=>F<Button>("OpenHistoryButton").Click+=OpenHistory;
    private void OpenHistory(object sender,RoutedEventArgs e)
    {
        if(_disposed || _closing || _appUpdateHandoff)return;
        if(_historyWindow is {} existing){existing.Activate();return;}
        var window=new DownloadHistoryWindow(_window,_storage);_historyWindow=window;
        window.Closed+=(_,_)=>{if(ReferenceEquals(_historyWindow,window))_historyWindow=null;};
        window.Show();
    }
    private void DisposeHistory()
    {
        F<Button>("OpenHistoryButton").Click-=OpenHistory;
        _historyWindow?.Close();_historyWindow=null;
    }
}
