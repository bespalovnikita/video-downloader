using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Binding=System.Windows.Data.Binding;
using Color=System.Windows.Media.Color;
using Control=System.Windows.Controls.Control;
using Forms=System.Windows.Forms;
using Label=System.Windows.Controls.Label;

namespace VideoDownloader.Native;

internal sealed record DiagnosticSaveSelection(string Path,bool Overwrite=false);

internal sealed class JobDiagnosticsWindow:Window
{
    private sealed record Choice(string Id,string Label);
    private sealed record AttemptChoice(string Id,string Label);
    private sealed record EventRow(JobDiagnosticEvent Data)
    {
        public string Time=>Data.CapturedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff",CultureInfo.InvariantCulture);
        public string Level=>LevelLabel(Data.Level);
        public string Attempt=>Data.AttemptLabel;
        public string Event=>Data.Event;
        public string Message=>Data.Message;
        internal string Key=>Data.Journal+":"+Data.Sequence.ToString(CultureInfo.InvariantCulture);
    }
    private readonly JobDiagnosticTarget _target;
    private readonly JobDiagnosticsService _service;
    private readonly Func<long> _sequence;
    private readonly Func<Window,DiagnosticSaveSelection?> _chooseExport;
    private readonly TextBox _search,_details,_error;
    private readonly TextBlock _explanation,_rawLabel;
    private readonly ComboBox _attempt,_level;
    private readonly DataGrid _events;
    private readonly TextBlock _summary,_status,_empty;
    private readonly Button _refresh,_copy,_export;
    private readonly DispatcherTimer _timer=new(){Interval=TimeSpan.FromMilliseconds(1500)};
    private readonly CancellationTokenSource _stop=new();
    private JobDiagnosticSnapshot? _snapshot;
    private EventRow[] _visible=[];
    private bool _closed,_reading,_exporting,_binding;
    private long _lastSequence=-1;
    private Task _readTask=Task.CompletedTask,_exportTask=Task.CompletedTask;
    internal Task PendingOperations=>Task.WhenAll(_readTask,_exportTask);
    internal bool IsExporting=>_exporting;
    internal event Action? WorkStateChanged;
    internal Task CancelForOwnerAsync(){CloseOperations();return PendingOperations;}

