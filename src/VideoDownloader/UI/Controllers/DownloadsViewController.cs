using System.Collections;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using Binding=System.Windows.Data.Binding;

namespace VideoDownloader.Native;

/// <summary>Keeps active rows first in the view, without changing session or execution order.</summary>
internal sealed class DownloadsViewController:IDisposable,IComparer
{
    private sealed class RowPosition(long order,bool active)
    {
        internal readonly long Order=order;
        internal bool Active=active;
        internal readonly Dictionary<string,object?> Values=new(StringComparer.Ordinal);
    }
    private sealed record ColumnSort(DataGridColumn Column,PropertyDescriptor Property,ListSortDirection Direction);
    private readonly DataGrid _grid;
    private readonly ObservableCollection<DownloadItem> _rows;
    private readonly ListCollectionView _view;
    private readonly Dictionary<DownloadItem,RowPosition> _positions=[];
    private readonly List<ColumnSort> _sorts=[];
    private readonly Comparer _values=new(CultureInfo.CurrentCulture);
    private long _nextOrder;
    private bool _disposed;

    internal DownloadsViewController(DataGrid grid,ObservableCollection<DownloadItem> rows)
    {
        _grid=grid;_rows=rows;
        foreach(var row in rows)Observe(row);
        _view=new ListCollectionView(rows){CustomSort=this};
        _view.LiveSortingProperties.Add(nameof(DownloadItem.Status));
        _view.IsLiveSorting=true;
        grid.IsSynchronizedWithCurrentItem=false;
        grid.ItemsSource=_view;
        grid.Sorting+=Sorting;
        rows.CollectionChanged+=RowsChanged;
    }

    // Pausing does not release an admitted worker: its Status remains Running.
    // Phase/progress text must never decide whether a row belongs to this group.
    private static bool IsActive(DownloadItem row)=>row.Status is "Running" or "Starting";
    private RowPosition Observe(DownloadItem row)
    {
        if(_positions.TryGetValue(row,out var position))return position;
        position=new RowPosition(_nextOrder++,IsActive(row));
        foreach(var sort in _sorts)position.Values[sort.Property.Name]=sort.Property.GetValue(row);
        _positions.Add(row,position);
        row.PropertyChanged+=RowChanged;
        return position;
    }
    private void RowsChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        if(e.Action==NotifyCollectionChangedAction.Reset)
        {
            foreach(var row in _positions.Keys.Where(row=>!_rows.Contains(row)).ToArray())Forget(row);
            foreach(var row in _rows)Observe(row);
        }
        else
        {
            if(e.OldItems is not null)foreach(DownloadItem row in e.OldItems)
                if(!_rows.Contains(row))Forget(row);
            if(e.NewItems is not null)foreach(DownloadItem row in e.NewItems)Observe(row);
        }
    }
    private void Forget(DownloadItem row)
    {row.PropertyChanged-=RowChanged;_positions.Remove(row);}
    private void RowChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_disposed || sender is not DownloadItem row)return;
        if(!_grid.Dispatcher.CheckAccess())
        {_= _grid.Dispatcher.BeginInvoke(()=>RowChanged(row,e));return;}
        if(!_positions.TryGetValue(row,out var position))return;
        if(string.IsNullOrEmpty(e.PropertyName) || e.PropertyName==nameof(DownloadItem.Status))position.Active=IsActive(row);
        foreach(var sort in _sorts)
            if(string.IsNullOrEmpty(e.PropertyName) || e.PropertyName==sort.Property.Name)
                position.Values[sort.Property.Name]=sort.Property.GetValue(row);
    }
    int IComparer.Compare(object? x,object? y)
    {
        if(_disposed)return 0;
        if(ReferenceEquals(x,y))return 0;
        if(x is not DownloadItem left || y is not DownloadItem right)return 0;
        // Values are captured on the dispatcher, so a worker changing its status
        // cannot change comparison results in the middle of a view operation.
        var a=Observe(left);var b=Observe(right);
        if(a.Active!=b.Active)return a.Active?-1:1;
        foreach(var sort in _sorts)
        {
            int comparison=Math.Sign(_values.Compare(a.Values[sort.Property.Name],b.Values[sort.Property.Name]));
            if(comparison!=0)return sort.Direction==ListSortDirection.Ascending?comparison:-comparison;
        }
        return a.Order.CompareTo(b.Order);
    }
    private void Sorting(object sender,DataGridSortingEventArgs e)
    {
        e.Handled=true;
        if(_disposed || !e.Column.CanUserSort)return;
        string path=e.Column.SortMemberPath;
        if(string.IsNullOrEmpty(path) && e.Column is DataGridBoundColumn {Binding:Binding binding})path=binding.Path?.Path??"";
        var property=TypeDescriptor.GetProperties(typeof(DownloadItem))[path];
        if(property is null)return;
        var direction=e.Column.SortDirection==ListSortDirection.Ascending?ListSortDirection.Descending:ListSortDirection.Ascending;
        if((Keyboard.Modifiers&ModifierKeys.Shift)==0)
        {
            foreach(var sort in _sorts)sort.Column.SortDirection=null;
            _sorts.Clear();
        }
        int index=_sorts.FindIndex(sort=>ReferenceEquals(sort.Column,e.Column));
        var next=new ColumnSort(e.Column,property,direction);
        if(index<0)_sorts.Add(next);else _sorts[index]=next;
        e.Column.SortDirection=direction;

        var selected=_grid.SelectedItem as DownloadItem;
        var current=_grid.CurrentCell;
        var currentItem=_view.CurrentItem as DownloadItem;
        foreach(var pair in _positions)
        {
            pair.Value.Values.Clear();
            foreach(var sort in _sorts)pair.Value.Values[sort.Property.Name]=sort.Property.GetValue(pair.Key);
        }
        using(_view.DeferRefresh())
        {
            _view.LiveSortingProperties.Clear();
            _view.LiveSortingProperties.Add(nameof(DownloadItem.Status));
            foreach(string name in _sorts.Select(sort=>sort.Property.Name).Distinct(StringComparer.Ordinal))
                if(name!=nameof(DownloadItem.Status))_view.LiveSortingProperties.Add(name);
            _view.CustomSort=this;
        }
        if(currentItem is not null && _positions.ContainsKey(currentItem) && !ReferenceEquals(_view.CurrentItem,currentItem))
            _view.MoveCurrentTo(currentItem);
        if(selected is not null && _positions.ContainsKey(selected) && !ReferenceEquals(_grid.SelectedItem,selected))
            _grid.SelectedItem=selected;
        if(current.IsValid && current.Item is DownloadItem currentRow && _positions.ContainsKey(currentRow) && !_grid.CurrentCell.Equals(current))
            _grid.CurrentCell=current;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _grid.Sorting-=Sorting;_rows.CollectionChanged-=RowsChanged;
        _view.IsLiveSorting=false;
        foreach(var row in _positions.Keys)row.PropertyChanged-=RowChanged;
        _positions.Clear();
    }
}
