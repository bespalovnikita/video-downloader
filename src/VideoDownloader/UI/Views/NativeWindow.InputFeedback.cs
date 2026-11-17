using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private long _inputFeedbackGeneration;

    private void InitializeInputFeedback()=>RefreshQueueEmptyState();

    private void RefreshQueueEmptyState()
    {
        if(_window.FindName("QueueEmptyState") is FrameworkElement empty)
            empty.Visibility=SessionState.Queue.Count==0?Visibility.Visible:Visibility.Collapsed;
    }

    private void SubmitInputText()=>AddInputText(B("UrlInput").Text);

    // This path is only used by explicit Add/Enter/Paste. File/protocol ingress
    // and clipboard polling retain their existing parsing and admission rules.
    private void ProcessOrdinaryInput(string text,bool clipboard,long generation)
    {
        var input=B("UrlInput");
        string originalInput=input.Text;
        if(_closing || _disposed || _appUpdateHandoff || _restoringSession || _checkpointReadOnly ||
            _legacyEngineBlocked || _profileBackupBusy || _profileBackupRecoveryRequired || _profileBackupReplaying)
        {
            ShowInputFeedback(ErrorMessages.Text("Очередь сейчас недоступна. Введённый текст сохранён.",
                "The queue is unavailable. Your input has been kept."));
            return;
        }
        string[] tokens=text.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries);
        if(tokens.Length==0)
        {
            ShowInputFeedback(ErrorMessages.Text("Вставьте ссылку, начинающуюся с https:// или http://.",
                "Paste a link starting with https:// or http://."));
            return;
        }
        var urls=InputIngressController.ParseUrls(text);
        var invalid=tokens.Where(token=>!AppPersistence.ValidUrl(token)).ToArray();
        IReadOnlyList<DownloadItem> added=urls.Count>0?AddUrlsWithResult(urls,clipboard,clipboard):[];
        if(_closing || _disposed || generation!=_inputFeedbackGeneration)return;
        var addedUrls=added.Select(row=>row.Url).ToHashSet(StringComparer.Ordinal);
        var queued=SessionState.Queue.ToDictionary(row=>row.Url,StringComparer.Ordinal);
        var consumed=new HashSet<string>(StringComparer.Ordinal);
        int duplicates=0;
        DownloadItem? firstDuplicate=null;
        foreach(string token in tokens)
        {
            if(!AppPersistence.ValidUrl(token) || !queued.TryGetValue(token,out var row))continue;
            bool first=consumed.Add(token);
            if(!addedUrls.Contains(token) || !first)
            {
                duplicates++;
                if(!addedUrls.Contains(token))firstDuplicate??=row;
            }
        }
        string[] remaining=tokens.Where(token=>!consumed.Contains(token)).ToArray();
        bool remainderSaved=!clipboard && input.Text==text;
        // A duplicate/history dialog may have pumped other input events. Do not
        // overwrite text the user or another explicit action changed meanwhile.
        if(!clipboard && input.Text==text && consumed.Count>0)
            input.Text=string.Join(Environment.NewLine,remaining);
        else if(clipboard && input.Text==originalInput && remaining.Length>0)
        {
            string remainder=consumed.Count==0?text:string.Join(Environment.NewLine,remaining);
            input.Text=originalInput.Length==0?remainder:originalInput+Environment.NewLine+remainder;
            remainderSaved=true;
        }

        bool duplicateHidden=firstDuplicate is not null && !IsVisibleQueueRow(firstDuplicate);
        if(added.Count==0 && firstDuplicate is not null && !duplicateHidden)
        {
            Q.SelectedItem=firstDuplicate;
            Q.ScrollIntoView(firstDuplicate);
            ShowPreview(firstDuplicate.Url);
        }
        int refused=remaining.Count(token=>AppPersistence.ValidUrl(token));
        string message=urls.Count==0
            ? ErrorMessages.Text("Ссылка не распознана. Нужен полный адрес с https:// или http://; текст сохранён.",
                "No link was recognized. Use a complete https:// or http:// address; your input has been kept.")
            : added.Count==0 && duplicates==1 && invalid.Length==0 && refused==0
                ? ErrorMessages.Text("Ссылка уже в очереди.","This link is already in the queue.")
                : ErrorMessages.Text($"Добавлено: {added.Count} · Уже в очереди: {duplicates} · Не распознано: {invalid.Length}.",
                    $"Added: {added.Count} · Already in queue: {duplicates} · Unrecognized: {invalid.Length}.");
        if(refused>0)message+=clipboard && !remainderSaved
            ?ErrorMessages.Text(" Не добавленные ссылки остаются в буфере обмена.","Links that were not added remain in the clipboard.")
            :remainderSaved?ErrorMessages.Text(" Не добавленные ссылки сохранены в поле ввода.","Links that were not added have been kept in the input field.")
            :ErrorMessages.Text(" Ввод изменён; не добавленные ссылки не перенесены в новое поле.","The input changed; links that were not added were not copied into the new draft.");
        if(duplicateHidden)message+=ErrorMessages.Text(" Существующая строка скрыта фильтрами — сбросьте их, чтобы увидеть её.",
            "The existing row is hidden by filters; reset them to see it.");
        else if(added.Count>0)message+=QueueFilterIngressHint;
        ShowInputFeedback(message);
        RefreshQueueEmptyState();
    }

    private void ShowInputFeedback(string message)
    {
        if(_window.FindName("InputFeedbackText") is TextBlock feedback)
        {
            feedback.Text=message;
            feedback.Visibility=Visibility.Visible;
        }
    }
}
