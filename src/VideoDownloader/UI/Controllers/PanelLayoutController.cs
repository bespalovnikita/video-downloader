using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using TabControl=System.Windows.Controls.TabControl;

namespace VideoDownloader.Native;

// Saved widths describe the wide layout. Compact navigation never replaces
// those preferences with the zero-width columns used to show one panel.
internal sealed class PanelLayoutController : IDisposable
{
    internal const double DefaultQueueWidth=380d*4/3;
    internal const double DefaultPreviewWidth=390;
    private const double PanelMinimum=300;
    private readonly Window _window;
    private readonly Grid _grid;
    private readonly ColumnDefinition _queue,_downloads,_preview;
    private readonly FrameworkElement _queuePanel,_downloadsPanel,_previewPanel,_navigation;
    private readonly GridSplitter _leftSplitter,_rightSplitter;
    private readonly Button _showQueue,_showDownloads,_showDetails,_toggleDetails,_openOptions;
    private readonly TabControl _detailsTabs;
    private readonly TabItem _queueOptions;
    private readonly Action _save;
    private bool _adjusting,_compact,_detailsVisible=true,_disposed;
    private int _compactPanel;
    private double _queueWidth=DefaultQueueWidth,_previewWidth=DefaultPreviewWidth;
    private ColumnDefinition? _dragColumn;
    private double _dragMaximum;

    internal PanelLayoutController(Window window,Action save)
    {
        T Control<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing layout control: "+name));
        _window=window;_grid=Control<Grid>("MainPanelsGrid");
        _queue=Control<ColumnDefinition>("QueuePanelColumn");
        _downloads=Control<ColumnDefinition>("DownloadsPanelColumn");
        _preview=Control<ColumnDefinition>("PreviewPanelColumn");
        _queuePanel=Control<FrameworkElement>("QueuePanel");
        _downloadsPanel=Control<FrameworkElement>("DownloadsPanel");
        _previewPanel=Control<FrameworkElement>("PreviewPanel");
        _navigation=Control<FrameworkElement>("CompactNavigation");
        _leftSplitter=Control<GridSplitter>("QueueDownloadsSplitter");
        _rightSplitter=Control<GridSplitter>("DownloadsPreviewSplitter");
        _showQueue=Control<Button>("ShowQueuePanelButton");
        _showDownloads=Control<Button>("ShowDownloadsPanelButton");
        _showDetails=Control<Button>("ShowDetailsPanelButton");
        _toggleDetails=Control<Button>("ToggleDetailsPanelButton");
        _openOptions=Control<Button>("OpenQueueOptionsButton");
        _detailsTabs=Control<TabControl>("DetailsTabs");
        _queueOptions=Control<TabItem>("QueueOptionsTab");
        _save=save;
        _leftSplitter.DragStarted+=DragStarted;_rightSplitter.DragStarted+=DragStarted;
        _leftSplitter.DragCompleted+=DragCompleted;_rightSplitter.DragCompleted+=DragCompleted;
        _grid.SizeChanged+=GridSizeChanged;
        _showQueue.Click+=ShowQueue;_showDownloads.Click+=ShowDownloads;_showDetails.Click+=ShowDetails;
        _toggleDetails.Click+=ToggleDetails;_openOptions.Click+=OpenOptions;
        _window.SourceInitialized+=SourceInitialized;
        // Registered before the queue filter controller: its existing Ctrl+F
        // handler must receive a visible text box before it requests focus.
        _window.PreviewKeyDown+=RevealQueueForSearch;
    }

    internal double QueueWidth=>_queueWidth;
    internal double PreviewWidth=>_previewWidth;
    internal bool IsCompact=>_compact;

    internal void Apply(double queueWidth,double previewWidth)
    {
        _queueWidth=Valid(queueWidth)?queueWidth:DefaultQueueWidth;
        _previewWidth=Valid(previewWidth)?previewWidth:DefaultPreviewWidth;
        ArrangePanels();
    }
    private static bool Valid(double value)=>double.IsFinite(value) && value>=PanelMinimum && value<=10_000;
    private void SourceInitialized(object? sender,EventArgs e)
    {
        // WPF work-area values use the same logical units as Window dimensions.
        var area=SystemParameters.WorkArea;
        if(area.Width<=0 || area.Height<=0)return;
        _window.MinWidth=Math.Min(_window.MinWidth,area.Width);
        _window.MinHeight=Math.Min(_window.MinHeight,area.Height);
        if(_window.WindowState==WindowState.Normal)
        {_window.Width=Math.Min(_window.Width,area.Width);_window.Height=Math.Min(_window.Height,area.Height);}
    }
    private void GridSizeChanged(object sender,SizeChangedEventArgs e)=>ArrangePanels();
    private void ShowQueue(object sender,RoutedEventArgs e)=>SelectCompactPanel(0);
    private void ShowDownloads(object sender,RoutedEventArgs e)=>SelectCompactPanel(1);
    private void ShowDetails(object sender,RoutedEventArgs e)=>SelectCompactPanel(2);
    private void RevealQueueForSearch(object sender,System.Windows.Input.KeyEventArgs e)
    {
        if(e.Key==System.Windows.Input.Key.F && System.Windows.Input.Keyboard.Modifiers==System.Windows.Input.ModifierKeys.Control)
            SelectCompactPanel(0);
    }
    private void SelectCompactPanel(int panel)
    {
        _compactPanel=panel;ArrangePanels();
        (_compactPanel==0?_queuePanel:_compactPanel==1?_downloadsPanel:_previewPanel).MoveFocus(
            new System.Windows.Input.TraversalRequest(System.Windows.Input.FocusNavigationDirection.First));
    }
    private void ToggleDetails(object sender,RoutedEventArgs e)
    {_detailsVisible=!_detailsVisible;ArrangePanels();}
    private void OpenOptions(object sender,RoutedEventArgs e)
    {
        _detailsVisible=true;_compactPanel=2;_detailsTabs.SelectedItem=_queueOptions;ArrangePanels();
        _queueOptions.Focus();
    }

