using System.Globalization;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private SpeedDebugWindow? _speedDebugWindow;

    private bool CanOpenSpeedDebug()=>!_disposed && !_closing && !_restoringSession && !_legacyEngineBlocked &&
        !_startingSession && !_appUpdateHandoff && !_appUpdateBusy && !_dependencyOperationRunning &&
        !_profileBackupBusy && !_profileBackupRecoveryRequired && !_profileBackupReplaying &&
        !_queueItemActionBusy && SessionState.Coordinator?.IsRunning!=true &&
        SessionState.RunningTask?.IsCompleted!=false && _subscriptions?.HasActiveWork!=true;

    private void RefreshSpeedDebugButton()
    {
        var button=F<Button>("SpeedDebugButton");
        bool enabled=CanOpenSpeedDebug();
        if(button.IsEnabled==enabled && button.ToolTip is not null)return;
        button.IsEnabled=enabled;
        button.ToolTip=enabled
            ? ErrorMessages.Text("Сравнить скорость загрузки с разными настройками за минуту.","Compare download settings in a one-minute benchmark.")
            : ErrorMessages.Text("Дождитесь окончания загрузок и других операций.","Wait for downloads and other operations to finish.");
    }

    private void OpenSpeedDebug()
    {
        if(_speedDebugWindow is not null || !CanOpenSpeedDebug())return;
        var dialog=new SpeedDebugWindow(_window,ReadOptions(),_ytDlp,SaveSpeedDebugDefaults);
        _speedDebugWindow=dialog;
        try { dialog.ShowDialog(); }
        finally { _speedDebugWindow=null; }
    }

    private DownloadOptions? SaveSpeedDebugDefaults(SpeedDebugResult winner)
    {
        if(_speedDebugWindow is null || _checkpointReadOnly || !CanOpenSpeedDebug())return null;
        var options=ReadOptions();
        if(winner.Kind==SpeedDebugKind.Regular)
        {
            options.Threads=winner.Parallel.ToString(CultureInfo.InvariantCulture);
            options.Fragments=winner.Fragments.ToString(CultureInfo.InvariantCulture);
            options.UseHttpChunks=winner.Chunked;
        }
        else options.PreferAria2c=winner.Kind==SpeedDebugKind.HttpsAria2;

        if(!SaveCheckpointCore(SessionState.Queue.ToArray(),options,force:true))
        {
            UpdateState();
            return null;
        }
        B("ThreadsBox").Text=options.Threads;
        C("FragmentsCombo").SelectedItem=options.Fragments;
        K("HttpChunksCheck").IsChecked=options.UseHttpChunks;
        K("PreferAria2cCheck").IsChecked=options.PreferAria2c;
        SessionState.SetOptions(options);
        return options;
    }

    private Task StopSpeedDebugAsync()=>_speedDebugWindow?.CancelAndWaitAsync()??Task.CompletedTask;
}
