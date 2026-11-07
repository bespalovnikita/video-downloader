using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private readonly HashSet<DownloadItem> _resettingQueueItems=[];
    private bool CanResetQueueItem(DownloadItem row)=>SessionState.Queue.Contains(row) && row.CanResume;

    private void InitializeResetQueueItemActions()
    {
        F<Button>("ResetQueueItemsButton").Content=ErrorMessages.Text("Сбросить и скачать заново…","Reset and download again…");
        F<Button>("ResetQueueItemsButton").ToolTip=ErrorMessages.Text(
            "Удалить данные и кеш метаданных выбранных заданий и начать их заново. Остальные загрузки продолжатся.",
            "Delete selected tasks' data and cached metadata and start them again. Other downloads continue.");
        F<Button>("ResetQueueItemsButton").Click+=async(_,_)=>await ResetRowsAsync(SelectedVisibleQueueRows());
        void AddResetAction(ContextMenu menu,Func<DownloadItem[]> selected)
        {
            var entry=new MenuItem{Header=ErrorMessages.Text("Сбросить выбранные и скачать заново…","Reset selected and download again…")};
            entry.Click+=async(_,_)=>await ResetRowsAsync(selected());
            menu.Opened+=(_,_)=>entry.IsEnabled=!QueueItemActionsBlocked() && selected().Any(CanResetQueueItem);
            menu.Items.Add(entry);
        }
        AddResetAction(Q.ContextMenu,SelectedVisibleQueueRows);
        AddResetAction(D.ContextMenu,SelectedDownloadRows);
    }

    private void RefreshResetQueueItemActions()
    {
        if(!_disposed)F<Button>("ResetQueueItemsButton").IsEnabled=!QueueItemActionsBlocked() && SelectedVisibleQueueRows().Any(CanResetQueueItem);
    }

    private async Task ResetRowsAsync(IEnumerable<DownloadItem> rows)
    {
        if(QueueItemActionsBlocked())return;
        var selected=rows.Distinct().Where(CanResetQueueItem).OrderBy(row=>row.Order).ToArray();
        if(selected.Length==0)return;
        var captured=selected.ToDictionary(row=>row,row=>(row.Url,row.Selection));
        bool CurrentSelection()=>selected.All(row=>CanResetQueueItem(row) &&
            row.Url==captured[row].Url && row.Selection==captured[row].Selection);
        if(MessageBox.Show(_window,ErrorMessages.Text(
                $"Сбросить выбранные загрузки ({selected.Length}) и начать заново?\nБудут безвозвратно удалены их готовые и частично скачанные файлы, прогресс и кеш метаданных.\nСсылка, порядок, выбор формата и фрагментов сохранятся. История успехов не очищается.\nОстальные загрузки продолжатся.",
                $"Reset {selected.Length} selected downloads and start again?\nTheir published and partial files, progress and cached metadata will be permanently deleted.\nURLs, order and selected formats/fragments will be kept. Successful history is retained.\nOther downloads continue."),
                ErrorMessages.Text("Сбросить загрузки","Reset downloads"),MessageBoxButton.YesNo,MessageBoxImage.Warning)!=MessageBoxResult.Yes)return;
        if(QueueItemActionsBlocked() || !CurrentSelection())return;
        // This is an explicit redownload. Obtain the ordinary history choices
        // before deleting anything; Cancel leaves all data untouched.
        var approved=ConfirmDuplicates(selected.ToList(),false);
        if(approved is null || QueueItemActionsBlocked() || !CurrentSelection())return;
        var declined=selected.Except(approved).ToArray();
        var ready=new List<DownloadItem>();
        var expected=SessionState.Coordinator;
        await RunQueueItemActionAsync(async()=>
        {
            foreach(var row in selected)_resettingQueueItems.Add(row);
            try
            {
                if(_mediaSelectionDialog is {} editor && selected.Contains(editor.Row))editor.Close();
                var joins=selected.Select(row=>expected?.IsPending(row)==true
                    ?expected.CancelAndWaitAsync(row):Task.FromResult(false)).ToArray();
                await Task.WhenAll(joins);
                if(QueueItemActionsBlocked(true) || !selected.All(SessionState.Queue.Contains))return;
                await _preview.CancelLocalPostersAsync(selected);
                if(QueueItemActionsBlocked(true))return;
                foreach(var row in declined)
                {
                    if(row.CleanupError.Length>0 || row.PendingRemoval)
                        throw new IOException("The declined download still requires cleanup. Its queue entry was kept.");
                    if(row.ResumeState is not null)await Task.Run(()=>DownloadResumeStore.DeleteRetained(row));
                    SessionState.RejectQueueEntry(row);
                }
                if(approved.Count==0){Reindex();return;}
                // ResetAsync joins readers and old metadata callbacks. Keep
                // local-poster admission blocked until all owned deletes finish.
                await _preview.ResetAsync(approved);
                if(QueueItemActionsBlocked(true))return;
                int failed=0;
                foreach(var row in approved)
                {
                    if(QueueItemActionsBlocked(true) || !SessionState.Queue.Contains(row))break;
                    try
                    {
                        await Task.Run(()=>DownloadCleanup.Retry(row));
                        if(QueueItemActionsBlocked(true) || !SessionState.Queue.Contains(row))break;
                        row.Status="Queued";row.State="Queued";row.Progress=0;row.ProgressDetail="";
                        row.Title="";row.Speed="";row.Downloaded="";row.ETA="";row.Slot="";
                        row.TransferredBytes=0;row.RawError="";row.CompletedSelection=null;
                        row.OutputRoot="";row.RunDir="";row.PendingRemoval=false;
                        row.AttemptFinished=true;row.DeferredToNextStart=false;
                        ready.Add(row);
                    }
                    catch(Exception error)
                    {
                        DownloadCleanup.Failed(row,error.Message);failed++;
                        Log("Reset cleanup failed for "+row.Url+": "+error);
                    }
                }
                Reindex();
                if(failed>0 && !_disposed && !_closing)
                    MessageBox.Show(_window,ErrorMessages.Text(
                        $"Не удалось очистить заданий: {failed}. Их строки и пути сохранены; исправьте причину и повторите удаление.\nУспешно сброшенные задания: {ready.Count}.",
                        $"Tasks that could not be cleaned: {failed}. Their entries and paths were kept; fix the cause and retry removal.\nSuccessfully reset tasks: {ready.Count}."),
                        ErrorMessages.Text("Сброс выполнен частично","Reset partially completed"),MessageBoxButton.OK,MessageBoxImage.Warning);
            }
            finally {foreach(var row in selected)_resettingQueueItems.Remove(row);}
        });
        if(ready.Count==0 || _closing || _disposed || _appUpdateHandoff || _checkpointReadOnly || RejectProfileBackupMutation())return;
        ready=ready.Where(row=>SessionState.Queue.Contains(row) && row.CanResume && row.Status=="Queued").ToList();
        foreach(var row in ready)RequestRowPreview(row,true);
        if(expected?.IsRunning==true && ReferenceEquals(expected,SessionState.Coordinator))
        {
            foreach(var row in ready)
                if(!SessionState.TryRestartActive(expected,row))row.DeferredToNextStart=true;
            UpdateState();SaveCheckpoint();
        }
        else if(SessionState.Coordinator?.IsRunning!=true)
        {
            // Let the previous Start's dispatcher continuation finish its
            // teardown when resetting the last active task ended that session.
            await System.Windows.Threading.Dispatcher.Yield(DispatcherPriority.Background);
            if(!QueueItemActionsBlocked())await StartWithConfirmationAsync(ready,ready.ToHashSet());
        }
        else
        {
            foreach(var row in ready)row.DeferredToNextStart=true;
            SaveCheckpoint();
        }
    }
}
