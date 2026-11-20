using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

// Only column presentation follows the current view. Source order, filtering,
// sorting and the selected task remain owned by their existing controllers.
internal sealed class SubscriptionColumnController:IDisposable
{
    private readonly DataGrid _grid;
    private readonly DataGridColumn _column;
    private readonly HashSet<DownloadItem> _observed=[];
    private readonly HashSet<DownloadItem> _labeled=[];
    private bool _disposed;

    internal SubscriptionColumnController(DataGrid grid,DataGridColumn column)
    {
        _grid=grid;_column=column;
        ((INotifyCollectionChanged)grid.Items).CollectionChanged+=RowsChanged;
        Rebuild();Refresh();
    }
    private void Rebuild()
    {
        foreach(var row in _observed)row.PropertyChanged-=RowChanged;
        _observed.Clear();_labeled.Clear();
        foreach(var row in _grid.Items.OfType<DownloadItem>())Observe(row);
    }
    private void Observe(DownloadItem row)
    {
        if(!_observed.Add(row))return;
        row.PropertyChanged+=RowChanged;UpdateLabel(row);
    }
    private void UpdateLabel(DownloadItem row)
    {
        if(string.IsNullOrWhiteSpace(row.SubscriptionName))_labeled.Remove(row);
        else _labeled.Add(row);
    }
    private void RowsChanged(object? sender,NotifyCollectionChangedEventArgs e)
    {
        if(_disposed || e.Action==NotifyCollectionChangedAction.Move)return;
        if(e.Action==NotifyCollectionChangedAction.Reset)Rebuild();
        else
        {
            if(e.OldItems is not null)foreach(DownloadItem row in e.OldItems)
            {row.PropertyChanged-=RowChanged;_observed.Remove(row);_labeled.Remove(row);}
            if(e.NewItems is not null)foreach(DownloadItem row in e.NewItems)Observe(row);
        }
        Refresh();
    }
    private void RowChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(_disposed || sender is not DownloadItem row ||
            !string.IsNullOrEmpty(e.PropertyName) && e.PropertyName!=nameof(DownloadItem.SubscriptionName))return;
        if(!_grid.Dispatcher.CheckAccess())
        {
            if(!_grid.Dispatcher.HasShutdownStarted)_=_grid.Dispatcher.BeginInvoke(()=>RowChanged(row,e));
            return;
        }
        if(!_observed.Contains(row))return;
        UpdateLabel(row);Refresh();
    }
    private void Refresh()=>_column.Visibility=_labeled.Count>0?Visibility.Visible:Visibility.Collapsed;
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        ((INotifyCollectionChanged)_grid.Items).CollectionChanged-=RowsChanged;
        foreach(var row in _observed)row.PropertyChanged-=RowChanged;
        _observed.Clear();_labeled.Clear();
    }
}
