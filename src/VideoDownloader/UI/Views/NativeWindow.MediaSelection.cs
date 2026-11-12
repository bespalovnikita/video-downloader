using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private MediaSelectionDialog? _mediaSelectionDialog;
    internal MediaSelectionDialog? MediaSelectionEditor=>_mediaSelectionDialog;

    private void InitializeMediaSelectionControls()
    {
        F<Button>("MediaSelectionButton").Click+=OpenMediaSelection;
        Q.SelectionChanged+=MediaRowSelectionChanged;
        _preview.Changed+=MediaMetadataChanged;
        _preview.Failed+=MediaMetadataFailed;
        RefreshMediaSelectionState();
    }

    private void MediaRowSelectionChanged(object sender,SelectionChangedEventArgs e)=>RefreshMediaSelectionState();

    private bool CanEditMediaSelection(DownloadItem row)=>!_disposed && !_closing && !_appUpdateHandoff && !_profileBackupBusy && !_profileBackupRecoveryRequired && !_profileBackupReplaying &&
        !(_startingSession && SessionState.Coordinator?.IsRunning!=true) &&
        !_checkpointReadOnly && !_legacyEngineBlocked &&
        !_restoringSession && !_queueItemActionBusy && SessionState.Queue.Contains(row) && row.ResumeState is null && !row.PendingRemoval && row.CleanupError.Length==0 &&
        row.Status is not ("Running" or "Starting") &&
        !(SessionState.Coordinator?.IsRunning==true && SessionState.Urls.Contains(row.Url,StringComparer.Ordinal));

    private void RefreshMediaSelectionState()
    {
        if(_disposed)return;
        var row=Q.SelectedItems.Count==1?Q.SelectedItem as DownloadItem:null;
        if(row is not null && !string.Equals(row.Url,_preview.SelectedUrl,StringComparison.Ordinal))row=null;
        F<Button>("MediaSelectionButton").IsEnabled=row!=null && CanEditMediaSelection(row);
        var summary=T("MediaSelectionSummary");
        summary.Text=row==null ? "Выберите одну строку очереди." :
            "Следующая попытка: "+(row.Selection?.Summary??"автоматический формат · всё видео")+
            (row.CompletedSelection is {} completed ? "\nСохранённый результат: "+completed.Summary : "")+
            (row.ResumeState is not null?"\nСохранены частичные данные с закреплённым форматом. Для другого формата удалите строку и добавьте ссылку заново.":
                row.Status=="Completed"?"\nДля новой попытки: меню строки → «Скачать выбранные строки» или «Скачать повторно».":"");
        summary.ToolTip=row?.Url;
        if(_mediaSelectionDialog is {} editor)editor.SetEditable(CanEditMediaSelection(editor.Row));
    }

    private void OpenMediaSelection(object sender,RoutedEventArgs e)
    {
        if(_mediaSelectionDialog is {} existing){existing.Activate();return;}
        if(Q.SelectedItems.Count!=1 || Q.SelectedItem is not DownloadItem row ||
            !string.Equals(row.Url,_preview.SelectedUrl,StringComparison.Ordinal) || !CanEditMediaSelection(row))return;
        var dialog=new MediaSelectionDialog(_window,row,ReadOptions,selection=>ApplyMediaSelection(row,selection),_preview.GetFrameAsync,_preview.GetClipAsync,
            waveformPreview:_preview.GetWaveformAsync);
        _mediaSelectionDialog=dialog;
        RefreshAppUpdateControls();
        dialog.Closed+=(_,_)=>
        {
            if(!ReferenceEquals(_mediaSelectionDialog,dialog))return;
            _mediaSelectionDialog=null;
            if(!_disposed)RefreshAppUpdateControls();
        };
        if(_preview.GetCached(row.Url) is {} data)dialog.ReceiveMetadata(data);
        dialog.Show();
        _preview.RequestFormats(row.Url);
    }

    private string? ApplyMediaSelection(DownloadItem row,DownloadSelection? selection)
    {
        if(!CanEditMediaSelection(row))return "Эта строка уже выполняется, удаляется или недоступна для изменения. Закройте окно и дождитесь завершения операции.";
        if(selection!=null && !selection.TryValidate(row.Url,ReadOptions(),out string error))return ErrorMessages.ForDisplay(error);
        var previous=row.Selection;
        row.Selection=selection;
        SaveCheckpoint();
        if(_checkpointReadOnly)
        {
            row.Selection=previous;
            RefreshMediaSelectionState();
            return "Не удалось сохранить параметры. Прежний выбор сохранён; проверьте журнал и доступ к файлу настроек.";
        }
        Q.Items.Refresh();D.Items.Refresh();RefreshMediaSelectionState();
        return null;
    }

    private void MediaMetadataChanged(PreviewData data,bool isFinal)
    {
        if(_mediaSelectionDialog is {} editor && string.Equals(editor.Row.Url,data.Url,StringComparison.Ordinal))
            editor.ReceiveMetadata(data);
    }
    private void MediaMetadataFailed(string url,string error)
    {
        if(_mediaSelectionDialog is {} editor && string.Equals(editor.Row.Url,url,StringComparison.Ordinal))
            editor.ReceiveMetadataFailure(ErrorMessages.ForDisplay(error));
    }

    private void DisposeMediaSelection()
    {
        F<Button>("MediaSelectionButton").Click-=OpenMediaSelection;
        Q.SelectionChanged-=MediaRowSelectionChanged;
        _preview.Changed-=MediaMetadataChanged;
        _preview.Failed-=MediaMetadataFailed;
        _mediaSelectionDialog?.Close();_mediaSelectionDialog=null;
    }
}
