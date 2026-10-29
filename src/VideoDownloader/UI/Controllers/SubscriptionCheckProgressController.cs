using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace VideoDownloader.Native;

// Owns one visual scan batch independently from the main download session.
// Window + version + exact source identity guard every delayed scanner report.
internal sealed class SubscriptionCheckProgressController
{
    private readonly Dispatcher _dispatcher;
    private readonly Func<Window?,bool> _isCurrentWindow;
    private readonly Func<bool> _isCancelled;
    private readonly Stopwatch _clock=new();
    private DispatcherTimer? _timer;
    private Window? _window;
    private long _version;
    private string _sourceId="";
    private int _phase;
    private int _total;
    private int _completed;
    private int _skipped;
    private int? _items;
    private long? _bytes;
    private bool _active;
    private bool _recentWindow;

    internal SubscriptionCheckProgressController(Dispatcher dispatcher,Func<Window?,bool> isCurrentWindow,Func<bool> isCancelled)
    {
        _dispatcher=dispatcher;_isCurrentWindow=isCurrentWindow;_isCancelled=isCancelled;
    }
    internal bool IsActive=>_active;
    internal bool HasTimer=>_timer!=null;
    internal bool IsClockRunning=>_clock.IsRunning;
    private T Control<T>(string name) where T:class =>
        (T)(_window?.FindName(name) ?? throw new InvalidOperationException("Missing subscription progress control: "+name));

    private sealed class SubscriptionProgressObserver(Action<SubscriptionScanProgress> report) : IProgress<SubscriptionScanProgress>
    {
        public void Report(SubscriptionScanProgress value) => report(value);
    }

    private bool Current(long version,string? sourceId=null) =>
        _active && version==_version && _isCurrentWindow(_window) &&
        (sourceId==null || string.Equals(sourceId,_sourceId,StringComparison.Ordinal));

