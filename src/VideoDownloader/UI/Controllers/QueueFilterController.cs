using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;

namespace VideoDownloader.Native;

/// <summary>Filters presentation only. Source order and download admission remain owned by QueueSessionController.</summary>
internal sealed class QueueFilterController:IDisposable
{
    private sealed record Choice(string? Id,string Label);
    private readonly Window _window;
    private readonly DataGrid _grid;
    private readonly ObservableCollection<DownloadItem> _rows;
    private readonly TextBox _query;
    private readonly ComboBox _status,_subscription;
    private readonly Button _reset;
    private readonly TextBlock _count,_empty,_scope;
    private readonly Action _changed;
    private readonly ListCollectionView _view;
    private readonly DispatcherTimer _refresh=new(){Interval=TimeSpan.FromMilliseconds(120)};
    private readonly HashSet<DownloadItem> _observed=[];
    private bool _updatingChoices,_choicesDirty=true,_disposed;
    private string _text="";
    private string? _statusId,_subscriptionId;

    internal bool IsRefreshing {get;private set;}
    internal bool IsFiltering=>_text.Length>0 || _statusId is not null || _subscriptionId is not null;
    internal int VisibleCount=>_view.Count;
    internal bool HasSelection=>_grid.SelectedItems.Cast<DownloadItem>().Any(IncludesRow);

    internal QueueFilterController(Window window,DataGrid grid,ObservableCollection<DownloadItem> rows,
        TextBox query,ComboBox status,ComboBox subscription,Button reset,TextBlock count,
        TextBlock empty,TextBlock scope,Action changed)
    {
        _window=window;_grid=grid;_rows=rows;_query=query;_status=status;_subscription=subscription;
        _reset=reset;_count=count;_empty=empty;_scope=scope;_changed=changed;
        _view=new ListCollectionView(rows){Filter=Includes};
        _grid.IsSynchronizedWithCurrentItem=false;
        _grid.ItemsSource=_view;
        foreach(var combo in new[]{status,subscription})
        {combo.DisplayMemberPath=nameof(Choice.Label);combo.SelectedValuePath=nameof(Choice.Id);}
        query.TextChanged+=QueryChanged;query.KeyDown+=QueryKeyDown;
        status.SelectionChanged+=ChoiceChanged;subscription.SelectionChanged+=ChoiceChanged;
        reset.Click+=ResetClicked;
        rows.CollectionChanged+=RowsChanged;grid.SelectionChanged+=SelectionChanged;
        window.PreviewKeyDown+=WindowKeyDown;
        _refresh.Tick+=RefreshTick;
        foreach(var row in rows)Observe(row);
        Refresh();
    }

    private bool Includes(object candidate)=>candidate is DownloadItem row &&
        (_statusId is null || string.Equals(row.Status,_statusId,StringComparison.Ordinal)) &&
        (_subscriptionId is null || string.Equals(row.SubscriptionId,_subscriptionId,StringComparison.Ordinal)) &&
        (_text.Length==0 || row.Title.Contains(_text,StringComparison.OrdinalIgnoreCase) ||
            row.Url.Contains(_text,StringComparison.OrdinalIgnoreCase));

    internal DownloadItem[] SelectedRows()=>_grid.SelectedItems.Cast<DownloadItem>()
        .Where(row=>_observed.Contains(row) && Includes(row)).Distinct().OrderBy(row=>row.Order).ToArray();
    internal bool IncludesRow(DownloadItem row)=>_observed.Contains(row) && Includes(row);

