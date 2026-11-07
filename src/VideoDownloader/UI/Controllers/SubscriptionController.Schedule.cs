using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

internal sealed partial class SubscriptionController
{
    private SubscriptionSchedulePlanner _schedulePlanner=new();
    private DispatcherTimer? _scheduleTimer;
    private readonly CancellationTokenSource _scheduleStop=new();
    private Func<bool> _canSchedule=()=>true;
    private Action<string>? _scheduleNotice;
    private Task? _manualCheckTask,_scheduledCheckTask;
    private Task _schedulePollTask=Task.CompletedTask;
    private Task<SubscriptionSnapshot>? _scheduleReadTask;
    private readonly HashSet<Task> _subscriptionReloads=[];
    private bool _scheduleStarted,_schedulePolling,_automaticCheck;
    private int _scheduleSuspensions,_scheduleGeneration;
    private int _pendingScheduleItems;
    private readonly HashSet<string> _pendingScheduleSources=new(StringComparer.Ordinal);
    private DateTimeOffset _nextScheduleNotice;
    private DateTimeOffset _nextSchedulePollUtc;
    private DateTimeOffset? _lastScheduleTimerUtc;
    private string _scheduleEditorId="",_scheduleEditorValue="",_scheduleReadError="";
    internal bool IsSchedulingSuspended=>_scheduleSuspensions>0;
    internal bool IsAutomaticCheckActive=>_automaticCheck;
    internal string ScheduleStatus {get;private set;}="Проверки по расписанию выключены.";
    internal event Action? ScheduleChanged;

