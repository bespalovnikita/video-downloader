using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private bool CanRequestOutOfOrder(DownloadItem row)=>SessionState.Queue.Contains(row) && IsVisibleQueueRow(row) &&
        row.Status=="Queued" && row.CanResume && row.CleanupPaths.Count==0 &&
        (SessionState.Coordinator?.IsPending(row)==true || row.ResumeState is null && row.StageDir.Length==0 &&
            row.Path.Length==0 && row.OwnedPaths.Count==0);

    private bool OutOfOrderBlocked(bool ownOperation=false)=>QueueItemActionsBlocked(ownOperation) ||
        SessionState.Stopping && (SessionState.Coordinator?.IsRunning==true || SessionState.RunningTask?.IsCompleted==false) ||
        _mediaSelectionDialog is not null;

    private void ConfigureOutOfOrderAction(ContextMenu menu)
    {
        var action=new MenuItem{Name="DownloadOutOfOrderMenuItem",Header=ErrorMessages.Text("Скачать вне очереди","Download out of order")};
        ToolTipService.SetShowOnDisabled(action,true);
        action.Click+=async(_,_)=>
        {
            var selected=SelectedVisibleQueueRows();
            if(selected.Length==1)await StartOutOfOrderAsync(selected[0]);
        };
        menu.Opened+=(_,_)=>
        {
            var selected=SelectedVisibleQueueRows();
            var owner=SessionState.Coordinator;
            action.IsEnabled=!OutOfOrderBlocked() && selected.Length==1 && CanRequestOutOfOrder(selected[0]) &&
                (owner?.IsRunning!=true || owner.CanStartOutOfOrder(selected[0]));
            action.ToolTip=action.IsEnabled
                ?ErrorMessages.Text("Запустить выбранное ожидающее задание в отдельном временном потоке сверх обычного лимита. Поток освободится после этого задания.",
                    "Start the selected waiting task in its own temporary slot above the normal limit. That slot ends with this task.")
                :ErrorMessages.Text("Выберите одно ожидающее задание без незавершённой очистки. Во время остановки, обслуживания или редактирования запуск недоступен; уже работающая задача второй раз не запускается.",
                    "Select one waiting task without pending cleanup. Starting is unavailable during shutdown, maintenance or editing; a running task cannot start twice.");
        };
        menu.Items.Add(action);
    }

    private async Task StartOutOfOrderAsync(DownloadItem row)
    {
        if(OutOfOrderBlocked() || !CanRequestOutOfOrder(row))return;
        var expected=SessionState.Coordinator;
        if(expected?.IsRunning!=true)
        {
            // This is an explicit selected start, including the ordinary tools,
            // history, disk-space and profile-maintenance checks. Other queued
            // rows are not admitted implicitly.
            await StartAsync([row]);
            return;
        }
        if(!expected.CanStartOutOfOrder(row))return;
        var selection=row.Selection;var retained=row.ResumeState;
        string url=row.Url,subscriptionName=row.SubscriptionName,stage=row.StageDir,run=row.RunDir;
        long stopVersion=SessionState.StopVersion;
        bool Current()=>!OutOfOrderBlocked(ownOperation:true) && SessionState.StopVersion==stopVersion &&
            ReferenceEquals(SessionState.Coordinator,expected) && expected.IsRunning &&
            CanRequestOutOfOrder(row) && string.Equals(row.Url,url,StringComparison.Ordinal) && row.Selection==selection &&
            row.ResumeState==retained && row.StageDir==stage && row.RunDir==run &&
            string.Equals(row.SubscriptionName,subscriptionName,StringComparison.Ordinal);
        await RunQueueItemActionAsync(async()=>
        {
            if(!Current())return;
            bool alreadyAdmitted=expected.IsPending(row);
            DownloadCoordinator.OutOfOrderPreparation? preparation=null;
            if(!alreadyAdmitted)
            {
                // A subscription/portable row may still be deferred. This
                // explicit action uses the active session's pinned options,
                // never the edited settings intended for the next Start.
                var options=SessionState.Options.Snapshot();
                await Task.Run(()=>
                {
                    AudioTrackSelection.Validate(options);
                    selection?.Validate(url,options);
                    DependencyCenter.ValidateDownloadTools(_ytDlp);
                    OwnedFiles.ValidateTemplate(options.FilenameTemplate);
                    if(!string.IsNullOrWhiteSpace(options.Cookies) && !File.Exists(options.Cookies))
                        throw new FileNotFoundException("Cookie file is missing.",options.Cookies);
                });
                if(!Current())return;
                preparation=await expected.PrepareOutOfOrderAsync(row);
                if(preparation is null || !Current())return;
                await _preview.CancelLocalPostersAsync([row]);
                if(!Current())return;
                var approved=ConfirmDuplicates([row],false);
                if(approved is null || approved.Count==0 || !Current())return;
            }
            if(Current() && SessionState.TryStartOutOfOrderActive(expected,row,preparation))
            {
                T("ClipboardHint").Text=ErrorMessages.Text("Задание запущено вне очереди во временном потоке. Обычный лимит загрузок не изменён.",
                    "Task started out of order in a temporary slot. The normal download limit is unchanged.");
                Log("Explicit out-of-order task admitted: "+row.Url);
            }
            else if(SessionState.Queue.Contains(row) && row.Status=="Queued")
                T("ClipboardHint").Text=ErrorMessages.Text("Сеанс уже завершается или задание начало выполняться. Новая сессия автоматически не запущена.",
                    "The session is ending or the task has already started. A new session was not started automatically.");
        });
    }
}
