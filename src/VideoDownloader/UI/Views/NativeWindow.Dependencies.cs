using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private CancellationTokenSource? _dependencyStop;
    private Task? _dependencyTask;
    private Task<string>? _dependencyServiceTask;
    private DownloadToolReadiness? _dependencyReadiness;
    private bool _dependencyInspectionRunning;
    private int _dependencyGeneration;
    private string _dependencyOutcome="";
    private string _dependencyInspectionError="";

    private void InitializeDependencyControls()
    {
        F<Button>("ReadinessInstallButton").Click+=async(_,_)=>await InstallFfmpegFromUiAsync();
        F<Button>("ReadinessRefreshButton").Click+=(_,_)=>RefreshDependencyLabel();
        F<Button>("CancelDependencyButton").Click+=(_,_)=>CancelDependencyOperation();
        RefreshDependencyControls();
    }

    private Task InstallFfmpegFromUiAsync()=>DependencyOperation(
        ErrorMessages.Text("Подготовка установки FFmpeg…","Preparing FFmpeg installation…"),
        async(progress,cancel)=>
        {
            await DependencyCenter.InstallFfmpegAsync(progress,cancel).ConfigureAwait(false);
            return ErrorMessages.Text("FFmpeg и ffprobe установлены. Скачивание запускается отдельно.",
                "FFmpeg and ffprobe installed. Start downloads separately.");
        });

    private Task UpdateYtDlpFromUiAsync()=>DependencyOperation(
        ErrorMessages.Text("Подготовка обновления yt-dlp…","Preparing yt-dlp update…"),
        (progress,cancel)=>DependencyCenter.UpdateYtDlpAsync(progress,cancel));

    private void RefreshDependencyLabel()
    {
        if(_dependencyInspectionRunning || _disposed || _closing || _dependencyOperationRunning)return;
        _=InspectDependenciesAsync();
    }

    private async Task InspectDependenciesAsync()
    {
        int generation=_dependencyGeneration;
        _dependencyInspectionRunning=true;
        RefreshDependencyControls();
        try
        {
            // PATH can contain slow network locations. Inspect outside WPF;
            // late observations cannot overwrite an in-flight install's state.
            var result=await Task.Run(()=>DependencyCenter.InspectTools(_ytDlp));
            if(_disposed || _closing || _dependencyOperationRunning || generation!=_dependencyGeneration)return;
            _dependencyReadiness=result;
            _dependencyInspectionError="";
            T("DependencyStatus").Text=result.Description;
        }
        catch(Exception error)
        {
            if(!_disposed && !_closing && !_dependencyOperationRunning && generation==_dependencyGeneration)
            {
                _dependencyInspectionError=ErrorMessages.Text("Не удалось проверить компоненты. Нажмите «Проверить ещё раз».",
                    "Cannot inspect components. Click Check again.");
                T("DependencyStatus").Text=_dependencyInspectionError+Environment.NewLine+DisplayError(error);
            }
        }
        finally
        {
            _dependencyInspectionRunning=false;
            if(!_disposed && !_closing)
            {
                RefreshDependencyControls();
                if(generation!=_dependencyGeneration && !_dependencyOperationRunning)RefreshDependencyLabel();
            }
        }
    }

    private bool CanStartDependencyOperation()=>!_disposed && !_closing && !_appUpdateHandoff &&
        !_dependencyOperationRunning && !_queueItemActionBusy && !_startingSession && !_restoringSession &&
        !_checkpointReadOnly && !_legacyEngineBlocked && !_appUpdateBusy && !_profileBackupBusy &&
        !_profileBackupRecoveryRequired && !_profileBackupReplaying && _mediaSelectionDialog is null && !HasJobDiagnosticWork &&
        _subscriptions?.HasActiveWork!=true && _subscriptions?.IsSchedulingSuspended!=true &&
        SessionState.Coordinator?.IsRunning!=true && SessionState.RunningTask?.IsCompleted!=false &&
        !SessionState.Queue.Any(row=>row.PendingRemoval || row.CleanupPaths.Count>0 || row.CleanupError.Length>0);

    private void RefreshDependencyControls()
    {
        if(_disposed)return;
        bool available=CanStartDependencyOperation();
        foreach(string name in new[]{"InstallFfmpegButton","UpdateYtDlpButton","ReadinessInstallButton"})
            F<Button>(name).IsEnabled=available;
        foreach(string name in new[]{"RefreshDepsButton","ReadinessRefreshButton"})
            F<Button>(name).IsEnabled=!_dependencyInspectionRunning && !_dependencyOperationRunning && !_closing;
        F<Button>("ReadinessInstallButton").Visibility=_dependencyReadiness?.NeedsFfmpeg==true && !_dependencyOperationRunning
            ?Visibility.Visible:Visibility.Collapsed;
        F<Button>("ReadinessRefreshButton").Visibility=(_dependencyReadiness?.Ready!=true || _dependencyInspectionError.Length>0) && !_dependencyOperationRunning
            ?Visibility.Visible:Visibility.Collapsed;
        F<Button>("CancelDependencyButton").Visibility=_dependencyOperationRunning?Visibility.Visible:Visibility.Collapsed;
        F<Button>("CancelDependencyButton").IsEnabled=_dependencyOperationRunning && _dependencyStop?.IsCancellationRequested==false && !_closing;
        F<ProgressBar>("DependencyProgress").Visibility=_dependencyOperationRunning?Visibility.Visible:Visibility.Collapsed;
        T("DependencyProgressText").Visibility=_dependencyOperationRunning || _dependencyOutcome.Length>0?Visibility.Visible:Visibility.Collapsed;
        if(!_dependencyOperationRunning)
        {
            T("DependencyProgressText").Text=_dependencyOutcome;
            T("ReadinessText").Text=_dependencyInspectionError.Length>0 && !_dependencyInspectionRunning
                ?_dependencyInspectionError
                :_dependencyInspectionRunning || _dependencyReadiness is null
                ?ErrorMessages.Text("Проверяем готовность к скачиванию…","Checking download readiness…")
                :_dependencyReadiness.Ready
                    ?ErrorMessages.Text("Готово к скачиванию. Вставьте ссылку и выберите папку.","Ready to download. Paste a link and choose a folder.")
                    :!_dependencyReadiness.HasYtDlp
                        ?ErrorMessages.Text("Не найден yt-dlp. Распакуйте полный Windows ZIP приложения.","yt-dlp is missing. Extract the complete Windows ZIP package.")
                        :ErrorMessages.Text("Для первой загрузки нужны FFmpeg и ffprobe. Нажмите «Установить FFmpeg».",
                            "FFmpeg and ffprobe are required. Click Install FFmpeg.");
        }
    }

    // All entry points use the same task, CTS, guards and UI feedback, including
    // the first-run card and Settings. The service delegate runs outside WPF.
    internal Task DependencyOperation(string initialStatus,Func<IProgress<DependencyProgress>,CancellationToken,Task<string>> operation)
    {
        if(!CanStartDependencyOperation() || RejectProfileBackupMutation())return Task.CompletedTask;
        _dependencyOperationRunning=true;
        _dependencyOutcome="";
        _dependencyStop=new CancellationTokenSource();
        int generation=++_dependencyGeneration;
        T("ReadinessText").Text=initialStatus;
        T("DependencyStatus").Text=initialStatus;
        T("DependencyProgressText").Text=initialStatus;
        F<ProgressBar>("DependencyProgress").IsIndeterminate=true;
        RefreshDependencyControls();
        UpdateState();
        return _dependencyTask=RunDependencyOperationAsync(generation,operation,_dependencyStop);
    }

    private async Task RunDependencyOperationAsync(int generation,
        Func<IProgress<DependencyProgress>,CancellationToken,Task<string>> operation,CancellationTokenSource stop)
    {
        bool Current()=>!_disposed && !_closing && generation==_dependencyGeneration;
        var progress=new DependencyUiProgress(this,generation);
        try
        {
            using var schedulingPause=await SuspendSubscriptionSchedulingAsync();
            await _preview.CancelLocalPostersAsync();
            stop.Token.ThrowIfCancellationRequested();
            if(!Current() || _appUpdateHandoff)throw new OperationCanceledException(stop.Token);
            _dependencyServiceTask=Task.Run(()=>operation(progress,stop.Token),stop.Token);
            var response=await _dependencyServiceTask;
            stop.Token.ThrowIfCancellationRequested();
            if(!Current())return;
            Log(response);
            _dependencyOutcome=ErrorMessages.Text("Операция с компонентами завершена. Готовность к скачиванию показана выше.",
                "Component operation completed. Download readiness is shown above.");
        }
        catch(OperationCanceledException) when(stop.IsCancellationRequested || _disposed || _closing)
        {
            if(Current())_dependencyOutcome=ErrorMessages.Text("Операция отменена. Предыдущие компоненты сохранены, если установка не была завершена.",
                "Operation cancelled. Previous components were kept if installation had not completed.");
        }
        catch(Exception error)
        {
            Log("Dependency operation failed: "+error);
            if(Current())
            {
                _dependencyOutcome=ErrorMessages.Text("Операция с компонентами не завершена: ","Component operation did not complete: ")+DisplayError(error);
                T("DependencyStatus").Text=_dependencyOutcome;
            }
        }
        finally
        {
            if(ReferenceEquals(_dependencyStop,stop))_dependencyStop=null;
            stop.Dispose();
            if(generation==_dependencyGeneration)
            {
                ++_dependencyGeneration;
                _dependencyOperationRunning=false;
                _dependencyServiceTask=null;
                if(!_disposed && !_closing)
                {
                    RefreshDependencyControls();
                    RefreshDependencyLabel();
                    RefreshAppUpdateControls();
                    _preview.ResetLocalPosterAttempts();
                    _preview.BackfillLocalPosters();
                    UpdateState();
                }
            }
        }
    }

    private void ShowDependencyProgress(int generation,DependencyProgress value)
    {
        if(_disposed || _closing || !_dependencyOperationRunning || generation!=_dependencyGeneration || _dependencyStop?.IsCancellationRequested!=false)return;
        string stage=value.Stage switch
        {
            DependencyStage.Downloading=>ErrorMessages.Text("Скачиваем FFmpeg…","Downloading FFmpeg…"),
            DependencyStage.Verifying=>ErrorMessages.Text("Проверяем пакет и сохраняем прежние компоненты…","Checking package and backing up existing components…"),
            DependencyStage.Installing=>ErrorMessages.Text("Устанавливаем FFmpeg и ffprobe…","Installing FFmpeg and ffprobe…"),
            _=>ErrorMessages.Text("Обновляем yt-dlp… Размер загрузки неизвестен.","Updating yt-dlp… Download size is unknown.")
        };
        bool measured=value.Stage==DependencyStage.Downloading && value.TotalBytes is >0;
        var bar=F<ProgressBar>("DependencyProgress");
        bar.IsIndeterminate=!measured;
        bar.Value=measured?Math.Clamp(100d*value.DownloadedBytes/value.TotalBytes!.Value,0,100):0;
        string bytes=value.Stage!=DependencyStage.Downloading || value.DownloadedBytes==0 && value.TotalBytes is null?"":
            value.TotalBytes is >0?$" {DisplayValueConverter.Bytes(value.DownloadedBytes)} / {DisplayValueConverter.Bytes(value.TotalBytes.Value)} ({bar.Value:F0}%)":
                " "+DisplayValueConverter.Bytes(value.DownloadedBytes)+ErrorMessages.Text(" · общий размер неизвестен"," · total size unknown");
        T("DependencyProgressText").Text=stage+bytes;
        T("DependencyStatus").Text=stage+bytes;
        T("ReadinessText").Text=stage;
    }

    private void CancelDependencyOperation()
    {
        _dependencyStop?.Cancel();
        if(!_disposed && !_closing && _dependencyOperationRunning)
        {
            T("DependencyProgressText").Text=ErrorMessages.Text("Отменяем; ждём завершения очистки и отката…","Cancelling; waiting for cleanup and rollback…");
            RefreshDependencyControls();
        }
    }

    private async Task StopDependencyOperationAsync()
    {
        CancelDependencyOperation();
        if(_dependencyTask is {} task)await task;
    }

    private void DisposeDependencyControls()
    {
        ++_dependencyGeneration;
        _dependencyStop?.Cancel();
        // Only the service task is joined synchronously. It is started via
        // Task.Run and never needs the WPF dispatcher to finish cancellation.
        try{_dependencyServiceTask?.GetAwaiter().GetResult();}
        catch(OperationCanceledException){}
        catch(Exception error){Log("Dependency shutdown failed: "+error);}
    }

    private sealed class DependencyUiProgress(NativeWindow owner,int generation):IProgress<DependencyProgress>
    {
        public void Report(DependencyProgress value)
        {
            if(owner.Dispatcher.HasShutdownStarted)return;
            _=owner.Dispatcher.BeginInvoke(DispatcherPriority.Background,
                new Action(()=>owner.ShowDependencyProgress(generation,value)));
        }
    }
}
