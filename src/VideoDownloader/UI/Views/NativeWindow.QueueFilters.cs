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
        F<Button>("RemoveQueueButton").Content=ErrorMessages.Text("Удалить выбранные","Remove selected");
        F<Button>("RemoveQueueButton").ToolTip=ErrorMessages.Text("Только выбранные видимые строки; скрытые строки не затрагиваются.",
            "Only selected visible rows; hidden rows are not affected.");
        F<Button>("ClearQueueButton").Content=ErrorMessages.Text("Очистить всю очередь","Clear all");
        F<Button>("ClearQueueButton").ToolTip=ErrorMessages.Text("Сначала убирает только готовые записи, включая скрытые. Если готовых нет, очищает остальные, сохраняя пропущенные; если все пропущены — очищает и их. Скачанные файлы и история сохраняются.",
            "First removes completed entries, including hidden rows. With none completed, clears the rest, keeping skipped tasks unless all are skipped. Published files and history are kept.");
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
        string title=ErrorMessages.Text("Очистить всю очередь","Clear entire queue");
        int total=candidates.Count,visible=candidates.Count(IsVisibleQueueRow);
        int kept=SessionState.Queue.Count(row=>row.Status=="Skipped" && !candidates.Contains(row));
        string skipped=kept>0 ? ErrorMessages.Text($"\nПропущенные задания будут сохранены: {kept}.",$"\nSkipped tasks will be kept: {kept}."):
            candidates.Count>0 && candidates.All(row=>row.Status=="Skipped") ? ErrorMessages.Text("\nВсе задания пропущены: их временные данные будут удалены.",
                "\nAll tasks are skipped: their temporary data will be deleted."):"";
        string question=completedOnly ? ErrorMessages.Text(
            $"Убрать только готовые записи ({total})?\nСейчас показано {visible}, скрыто {total-visible}.\nНезавершённые и пропущенные задания, скачанные файлы и история сохранятся.\nКогда готовых записей нет, эта кнопка очищает остальную очередь.",
            $"Remove only completed entries ({total})?\nShowing {visible}; {total-visible} are hidden.\nUnfinished and skipped tasks, published files and history will be kept.\nWhen no completed entries remain, this button clears the rest of the queue.") : ErrorMessages.Text(
            $"Очистить очередь ({total} строк)?\nСейчас показано {visible}, скрыто {total-visible}.\nГотовые скачанные файлы сохранятся.{skipped}\nНовые строки и строки, пропущенные после этого вопроса, сохранятся.",
            $"Clear the queue ({total} rows)?\nShowing {visible}; {total-visible} are hidden.\nPublished downloads will be kept.{skipped}\nNew rows and tasks skipped after this question will be kept.");
        return MessageBox.Show(_window,question,title,MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    }
    private string QueueFilterIngressHint=>QueueFiltersActive
        ?ErrorMessages.Text(" Новые строки могут быть скрыты фильтрами."," New rows may be hidden by filters."):"";
    private string QueueAddedHint(int count)=>ErrorMessages.Text($"Добавлено ссылок: {count}.",$"{count} URL(s) added.")+QueueFilterIngressHint;
}
