using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Binding=System.Windows.Data.Binding;
using Label=System.Windows.Controls.Label;

namespace VideoDownloader.Native;

internal sealed class SpeedDebugWindow : Window
{
    private const string DefaultUrls="""
        https://rt.pornhub.com/view_video.php?viewkey=6afda993ab932
        https://rt.pornhub.com/view_video.php?viewkey=662a6761bd68e
        https://rt.pornhub.com/view_video.php?viewkey=6911ce334ae78
        https://rt.pornhub.com/view_video.php?viewkey=6793cd8049d00
        """;
    private readonly DownloadOptions _options;
    private readonly string _executable;
    private readonly TextBox _urls;
    private readonly TextBlock _step,_summary,_details;
    private readonly ProgressBar _progress;
    private readonly DataGrid _results;
    private readonly Button _start,_cancel;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly Stopwatch _elapsed=new();
    private CancellationTokenSource? _runCancellation;
    private Task _runTask=Task.CompletedTask;
    private SpeedDebugUpdate? _update;
    private bool _closeWhenDone,_closed;

    internal SpeedDebugWindow(Window owner,DownloadOptions options,string executable)
    {
        _options=options;
        _executable=executable;
        Owner=owner;
        Name="SpeedDebugWindow";
        Title="Speed debug mode";
        ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Width=Math.Min(870,SystemParameters.WorkArea.Width-32);
        Height=Math.Min(570,SystemParameters.WorkArea.Height-32);
        MinWidth=580;
        MinHeight=440;
        Background=owner.Background;
        Foreground=owner.Foreground;
        FontFamily=owner.FontFamily;
        Resources.MergedDictionaries.Add(owner.Resources);
        NameScope.SetNameScope(this,new NameScope());

        var root=new DockPanel{Margin=new Thickness(16)};
        var setup=new StackPanel();
        setup.Children.Add(Note(ErrorMessages.Text(
            "Тест сравнивает настройки на четырёх случайных ссылках из списка. На все замеры отводится до минуты, ограничение скорости отключено.",
            "The benchmark compares settings on four random URLs from the list. All measurements share a one-minute budget with no speed cap.")));
        _urls=Named("SpeedDebugUrls",new TextBox
        {
            Text=DefaultUrls,AcceptsReturn=true,MinLines=4,MaxLines=5,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto
        });
        setup.Children.Add(new Label
        {
            Content=ErrorMessages.Text("Ссылки на видео (по одной на строку)","Video URLs (one per line)"),
            Target=_urls,Foreground=Foreground
        });
        setup.Children.Add(_urls);
        _step=Named("SpeedDebugStep",Note(""));
        setup.Children.Add(_step);
        _progress=Named("SpeedDebugProgress",new ProgressBar
        {
            Height=5,Maximum=SpeedDebugBenchmark.Budget.TotalSeconds,Margin=new Thickness(0,0,0,6),
            Visibility=Visibility.Collapsed
        });
        setup.Children.Add(_progress);
        DockPanel.SetDock(setup,Dock.Top);
        root.Children.Add(setup);

        var footer=new StackPanel{Margin=new Thickness(0,6,0,0)};
        _summary=Named("SpeedDebugSummary",Note(ErrorMessages.Text(
            "Тест использует временные файлы. Подходящие параметры можно выставить в настройках после замера.",
            "The benchmark uses temporary files. You can set the recommended values in Settings after the run.")));
        footer.Children.Add(_summary);
        _details=Named("SpeedDebugDetails",Note(""));
        var detailScroll=new ScrollViewer{Content=_details,MaxHeight=54,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        detailScroll.Visibility=Visibility.Collapsed;
        footer.Children.Add(detailScroll);
        var buttons=new WrapPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        _start=Named("StartSpeedDebugButton",ActionButton(ErrorMessages.Text("Запустить тест","Run benchmark")));
        _cancel=Named("CancelSpeedDebugButton",ActionButton(ErrorMessages.Text("Отменить","Cancel")));
        _cancel.IsEnabled=false;
        var close=Named("CloseSpeedDebugButton",ActionButton(ErrorMessages.Text("Закрыть","Close")));
        foreach(var button in new[]{_start,_cancel,close})buttons.Children.Add(button);
        footer.Children.Add(buttons);
        DockPanel.SetDock(footer,Dock.Bottom);
        root.Children.Add(footer);

        _results=Named("SpeedDebugResults",new DataGrid
        {
            IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserSortColumns=false,
            SelectionMode=DataGridSelectionMode.Single,MinHeight=75
        });
        AddColumn(ErrorMessages.Text("Вариант","Profile"),nameof(ResultRow.Profile),new DataGridLength(1,DataGridLengthUnitType.Star));
        AddColumn(ErrorMessages.Text("Потоки","Jobs"),nameof(ResultRow.Parallel),64);
        AddColumn(ErrorMessages.Text("Фрагменты","Fragments"),nameof(ResultRow.Fragments),86);
        AddColumn(ErrorMessages.Text("Скорость","Speed"),nameof(ResultRow.Speed),158);
        AddColumn(ErrorMessages.Text("Объём","Volume"),nameof(ResultRow.Volume),83);
        AddColumn(ErrorMessages.Text("Время","Time"),nameof(ResultRow.Elapsed),66);
        AddColumn(ErrorMessages.Text("Видео","Videos"),nameof(ResultRow.Coverage),56);
        root.Children.Add(_results);
        Content=root;

        _start.Click+=async(_,_)=>await StartRunAsync();
        _cancel.Click+=(_,_)=>CancelRun();
        close.Click+=(_,_)=>Close();
        _results.SelectionChanged+=(_,_)=>
        {
            _details.Text=(_results.SelectedItem as ResultRow)?.Details??"";
            detailScroll.Visibility=_details.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        };
        _timer.Tick+=(_,_)=>RefreshProgress();
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key!=Key.Escape)return;
            e.Handled=true;
            if(_runCancellation is null)Close();
            else CancelRun();
        };
    }

    private T Named<T>(string name,T element) where T:FrameworkElement
    {
        element.Name=name;
        RegisterName(name,element);
        return element;
    }

    private static TextBlock Note(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};
    private Button ActionButton(string caption)=>new(){Content=caption,Style=TryFindResource("GuardedActionButton") as Style};

    private void AddColumn(string header,string property,DataGridLength width)
    {
        var column=new DataGridTextColumn{Header=header,Binding=new Binding(property),Width=width};
        if(TryFindResource("GridTextStyle") is Style style)column.ElementStyle=style;
        _results.Columns.Add(column);
    }

    private Task StartRunAsync()
    {
        if(_closed || _runCancellation is not null)return Task.CompletedTask;
        string[] urls=_urls.Text.Split('\n',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries)
            .Distinct(StringComparer.Ordinal).ToArray();
        if(urls.Length<SpeedDebugBenchmark.SampleCount || urls.Any(url=>!AppPersistence.ValidUrl(url) || url.Any(char.IsControl)))
        {
            _summary.Text=ErrorMessages.Text("Введите хотя бы четыре разные действительные ссылки HTTP или HTTPS.",
                "Enter at least four distinct, valid HTTP or HTTPS URLs.");
            _urls.Focus();
            return Task.CompletedTask;
        }

        Random.Shared.Shuffle(urls);
        _runCancellation=new CancellationTokenSource();
        return _runTask=MeasureAsync(urls[..SpeedDebugBenchmark.SampleCount],_runCancellation);
    }

    private async Task MeasureAsync(IReadOnlyList<string> samples,CancellationTokenSource cancellation)
    {
        _results.ItemsSource=null;
        _summary.Text="";
        _urls.IsEnabled=_start.IsEnabled=false;
        _cancel.IsEnabled=true;
        _progress.Visibility=Visibility.Visible;
        _update=null;
        _elapsed.Restart();
        _timer.Start();
        RefreshProgress();
        var progress=new Progress<SpeedDebugUpdate>(update=>
        {
            if(!ReferenceEquals(_runCancellation,cancellation) || cancellation.IsCancellationRequested || _closed)return;
            _update=update;
            RefreshProgress();
        });
        try
        {
            var result=await Task.Run(()=>SpeedDebugBenchmark.RunAsync(samples,_options,progress,cancellation.Token,_executable),cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(!_closed)ShowResults(result);
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {
            if(!_closed)_summary.Text=ErrorMessages.Text("Тест отменён.","Benchmark cancelled.");
        }
        catch(Exception error)
        {
            if(!_closed)_summary.Text=ErrorMessages.Text("Тест завершился с ошибкой. ","Benchmark failed. ")+
                DiagnosticPrivacy.Text(ErrorMessages.ForException(error));
        }
        finally
        {
            _timer.Stop();
            _elapsed.Stop();
            cancellation.Dispose();
            _runCancellation=null;
            if(!_closed)
            {
                _step.Text="";
                _urls.IsEnabled=_start.IsEnabled=true;
                _cancel.IsEnabled=false;
                _progress.Visibility=Visibility.Collapsed;
                if(_closeWhenDone)_=Dispatcher.BeginInvoke(()=>{if(!_closed)Close();});
            }
        }
    }

    private void RefreshProgress()
    {
        if(_closed || _runCancellation?.IsCancellationRequested!=false)return;
        double elapsed=Math.Min(SpeedDebugBenchmark.Budget.TotalSeconds,_elapsed.Elapsed.TotalSeconds);
        _progress.Value=elapsed;
        string time=$"{(int)elapsed}/{SpeedDebugBenchmark.Budget.TotalSeconds:0} с";
        if(!ErrorMessages.IsRussian)time=$"{(int)elapsed}/{SpeedDebugBenchmark.Budget.TotalSeconds:0} s";
        _step.Text=_update is {} update
            ? ErrorMessages.Text($"Вариант {update.ProfileIndex} из {update.ProfileCount} · {update.Profile} · {time}",
                $"Profile {update.ProfileIndex} of {update.ProfileCount} · {update.Profile} · {time}")
            : ErrorMessages.Text($"Подготовка · {time}",$"Preparing · {time}");
    }

    private void ShowResults(IReadOnlyList<SpeedDebugResult> results)
    {
        _results.ItemsSource=results.Select(ResultRow.From).ToArray();
        if(results.Count>0)_results.SelectedIndex=0;
        var best=results.FirstOrDefault(result=>result.MeasuredUrls==SpeedDebugBenchmark.SampleCount &&
            result.FailedUrls==0 && result.Bytes>0);
        _summary.Text=best is null
            ? ErrorMessages.Text("Для рекомендации нужны замеры по всем четырём ссылкам в одном варианте.",
                "A recommendation requires measurements for all four URLs in one profile.")
            : ErrorMessages.Text(
                $"Лучший вариант — {best.Profile}, {best.Parallel} одновременных загрузок, {best.Fragments} фрагментов, {FormatSpeed(best.BytesPerSecond)}.",
                $"Fastest profile: {best.Profile}, {best.Parallel} simultaneous downloads, {best.Fragments} fragments, {FormatSpeed(best.BytesPerSecond)}.");
        if(best?.Chunked==true)
            _summary.Text+=ErrorMessages.Text(" Параметр HTTP-блоков доступен в этом тесте."," The HTTP chunk size option is available in this benchmark.");
    }

    private static string FormatSpeed(double bytesPerSecond)=>ErrorMessages.Text(
        $"{DisplayValueConverter.Speed(bytesPerSecond)} · {bytesPerSecond*8/1_000_000:0.#} Мбит/с",
        $"{DisplayValueConverter.Speed(bytesPerSecond)} · {bytesPerSecond*8/1_000_000:0.#} Mbps");

    internal void CancelRun()
    {
        if(_runCancellation?.IsCancellationRequested!=false)return;
        _cancel.IsEnabled=false;
        _step.Text=ErrorMessages.Text("Останавливаем замер и очищаем временные файлы…","Stopping the run and cleaning up temporary files…");
        _=_runCancellation.CancelAsync();
    }

    internal Task CancelAndWaitAsync()
    {
        CancelRun();
        return _runTask;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if(_runCancellation is not null)
        {
            e.Cancel=true;
            _closeWhenDone=true;
            CancelRun();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed=true;
        _timer.Stop();
        CancelRun();
        base.OnClosed(e);
    }

    private sealed record ResultRow(string Profile,int Parallel,int Fragments,string Speed,string Volume,string Elapsed,string Coverage,string Details)
    {
        internal static ResultRow From(SpeedDebugResult result)
        {
            string speed=result.Bytes>0?FormatSpeed(result.BytesPerSecond):"—";
            string seconds=ErrorMessages.Text($"{result.Duration.TotalSeconds:0.#} с",$"{result.Duration.TotalSeconds:0.#} s");
            string coverage=$"{result.MeasuredUrls}/{SpeedDebugBenchmark.SampleCount}";
            string details=result.FailedUrls>0?ErrorMessages.Text($"Ошибок: {result.FailedUrls}. ",$"Failed: {result.FailedUrls}. "):"";
            if(result.Error is {} error)details+=DiagnosticPrivacy.Text(ErrorMessages.ForDisplay(error));
            return new(result.Profile,result.Parallel,result.Fragments,speed,DisplayValueConverter.Bytes(result.Bytes),seconds,coverage,details);
        }
    }
}
