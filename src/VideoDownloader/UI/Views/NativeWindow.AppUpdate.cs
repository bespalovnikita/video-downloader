using System.Reflection;
using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private IAppUpdateService? _appUpdateService;
    private CancellationTokenSource? _appUpdateStop;
    private AppUpdateRelease? _availableAppUpdate;
    private PreparedAppUpdate? _pendingAppUpdate;
    private bool _appUpdateBusy,_appUpdateEventsBound;
    private bool _appUpdateHandoff;
    private int _appUpdateGeneration;
    private string _appUpdateStatus="";
    internal Action<PreparedAppUpdate>? AppUpdateLaunchOverride {get;set;}

    internal void InitializeAppUpdates(IAppUpdateService? service=null,bool automatic=true)
    {
        DisposeAppUpdates();
        _appUpdateService=service??new AppUpdateClient();
        _appUpdateStop=new CancellationTokenSource();
        _appUpdateStatus=ErrorMessages.Text("Обновления проверяются автоматически при запуске и по кнопке.","Updates are checked at startup and on request.");
        if(!_appUpdateEventsBound)
        {
            F<Button>("CheckAppUpdateButton").Click+=async(_,_)=>await CheckAppUpdateAsync(false);
            F<Button>("InstallAppUpdateButton").Click+=async(_,_)=>await InstallAppUpdateAsync();
            _appUpdateEventsBound=true;
        }
        RefreshAppUpdateControls();
        if(automatic)_=CheckAppUpdateAsync(true);
    }
    private bool CanInstallAppUpdate()=>!_disposed && !_closing && !_appUpdateHandoff && !_restoringSession &&
        !_checkpointReadOnly && !_legacyEngineBlocked && !_startingSession && !_dependencyOperationRunning &&
        _window.IsEnabled && _mediaSelectionDialog is null && _subscriptions?.HasActiveWork!=true &&
        _subscriptions?.IsSchedulingSuspended!=true && SessionState.Coordinator?.IsRunning!=true &&
        SessionState.RunningTask?.IsCompleted!=false &&
        !SessionState.Queue.Concat(SessionState.Downloads).Any(row=>row.Status=="Running" || row.PendingRemoval ||
            row.CleanupPaths.Count>0 || row.CleanupError.Length>0);

    internal void RefreshAppUpdateControls()
    {
        if(_appUpdateService is null || _disposed)return;
        F<Button>("CheckAppUpdateButton").IsEnabled=!_appUpdateBusy && !_closing && !_appUpdateHandoff;
        F<Button>("InstallAppUpdateButton").IsEnabled=_availableAppUpdate!=null && !_appUpdateBusy && CanInstallAppUpdate();
        T("AppUpdateStatusText").Text=_appUpdateStatus;
    }
    private async Task CheckAppUpdateAsync(bool automatic)
    {
        if(_appUpdateBusy || _appUpdateService is null || _appUpdateStop is null || _closing || _disposed)return;
        int generation=_appUpdateGeneration;var cancel=_appUpdateStop.Token;var service=_appUpdateService;
        _appUpdateBusy=true;_appUpdateStatus=ErrorMessages.Text("Проверка новой версии…","Checking for a new version…");RefreshAppUpdateControls();
        try
        {
            var current=Assembly.GetExecutingAssembly().GetName().Version??new Version(2,0,0);
            var release=await service.CheckAsync(current,cancel);
            if(generation!=_appUpdateGeneration || _disposed || _closing)return;
            _availableAppUpdate=release;
            _appUpdateStatus=release is null?ErrorMessages.Text("Установлена актуальная версия приложения.","The application is up to date."):
                ErrorMessages.Text($"Доступна версия {release.Version}. Обновить можно после завершения текущих операций.",$"Version {release.Version} is available. Finish current operations before installing.");
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested){}
        catch(Exception error)
        {
            if(generation!=_appUpdateGeneration || _disposed || _closing)return;
            _appUpdateStatus=automatic?ErrorMessages.Text("Проверка обновлений недоступна. Можно повторить вручную.","Update checking is unavailable. Retry manually."):
                ErrorMessages.Text("Не удалось проверить обновления: ","Cannot check for updates: ")+DisplayError(error);
        }
        finally {if(generation==_appUpdateGeneration){_appUpdateBusy=false;RefreshAppUpdateControls();}}
    }
    private async Task InstallAppUpdateAsync()
    {
        if(_appUpdateBusy || _availableAppUpdate is null || _appUpdateService is null || _appUpdateStop is null || !CanInstallAppUpdate())return;
        if(_appUpdateService is AppUpdateClient && !File.Exists(Path.Combine(AppContext.BaseDirectory,AppUpdatePackage.ManifestName)))
        {
            _appUpdateStatus=ErrorMessages.Text("Для первого обновления распакуй полный новый Windows ZIP. В этой установке отсутствует манифест файлов.",
                "Install the complete new Windows ZIP first. This installation has no package ownership manifest.");RefreshAppUpdateControls();return;
        }
        int generation=_appUpdateGeneration;var cancel=_appUpdateStop.Token;
        _appUpdateBusy=true;_appUpdateStatus=ErrorMessages.Text("Загрузка и проверка обновления…","Downloading and verifying the update…");RefreshAppUpdateControls();
        PreparedAppUpdate? prepared=null;
        try
        {
            prepared=await _appUpdateService.PrepareAsync(_availableAppUpdate,AppContext.BaseDirectory,cancel);
            if(generation!=_appUpdateGeneration || _disposed || _closing)return;
            if(!CanInstallAppUpdate())
            {
                _appUpdateStatus=ErrorMessages.Text("Установка отложена. Заверши текущие операции и нажми «Обновить» повторно.",
                    "Installation was deferred. Finish current operations and click Update again.");
                return;
            }
            SaveCheckpoint();
            if(_checkpointReadOnly)throw new IOException(ErrorMessages.Text("Сначала восстанови возможность сохранения очереди и настроек.","Restore checkpoint saving before updating."));
            if(AppUpdateLaunchOverride is not null){AppUpdateLaunchOverride(prepared);return;}
            _pendingAppUpdate=prepared;prepared=null;
            _appUpdateHandoff=true;_ingress.Stop();_window.IsEnabled=false;
            _appUpdateStatus=ErrorMessages.Text("Закрытие приложения перед обновлением…","Closing the application before updating…");
            _lifecycle.RequestExit();
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested){}
        catch(Exception error)
        {
            if(generation==_appUpdateGeneration && !_disposed && !_closing)
                _appUpdateStatus=ErrorMessages.Text("Обновление не установлено: ","The update was not installed: ")+DisplayError(error);
        }
        finally {prepared?.Dispose();if(generation==_appUpdateGeneration){_appUpdateBusy=false;RefreshAppUpdateControls();}}
    }
    internal void DisposeAppUpdates()
    {
        _appUpdateGeneration++;
        _appUpdateStop?.Cancel();_appUpdateStop?.Dispose();_appUpdateStop=null;
        _appUpdateService?.Dispose();_appUpdateService=null;_appUpdateBusy=false;
    }
    internal void LaunchPendingAppUpdate(string mutexName)
    {
        var prepared=_pendingAppUpdate;_pendingAppUpdate=null;
        if(prepared is null)return;
        if(!_closeReady || _checkpointReadOnly || !_appUpdateHandoff){prepared.Dispose();return;}
        try {using var helper=AppUpdateBootstrap.StartHelper(prepared,mutexName);}
        catch(Exception error)
        {
            prepared.Dispose();
            System.Windows.Forms.MessageBox.Show(ErrorMessages.Text("Не удалось запустить обновление: ","Cannot start the updater: ")+error.Message,"Video Downloader");
        }
    }
}
