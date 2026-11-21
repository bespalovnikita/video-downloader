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
    private DownloadOptions _options;
    private readonly string _executable;
    private readonly Func<SpeedDebugResult,DownloadOptions?> _saveDefaults;
    private readonly TextBox _urls;
    private readonly TextBlock _step,_summary,_details,_aria2Availability;
    private readonly ProgressBar _progress;
    private readonly DataGrid _results;
    private readonly Button _start,_compareAria2,_cancel;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(250)};
    private readonly Stopwatch _elapsed=new();
    private CancellationTokenSource? _runCancellation;
    private Task _runTask=Task.CompletedTask;
    private SpeedDebugUpdate? _update;
    private bool _closeWhenDone,_closed,_aria2Available;

    internal SpeedDebugWindow(Window owner,DownloadOptions options,string executable,Func<SpeedDebugResult,DownloadOptions?> saveDefaults)
    {
        _options=options;
        _executable=executable;
        _saveDefaults=saveDefaults;
        Owner=owner;
        Name="SpeedDebugWindow";
        Title="Speed debug mode";
        ShowInTaskbar=false;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Width=Math.Min(870,SystemParameters.WorkArea.Width-32);
        Height=Math.Min(610,SystemParameters.WorkArea.Height-32);
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
            "Тест использует четыре случайные ссылки из списка. На каждый запуск отводится до минуты, ограничение скорости отключено.",
            "The benchmark uses four random URLs from the list. Each run has a one-minute budget with no speed cap.")));
        _aria2Availability=Named("SpeedDebugAria2Availability",Note(ErrorMessages.Text("Проверяем доступность aria2c…","Checking for aria2c…")));
        setup.Children.Add(_aria2Availability);
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
            "Тест использует временные файлы. Настройки победившего варианта сохраняются для новых загрузок.",
            "The benchmark uses temporary files. The winning settings are saved for new downloads.")));
        footer.Children.Add(_summary);
        _details=Named("SpeedDebugDetails",Note(""));
        var detailScroll=new ScrollViewer{Content=_details,MaxHeight=54,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        detailScroll.Visibility=Visibility.Collapsed;
        footer.Children.Add(detailScroll);
        var buttons=new WrapPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Right};
        _start=Named("StartSpeedDebugButton",ActionButton(ErrorMessages.Text("Запустить тест","Run benchmark")));
        _compareAria2=Named("CompareAria2SpeedDebugButton",ActionButton(ErrorMessages.Text("Сравнить с aria2c","Compare with aria2c")));
        _compareAria2.IsEnabled=false;
        _cancel=Named("CancelSpeedDebugButton",ActionButton(ErrorMessages.Text("Отменить","Cancel")));
        _cancel.IsEnabled=false;
        var close=Named("CloseSpeedDebugButton",ActionButton(ErrorMessages.Text("Закрыть","Close")));
        foreach(var button in new[]{_start,_compareAria2,_cancel,close})buttons.Children.Add(button);
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

        _start.Click+=async(_,_)=>await StartRunAsync(compareAria2:false);
        _compareAria2.Click+=async(_,_)=>await StartRunAsync(compareAria2:true);
        _cancel.Click+=(_,_)=>CancelRun();
        close.Click+=(_,_)=>Close();
        _results.SelectionChanged+=(_,_)=>
        {
            _details.Text=(_results.SelectedItem as ResultRow)?.Details??"";
            detailScroll.Visibility=_details.Text.Length>0?Visibility.Visible:Visibility.Collapsed;
        };
        _timer.Tick+=(_,_)=>RefreshProgress();
        Loaded+=async(_,_)=>await RefreshAria2AvailabilityAsync();
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

    private Task StartRunAsync(bool compareAria2)
    {
        if(_closed || _runCancellation is not null || (compareAria2 && !_aria2Available))return Task.CompletedTask;
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
        return _runTask=MeasureAsync(urls[..SpeedDebugBenchmark.SampleCount],_runCancellation,compareAria2);
    }

    private async Task MeasureAsync(IReadOnlyList<string> samples,CancellationTokenSource cancellation,bool compareAria2)
    {
        _results.ItemsSource=null;
        _summary.Text="";
        _urls.IsEnabled=_start.IsEnabled=_compareAria2.IsEnabled=false;
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
            var result=await Task.Run(()=>SpeedDebugBenchmark.RunAsync(samples,_options,progress,cancellation.Token,_executable,compareAria2),cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if(!_closed)ShowResults(result,compareAria2);
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
                _compareAria2.IsEnabled=_aria2Available;
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

    private void ShowResults(IReadOnlyList<SpeedDebugResult> results,bool compareAria2)
    {
        IEnumerable<SpeedDebugResult> displayResults=compareAria2?results.OrderBy(result=>result.Kind):results;
        _results.ItemsSource=displayResults.Select(ResultRow.From).ToArray();
        if(results.Count>0)_results.SelectedIndex=0;
        var best=SpeedDebugBenchmark.SelectDefaults(results,compareAria2);
        if(compareAria2)ShowAria2Comparison(results,best);
        else
            _summary.Text=best is null
                ? ErrorMessages.Text("Для сохранения настроек нужен вариант с замерами по всем четырём ссылкам без ошибок.",
                    "Saving defaults requires an error-free profile with measurements for all four URLs.")
                : ErrorMessages.Text(
                    $"Лучший вариант — {best.Profile}, {best.Parallel} одновременных загрузок, {best.Fragments} фрагментов, {FormatSpeed(best.BytesPerSecond)}.",
                    $"Fastest profile: {best.Profile}, {best.Parallel} simultaneous downloads, {best.Fragments} fragments, {FormatSpeed(best.BytesPerSecond)}.");
        if(best is null)return;

        _cancel.IsEnabled=false;
        if(_saveDefaults(best) is not {} saved)
        {
            _summary.Text+=ErrorMessages.Text(" Не удалось сохранить настройки для новых загрузок."," The settings could not be saved for new downloads.");
            return;
        }
        // A second run uses the newly committed settings as its baseline.
        _options=saved;
        _summary.Text+=best.Kind switch
        {
            SpeedDebugKind.HttpsNative=>ErrorMessages.Text(" yt-dlp сохранён как загрузчик прямого HTTP(S).",
                " yt-dlp was saved as the direct HTTP(S) downloader."),
            SpeedDebugKind.HttpsAria2=>ErrorMessages.Text(" aria2c сохранён как загрузчик прямого HTTP(S).",
                " aria2c was saved as the direct HTTP(S) downloader."),
            _=>ErrorMessages.Text(" Настройки сохранены для новых загрузок."," Settings were saved for new downloads.")
        };
    }

    private async Task RefreshAria2AvailabilityAsync()
    {
        bool available=await Task.Run(()=>SpeedDebugBenchmark.Aria2Available);
        if(_closed)return;
        _aria2Available=available;
        _compareAria2.IsEnabled=available && _runCancellation is null;
        _aria2Availability.Text=available
            ? ErrorMessages.Text("aria2c найден. Сравнение доступно для прямых HTTPS-потоков.",
                "aria2c found. Comparison is available for direct HTTPS streams.")
            : ErrorMessages.Text("Для сравнения на прямых HTTPS-потоках разместите aria2c.exe рядом с приложением или добавьте его в PATH.",
                "To compare direct HTTPS downloads, put aria2c.exe next to the app or add it to PATH.");
    }

    private void ShowAria2Comparison(IReadOnlyList<SpeedDebugResult> results,SpeedDebugResult? winner)
    {
        var native=results.FirstOrDefault(result=>result.Kind==SpeedDebugKind.HttpsNative);
        var aria2=results.FirstOrDefault(result=>result.Kind==SpeedDebugKind.HttpsAria2);
        if(winner is not null && native is not null && aria2 is not null)
        {
            double ratio=aria2.BytesPerSecond/native.BytesPerSecond;
            _summary.Text=ErrorMessages.Text(
                $"Прямой HTTPS · {native.EligibleUrls} видео · скорость aria2c составляет {ratio:P0} от скорости yt-dlp.",
                $"Direct HTTPS · {native.EligibleUrls} videos · aria2c reached {ratio:P0} of yt-dlp’s speed.");
        }
        else
        {
            _summary.Text=native is {EligibleUrls: 0,FailedUrls: 0}
                ? ErrorMessages.Text("Тест не получил подходящих прямых HTTPS-потоков для сравнения с aria2c.",
                    "The benchmark found no eligible direct HTTPS streams for the aria2c comparison.")
                : ErrorMessages.Text("Для сравнения с aria2c нужны результаты обоих загрузчиков по всем выбранным прямым HTTPS-потокам.",
                    "The aria2c comparison requires results from both downloaders for every eligible direct HTTPS stream.");
        }
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

    private sealed record ResultRow(string Profile,int Parallel,string Fragments,string Speed,string Volume,string Elapsed,string Coverage,string Details)
    {
        internal static ResultRow From(SpeedDebugResult result)
        {
            string speed=result.Bytes>0?FormatSpeed(result.BytesPerSecond):"—";
            string seconds=ErrorMessages.Text($"{result.Duration.TotalSeconds:0.#} с",$"{result.Duration.TotalSeconds:0.#} s");
            string fragments=result.Kind==SpeedDebugKind.Regular?result.Fragments.ToString():"—";
            string coverage=result.EligibleUrls>0?$"{result.MeasuredUrls}/{result.EligibleUrls}":"—";
            string details=result.FailedUrls>0?ErrorMessages.Text($"Ошибок: {result.FailedUrls}. ",$"Failed: {result.FailedUrls}. "):"";
            if(result.Error is {} error)details+=DiagnosticPrivacy.Text(ErrorMessages.ForDisplay(error));
            return new(result.Profile,result.Parallel,fragments,speed,DisplayValueConverter.Bytes(result.Bytes),seconds,coverage,details);
        }
    }
}