    internal long Begin(Window window,int total)
    {
        Stop();
        _window=window;
        _active=true;
        _total=total;
        _completed=0;
        _skipped=0;
        _phase=0;
        _clock.Restart();
        Control<Border>("SubscriptionCheckProgressPanel").Visibility=Visibility.Visible;
        Control<TextBlock>("SubscriptionCheckSourceText").Text="Проверка подписок";
        Control<TextBlock>("SubscriptionCheckSourceText").ToolTip=null;
        Control<TextBlock>("SubscriptionCheckStageText").Text="Подготавливаем список источников…";
        Control<TextBlock>("SubscriptionCheckStageText").Foreground=Brushes.LightGray;
        Control<TextBlock>("SubscriptionCheckDetailText").Text="";
        Control<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate=true;
        UpdateCounter();
        UpdateElapsed();
        long version=_version;
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(200)};
        timer.Tick+=(_,_)=>
        {
            if(!Current(version)) { timer.Stop();return; }
            UpdateElapsed();
        };
        _timer=timer;
        timer.Start();
        return version;
    }

    internal void Stop()
    {
        _timer?.Stop();
        _timer=null;
        _clock.Stop();
        if(_window?.FindName("SubscriptionCheckActivityProgress") is ProgressBar activity)
            activity.IsIndeterminate=false;
        _active=false;
        _sourceId="";
        _window=null;
        _version++;
    }

    private void UpdateElapsed()
    {
        var elapsed=_clock.Elapsed;
        Control<TextBlock>("SubscriptionCheckElapsedText").Text=$"Прошло: {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void UpdateCounter()
    {
        var bar=Control<ProgressBar>("SubscriptionCheckOverallProgress");
        bar.Maximum=Math.Max(1,_total);
        bar.Value=Math.Min(_completed,_total);
        Control<TextBlock>("SubscriptionCheckCountText").Text=$"Проверено: {_completed} / {_total}"+
            (_skipped>0?$"; пропущено: {_skipped}":"");
    }

    internal void StartSource(long version,SubscriptionDefinition source)
    {
        if(!Current(version))return;
        _sourceId=source.Id;
        _phase=0;
        _items=null;
        _bytes=null;
        _recentWindow=false;
        var title=Control<TextBlock>("SubscriptionCheckSourceText");
        title.Text=string.IsNullOrWhiteSpace(source.Title)?source.Url:source.Title;
        title.ToolTip=source.Url;
        Control<TextBlock>("SubscriptionCheckDetailText").Text="Число материалов станет известно после полного обхода.";
        SetStage(version,source.Id,1,"Получаем полный список материалов…");
    }

    internal void SetStage(long version,string sourceId,int phase,string text)
    {
        if(!Current(version,sourceId) || phase<_phase)return;
        _phase=phase;
        Control<TextBlock>("SubscriptionCheckStageText").Text=text;
    }

    internal IProgress<SubscriptionScanProgress> CreateObserver(long version,string sourceId) =>
        new SubscriptionProgressObserver(value=>
        {
            if(_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)return;
            _=_dispatcher.BeginInvoke(()=>Report(version,sourceId,value));
        });

    internal void Report(long version,string sourceId,SubscriptionScanProgress progress)
    {
        if(!Current(version,sourceId) || _isCancelled())return;
        // A deliberate full fallback starts a second enumeration after the
        // probe. Other late callbacks still cannot roll back persistence.
        if (progress.Stage=="FullFallback" && _phase<5)
        {
            _recentWindow=false;
            _items=null;
            _bytes=null;
            _phase=0;
        }
        if (progress.Stage=="RecentWindow" && _phase<2) _recentWindow=true;
        var (phase,text)=progress.Stage switch {
            "RecentWindow" => (1,"Проверяем 20 новых материалов…"),
            "FullFallback" => (1,"Окна недостаточно: получаем полный список…"),
            "Enumerating" => (1,_recentWindow?"Проверяем 20 новых материалов…":"Получаем полный список материалов…"),
            "Parsing" => (2,_recentWindow?"Сравниваем окно новых материалов…":"Разбираем и проверяем полный список…"),
            "Feed" => (3,"Дополняем названия из RSS / Atom…"),
            "Ready" => (4,_recentWindow?"Окно проверено. Подготавливаем сохранение…":"Полный список получен. Подготавливаем сохранение…"),
            _ => (0,"")
        };
        if(phase==0 || phase<_phase)return;
        SetStage(version,sourceId,phase,text);
        if(progress.ItemCount is >=0)_items=progress.ItemCount;
        if(progress.MetadataBytes is >=0)_bytes=Math.Max(_bytes??0,progress.MetadataBytes.Value);
        var details=new List<string>();
        if(_items is { } items)details.Add(_recentWindow?$"Материалов в проверенном окне: {items}":$"Материалов в полном списке: {items}");
        else details.Add(_recentWindow?"Окно: до 20 новых материалов. Сравниваем точные URL.":"Число материалов станет известно после полного обхода.");
        if(_bytes is { } bytes)details.Add($"Получено метаданных: {DownloadFormat.Bytes(bytes)}");
        Control<TextBlock>("SubscriptionCheckDetailText").Text=string.Join(" · ",details);
    }

    internal void CompleteSource(long version,string sourceId)
    {
        if(!Current(version,sourceId))return;
        _completed++;
        _sourceId="";
        UpdateCounter();
    }

    internal void SkipSource(long version)
    {
        if(!Current(version))return;
        _skipped++;
        _total=Math.Max(_completed,_total-1);
        UpdateCounter();
    }

    internal void Cancel()
    {
        if(!Current(_version))return;
        _phase=int.MaxValue;
        _clock.Stop();
        _timer?.Stop();
        _timer=null;
        UpdateElapsed();
        Control<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate=false;
        Control<TextBlock>("SubscriptionCheckStageText").Text="Отменяем проверку…";
    }

    internal void Finish(long version,bool cancelled,int succeeded,int failed,int queued)
    {
        if(!Current(version))return;
        _clock.Stop();
        _timer?.Stop();
        _timer=null;
        UpdateElapsed();
        UpdateCounter();
        Control<ProgressBar>("SubscriptionCheckActivityProgress").IsIndeterminate=false;
        var stage=Control<TextBlock>("SubscriptionCheckStageText");
        stage.Text=cancelled?"Проверка отменена":failed>0?"Проверка завершена с ошибками":"Проверка завершена";
        stage.Foreground=failed>0?Brushes.LightSalmon:cancelled?Brushes.Gold:Brushes.LightGreen;
        Control<TextBlock>("SubscriptionCheckDetailText").Text=$"Успешных проверок: {succeeded}; ошибок: {failed}; добавлено в очередь: {queued}.";
        _active=false;
        _sourceId="";
    }

}
