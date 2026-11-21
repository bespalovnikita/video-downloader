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
        var dialog=new SpeedDebugWindow(_window,ReadOptions(),_ytDlp);
        _speedDebugWindow=dialog;
        try { dialog.ShowDialog(); }
        finally { _speedDebugWindow=null; }
    }

    private Task StopSpeedDebugAsync()=>_speedDebugWindow?.CancelAndWaitAsync()??Task.CompletedTask;
}
