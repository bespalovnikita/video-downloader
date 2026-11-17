using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private QueueFilterController? _queueFilters;
    private bool QueueFiltersActive=>_queueFilters?.IsFiltering==true;
    private bool QueueFilterRefreshing=>_queueFilters?.IsRefreshing==true;
    private bool HasVisibleQueueSelection=>_queueFilters?.HasSelection??Q.SelectedItems.Count>0;
    private DownloadItem[] SelectedVisibleQueueRows()=>_queueFilters?.SelectedRows()??Q.SelectedItems.Cast<DownloadItem>().ToArray();
    private bool IsVisibleQueueRow(DownloadItem row)=>_queueFilters?.IncludesRow(row)??SessionState.Queue.Contains(row);
    private void InitializeQueueFilters()
    {
        _queueFilters=new QueueFilterController(_window,Q,SessionState.Queue,B("QueueSearchBox"),C("QueueStatusFilter"),
            C("QueueSubscriptionFilter"),F<Button>("ResetQueueFiltersButton"),T("QueueCount"),T("QueueEmptyFilterHint"),
            T("QueueFilterScopeHint"),RefreshQueueFilterActions,F<FrameworkElement>("QueueFilterPanel"),
            F<Button>("ToggleQueueFiltersButton"),T("QueueActiveFiltersBadge"));
        F<Button>("RemoveQueueButton").Content=ErrorMessages.Text("Удалить загрузки и файлы…","Delete downloads and files…");
        F<Button>("RemoveQueueButton").ToolTip=ErrorMessages.Text("Остановить выбранные видимые задания и безвозвратно удалить только принадлежащие им файлы. Скрытые строки не затрагиваются.",
            "Stop selected visible tasks and permanently delete only their owned files. Hidden rows are not affected.");
        F<Button>("ForgetQueueButton").Content=ErrorMessages.Text("Убрать из очереди","Remove queue entries");
        F<Button>("ForgetQueueButton").Click+=async(_,_)=>await ForgetRowsAsync(SelectedVisibleQueueRows());
        F<Button>("ClearQueueButton").Content=ErrorMessages.Text("Убрать завершённые","Remove completed entries");
        F<Button>("ClearQueueButton").ToolTip=ErrorMessages.Text("Убирает только завершённые записи, включая скрытые. Скачанные файлы и история сохраняются.",
            "Removes only completed entries, including hidden rows. Published files and history are kept.");
        F<Button>("ClearAllQueueButton").Content=ErrorMessages.Text("Очистить очередь…","Clear queue…");
        F<Button>("ClearAllQueueButton").ToolTip=ErrorMessages.Text("Убирает все текущие записи, включая скрытые и пропущенные. Временные данные пропущенных заданий удаляются; готовые файлы и история сохраняются.",
            "Removes all current entries, including hidden and skipped tasks. Retained temporary data is discarded; published files and history are kept.");
        F<Button>("ClearAllQueueButton").Click+=async(_,_)=>await ClearQueueEntriesAsync(completedOnly:false);
        F<Button>("RetryFailedButton").Content=ErrorMessages.Text("Повторить ошибки","↻ All failed");
        F<Button>("RetryFailedButton").ToolTip=ErrorMessages.Text("Повторить все задания с ошибками, включая скрытые фильтром.",
            "Retry all failed tasks, including filtered-out rows.");
        F<Button>("StartButton").Content=ErrorMessages.Text("▶ Скачать всю очередь","▶ Start all");
        F<Button>("StartButton").ToolTip=ErrorMessages.Text("Запускает готовую очередь, включая скрытые строки. Пропущенные продолжаются только отдельным действием.",
            "Starts eligible tasks, including hidden rows. Skipped tasks require an explicit resume.");
        F<Button>("PortableQueueButton").ToolTip=ErrorMessages.Text("Копирование и экспорт переносят всю очередь, включая скрытые строки.",
            "Copy and export transfer the whole queue, including filtered-out rows.");
    }
    private void RefreshQueueFilterActions()
    {
        if(_disposed)return;
        bool canReorder=SessionState.Coordinator?.IsRunning!=true && !QueueFiltersActive;
        F<Button>("MoveUpButton").IsEnabled=F<Button>("MoveDownButton").IsEnabled=canReorder;
        Q.AllowDrop=canReorder;
        F<Button>("MoveUpButton").ToolTip=F<Button>("MoveDownButton").ToolTip=QueueFiltersActive
            ? ErrorMessages.Text("Сбросьте фильтры, чтобы изменить порядок всей очереди.","Reset filters to change the whole queue order."):null;
        F<Button>("RemoveQueueButton").IsEnabled=HasVisibleQueueSelection;
        RefreshQueueRecordActions();
        RefreshQueueItemActions();
        RefreshMediaSelectionState();
    }
    private void RefreshQueueView(bool rebuildChoices=false)
    {
        if(_queueFilters is {} filters)filters.Refresh(rebuildChoices);
        else Q.Items.Refresh();
    }
    private bool ConfirmClearEntireQueue(IReadOnlyList<DownloadItem> candidates,bool completedOnly)
    {
        string title=completedOnly?ErrorMessages.Text("Убрать завершённые записи","Remove completed entries"):
            ErrorMessages.Text("Очистить очередь","Clear queue");
        int total=candidates.Count,visible=candidates.Count(IsVisibleQueueRow);
        int retained=candidates.Count(row=>row.ResumeState is not null);
        string question=completedOnly ? ErrorMessages.Text(
            $"Убрать только завершённые записи ({total})?\nСейчас показано {visible}, скрыто {total-visible}.\nНезавершённые и пропущенные задания, скачанные файлы и история сохранятся.\nНовые строки не затрагиваются.",
            $"Remove only completed entries ({total})?\nShowing {visible}; {total-visible} are hidden.\nUnfinished and skipped tasks, published files and history will be kept.\nNew rows are not affected.") : ErrorMessages.Text(
            $"Очистить очередь ({total} строк), включая пропущенные задания?\nСейчас показано {visible}, скрыто {total-visible}.\nВременные данные для продолжения будут удалены у {retained} заданий.\nГотовые скачанные файлы и история сохранятся.\nНовые строки не затрагиваются.",
            $"Clear the queue ({total} rows), including skipped tasks?\nShowing {visible}; {total-visible} are hidden.\nRetained temporary data for {retained} tasks will be discarded.\nPublished downloads and history will be kept.\nNew rows are not affected.");
        return MessageBox.Show(_window,question,title,MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    }
    private string QueueFilterIngressHint=>QueueFiltersActive
        ?ErrorMessages.Text(" Новые строки могут быть скрыты фильтрами."," New rows may be hidden by filters."):"";
    private string QueueAddedHint(int count)=>ErrorMessages.Text($"Добавлено ссылок: {count}.",$"{count} URL(s) added.")+QueueFilterIngressHint;
}
