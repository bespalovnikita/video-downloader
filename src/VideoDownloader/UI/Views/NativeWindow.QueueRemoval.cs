using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    // Record removal never invokes DownloadCleanup.Retry: published paths are
    // intentionally preserved, including legacy paths without an ownership grant.
    // The session must be idle so that no late publication can race this action.
    private bool QueueRecordMutationBlocked(bool ownOperation=false)=>_closing || _disposed || _appUpdateHandoff ||
        _startingSession || (!ownOperation && (_queueItemActionBusy || _queueClearTask?.IsCompleted==false)) ||
        _dependencyOperationRunning || _legacyEngineBlocked || SessionState.Coordinator?.IsRunning==true || QueueRemovalBlocked ||
        _profileBackupBusy || _profileBackupRecoveryRequired || _profileBackupReplaying;

    private bool CanForgetQueueRecord(DownloadItem row)=>SessionState.Queue.Contains(row) && row.CanResume &&
        row.Status is not ("Running" or "Starting") && row.CleanupPaths.Count==0 &&
        (row.StageDir.Length==0 || row.ResumeState is not null);

    private void RefreshQueueRecordActions()
    {
        bool idle=!QueueRecordMutationBlocked();
        F<Button>("ForgetQueueButton").IsEnabled=idle && SelectedVisibleQueueRows().Any(CanForgetQueueRecord);
        F<Button>("ForgetQueueButton").ToolTip=SessionState.Coordinator?.IsRunning==true
            ?ErrorMessages.Text("Дождитесь завершения скачивания, чтобы убрать записи, сохранив готовые файлы. Для остановки отдельного задания используйте «Пропустить».",
                "Wait for downloads to finish before removing entries while keeping published files. Use Skip to stop an individual task.")
            :ErrorMessages.Text("Убирает только выбранные видимые записи. Готовые файлы и история сохраняются; временные данные пропущенных заданий удаляются после подтверждения.",
                "Removes only selected visible entries. Published files and history are kept; retained temporary data is discarded after confirmation.");
        F<Button>("ClearQueueButton").IsEnabled=idle && SessionState.Queue.Any(row=>row.Status=="Completed");
        F<Button>("ClearAllQueueButton").IsEnabled=idle && SessionState.Queue.Count>0;
    }

    private Task ForgetRowsAsync(IEnumerable<DownloadItem> rows)
    {
        if(QueueRecordMutationBlocked() || RejectProfileBackupMutation())return Task.CompletedTask;
        var selected=rows.Where(SessionState.Queue.Contains).Distinct().ToArray();
        if(selected.Length==0)return Task.CompletedTask;
        int retained=selected.Count(row=>row.ResumeState is not null);
        if(retained>0 && MessageBox.Show(_window,ErrorMessages.Text(
                $"Убрать выбранные записи ({selected.Length}) из очереди?\nГотовые файлы и история сохранятся.\nВременные данные для продолжения будут удалены у {retained} заданий.",
                $"Remove {selected.Length} selected queue entries?\nPublished files and history will be kept.\nRetained temporary data for {retained} tasks will be discarded."),
                ErrorMessages.Text("Убрать записи из очереди","Remove queue entries"),MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
            return Task.CompletedTask;
        if(QueueRecordMutationBlocked() || RejectProfileBackupMutation())return Task.CompletedTask;
        return _queueClearTask=ForgetQueueRecordsCoreAsync(selected,completedOnly:false);
    }

    private Task ClearQueueEntriesAsync(bool completedOnly)
    {
        if(QueueRecordMutationBlocked() || RejectProfileBackupMutation())return Task.CompletedTask;
        // Capture scope before the modal dispatcher. Rechecking can only shrink
        // this set; a new row or a later button mode must never enter it.
        var candidates=SessionState.Queue.Where(row=>!completedOnly || row.Status=="Completed").ToArray();
        if(candidates.Length==0 || !ConfirmClearEntireQueue(candidates,completedOnly))return Task.CompletedTask;
        if(QueueRecordMutationBlocked() || RejectProfileBackupMutation())return Task.CompletedTask;
        return _queueClearTask=ForgetQueueRecordsCoreAsync(candidates,completedOnly);
    }

    private async Task ForgetQueueRecordsCoreAsync(IReadOnlyCollection<DownloadItem> candidates,bool completedOnly)
    {
        bool ownsMutation=false;
        try
        {
            bool Eligible(DownloadItem row)=>CanForgetQueueRecord(row) && (!completedOnly || row.Status=="Completed");
            var selected=candidates.Where(Eligible).ToArray();
            await _preview.CancelLocalPostersAsync(selected);
            if(QueueRecordMutationBlocked(ownOperation:true) || _queueItemActionBusy || RejectProfileBackupMutation())return;
            _queueItemActionBusy=true;ownsMutation=true;UpdateState();
            foreach(var row in selected)
            {
                if(QueueRecordMutationBlocked(ownOperation:true) || !Eligible(row))continue;
                if(row.ResumeState is not null)
                {
                    try { await Task.Run(()=>DownloadResumeStore.DeleteRetained(row)); }
                    catch(Exception error)
                    {
                        DownloadCleanup.Failed(row,error.Message);
                        Log("Could not discard retained queue data: "+error);
                        continue;
                    }
                }
                if(Eligible(row) && row.StageDir.Length==0)ForgetRow(row);
            }
            int kept=candidates.Count(SessionState.Queue.Contains);
            if(kept>0 && !_closing && !_disposed)
                T("ClipboardHint").Text=ErrorMessages.Text(
                    $"Сохранено записей: {kept}. Работающие задания и записи с незавершённой очисткой не убираются; готовые файлы сохранены.",
                    $"{kept} entries kept. Running tasks and entries awaiting cleanup cannot be removed; published files were kept.");
            Reindex();SaveReducedRecoveryQueue();
        }
        catch(Exception error)
        {
            Log("Queue record removal failed: "+error);
            if(!_closing && !_disposed)
                MessageBox.Show(_window,DisplayError(error),ErrorMessages.Text("Записи не убраны","Queue entries were not removed"),
                    MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            if(ownsMutation)_queueItemActionBusy=false;
            if(!_disposed)UpdateState();
        }
    }
}
