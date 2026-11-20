using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Binding=System.Windows.Data.Binding;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed class HistoryFileRow(string path,HistoryFileAvailability availability):INotifyPropertyChanged
{
    private HistoryFileAvailability _availability=availability;
    public string Path { get; }=path;
    public string StateLabel=>_availability.Explanation;
    public HistoryFileAvailability Availability=>_availability;
    internal void Set(HistoryFileAvailability value)
    {_availability=value;PropertyChanged?.Invoke(this,new(nameof(StateLabel)));PropertyChanged?.Invoke(this,new(nameof(Availability)));}
    public event PropertyChangedEventHandler? PropertyChanged;
}
internal sealed class HistoryRecordRow(DownloadHistoryEntry entry):INotifyPropertyChanged
{
    public DownloadHistoryEntry Entry { get; }=entry;
    public string Title=>Entry.DisplayTitle;
    public string Url=>Entry.Url;
    public string Date=>Entry.DisplayDate;
    public string Source=>Entry.DisplaySource;
    public string FileCount=>Entry.DisplayFileCount;
    private string _availability=entry.OutputPaths.Count==0?"Пути не записаны":"Не проверено";
    public string Availability=>_availability;
    internal void SetAvailability(string value)
    {_availability=value;PropertyChanged?.Invoke(this,new(nameof(Availability)));}
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>A read-only library. File checks and history reads never run on the dispatcher.</summary>
internal sealed class DownloadHistoryWindow:Window
{
    private readonly AppPersistence _storage;
    private readonly HistoryFileAccess _files;
    private readonly Action<HistoryOpenCommand,CancellationToken> _open;
    private readonly CancellationTokenSource _stop=new();
    private CancellationTokenSource? _load,_checking;
    private IReadOnlyList<HistoryRecordRow> _rows=[];
    private readonly Dictionary<string,HistoryFileAvailability> _knownFiles=new(StringComparer.OrdinalIgnoreCase);
    private readonly DataGrid _grid,_fileGrid;
    private readonly TextBox _search;
    private readonly TextBlock _status,_selectionStatus,_emptyStateText;
    private readonly FrameworkElement _emptyState;
    private readonly Button _check,_cancelCheck,_openFile,_openFolder,_clearSearch;
    private long _generation;
    private bool _closed,_opening;
    internal IReadOnlyCollection<HistoryRecordRow> Rows=>_rows;

