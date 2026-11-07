namespace VideoDownloader.Native;

public sealed partial class NativeWindow
{
    private SubscriptionController? _subscriptions;
    internal SubscriptionController Subscriptions=>_subscriptions ??= new SubscriptionController(
        _window,new SubscriptionStore(Path.GetDirectoryName(_storage.SessionPath)!),_ytDlp,
        Path.Combine(AppContext.BaseDirectory,"ui","SubscriptionsWindow.xaml"),
        _storage.ReadHistory,()=>B("CookiesBox").Text.Trim(),QueueSubscriptionUrls,OpenUrl,Log,
        urls=>_preview.PrefetchUrls(urls),_preview);

    private void OpenSubscriptions()
    {
        if(!_disposed && !RejectProfileBackupMutation())Subscriptions.Open();
    }
    private void DisposeSubscriptions()
    {
        if(_subscriptions is null)return;
        _subscriptions.ScheduleChanged-=RefreshSubscriptionScheduleStatus;
        _subscriptions.Dispose();
    }

    private void InitializeSubscriptionScheduling(TimeProvider? clock,bool startTimer)
    {
        var controller=Subscriptions;
        controller.ScheduleChanged+=RefreshSubscriptionScheduleStatus;
        controller.StartScheduling(()=>!_disposed && !_closing && !_restoringSession && !_checkpointReadOnly &&
            !_legacyEngineBlocked && !_startingSession && !_dependencyOperationRunning && !_appUpdateBusy &&
            !_appUpdateHandoff && _window.IsEnabled,message=>
            {
                if(_disposed || _closing)return;
                _lifecycle.NotifyWarning("Новые материалы подписок",message);
            },clock,startTimer);
        RefreshSubscriptionScheduleStatus();
    }

    private void RefreshSubscriptionScheduleStatus()
    {
        if(_disposed || _closing)return;
        T("MainSubscriptionScheduleStatusText").Text=_subscriptions?.ScheduleStatus??"Проверки по расписанию выключены.";
    }

    // Profile restore owns this lease through its final revalidation and writes.
    // It blocks manual edits/checks too, but HasActiveWork reports only actual
    // work so the lease owner can revalidate that all workers have joined.
    internal Task<IDisposable> SuspendSubscriptionSchedulingAsync()=>Subscriptions.SuspendSchedulingAsync();
    internal void CloseSubscriptionsForMaintenance()=>_subscriptions?.CloseViewForMaintenance();
}
