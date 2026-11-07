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
            T("QueueFilterScopeHint"),RefreshQueueFilterActions);
        F<Button>("RemoveQueueButton").Content=ErrorMessages.Text("Удалить выбранные","Remove selected");
        F<Button>("RemoveQueueButton").ToolTip=ErrorMessages.Text("Только выбранные видимые строки; скрытые строки не затрагиваются.",
            "Only selected visible rows; hidden rows are not affected.");
        F<Button>("ClearQueueButton").Content=ErrorMessages.Text("Очистить всю очередь","Clear all");
        F<Button>("ClearQueueButton").ToolTip=ErrorMessages.Text("Удаляет строки всей очереди, включая скрытые фильтром. Скачанные файлы сохраняются.",
            "Clears the whole queue, including filtered-out rows. Downloaded files are kept.");
        F<Button>("RetryFailedButton").Content=ErrorMessages.Text("Повторить ошибки","↻ All failed");
        F<Button>("RetryFailedButton").ToolTip=ErrorMessages.Text("Повторить все задания с ошибками, включая скрытые фильтром.",
            "Retry all failed tasks, including filtered-out rows.");
        F<Button>("StartButton").Content=ErrorMessages.Text("▶ Скачать всю очередь","▶ Start all");
        F<Button>("StartButton").ToolTip=ErrorMessages.Text("Запускает всю готовую очередь, включая скрытые фильтром строки.",
            "Starts the whole eligible queue, including filtered-out rows.");
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
        RefreshMediaSelectionState();
    }
    private void RefreshQueueView(bool rebuildChoices=false)
    {
        if(_queueFilters is {} filters)filters.Refresh(rebuildChoices);
        else Q.Items.Refresh();
    }
    private bool ConfirmClearEntireQueue(IReadOnlyList<DownloadItem> candidates)
    {
        string title=ErrorMessages.Text("Очистить всю очередь","Clear entire queue");
        int total=candidates.Count,visible=candidates.Count(IsVisibleQueueRow);
        string question=ErrorMessages.Text(
            $"Очистить всю очередь ({total} строк)?\nСейчас показано {visible}, скрыто {total-visible}.\nБудут удалены записи, а не скачанные файлы.\nСтроки, добавленные после этого вопроса, сохранятся.",
            $"Clear the entire queue ({total} rows)?\nShowing {visible}; {total-visible} are hidden.\nThis removes entries, not downloaded files.\nRows added after this question will be kept.");
        return MessageBox.Show(_window,question,title,MessageBoxButton.YesNo,MessageBoxImage.Question)==MessageBoxResult.Yes;
    }
    private string QueueFilterIngressHint=>QueueFiltersActive
        ?ErrorMessages.Text(" Новые строки могут быть скрыты фильтрами."," New rows may be hidden by filters."):"";
    private string QueueAddedHint(int count)=>ErrorMessages.Text($"Добавлено ссылок: {count}.",$"{count} URL(s) added.")+QueueFilterIngressHint;
}