    internal JobDiagnosticsWindow(Window owner,JobDiagnosticTarget target,JobDiagnosticsService service,
        Func<long> sequence,Func<Window,DiagnosticSaveSelection?>? chooseExport=null)
    {
        _target=target;_service=service;_sequence=sequence;_chooseExport=chooseExport??ChooseExport;
        Owner=owner;Title="Диагностика: "+target.Label;Name="JobDiagnosticsWindow";
        Width=Math.Min(1120,Math.Max(740,SystemParameters.WorkArea.Width-48));
        Height=Math.Min(800,Math.Max(520,SystemParameters.WorkArea.Height-48));MinWidth=700;MinHeight=500;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=owner.Background;Foreground=owner.Foreground;FontFamily=owner.FontFamily;
        Resources.MergedDictionaries.Add(owner.Resources);NameScope.SetNameScope(this,new NameScope());
        var root=new Grid{Margin=new Thickness(14)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),new GridLength(1,GridUnitType.Star),GridLength.Auto})
            root.RowDefinitions.Add(new RowDefinition{Height=height});
        var header=new StackPanel();
        header.Children.Add(new TextBlock{Text=target.Label,FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        header.Children.Add(Named("JobDiagnosticsPurpose",Note("История выбранной загрузки помогает понять причину ошибки и подготовить отчёт. Выберите событие в таблице или откройте «Как разобраться».")));
        header.Children.Add(Named("JobDiagnosticUrl",new TextBox{Text=target.Url,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,
            MaxHeight=52,Margin=new Thickness(0,5,0,4),ToolTip="Адрес с маскированием приватных параметров"}));
        header.Children.Add(Note("Состояние при открытии: "+StatusLabel(target.Status)));
        _explanation=Named("JobDiagnosticExplanation",Note(""));header.Children.Add(_explanation);
        _rawLabel=Note("Последнее сообщение об ошибке:");header.Children.Add(_rawLabel);
        _error=Named("JobDiagnosticError",ReadOnlyText(64));header.Children.Add(_error);ShowError(target.Error);
        root.Children.Add(new ScrollViewer{Content=header,MaxHeight=160,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled});

        var filters=new Grid{Margin=new Thickness(0,8,0,4)};
        filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});
        filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(235)});
        filters.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(155)});
        _search=Named("JobDiagnosticsSearchBox",new TextBox{MaxLength=2048,Margin=new Thickness(0,0,6,0),
            ToolTip="Найти текст в событиях этого задания · Ctrl+F"});
        _attempt=Named("JobDiagnosticsAttemptCombo",new ComboBox{DisplayMemberPath=nameof(AttemptChoice.Label),SelectedValuePath=nameof(AttemptChoice.Id),Margin=new Thickness(0,0,6,0),
            ToolTip="Один запуск загрузки или её повтор. «Все попытки» показывает доступную историю целиком."});
        _level=Named("JobDiagnosticsLevelCombo",new ComboBox{DisplayMemberPath=nameof(Choice.Label),SelectedValuePath=nameof(Choice.Id),
            ToolTip="Начните с «Ошибки», если загрузка не удалась. Предупреждение само по себе не означает сбой."});
        _level.ItemsSource=new[]{new Choice("*","Все уровни"),new Choice(nameof(JournalLevel.Error),"Ошибки"),
            new Choice(nameof(JournalLevel.Warning),"Предупреждения"),new Choice(nameof(JournalLevel.Information),"Сведения"),
            new Choice(nameof(JournalLevel.Progress),"Прогресс"),new Choice(nameof(JournalLevel.Verbose),"Подробный вывод")};
        _level.SelectedIndex=0;
        foreach(var (label,control,column) in new (string,Control,int)[]{("Поиск по событиям",_search,0),("Попытка загрузки",_attempt,1),("Уровень сообщения",_level,2)})
        {
            var field=new StackPanel();field.Children.Add(new Label{Content=label,Target=control,Foreground=Foreground,Padding=new Thickness(0,0,0,3)});
            field.Children.Add(control);Grid.SetColumn(field,column);filters.Children.Add(field);
        }
        Grid.SetRow(filters,1);root.Children.Add(filters);
        _summary=Named("JobDiagnosticsSummary",Note("Читаем события выбранного задания…"));Grid.SetRow(_summary,2);root.Children.Add(_summary);

        _events=Named("JobDiagnosticsEventsGrid",new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,
            CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,
            SelectionMode=DataGridSelectionMode.Single,RowHeight=27,Margin=new Thickness(0,4,0,4)});
        foreach(var (label,path,width) in new (string,string,double)[]{("Время",nameof(EventRow.Time),165),("Уровень",nameof(EventRow.Level),110),
            ("Попытка",nameof(EventRow.Attempt),230),("Событие",nameof(EventRow.Event),135),("Сообщение",nameof(EventRow.Message),0)})
        {
            var column=new DataGridTextColumn{Header=label,Binding=new Binding(path),Width=width>0?new DataGridLength(width):new DataGridLength(1,DataGridLengthUnitType.Star)};
            if(TryFindResource("GridTextStyle") is Style style)column.ElementStyle=style;
            _events.Columns.Add(column);
        }
        var eventArea=new Grid{ClipToBounds=true};eventArea.Children.Add(_events);
        _empty=Named("JobDiagnosticsEmptyHint",Note(""));_empty.Margin=new Thickness(16,38,16,8);_empty.IsHitTestVisible=false;
        _empty.Visibility=Visibility.Collapsed;eventArea.Children.Add(_empty);Grid.SetRow(eventArea,3);root.Children.Add(eventArea);
        _details=Named("JobDiagnosticsEventDetails",ReadOnlyText(0));_details.ToolTip="Полное удерживаемое событие и параметры его попытки";
        var detailArea=new DockPanel{LastChildFill=true};
        var detailButtons=new WrapPanel();
        var showDetails=Named("JobDiagnosticsDetailsButton",new Button{Content="Выбранное событие",FontWeight=FontWeights.SemiBold,Padding=new Thickness(9,4,9,4)});
        var showHelp=Named("JobDiagnosticsHelpButton",new Button{Content="Как разобраться",Padding=new Thickness(9,4,9,4)});
        detailButtons.Children.Add(showDetails);detailButtons.Children.Add(showHelp);DockPanel.SetDock(detailButtons,Dock.Top);detailArea.Children.Add(detailButtons);
        var detailContent=new Grid();detailContent.Children.Add(_details);
        var help=Named("JobDiagnosticsHelp",Note(
            "Если загрузка не удалась\nВыберите уровень «Ошибки» и последнюю по времени запись. Полное сообщение появится в разделе «Выбранное событие». Если понятна причина, исправьте её и повторите загрузку обычной командой в очереди.\n\n"+
            "Что означают записи\nПопытка — отдельный запуск или повтор загрузки. Фильтр помогает отделить прежнюю ошибку от нового запуска. «Сведения» и «Прогресс» описывают ход работы; «Предупреждения» не всегда мешают завершению. Служебные названия в колонке «Событие» полезны для разбора отчёта.\n\n"+
            "Что отправить для помощи\n«Копировать видимые события» копирует только строки после применения поиска и фильтров. Вставьте их в сообщение и опишите, что хотели сделать и что произошло. «Сохранить отчёт в ZIP…» сохраняет все доступные события этой загрузки, включая скрытые фильтрами, и версии компонентов. Приложите ZIP, если короткого сообщения недостаточно; окно само ничего не отправляет. Названия материалов и публичные адреса могут оставаться в отчёте.\n\n"+
            "Почему событий мало или нет\nСтарые записи могли уже не сохраниться. Профиль журнала «Только ошибки» сохраняет меньше подробностей; смена профиля влияет только на будущие события. «Обновить» перечитывает доступные записи и не начинает загрузку. Подробная диагностика не восстанавливает события прошлых запусков."));
        var helpScroll=new ScrollViewer{Content=help,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Visibility=Visibility.Collapsed};detailContent.Children.Add(helpScroll);detailArea.Children.Add(detailContent);
        void ShowHelp(bool visible)
        {
            helpScroll.Visibility=visible?Visibility.Visible:Visibility.Collapsed;_details.Visibility=visible?Visibility.Collapsed:Visibility.Visible;
            showHelp.FontWeight=visible?FontWeights.SemiBold:FontWeights.Normal;showDetails.FontWeight=visible?FontWeights.Normal:FontWeights.SemiBold;
        }
        showDetails.Click+=(_,_)=>ShowHelp(false);showHelp.Click+=(_,_)=>ShowHelp(true);
        Grid.SetRow(detailArea,4);root.Children.Add(detailArea);
        var footer=new StackPanel{Margin=new Thickness(0,7,0,0)};
        _status=Named("JobDiagnosticsStatus",Note("События обновляются автоматически. Отчёт можно приложить к сообщению о проблеме."));footer.Children.Add(_status);
        var buttons=new WrapPanel();
        _refresh=Named("RefreshJobDiagnosticsButton",new Button{Content="Обновить",ToolTip="Перечитать доступные события. Новые записи также появляются автоматически."});
        _copy=Named("CopyJobDiagnosticsButton",new Button{Content="Копировать видимые события",IsEnabled=false,
            ToolTip="Скопировать только показанные строки с учётом поиска, попытки и уровня. Вставьте текст в сообщение о проблеме."});
        _export=Named("ExportJobDiagnosticsButton",new Button{Content="Сохранить отчёт в ZIP…",IsEnabled=false,
            ToolTip="Сохранить все доступные события выбранной загрузки и версии компонентов. Фильтры не ограничивают ZIP. Файл можно приложить к сообщению о проблеме."});
        var close=Named("CloseJobDiagnosticsButton",new Button{Content="Закрыть"});
        foreach(var button in new[]{_refresh,_copy,_export,close})buttons.Children.Add(button);
        footer.Children.Add(buttons);Grid.SetRow(footer,5);root.Children.Add(footer);Content=root;
        _search.TextChanged+=FilterChanged;_attempt.SelectionChanged+=ChoiceChanged;_level.SelectionChanged+=ChoiceChanged;
        _events.SelectionChanged+=(_,_)=>ShowEvent();
        _refresh.Click+=(_,_)=>BeginRefresh(true);_copy.Click+=(_,_)=>CopyVisible();_export.Click+=(_,_)=>BeginExport();close.Click+=(_,_)=>Close();
        PreviewKeyDown+=(_,e)=>
        {
            if(e.Key==Key.Escape){e.Handled=true;Close();}
            else if(e.Key==Key.F && (Keyboard.Modifiers&ModifierKeys.Control)!=0){e.Handled=true;_search.Focus();_search.SelectAll();}
        };
        _timer.Tick+=RefreshTick;Loaded+=(_,_)=>{if(!_closed){BeginRefresh(true);_timer.Start();}};Closed+=(_,_)=>CloseOperations();
    }

    private T Named<T>(string name,T control) where T:FrameworkElement
    {control.Name=name;RegisterName(name,control);return control;}
    private static TextBlock Note(string text)=>new(){Text=text,TextWrapping=TextWrapping.Wrap,
        Foreground=new SolidColorBrush(Color.FromRgb(183,192,216)),Margin=new Thickness(0,3,0,3)};
    private static TextBox ReadOnlyText(double maxHeight)=>new(){IsReadOnly=true,TextWrapping=TextWrapping.Wrap,
        AcceptsReturn=true,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,Margin=new Thickness(0,3,0,3),MaxHeight=maxHeight>0?maxHeight:double.PositiveInfinity};
    private static string LevelLabel(JournalLevel level)=>level switch
    {JournalLevel.Error=>"Ошибка",JournalLevel.Warning=>"Предупреждение",JournalLevel.Information=>"Сведения",JournalLevel.Progress=>"Прогресс",_=>"Подробный вывод"};
    private static string StatusLabel(string status)=>status switch
    {"Queued"=>"В очереди","Running"=>"Выполняется","Starting"=>"Запуск","Completed"=>"Готово","Failed"=>"Ошибка","Interrupted"=>"Прервано","Cancelled"=>"Отменено",_=>status};
    private static string AttemptKey(JobDiagnosticEvent entry)=>string.Join("|",entry.Journal,entry.Session,
        entry.Invocation?.ToString(CultureInfo.InvariantCulture)??"?",entry.Attempt?.ToString(CultureInfo.InvariantCulture)??"?");
    private void ShowError(string error)
    {
        _error.Text=error;_explanation.Text=error.Length>0?DiagnosticPrivacy.Text(ErrorMessages.ForDisplay(error),true):"";
        _error.Visibility=_explanation.Visibility=_rawLabel.Visibility=error.Length>0?Visibility.Visible:Visibility.Collapsed;
    }

    private void RefreshTick(object? sender,EventArgs e)=>BeginRefresh(false);
    private void BeginRefresh(bool force)
    {
        if(_closed || _reading || _exporting || !force && _sequence()==_lastSequence)return;
        _reading=true;
        _readTask=Dispatcher.InvokeAsync(RefreshAsync,DispatcherPriority.Background).Task.Unwrap();
    }
    private async Task RefreshAsync()
    {
        try
        {
            if(_closed)return;
            _refresh.IsEnabled=false;
            var snapshot=await _service.ReadAsync(_target,_stop.Token);
            if(_closed)return;
            _snapshot=snapshot;_lastSequence=snapshot.JournalSequence;
            string selected=_attempt.SelectedValue as string??"*";
            _binding=true;
            try
            {
                var choices=new List<AttemptChoice>{new("*","Все попытки")};
                choices.AddRange(snapshot.Events.GroupBy(AttemptKey,StringComparer.Ordinal)
                    .OrderBy(group=>group.Min(entry=>entry.CapturedAt)).Select(group=>new AttemptChoice(group.Key,
                        group.First().AttemptLabel+" · журнал "+group.First().Journal[..Math.Min(8,group.First().Journal.Length)])));
                _attempt.ItemsSource=choices;_attempt.SelectedValue=choices.Any(choice=>choice.Id==selected)?selected:"*";
            }
            finally{_binding=false;}
            string error=snapshot.Events.LastOrDefault(entry=>entry.Level==JournalLevel.Error)?.Message??_target.Error;
            ShowError(error);
            RebuildView();_export.IsEnabled=true;
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
        catch(Exception error){if(!_closed)_status.Text="Не удалось прочитать диагностику: "+DiagnosticPrivacy.Text(error.Message);}
        finally{_reading=false;if(!_closed)_refresh.IsEnabled=true;}
    }
    private void FilterChanged(object sender,TextChangedEventArgs e)=>RebuildView();
    private void ChoiceChanged(object sender,SelectionChangedEventArgs e){if(!_binding)RebuildView();}
    private void RebuildView()
    {
        if(_closed || _snapshot is null)return;
        string? selected=(_events.SelectedItem as EventRow)?.Key;
        string query=_search.Text.Trim(),attempt=_attempt.SelectedValue as string??"*",level=_level.SelectedValue as string??"*";
        _visible=_snapshot.Events.Where(entry=>(attempt=="*" || AttemptKey(entry)==attempt) &&
            (level=="*" || entry.Level.ToString()==level) && (query.Length==0 ||
                entry.Message.Contains(query,StringComparison.OrdinalIgnoreCase) || entry.Event.Contains(query,StringComparison.OrdinalIgnoreCase) ||
                entry.Worker.Contains(query,StringComparison.OrdinalIgnoreCase) || entry.Session.Contains(query,StringComparison.OrdinalIgnoreCase)))
            .Select(entry=>new EventRow(entry)).ToArray();
        _events.ItemsSource=_visible;_events.SelectedItem=_visible.FirstOrDefault(row=>row.Key==selected)??_visible.LastOrDefault();
        _copy.IsEnabled=_visible.Length>0;
        _empty.Visibility=_visible.Length==0?Visibility.Visible:Visibility.Collapsed;
        _empty.Text=_snapshot.Events.Count==0?
            "Для этой загрузки нет сохранённых событий. Во время загрузки новые записи появятся здесь автоматически; старые могли уже не сохраниться. Подробнее — «Как разобраться».":
            "По выбранным условиям ничего не найдено. Очистите поиск или выберите «Все попытки» и «Все уровни».";
        _summary.Text=$"Показано {_visible.Length} из {_snapshot.Events.Count} событий этого задания. "+
            (_snapshot.Truncated?"Список ограничен. ":"")+
            (_snapshot.OmittedEvents>0?$"Пропущено при ограничениях: {_snapshot.OmittedEvents}. ":"")+
            (_snapshot.MalformedRecords>0?$"Повреждённых записей: {_snapshot.MalformedRecords}. ":"")+
            string.Join(" ",_snapshot.Warnings.Take(8));
        ShowEvent();
    }
    private void ShowEvent()
    {
        _details.Text=_events.SelectedItem is EventRow row?row.Data.DisplayText+
            (row.Data.Settings is {} settings?Environment.NewLine+Environment.NewLine+"Параметры этой попытки: "+settings.DisplayText:""):"Выберите событие для подробностей.";
    }
    private void CopyVisible()
    {
        if(_closed || _visible.Length==0)return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine,_visible.Select(row=>row.Data.DisplayText)));
            _status.Text=$"Скопировано событий: {_visible.Length}.";
        }
        catch(Exception error){_status.Text="Не удалось скопировать: "+DiagnosticPrivacy.Text(error.Message);}
    }
    private void BeginExport()
    {
        if(_closed || _exporting || _snapshot is null)return;
        DiagnosticSaveSelection? destination;
        try{destination=_chooseExport(this);}catch(Exception error){_status.Text="Не удалось выбрать файл: "+DiagnosticPrivacy.Text(error.Message);return;}
        if(destination is null || _closed)return;
        var snapshot=_snapshot;_exporting=true;_export.IsEnabled=false;
        _exportTask=Dispatcher.InvokeAsync(()=>ExportAsync(snapshot,destination),DispatcherPriority.Background).Task.Unwrap();
    }
    private async Task ExportAsync(JobDiagnosticSnapshot snapshot,DiagnosticSaveSelection destination)
    {
        try
        {
            if(_closed)return;
            _status.Text="Собираем выбранное задание и версии компонентов…";
            WorkStateChanged?.Invoke();
            bool overwrite=destination.Overwrite;
            if(!overwrite && await Task.Run(()=>System.IO.File.Exists(destination.Path),_stop.Token))
            {
                if(_closed || _stop.IsCancellationRequested)return;
                if(MessageBox.Show(this,"Этот ZIP уже существует. Заменить его новым пакетом диагностики?",
                    "Сохранить диагностику",MessageBoxButton.YesNo,MessageBoxImage.Question)!=MessageBoxResult.Yes)
                {_status.Text="Сохранение отменено.";return;}
                overwrite=true;
            }
            if(_closed || _stop.IsCancellationRequested)return;
            var result=await _service.ExportAsync(_target,snapshot,destination.Path,_stop.Token,overwrite);
            if(!_closed)_status.Text=$"ZIP сохранён: {result.Events} событий, {DisplayValueConverter.Bytes(result.Bytes)}. "+
                string.Join("; ",result.Components.Select(component=>component.Component+": "+component.Status));
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
        catch(Exception error){if(!_closed)_status.Text="Не удалось сохранить диагностику: "+DiagnosticPrivacy.Text(error.Message);}
        finally{_exporting=false;if(!_closed)_export.IsEnabled=true;WorkStateChanged?.Invoke();}
    }
    private DiagnosticSaveSelection? ChooseExport(Window owner)
    {
        using var picker=new Forms.SaveFileDialog{Title="Сохранить диагностику задания",Filter="Пакет диагностики|*.zip",
            DefaultExt="zip",AddExtension=true,OverwritePrompt=false,
            FileName="VideoDownloader-job-"+_target.Job+"-"+DateTime.Now.ToString("yyyyMMdd-HHmmss",CultureInfo.InvariantCulture)+".zip"};
        return picker.ShowDialog()==Forms.DialogResult.OK?new(picker.FileName):null;
    }
    private void CloseOperations()
    {
        if(_closed)return;_closed=true;_timer.Stop();_timer.Tick-=RefreshTick;
        _search.TextChanged-=FilterChanged;_attempt.SelectionChanged-=ChoiceChanged;_level.SelectionChanged-=ChoiceChanged;
        _stop.Cancel();_=DisposeCancellationAsync();
    }
    private async Task DisposeCancellationAsync()
    {
        try{await PendingOperations;}catch(Exception){ }
        finally{_stop.Dispose();}
    }
}
