using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private bool _queueItemActionBusy;
    private Task? _queueItemActionTask;

    private bool QueueItemActionsBlocked(bool ownOperation=false)=>_disposed || _closing || _appUpdateHandoff ||
        _restoringSession || _checkpointReadOnly || _legacyEngineBlocked || _dependencyOperationRunning ||
        _profileBackupBusy || _profileBackupRecoveryRequired || _profileBackupReplaying ||
        (_startingSession && SessionState.Coordinator?.IsRunning!=true) || (_queueItemActionBusy && !ownOperation);

    private bool CanSkipQueueItem(DownloadItem row)=>SessionState.Queue.Contains(row) && row.CanResume &&
        row.Status is not ("Completed" or "Skipped");
    private bool CanContinueQueueItem(DownloadItem row)=>SessionState.Queue.Contains(row) && row.CanResume &&
        row.Status=="Skipped" && row.AttemptFinished;

    private void InitializeQueueItemActions()
    {
        F<Button>("SkipQueueItemsButton").Content=ErrorMessages.Text("Пропустить выбранные","Skip selected");
        F<Button>("SkipQueueItemsButton").ToolTip=ErrorMessages.Text(
            "Остановить выбранные задания, сохранить временные данные и освободить работников для очереди.",
            "Stop selected tasks, retain their temporary data and free workers for the queue.");
        F<Button>("ResumeQueueItemsButton").Content=ErrorMessages.Text("Продолжить выбранные","Resume selected");
        F<Button>("ResumeQueueItemsButton").ToolTip=ErrorMessages.Text(
            "Продолжить пропущенные задания с сохранёнными данными. Возможность дозагрузки зависит от источника и формата.",
            "Resume skipped tasks using retained data. Resuming depends on the source and format.");
        F<Button>("SkipQueueItemsButton").Click+=async(_,_)=>await SkipRowsAsync(SelectedVisibleQueueRows());
        F<Button>("ResumeQueueItemsButton").Click+=async(_,_)=>await ResumeRowsAsync(SelectedVisibleQueueRows());
    }

    private void RefreshQueueItemActions()
    {
        if(_disposed)return;
        bool ready=!QueueItemActionsBlocked();
        var selected=SelectedVisibleQueueRows();
        F<Button>("SkipQueueItemsButton").IsEnabled=ready && selected.Any(CanSkipQueueItem);
        F<Button>("ResumeQueueItemsButton").IsEnabled=ready && selected.Any(CanContinueQueueItem);
        RefreshResetQueueItemActions();
    }

    private Task RunQueueItemActionAsync(Func<Task> action)
    {
        if(QueueItemActionsBlocked())return Task.CompletedTask;
        _queueItemActionBusy=true;
        UpdateState();
        return _queueItemActionTask=RunQueueItemActionCoreAsync(action);
    }
    private async Task RunQueueItemActionCoreAsync(Func<Task> action)
    {
        try { await action(); }
        catch(Exception error)
        {
            Log("Queue item operation failed: "+error);
            if(!_disposed && !_closing)
                MessageBox.Show(_window,DisplayError(error),ErrorMessages.Text("Операция с очередью не завершена","Queue operation did not finish"),
                    MessageBoxButton.OK,MessageBoxImage.Warning);
        }
        finally
        {
            _queueItemActionBusy=false;
            if(!_disposed)
            {
                RefreshQueueView(true);UpdateState();SaveCheckpoint();
                _preview.BackfillLocalPosters();
            }
        }
    }

    private Task SkipRowsAsync(IEnumerable<DownloadItem> rows)
    {
        if(QueueItemActionsBlocked())return Task.CompletedTask;
        var selected=rows.Distinct().Where(CanSkipQueueItem).OrderBy(row=>row.Order).ToArray();
        if(selected.Length==0)return Task.CompletedTask;
        return RunQueueItemActionAsync(async()=>
        {
            int skipped=0;
            // Send every cancellation before waiting for any individual child.
            // Other workers and queue ingress continue while these jobs join.
            var pending=new List<(DownloadItem Row,Task<bool>? Completion)>();
            foreach(var row in selected)
            {
                if(QueueItemActionsBlocked(true) || !CanSkipQueueItem(row))continue;
                var owner=SessionState.Coordinator;
                pending.Add((row,owner?.IsRunning==true && owner.IsPending(row)?owner.SkipAsync(row):null));
            }
            foreach(var (row,completion) in pending)
            {
                bool accepted=completion is null || await completion;
                if(!accepted || QueueItemActionsBlocked(true) || !SessionState.Queue.Contains(row) ||
                    !CanSkipQueueItem(row) && row.Status!="Skipped")continue;
                if(completion is null)
                {
                    row.Status="Skipped";row.State="Skipped";row.Speed="";row.ETA="";
                    row.AttemptFinished=true;
                }
                if(SessionState.Queue.Contains(row) && row.Status=="Skipped")skipped++;
            }
            T("ClipboardHint").Text=ErrorMessages.Text($"Пропущено заданий: {skipped}. Для продолжения выберите их и нажмите «Продолжить выбранные».",
                $"Skipped tasks: {skipped}. Select them and use Resume selected to continue.");
        });
    }

    private async Task ResumeRowsAsync(IEnumerable<DownloadItem> rows)
    {
        if(QueueItemActionsBlocked())return;
        var selected=rows.Distinct().Where(CanContinueQueueItem).OrderBy(row=>row.Order).ToArray();
        if(selected.Length==0)return;
        if(selected.Any(row=>row.ResumeState?.Selection?.IsPartial==true) &&
            MessageBox.Show(_window,ErrorMessages.Text(
                "Для фрагментов, скачиваемых через FFmpeg, загрузка может начаться заново: FFmpeg не всегда поддерживает дозагрузку. Продолжить?",
                "Fragments downloaded through FFmpeg may restart because FFmpeg does not always support resuming. Continue?"),
                ErrorMessages.Text("Продолжение фрагментов","Resume fragments"),MessageBoxButton.YesNo,MessageBoxImage.Information)!=MessageBoxResult.Yes)return;
        if(QueueItemActionsBlocked())return;
        selected=selected.Where(CanContinueQueueItem).ToArray();
        var expected=SessionState.Coordinator;
        if(expected?.IsRunning!=true)
        {
            await StartAsync(selected);
            return;
        }
        await RunQueueItemActionAsync(async()=>
        {
            await Task.Run(()=>
            {
                foreach(var row in selected.Where(row=>row.ResumeState is not null))DownloadResumeStore.Validate(row);
            });
            if(QueueItemActionsBlocked(true) || !ReferenceEquals(expected,SessionState.Coordinator) || !expected.IsRunning)return;
            int resumed=0;
            foreach(var row in selected.Where(CanContinueQueueItem))
                if(SessionState.TryResumeActive(expected,row))resumed++;
            T("ClipboardHint").Text=ErrorMessages.Text($"Возобновлено заданий: {resumed}.",$"Resumed tasks: {resumed}.");
        });
    }

    private DownloadItem[] SelectedDownloadRows()=>D.SelectedItems.Cast<DownloadItem>()
        .Where(SessionState.Queue.Contains).Distinct().OrderBy(row=>row.Order).ToArray();

    private async Task DiscardDeclinedRowsAsync(IReadOnlyList<DownloadItem> rows)
    {
        if(rows.Count==0)return;
        _queueItemActionBusy=true;UpdateState();
        try
        {
            foreach(var row in rows)
            {
                if(_closing || _disposed || _appUpdateHandoff || _checkpointReadOnly || !SessionState.Queue.Contains(row))return;
                if(row.ResumeState is not null)
                {
                    try { await Task.Run(()=>DownloadResumeStore.DeleteRetained(row)); }
                    catch(Exception error) {DownloadCleanup.Failed(row,error.Message);throw;}
                }
                SessionState.RejectQueueEntry(row);
            }
            Reindex();
        }
        finally {_queueItemActionBusy=false;UpdateState();SaveCheckpoint();}
    }
}