    internal void StartScheduling(Func<bool> canRun,Action<string> notice,TimeProvider? clock=null,bool startTimer=true)
    {
        if(_disposed || _scheduleStarted)return;
        _scheduleStarted=true;
        _canSchedule=canRun;_scheduleNotice=notice;_schedulePlanner=new(clock);
        if(startTimer)
        {
            _scheduleTimer=new DispatcherTimer(DispatcherPriority.Background,Dispatcher){Interval=TimeSpan.FromSeconds(5)};
            _scheduleTimer.Tick+=ScheduleTick;_scheduleTimer.Start();
        }
        _=PollScheduledChecksAsync();
    }
    private async void ScheduleTick(object? sender,EventArgs e)
    {
        var now=_schedulePlanner.UtcNow;
        if(_lastScheduleTimerUtc is {} previous && now<previous-TimeSpan.FromMinutes(1))_nextSchedulePollUtc=default;
        _lastScheduleTimerUtc=now;
        // A disabled schedule does not repeatedly deserialize a large archive.
        // UI edits/reloads and maintenance explicitly refresh its deadlines.
        if(now<_nextSchedulePollUtc || HasActiveWork || !_canSchedule())return;
        await PollScheduledChecksAsync();
    }
    internal DateTimeOffset? NextScheduledCheckUtc(string id)=>_schedulePlanner.NextCheckUtc(id);
    internal Task PollScheduledChecksAsync()=>Dispatcher.InvokeAsync(BeginSchedulePoll,
        DispatcherPriority.Background).Task.Unwrap();
    private Task BeginSchedulePoll()
    {
        if(_schedulePolling)return _schedulePollTask;
        return _schedulePollTask=PollScheduledChecksCoreAsync();
    }
    private async Task PollScheduledChecksCoreAsync()
    {
        if(_disposed || _scheduleStop.IsCancellationRequested || _schedulePolling || IsSchedulingSuspended)return;
        int generation=_scheduleGeneration;
        _schedulePolling=true;
        try
        {
            var read=Task.Run(_store.Read,_scheduleStop.Token);_scheduleReadTask=read;
            SubscriptionSnapshot snapshot;
            try{snapshot=await read;}finally{if(ReferenceEquals(_scheduleReadTask,read))_scheduleReadTask=null;}
            if(_disposed || _scheduleStop.IsCancellationRequested || IsSchedulingSuspended || generation!=_scheduleGeneration)return;
            _schedulePlanner.Synchronize(snapshot.Subscriptions);
            FlushScheduleNotice();
            if(!HasActiveWork && _canSchedule())
            {
                string? id=_schedulePlanner.TakeDue();
                if(id is not null)
                {
                    var task=RunScheduledCheckAsync(snapshot.Subscriptions.Single(source=>source.Id==id));_scheduledCheckTask=task;
                    try{await task;}finally{if(ReferenceEquals(_scheduledCheckTask,task))_scheduledCheckTask=null;}
                }
                else SetScheduleSummary(snapshot);
            }
            _scheduleReadError="";
            SetNextSchedulePoll(snapshot);
            if(_window is not null)Sources.Items.Refresh();
            ReportScheduleChanged();
        }
        catch(OperationCanceledException) when(_scheduleStop.IsCancellationRequested){ }
        catch(Exception error)
        {
            string message="Расписание недоступно: "+ErrorMessages.ForException(error)+" Данные не изменены.";
            ScheduleStatus=message;
            _nextSchedulePollUtc=_schedulePlanner.UtcNow+TimeSpan.FromMinutes(1);
            if(_scheduleReadError!=message){_scheduleReadError=message;ScheduleLog(message);}
            ReportScheduleChanged();
        }
        finally{_schedulePolling=false;}
    }
    private void SetNextSchedulePoll(SubscriptionSnapshot snapshot)
    {
        var now=_schedulePlanner.UtcNow;
        var next=snapshot.Subscriptions.Select(source=>_schedulePlanner.NextCheckUtc(source.Id)).Min()
            ??now+TimeSpan.FromMinutes(5);
        if(_pendingScheduleItems>0 && _nextScheduleNotice<next)next=_nextScheduleNotice;
        _nextSchedulePollUtc=next>now?next:now+TimeSpan.FromSeconds(5);
    }
    private void SetScheduleSummary(SubscriptionSnapshot snapshot)
    {
        var enabled=snapshot.Subscriptions.Where(source=>source.ScheduleEnabled && source.Initialized && !source.Paused).ToArray();
        var next=enabled.Select(source=>NextScheduledCheckUtc(source.Id)).Where(value=>value.HasValue).Min();
        ScheduleStatus=enabled.Length==0?"Проверки по расписанию выключены.":
            $"Расписание: {enabled.Length} подписок · следующая проверка {next?.ToLocalTime():dd.MM HH:mm}. Новинки ждут принятия в очередь.";
    }
    private async Task RunScheduledCheckAsync(SubscriptionDefinition requested)
    {
        string id=requested.Id;
        bool success=false,cancelled=false;DateTimeOffset? attempt=requested.LastAttemptUtc;
        if(_disposed || HasActiveWork || IsSchedulingSuspended || !_canSchedule())
        {_schedulePlanner.Complete(id,false,null,cancelled:true);return;}
        using var cancel=CancellationTokenSource.CreateLinkedTokenSource(_scheduleStop.Token);
        _cancellation=cancel;_automaticCheck=true;UpdateButtons();
        try
        {
            if(IsSchedulingSuspended || !_canSchedule())
            {cancelled=true;return;}
            string name=AppPersistence.ValidUrl(requested.Title)?new Uri(requested.Url).Host:requested.Title;
            name=JournalSanitizer.Redact(name,multiline:false);if(name.Length>120)name=name[..120];
            ScheduleStatus="Проверка по расписанию: "+name+"…";ReportScheduleChanged();
            string cookies=_readCookies();
            var result=await new ScheduledSubscriptionCheck(_store,_ytDlp,_readHistory).RunAsync(id,cookies,cancel.Token);
            attempt=result.AttemptUtc;
            success=true;
            // Discovery is the only automatic network task. Archive-wide image
            // backfill remains part of manual scans; viewing uses normal preview.
            int found=result.NewItems;
            ScheduleStatus=found>0?$"Новых материалов: {found} · {name}. Откройте «Подписки», чтобы принять их в очередь.":
                $"Проверено по расписанию: {name}. Новых материалов нет.";
            ScheduleLog(ScheduleStatus);
            if(found>0)
            {
                _pendingScheduleItems+=found;_pendingScheduleSources.Add(id);
                FlushScheduleNotice();
            }
            if(_window is not null)await ReloadAsync(notify:false);
        }
        catch(OperationCanceledException)
        {cancelled=true;ScheduleStatus="Проверка по расписанию отменена. Прежние результаты сохранены.";}
        catch(Exception error)
        {
            if(cancel.IsCancellationRequested){cancelled=true;ScheduleStatus="Проверка по расписанию отменена. Прежние результаты сохранены.";return;}
            if(error is ScheduledSubscriptionCheckException scheduled)attempt=scheduled.AttemptUtc;
            ScheduleStatus="Проверка по расписанию не удалась: "+ErrorMessages.ForException(error)+" Следующая попытка отложена.";
            ScheduleLog(ScheduleStatus);
            if(_window is not null)await ReloadAsync(notify:false);
        }
        finally
        {
            _schedulePlanner.Complete(id,success,attempt,cancelled);
            if(ReferenceEquals(_cancellation,cancel))_cancellation=null;
            _automaticCheck=false;UpdateButtons();ReportScheduleChanged();
        }
    }
    internal Task<IDisposable> SuspendSchedulingAsync()
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(SuspendSchedulingAsync).Task.Unwrap();
        if(_disposed)throw new ObjectDisposedException(nameof(SubscriptionController));
        if(_manualCheckTask?.IsCompleted==false || _mutation || _finalizingCheck || _noticeShowing)
            throw new InvalidOperationException("Завершите ручную проверку и действия с подписками.");
        _scheduleSuspensions++;_scheduleGeneration++;UpdateButtons();
        if(_automaticCheck)_cancellation?.Cancel();
        // Install the barrier synchronously, then capture the real WPF context
        // for every continuation, including callers without a SyncContext.
        return Dispatcher.InvokeAsync(FinishScheduleSuspensionAsync).Task.Unwrap();
    }
    private async Task<IDisposable> FinishScheduleSuspensionAsync()
    {
        try
        {
            if(_scheduledCheckTask is {} task)await task;
            if(_scheduleReadTask is {} read)await read;
            await _schedulePollTask;
            if(_subscriptionReloads.Count>0)await Task.WhenAll(_subscriptionReloads.ToArray());
            return await Dispatcher.InvokeAsync<IDisposable>(()=>
            {
                if(_disposed || _scheduleStop.IsCancellationRequested)throw new OperationCanceledException("Приложение закрывается.");
                _schedulePlanner.Reset();return new ScheduleSuspension(this);
            });
        }
        catch{ReleaseScheduleSuspension();throw;}
    }
    internal void CloseViewForMaintenance()
    {
        if(!IsSchedulingSuspended || HasActiveWork)throw new InvalidOperationException("Сначала приостановите проверки и дождитесь завершения действий с подписками.");
        _window?.Close();_snapshot=null;_successes.Clear();
        _pendingScheduleItems=0;_pendingScheduleSources.Clear();_nextScheduleNotice=default;
    }
    private sealed class ScheduleSuspension(SubscriptionController owner):IDisposable
    {
        private SubscriptionController? _owner=owner;
        public void Dispose(){var current=Interlocked.Exchange(ref _owner,null);if(current is not null)current.ReleaseScheduleSuspension();}
    }
    private void ReleaseScheduleSuspension()
    {
        if(!Dispatcher.CheckAccess()){_=Dispatcher.BeginInvoke(ReleaseScheduleSuspension);return;}
        _scheduleSuspensions=Math.Max(0,_scheduleSuspensions-1);_scheduleGeneration++;_schedulePlanner.Reset();_nextSchedulePollUtc=default;UpdateButtons();
        if(!IsSchedulingSuspended && !_disposed)_=PollScheduledChecksAsync();
    }
    internal async Task StopChecksAsync()
    {
        StopScheduleTimer();_cancellation?.Cancel();
        var tasks=new[]{_manualCheckTask,_scheduledCheckTask,_scheduleReadTask,_schedulePollTask}.OfType<Task>()
            .Concat(_subscriptionReloads).ToArray();
        if(tasks.Length>0)await Task.WhenAll(tasks);
    }
    private void StopScheduleTimer()
    {
        _scheduleTimer?.Stop();if(_scheduleTimer is not null)_scheduleTimer.Tick-=ScheduleTick;_scheduleTimer=null;
        _scheduleStop.Cancel();
    }
    private void ScheduleLog(string message){try{_log(message);}catch(Exception){ }}
    private void FlushScheduleNotice()
    {
        if(_pendingScheduleItems==0 || _schedulePlanner.UtcNow<_nextScheduleNotice)return;
        string message=$"Новых материалов: {_pendingScheduleItems} · подписок: {_pendingScheduleSources.Count}. Откройте «Подписки», чтобы принять их в очередь.";
        _pendingScheduleItems=0;_pendingScheduleSources.Clear();
        _nextScheduleNotice=_schedulePlanner.UtcNow+TimeSpan.FromMinutes(1);
        try{_scheduleNotice?.Invoke(message);}
        catch(Exception error){ScheduleLog("Не удалось показать уведомление: "+ErrorMessages.ForException(error));}
    }
    private void ReportScheduleChanged(){try{ScheduleChanged?.Invoke();}catch(Exception error){ScheduleLog("Не удалось обновить сведения расписания: "+ErrorMessages.ForException(error));}}
    private void BindScheduleControls()
    {
        Control<Button>("SaveSubscriptionScheduleButton").Click+=async(_,_)=>await SaveScheduleAsync();
        Control<TextBox>("SubscriptionScheduleIntervalBox").TextChanged+=(_,_)=>UpdateButtons();
        Control<CheckBox>("SubscriptionScheduleEnabledCheck").Checked+=(_,_)=>UpdateButtons();
        Control<CheckBox>("SubscriptionScheduleEnabledCheck").Unchecked+=(_,_)=>UpdateButtons();
    }
    private void UpdateScheduleEditor()
    {
        if(_window is null)return;
        var source=(Sources.SelectedItem as SubscriptionSourceView)?.Definition;
        string id=source?.Id??"",value=$"{source?.ScheduleEnabled}|{source?.ScheduleIntervalMinutes}";
        if(_scheduleEditorId==id && _scheduleEditorValue==value)return;
        _scheduleEditorId=id;_scheduleEditorValue=value;
        Control<CheckBox>("SubscriptionScheduleEnabledCheck").IsChecked=source?.ScheduleEnabled==true;
        Control<TextBox>("SubscriptionScheduleIntervalBox").Text=(source?.ScheduleIntervalMinutes??30).ToString(CultureInfo.InvariantCulture);
    }
    private void UpdateScheduleButtons(bool ready,SubscriptionSourceView? source)
    {
        Control<CheckBox>("SubscriptionScheduleEnabledCheck").IsEnabled=ready && source?.Definition.Initialized==true;
        Control<TextBox>("SubscriptionScheduleIntervalBox").IsEnabled=ready && source is not null;
        bool valid=int.TryParse(Control<TextBox>("SubscriptionScheduleIntervalBox").Text,NumberStyles.None,CultureInfo.InvariantCulture,out int interval) &&
            interval>=SubscriptionSchedulePlanner.MinIntervalMinutes && interval<=SubscriptionSchedulePlanner.MaxIntervalMinutes;
        Control<Button>("SaveSubscriptionScheduleButton").IsEnabled=ready && source is not null && valid;
        Control<TextBlock>("SubscriptionScheduleStatusText").Text=ScheduleStatus;
    }
    private async Task SaveScheduleAsync()
    {
        if(Busy || Sources.SelectedItem is not SubscriptionSourceView source)return;
        if(!int.TryParse(Control<TextBox>("SubscriptionScheduleIntervalBox").Text,NumberStyles.None,CultureInfo.InvariantCulture,out int interval))return;
        bool enabled=Control<CheckBox>("SubscriptionScheduleEnabledCheck").IsChecked==true;
        _mutation=true;UpdateButtons();
        try
        {
            await Task.Run(()=>_store.SetSchedule(source.Id,enabled,interval));
            await ReloadAsync(notify:false);
            Status(enabled?"Расписание сохранено. Первая проверка — через выбранный интервал; скачивание запускается отдельно кнопкой «Скачать всю очередь».":"Проверки этой подписки по расписанию выключены.");
        }
        catch(Exception error){Status("Не удалось сохранить расписание: "+DisplayError(error),true);}
        finally{_mutation=false;UpdateButtons();}
        await PollScheduledChecksAsync();
    }
}
