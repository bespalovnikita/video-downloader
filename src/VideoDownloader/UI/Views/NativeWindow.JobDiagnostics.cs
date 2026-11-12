using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly Dictionary<string,JobDiagnosticsWindow> _jobDiagnosticWindows=new(StringComparer.Ordinal);
    private readonly HashSet<Task> _jobDiagnosticPending=[];
    private JobDiagnosticsService? _jobDiagnostics;
    internal IReadOnlyCollection<JobDiagnosticsWindow> JobDiagnosticWindows=>_jobDiagnosticWindows.Values.ToArray();
    private bool HasJobDiagnosticWork=>_jobDiagnosticPending.Any(task=>!task.IsCompleted) ||
        _jobDiagnosticWindows.Values.Any(window=>window.IsExporting);

    private void InitializeJobDiagnostics()
    {
        const string purpose="Посмотреть события и ошибки выбранной загрузки, скопировать сведения или сохранить отчёт для помощи.";
        F<Button>("QueueDiagnosticsButton").ToolTip=purpose;
        F<Button>("DownloadDiagnosticsButton").ToolTip=purpose;
        F<Button>("QueueDiagnosticsButton").Click+=(_,_)=>
        {if(Q.SelectedItems.Count==1 && Q.SelectedItem is DownloadItem row && IsVisibleQueueRow(row))OpenJobDiagnostics(row);};
        F<Button>("DownloadDiagnosticsButton").Click+=(_,_)=>
        {if(D.SelectedItems.Count==1 && D.SelectedItem is DownloadItem row)OpenJobDiagnostics(row);};
        Q.SelectionChanged+=(_,_)=>RefreshJobDiagnosticActions();D.SelectionChanged+=(_,_)=>RefreshJobDiagnosticActions();
        void AddMenu(DataGrid grid,string name,bool queue)
        {
            if(grid.ContextMenu is not {} menu)return;
            var entry=new MenuItem{Name=name,Header="Диагностика…",ToolTip=purpose};
            bool Available()=>grid.SelectedItems.Count==1 && grid.SelectedItem is DownloadItem row &&
                AppPersistence.ValidUrl(row.Url) && (!queue || IsVisibleQueueRow(row)) && !_closing && !_disposed && !_appUpdateHandoff;
            entry.Click+=(_,_)=>{if(Available() && grid.SelectedItem is DownloadItem row)OpenJobDiagnostics(row);};
            menu.Opened+=(_,_)=>entry.IsEnabled=Available();
            menu.Items.Add(new Separator());menu.Items.Add(entry);
        }
        AddMenu(Q,"QueueDiagnosticsMenuItem",true);AddMenu(D,"DownloadDiagnosticsMenuItem",false);
        RefreshJobDiagnosticActions();
    }
    private void RefreshJobDiagnosticActions()
    {
        if(_disposed)return;
        _jobDiagnosticPending.RemoveWhere(task=>task.IsCompleted);
        bool ready=!_closing && !_appUpdateHandoff;
        F<Button>("QueueDiagnosticsButton").IsEnabled=ready && Q.SelectedItems.Count==1 &&
            Q.SelectedItem is DownloadItem queue && IsVisibleQueueRow(queue) && AppPersistence.ValidUrl(queue.Url);
        F<Button>("DownloadDiagnosticsButton").IsEnabled=ready && D.SelectedItems.Count==1 && D.SelectedItem is DownloadItem download && AppPersistence.ValidUrl(download.Url);
    }
    private void OpenJobDiagnostics(DownloadItem row)
    {
        if(_closing || _disposed || _appUpdateHandoff || !AppPersistence.ValidUrl(row.Url) ||
            !SessionState.Queue.Contains(row) && !SessionState.Downloads.Contains(row))return;
        string url=row.Url;
        if(_jobDiagnosticWindows.TryGetValue(url,out var existing)){existing.Activate();return;}
        _jobDiagnosticPending.RemoveWhere(task=>task.IsCompleted);
        if(_jobDiagnosticWindows.Count+_jobDiagnosticPending.Count>=4)
        {
            MessageBox.Show(_window,"Открыто четыре окна диагностики или завершаются их операции. Закройте лишние окна и дождитесь завершения.",
                "Диагностика задания",MessageBoxButton.OK,MessageBoxImage.Information);return;
        }
        _jobDiagnostics??=new JobDiagnosticsService(_journal,_ytDlp);
        var window=new JobDiagnosticsWindow(_window,JobDiagnosticTarget.Capture(row),_jobDiagnostics,()=>_journal.Sequence);
        _jobDiagnosticWindows.Add(url,window);
        window.WorkStateChanged+=DiagnosticWorkChanged;
        window.Closed+=(_,_)=>
        {
            window.WorkStateChanged-=DiagnosticWorkChanged;
            if(_jobDiagnosticWindows.TryGetValue(url,out var current) && ReferenceEquals(current,window))
                _jobDiagnosticWindows.Remove(url);
            var pending=window.PendingOperations;if(!pending.IsCompleted)_jobDiagnosticPending.Add(pending);
            if(!_disposed)RefreshAppUpdateControls();
        };
        try{window.Show();}
        catch(Exception error)
        {
            _jobDiagnosticWindows.Remove(url);var pending=window.CancelForOwnerAsync();
            if(!pending.IsCompleted)_jobDiagnosticPending.Add(pending);window.Close();
            Log("Не удалось открыть диагностику задания: "+DiagnosticPrivacy.Text(error.Message));
        }
    }
    private void DiagnosticWorkChanged(){if(!_disposed)RefreshAppUpdateControls();}
    private void DisposeJobDiagnostics()
    {
        foreach(var window in _jobDiagnosticWindows.Values.ToArray())
        {
            var pending=window.CancelForOwnerAsync();if(!pending.IsCompleted)_jobDiagnosticPending.Add(pending);
            window.Close();
        }
        _jobDiagnosticWindows.Clear();
    }
    private async Task StopJobDiagnosticsAsync()
    {
        DisposeJobDiagnostics();
        await Task.WhenAll(_jobDiagnosticPending.ToArray());_jobDiagnosticPending.Clear();
    }
}