    private void QueryChanged(object sender,TextChangedEventArgs e)
    {_refresh.Stop();_refresh.Start();}
    private void ChoiceChanged(object sender,SelectionChangedEventArgs e)
    {if(!_updatingChoices)Refresh();}
    private void ResetClicked(object sender,RoutedEventArgs e)=>Reset();
    private void QueryKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.Escape)return;
        Reset();e.Handled=true;
    }
    private void WindowKeyDown(object sender,KeyEventArgs e)
    {
        if(e.Key!=Key.F || Keyboard.Modifiers!=ModifierKeys.Control)return;
        _query.Focus();_query.SelectAll();e.Handled=true;
    }
    private void RefreshTick(object? sender,EventArgs e){_refresh.Stop();Refresh();}
    private void SelectionChanged(object sender,SelectionChangedEventArgs e)
    {if(!IsRefreshing)_changed();}
    private void RowsChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        if(e.Action==NotifyCollectionChangedAction.Reset)
        {
            foreach(var row in _observed)row.PropertyChanged-=RowChanged;
            _observed.Clear();
            foreach(var row in _rows)Observe(row);
        }
        else
        {
            if(e.OldItems is not null)foreach(DownloadItem row in e.OldItems)
            {row.PropertyChanged-=RowChanged;_observed.Remove(row);}
            if(e.NewItems is not null)foreach(DownloadItem row in e.NewItems)Observe(row);
        }
        _choicesDirty=true;
        if(!_refresh.IsEnabled)_refresh.Start();
    }
    private void Observe(DownloadItem row)
    {if(_observed.Add(row))row.PropertyChanged+=RowChanged;}
    private void RowChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_disposed)return;
        if(e.PropertyName is nameof(DownloadItem.SubscriptionId) or nameof(DownloadItem.SubscriptionName))_choicesDirty=true;
        bool relevant=_choicesDirty || e.PropertyName==nameof(DownloadItem.Title) && _text.Length>0 ||
            e.PropertyName==nameof(DownloadItem.Url) && _text.Length>0 ||
            e.PropertyName==nameof(DownloadItem.Status) && _statusId is not null;
        if(relevant && !_refresh.IsEnabled)_refresh.Start();
    }

    // Called after a queue batch or an explicit presentation refresh. Selection
    // restoration never invokes the preview/network action for a surviving row.
    internal void Refresh(bool rebuildChoices=false)
    {
        if(_disposed || IsRefreshing)return;
        _refresh.Stop();_choicesDirty|=rebuildChoices;
        IsRefreshing=true;
        try
        {
            if(_choicesDirty)RefreshChoices();
            _text=_query.Text.Trim();
            _statusId=(_status.SelectedItem as Choice)?.Id;
            _subscriptionId=(_subscription.SelectedItem as Choice)?.Id;
            var selected=_grid.SelectedItems.Cast<DownloadItem>().ToArray();
            var anchor=_grid.SelectedItem as DownloadItem;
            _view.Refresh();
            var retained=selected.Where(IncludesRow).ToHashSet();
            foreach(var row in _grid.SelectedItems.Cast<DownloadItem>().Where(row=>!retained.Contains(row)).ToArray())
                _grid.SelectedItems.Remove(row);
            var remaining=_grid.SelectedItems.Cast<DownloadItem>().ToHashSet();
            foreach(var row in selected.Where(row=>retained.Contains(row) && !remaining.Contains(row)))_grid.SelectedItems.Add(row);
            if(anchor is not null && retained.Contains(anchor) && !ReferenceEquals(_grid.SelectedItem,anchor))_grid.SelectedItem=anchor;
        }
        finally {IsRefreshing=false;}
        RefreshSummary();_changed();
    }
    private void RefreshChoices()
    {
        _updatingChoices=true;
        try
        {
            var previousStatus=_status.SelectedItem as Choice;
            var statuses=new List<Choice>{new(null,ErrorMessages.Text("Все статусы","All statuses"))};
            string[] standard=["Queued","Starting","Running","Completed","Failed","Interrupted","Cancelled"];
            foreach(string id in standard.Concat(_rows.Select(row=>row.Status)).Distinct(StringComparer.Ordinal))
                statuses.Add(new(id,StatusLabel(id)));
            if(previousStatus?.Id is {} oldStatus && statuses.All(choice=>choice.Id!=oldStatus))statuses.Add(previousStatus);
            _status.ItemsSource=statuses;
            _status.SelectedItem=statuses.FirstOrDefault(choice=>choice.Id==previousStatus?.Id)??statuses[0];

            var previousSubscription=_subscription.SelectedItem as Choice;
            var sources=new List<Choice>{new(null,ErrorMessages.Text("Все подписки","All subscriptions")),
                new("",ErrorMessages.Text("Без подписки","No subscription"))};
            foreach(var group in _rows.Where(row=>row.SubscriptionId.Length>0).GroupBy(row=>row.SubscriptionId,StringComparer.Ordinal)
                .OrderBy(group=>group.First().SubscriptionName,StringComparer.OrdinalIgnoreCase).ThenBy(group=>group.Key,StringComparer.Ordinal))
            {
                string name=group.Select(row=>row.SubscriptionName).FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value))??
                    ErrorMessages.Text("Подписка","Subscription");
                string suffix=group.Key.Length<=8?group.Key:group.Key[..8];
                sources.Add(new(group.Key,name+" · "+suffix));
            }
            if(previousSubscription?.Id is {} oldSource && sources.All(choice=>choice.Id!=oldSource))sources.Add(previousSubscription);
            _subscription.ItemsSource=sources;
            _subscription.SelectedItem=sources.FirstOrDefault(choice=>choice.Id==previousSubscription?.Id)??sources[0];
            _choicesDirty=false;
        }
        finally {_updatingChoices=false;}
    }
    private static string StatusLabel(string status)=>status switch
    {
        "Queued"=>ErrorMessages.Text("В очереди","Queued"),"Starting"=>ErrorMessages.Text("Запуск","Starting"),
        "Running"=>ErrorMessages.Text("Выполняется","Running"),"Completed"=>ErrorMessages.Text("Готово","Completed"),
        "Failed"=>ErrorMessages.Text("Ошибка","Failed"),"Interrupted"=>ErrorMessages.Text("Прервано","Interrupted"),
        "Cancelled"=>ErrorMessages.Text("Отменено","Cancelled"),_=>status
    };
    internal void Reset()
    {
        if(_disposed)return;
        _updatingChoices=true;
        try {_query.Clear();_status.SelectedIndex=0;_subscription.SelectedIndex=0;}
        finally {_updatingChoices=false;}
        Refresh();
    }
    internal void RefreshSummary()
    {
        if(_disposed)return;
        _count.Text=ErrorMessages.Text($"Показано {_view.Count} из {_rows.Count}",$"Showing {_view.Count} of {_rows.Count}");
        _empty.Visibility=_rows.Count>0 && _view.Count==0?Visibility.Visible:Visibility.Collapsed;
        _empty.Text=ErrorMessages.Text("Ничего не найдено.\nИзмените поиск или сбросьте фильтры.","No matching rows.\nChange the search or reset filters.");
        _reset.IsEnabled=IsFiltering || _query.Text.Length>0;
        _scope.Text=IsFiltering
            ? ErrorMessages.Text("Фильтры меняют только список. Start и очистка действуют на всю очередь, включая скрытые строки.",
                "Filters change this list only. Start and Clear all apply to the whole queue, including hidden rows.")
            : ErrorMessages.Text("Поиск по названию или URL · Ctrl+F. Esc сбрасывает фильтры.","Search title or URL · Ctrl+F. Esc resets filters.");
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;_refresh.Stop();
        _refresh.Tick-=RefreshTick;_query.TextChanged-=QueryChanged;_query.KeyDown-=QueryKeyDown;
        _status.SelectionChanged-=ChoiceChanged;_subscription.SelectionChanged-=ChoiceChanged;
        _reset.Click-=ResetClicked;_rows.CollectionChanged-=RowsChanged;_grid.SelectionChanged-=SelectionChanged;
        _window.PreviewKeyDown-=WindowKeyDown;
        foreach(var row in _observed)row.PropertyChanged-=RowChanged;
        _observed.Clear();
    }
}