    private void ArrangePanels()
    {
        if(_disposed || _adjusting || _grid.ActualWidth<=0)return;
        _adjusting=true;
        try
        {
            RestoreDragMaximum();
            _compact=_grid.ActualWidth<1160 || _grid.ActualHeight<460;
            _navigation.Visibility=_compact?Visibility.Visible:Visibility.Collapsed;
            _toggleDetails.Visibility=_compact?Visibility.Collapsed:Visibility.Visible;
            _toggleDetails.Content=_detailsVisible?"Скрыть правую панель":"Показать правую панель";
            _showQueue.FontWeight=_compactPanel==0?FontWeights.Bold:FontWeights.Normal;
            _showDownloads.FontWeight=_compactPanel==1?FontWeights.Bold:FontWeights.Normal;
            _showDetails.FontWeight=_compactPanel==2?FontWeights.Bold:FontWeights.Normal;
            var panels=new[]{_queuePanel,_downloadsPanel,_previewPanel};
            for(int i=0;i<panels.Length;i++)
            {
                Grid.SetColumn(panels[i],_compact?0:i*2);
                Grid.SetColumnSpan(panels[i],_compact?5:1);
                panels[i].Visibility=(_compact?i==_compactPanel:i!=2 || _detailsVisible)?Visibility.Visible:Visibility.Collapsed;
            }
            _leftSplitter.Visibility=_compact?Visibility.Collapsed:Visibility.Visible;
            _rightSplitter.Visibility=_compact || !_detailsVisible?Visibility.Collapsed:Visibility.Visible;
            foreach(var column in _grid.ColumnDefinitions)column.MinWidth=0;
            if(_compact)
            {
                for(int i=0;i<_grid.ColumnDefinitions.Count;i++)
                    _grid.ColumnDefinitions[i].Width=i==0?new GridLength(1,GridUnitType.Star):new GridLength(0);
                return;
            }
            _queue.MinWidth=_downloads.MinWidth=PanelMinimum;
            _preview.MinWidth=_detailsVisible?PanelMinimum:0;
            _grid.ColumnDefinitions[1].Width=new GridLength(12);
            _grid.ColumnDefinitions[3].Width=new GridLength(_detailsVisible?12:0);
            _queue.Width=new GridLength(_queueWidth);
            _downloads.Width=new GridLength(1,GridUnitType.Star);
            _preview.Width=new GridLength(_detailsVisible?_previewWidth:0);
            FitToWindow();
        }
        finally {_adjusting=false;}
    }

    private void DragStarted(object sender,DragStartedEventArgs e)
    {
        if(_compact)return;
        RestoreDragMaximum();
        var active=ReferenceEquals(sender,_leftSplitter)?_queue:_preview;
        var other=ReferenceEquals(active,_queue)?_preview:_queue;
        double gaps=_grid.ColumnDefinitions[1].ActualWidth+_grid.ColumnDefinitions[3].ActualWidth;
        double available=_grid.ActualWidth-gaps-other.ActualWidth-_downloads.MinWidth;
        _dragColumn=active;_dragMaximum=active.MaxWidth;
        active.MaxWidth=Math.Min(_dragMaximum,Math.Max(active.MinWidth,available));
    }
    private void DragCompleted(object sender,DragCompletedEventArgs e)
    {
        if(_compact)return;
        RestoreDragMaximum();
        _queueWidth=_queue.Width.Value;
        if(_detailsVisible)_previewWidth=_preview.Width.Value;
        FitToWindow();_save();
    }
    private void RestoreDragMaximum()
    {
        if(_dragColumn==null)return;
        _dragColumn.MaxWidth=_dragMaximum;_dragColumn=null;
    }
    private void FitToWindow()
    {
        if(_compact || _grid.ActualWidth<=0)return;
        double gaps=_detailsVisible?24:12;
        double available=_grid.ActualWidth-gaps-PanelMinimum;
        double minimum=_detailsVisible?PanelMinimum*2:PanelMinimum;
        double queue=Math.Max(PanelMinimum,_queue.Width.Value);
        double preview=_detailsVisible?Math.Max(PanelMinimum,_preview.Width.Value):0;
        if(queue+preview>available && available>=minimum)
        {
            double fraction=(available-minimum)/(queue+preview-minimum);
            _queue.Width=new GridLength(PanelMinimum+(queue-PanelMinimum)*fraction);
            if(_detailsVisible)_preview.Width=new GridLength(PanelMinimum+(preview-PanelMinimum)*fraction);
        }
        _queueWidth=_queue.Width.Value;
        if(_detailsVisible)_previewWidth=_preview.Width.Value;
    }
    public void Dispose()
    {
        _disposed=true;RestoreDragMaximum();
        _leftSplitter.DragStarted-=DragStarted;_rightSplitter.DragStarted-=DragStarted;
        _leftSplitter.DragCompleted-=DragCompleted;_rightSplitter.DragCompleted-=DragCompleted;
        _grid.SizeChanged-=GridSizeChanged;_window.SourceInitialized-=SourceInitialized;
        _window.PreviewKeyDown-=RevealQueueForSearch;
        _showQueue.Click-=ShowQueue;_showDownloads.Click-=ShowDownloads;_showDetails.Click-=ShowDetails;
        _toggleDetails.Click-=ToggleDetails;_openOptions.Click-=OpenOptions;
    }
}
