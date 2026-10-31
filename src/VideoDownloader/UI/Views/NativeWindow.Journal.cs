using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using Forms=System.Windows.Forms;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private const int VisibleJournalChars=512*1024;
    private const int VisibleJournalLines=2000;
    private readonly DiagnosticJournal _journal;
    private bool _journalCloseRequested;
    private int _visibleJournalLines;
    private string _journalFileError="";
    private readonly Stopwatch _overallClock=new();
    private string _overallSession="";
    private long _overallSequence;
    private double _previousOverall;
    private int _previousOverallTotal=-1;
    private bool _previousOverallRunning;
    private long _lastOverallSample;
    internal DiagnosticJournal Journal=>_journal;

    private void InitializeJournalControls()
    {
        var profiles=C("LogProfileCombo");
        foreach(string name in JournalProfileCatalog.DisplayNames)profiles.Items.Add(name);
        profiles.SelectedItem=JournalProfileCatalog.Display(JournalProfile.Standard);
        profiles.SelectionChanged+=(_,_)=>
        {
            var profile=JournalProfileCatalog.Parse(Pick(profiles));
            _journal.Profile=profile;
            UpdateJournalHint();
            SaveCheckpoint();
        };
        F<Button>("CopyJournalButton").Click+=(_,_)=>CopyJournal();
        F<Button>("SaveJournalButton").Click+=async(_,_)=>
        {
            using var picker=new Forms.SaveFileDialog
            {
                Title="Сохранить журнал",Filter="Журнал|*.log|Текстовый файл|*.txt",
                DefaultExt="log",AddExtension=true,FileName="VideoDownloader-journal-"+DateTime.Now.ToString("yyyyMMdd-HHmmss")+".log"
            };
            if(picker.ShowDialog()!=Forms.DialogResult.OK)return;
            try { await ExportJournalAsync(picker.FileName); }
            catch(Exception error) when(error is IOException or UnauthorizedAccessException or ArgumentException)
            {
                JournalActionError("Не удалось сохранить журнал.",error);
            }
        };
        F<Button>("ClearJournalButton").Click+=(_,_)=>
        {
            _journal.Clear();
            B("LogBox").Clear();_visibleJournalLines=0;
            UpdateJournalHint("Экран очищен. Файлы журнала сохранены.");
        };
        UpdateJournalHint();
    }

    private void UpdateJournalHint(string? status=null)
    {
        string description=_journal.Profile switch
        {
            JournalProfile.Errors=>"Ошибки и предупреждения.",
            JournalProfile.Progress=>"События заданий, прогресс и причины изменения процентов.",
            JournalProfile.Detailed=>"Прогресс и подробный вывод stdout/stderr yt-dlp.",
            _=>"Запуск, завершение, повторные попытки, ошибки и предупреждения."
        };
        string fileError=_journal.LastFileError;
        T("JournalHint").Text=(status==null ? description : status)+" Профиль можно менять во время скачивания."+
            (fileError.Length>0 ? " Запись в файл недоступна; копирование и сохранение буфера доступны." : "");
        T("JournalHint").ToolTip="Файл: "+_journal.FilePath+Environment.NewLine+
            "Копируется и сохраняется весь удерживаемый буфер; экран показывает последние 2000 строк, до 524 288 символов. Смена профиля влияет на новые события."+
            (fileError.Length>0 ? Environment.NewLine+fileError : "");
    }

    // Worker callbacks only enqueue entries. One dispatcher tick appends a
    // bounded batch, so several download processes cannot flood WPF callbacks.
    internal void FlushJournal()
    {
        if(_disposed)return;
        string fileError=_journal.LastFileError;
        if(fileError!=_journalFileError){_journalFileError=fileError;UpdateJournalHint();}
        var entries=_journal.DrainPending(200);
        if(entries.Count==0)return;
        var box=B("LogBox");
        box.AppendText(string.Join(Environment.NewLine,entries)+Environment.NewLine);
        _visibleJournalLines+=entries.Sum(entry=>entry.Count(character=>character=='\n')+1);
        if(_visibleJournalLines>VisibleJournalLines || box.Text.Length>VisibleJournalChars)
        {
            string text=box.Text;
            int start=Math.Max(0,text.Length-VisibleJournalChars);
            if(start>0)
            {
                int boundary=text.IndexOf('\n',start);
                start=boundary<0 ? start : boundary+1;
            }
            string[] lines=text[start..].Split('\n');
            if(lines.Length>VisibleJournalLines+1)lines=lines.TakeLast(VisibleJournalLines+1).ToArray();
            box.Text=string.Join("\n",lines);
            _visibleJournalLines=Math.Max(0,lines.Length-1);
        }
        box.ScrollToEnd();
    }

    private void CopyJournal()
    {
        FlushJournal();
        try
        {
            string snapshot=_journal.Snapshot();
            if(snapshot.Length==0) { UpdateJournalHint("Журнал пуст.");return; }
            Clipboard.SetText(snapshot);
            UpdateJournalHint("Журнал скопирован.");
        }
        catch(Exception error) when(error is System.Runtime.InteropServices.COMException or ArgumentException)
        {
            JournalActionError("Не удалось скопировать журнал. Используйте «Сохранить журнал…».",error);
        }
    }

    internal async Task ExportJournalAsync(string file)
    {
        FlushJournal();
        string snapshot=_journal.Snapshot();
        await Task.Run(()=>AppPersistence.AtomicWrite(file,snapshot));
        if(!_disposed)UpdateJournalHint("Журнал сохранён.");
    }

    private void JournalActionError(string message,Exception error)
    {
        _journal.Write(new JournalRecord(JournalLevel.Error,"journal",message+" "+error.Message));
        FlushJournal();
        MessageBox.Show(_window,message+Environment.NewLine+ErrorMessages.ForException(error),
            "Журнал",MessageBoxButton.OK,MessageBoxImage.Warning);
    }

    private void TraceOverallProgress(QueueSessionMetrics metrics)
    {
        if(!_journal.IsEnabled(JournalLevel.Progress))return;
        if(SessionState.Coordinator==null && !_previousOverallRunning)return;
        string session=SessionState.Coordinator?.TraceSessionId??_overallSession;
        if(session.Length==0)return;
        string reason="sample";
        if(!string.Equals(session,_overallSession,StringComparison.Ordinal))
        {
            _overallSession=session;_overallSequence=0;_previousOverallTotal=-1;
            _overallClock.Restart();_lastOverallSample=-1000;
            reason="session_start";
        }
        else if(metrics.Total!=_previousOverallTotal)reason="members_changed";
        else if(metrics.Progress+0.01<_previousOverall)reason="percent_decreased";
        else if(_previousOverallRunning && !metrics.Running)reason="session_finished";
        long now=_overallClock.ElapsedMilliseconds;
        if(reason=="sample" && now-_lastOverallSample<1000)return;
        // Limit the per-row breakdown independently of queue size. Aggregate
        // counters still describe the complete current session.
        var rows=CurrentSessionRows();
        string members=DownloadTraceIdentity.OverallMembers(rows,out int shown);
        _journal.Write(new JournalRecord(JournalLevel.Progress,"overall",
            FormattableString.Invariant($"event=overall_progress ui_seq={++_overallSequence} ui_elapsed_ms={now} reason={reason} previous_pct={_previousOverall:0.###} current_pct={metrics.Progress:0.###} total={metrics.Total} active={metrics.Active} successful={metrics.Successful} failed={metrics.Failed} left={metrics.Left} sum_pct={metrics.Progress*metrics.Total:0.###} transferred_bytes={metrics.Transferred} members=[{members}] members_shown={shown} members_truncated={rows.Length>shown}"),
            Session:session));
        _lastOverallSample=now;_previousOverall=metrics.Progress;
        _previousOverallTotal=metrics.Total;_previousOverallRunning=metrics.Running;
    }
}