    internal DownloadHistoryWindow(Window owner,AppPersistence storage,HistoryFileAccess? files=null,Action<HistoryOpenCommand>? open=null)
    {
        _storage=storage;_files=files??new HistoryFileAccess();
        _open=open is null?HistoryFileLauncher.Open:(command,token)=>{token.ThrowIfCancellationRequested();open(command);};
        Name="DownloadHistoryWindow";Title="История загрузок";Owner=owner;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Width=Math.Min(1120,Math.Max(660,SystemParameters.WorkArea.Width-48));
        Height=Math.Min(760,Math.Max(440,SystemParameters.WorkArea.Height-48));
        MinWidth=660;MinHeight=440;
        WindowThemeController.Inherit(this,owner);NameScope.SetNameScope(this,new NameScope());
        var layout=new Grid{Margin=new Thickness(16)};
        foreach(var height in new[]{GridLength.Auto,GridLength.Auto,GridLength.Auto,new GridLength(1,GridUnitType.Star),
            GridLength.Auto,GridLength.Auto,GridLength.Auto})layout.RowDefinitions.Add(new RowDefinition{Height=height});
        Content=layout;
        void Add(UIElement element,int row){Grid.SetRow(element,row);layout.Children.Add(element);}
        Add(new TextBlock{Text="История подтверждённых загрузок",FontSize=19,FontWeight=FontWeights.SemiBold,Margin=new Thickness(0,0,0,6)},0);
        Add(Note("История сохраняет факт успеха, даже если файл перемещён или удалён. Старые записи могут не содержать название или пути. Просмотр и поиск ничего не скачивают."),1);
        var searchPanel=new DockPanel{Margin=new Thickness(0,7,0,7)};
        var reload=Named("ReloadHistoryButton",new Button{Content="Обновить историю",Margin=new Thickness(8,0,0,0)});
        DockPanel.SetDock(reload,Dock.Right);searchPanel.Children.Add(reload);
        _search=Named("HistorySearchBox",new TextBox{ToolTip="Название, URL, источник или дата ГГГГ-ММ-ДД",MinHeight=30,MaxLength=4096});
        searchPanel.Children.Add(_search);Add(searchPanel,2);
        _grid=Named("HistoryGrid",new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
            EnableRowVirtualization=true,SelectionMode=DataGridSelectionMode.Single,ItemsSource=_rows});
        Column(_grid,"Дата","Date",145);Column(_grid,"Название","Title",new DataGridLength(2,DataGridLengthUnitType.Star));
        Column(_grid,"Источник","Source",130);Column(_grid,"URL","Url",new DataGridLength(2,DataGridLengthUnitType.Star));
        Column(_grid,"Файлы","FileCount",125);Column(_grid,"Доступность","Availability",160);
        var results=new Grid();results.Children.Add(_grid);Add(results,3);
        var emptyContent=new StackPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Center,
            VerticalAlignment=System.Windows.VerticalAlignment.Center,MaxWidth=440,Margin=new Thickness(20)};
        _emptyStateText=Named("HistoryEmptyStateText",Note(""));
        _emptyStateText.TextAlignment=System.Windows.TextAlignment.Center;emptyContent.Children.Add(_emptyStateText);
        _clearSearch=Named("ClearHistorySearchButton",new Button{Content="Сбросить поиск",
            HorizontalAlignment=System.Windows.HorizontalAlignment.Center,Margin=new Thickness(0,8,0,0)});
        emptyContent.Children.Add(_clearSearch);
        _emptyState=Named("HistoryEmptyState",new Border{Child=emptyContent,Visibility=Visibility.Collapsed});
        results.Children.Add(_emptyState);
        var fileDetails=new StackPanel{Margin=new Thickness(0,6,0,0)};
        _selectionStatus=Named("HistorySelectionStatus",Note("Выберите запись, чтобы увидеть сохранённые пути."));fileDetails.Children.Add(_selectionStatus);
        _fileGrid=Named("HistoryFilesGrid",new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,CanUserAddRows=false,CanUserDeleteRows=false,
            EnableRowVirtualization=true,SelectionMode=DataGridSelectionMode.Single,MinHeight=75,Height=120});
        Column(_fileGrid,"Сохранённый путь","Path",new DataGridLength(1,DataGridLengthUnitType.Star));Column(_fileGrid,"Проверка","StateLabel",230);fileDetails.Children.Add(_fileGrid);
        var actions=new WrapPanel{Margin=new Thickness(0,9,0,4)};
        _check=Named("CheckHistoryFilesButton",new Button{Content="Проверить файлы записи"});
        _cancelCheck=Named("CancelHistoryFileCheckButton",new Button{Content="Отменить проверку"});
        _openFile=Named("OpenHistoryFileButton",new Button{Content="Открыть файл"});
        _openFolder=Named("OpenHistoryFolderButton",new Button{Content="Открыть папку"});
        var copy=Named("CopyHistoryPathButton",new Button{Content="Копировать путь"});
        var close=Named("CloseHistoryButton",new Button{Content="Закрыть"});
        foreach(var button in new[]{_check,_cancelCheck,_openFile,_openFolder,copy,close,_clearSearch})
        {
            if(TryFindResource("GuardedActionButton") is Style guarded)button.Style=guarded;
        }
        foreach(var button in new[]{_check,_openFile,_openFolder,copy})actions.Children.Add(button);
        fileDetails.Children.Add(actions);
        var fileScroll=Named("HistoryFilesScroll",new ScrollViewer{Content=fileDetails,
            VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,
            MaxHeight=Math.Clamp(Height*.3,100,240)});
        var fileExpander=Named("HistoryFilesExpander",new Expander{Header="Файлы выбранной загрузки",IsExpanded=false,
            HorizontalContentAlignment=System.Windows.HorizontalAlignment.Stretch,Content=fileScroll,Margin=new Thickness(0,8,0,0),
            ToolTip="Сохранённые пути выбранной записи и действия с файлами. Просмотр не меняет историю."});
        Add(fileExpander,4);
        // Cancellation stays reachable even if the user closes the file group
        // while a bounded filesystem probe is still running.
        var footer=new WrapPanel{HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,9,0,4)};
        footer.Children.Add(_cancelCheck);footer.Children.Add(close);Add(footer,5);
        _status=Named("HistoryStatusText",Note("Читаем историю…"));Add(_status,6);
        SizeChanged+=(_,_)=>fileScroll.MaxHeight=Math.Clamp(ActualHeight*.3,100,240);
        _search.TextChanged+=(_,_)=>QueueLoad(true);
        _clearSearch.Click+=(_,_)=>{if(!_closed){_search.Clear();_search.Focus();}};
        reload.Click+=(_,_)=>QueueLoad(false);
        _grid.SelectionChanged+=(_,_)=>SelectRecord();
        _fileGrid.SelectionChanged+=(_,_)=>RefreshActions();
        _check.Click+=async(_,_)=>await CheckSelectedAsync();
        _cancelCheck.Click+=(_,_)=>_checking?.Cancel();
        _openFile.Click+=async(_,_)=>await OpenSelectedAsync(false);
        _openFolder.Click+=async(_,_)=>await OpenSelectedAsync(true);
        copy.Click+=(_,_)=>
        {
            if(_closed || _fileGrid.SelectedItem is not HistoryFileRow file)return;
            try{Clipboard.SetText(file.Path);}
            catch(Exception error){_status.Text="Не удалось скопировать путь: "+Display(error.Message);}
        };
        close.Click+=(_,_)=>Close();
        Loaded+=(_,_)=>QueueLoad(false);
        Closed+=(_,_)=>{_closed=true;_generation++;_stop.Cancel();_load?.Cancel();_checking?.Cancel();};
        RefreshActions();
        OwnedWindowLayout.Attach(this,owner);
    }
    private T Named<T>(string name,T control) where T:FrameworkElement
    {control.Name=name;RegisterName(name,control);return control;}
    private static TextBlock Note(string text)
    {
        var note=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};
        note.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryTextBrush");return note;
    }
    private void Column(DataGrid grid,string header,string property,DataGridLength width)
    {
        var column=new DataGridTextColumn{Header=header,Binding=new Binding(property),Width=width};
        if(TryFindResource("GridTextStyle") is Style style)column.ElementStyle=style;
        grid.Columns.Add(column);
    }
    private void QueueLoad(bool debounce)
    {
        if(_closed)return;
        _generation++;_load?.Cancel();_checking?.Cancel();
        var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);_load=cancellation;
        string query=_search.Text;_status.Text="Читаем историю…";
        // The preceding query's empty state is not a result for this request.
        // Keep loading and read failures distinct from a successfully empty list.
        _emptyState.Visibility=Visibility.Collapsed;
        _=LoadAsync(query,_generation,cancellation,debounce);
    }
    private async Task LoadAsync(string query,long generation,CancellationTokenSource cancellation,bool debounce)
    {
        try
        {
            if(debounce)await Task.Delay(300,cancellation.Token);
            var library=await Task.Run(()=>_storage.ReadHistoryLibrary(query,cancellationToken:cancellation.Token),cancellation.Token);
            if(_closed || cancellation.IsCancellationRequested || generation!=_generation)return;
            // Replace the bounded snapshot once instead of sending thousands
            // of individual collection notifications through the DataGrid.
            _rows=library.Entries.Select(entry=>new HistoryRecordRow(entry)).ToArray();
            _grid.ItemsSource=_rows;
            _grid.SelectedItem=_rows.FirstOrDefault();
            _status.Text=$"Найдено записей: {library.MatchCount} из {library.TotalCount}."+
                (library.Truncated?$" Показаны последние {_rows.Count}; уточните поиск для более ранних записей.":"");
            bool noMatches=query.Trim().Length>0;
            _emptyStateText.Text=noMatches?"Поиск не нашёл записей.":"История пока пуста.";
            _clearSearch.Visibility=noMatches?Visibility.Visible:Visibility.Collapsed;
            _emptyState.Visibility=_rows.Count==0?Visibility.Visible:Visibility.Collapsed;
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested) { }
        catch(Exception error)
        {if(!_closed && generation==_generation)_status.Text="Не удалось прочитать историю; прежний снимок сохранён. "+Display(error.Message);}
        finally
        {
            if(ReferenceEquals(_load,cancellation))_load=null;
            cancellation.Dispose();
        }
    }
    private void SelectRecord()
    {
        _checking?.Cancel();
        if(_grid.SelectedItem is not HistoryRecordRow row)
        {_fileGrid.ItemsSource=null;_selectionStatus.Text="Выберите запись.";RefreshActions();return;}
        var files=row.Entry.OutputPaths.Select(path=>new HistoryFileRow(path,_knownFiles.GetValueOrDefault(path)??HistoryFileAvailability.Unknown)).ToArray();
        _fileGrid.ItemsSource=files;_fileGrid.SelectedItem=files.FirstOrDefault();
        _selectionStatus.Text=files.Length==0?"В этой прежней записи пути не сохранены. Факт успешной загрузки остаётся в истории.":
            "Пути служат для поиска результата и не дают права удалять файлы."+(row.Entry.OutputPathsTruncated?" Список путей сокращён.":"");
        RefreshActions();
    }
    private void RefreshActions()
    {
        if(_closed)return;
        bool hasPaths=_grid.SelectedItem is HistoryRecordRow row && row.Entry.OutputPaths.Count>0;
        bool safe=_fileGrid.SelectedItem is HistoryFileRow file && HistoryFileAccess.TryNormalize(file.Path,out _);
        _check.IsEnabled=hasPaths && _checking is null;
        _cancelCheck.IsEnabled=_checking is not null;
        _cancelCheck.Visibility=_checking is not null?Visibility.Visible:Visibility.Collapsed;
        _openFile.IsEnabled=safe && !_opening;
        _openFolder.IsEnabled=safe && !_opening;
        ((Button)FindName("CopyHistoryPathButton")).IsEnabled=_fileGrid.SelectedItem is HistoryFileRow;
    }
    private async Task CheckSelectedAsync()
    {
        if(_closed || _checking is not null || _grid.SelectedItem is not HistoryRecordRow row ||
            _fileGrid.ItemsSource is not HistoryFileRow[] files || files.Length==0)return;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(25));_checking=cancellation;RefreshActions();
        int checkedCount=0;
        try
        {
            await Task.WhenAll(files.Select(async file=>
            {
                var state=await _files.CheckAsync(file.Path,false,cancellation.Token);
                if(_closed || cancellation.IsCancellationRequested)return;
                file.Set(state);_knownFiles[file.Path]=state;
                checkedCount++;
                row.SetAvailability($"Проверено: {checkedCount}/{files.Length}");
            }));
            if(!_closed && !cancellation.IsCancellationRequested)
            {
                int present=files.Count(file=>file.Availability.State==HistoryFileState.Present);
                int missing=files.Count(file=>file.Availability.State==HistoryFileState.Missing);
                row.SetAvailability($"Доступно {present}; отсутствует {missing}; прочее {files.Length-present-missing}");
                _status.Text="Проверка файлов завершена. История успехов не изменена.";
            }
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested)
        {if(!_closed)_status.Text="Проверка остановлена; уже полученные сведения сохранены.";}
        catch(Exception error){if(!_closed)_status.Text="Проверка недоступна: "+Display(error.Message);}
        finally
        {
            if(ReferenceEquals(_checking,cancellation))_checking=null;
            if(_knownFiles.Count>10000)_knownFiles.Clear();
            RefreshActions();
        }
    }
    private async Task OpenSelectedAsync(bool folder)
    {
        if(_closed || _opening || _fileGrid.SelectedItem is not HistoryFileRow file ||
            !HistoryFileAccess.TryNormalize(file.Path,out string path))return;
        _opening=true;RefreshActions();
        try
        {
            var availability=await _files.CheckAsync(path,folder,_stop.Token);
            if(_closed || !ReferenceEquals(_fileGrid.SelectedItem,file))return;
            if(!folder){file.Set(availability);_knownFiles[file.Path]=availability;}
            if(availability.State!=HistoryFileState.Present)
            {_status.Text="Не удалось открыть: "+availability.Explanation;return;}
            await Task.Run(()=>_open(new(folder?HistoryOpenKind.Folder:HistoryOpenKind.Media,path),_stop.Token),_stop.Token);
            if(!_closed)_status.Text=folder?"Папка открыта.":"Файл открыт.";
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested) { }
        catch(Exception error){if(!_closed)_status.Text="Не удалось открыть: "+Display(error.Message);}
        finally{_opening=false;RefreshActions();}
    }
    private static string Display(string message)
    {
        string text=JournalSanitizer.Redact(ErrorMessages.ForDisplay(message),multiline:true);
        return text.Length>1024?text[..1024]:text;
    }
}
