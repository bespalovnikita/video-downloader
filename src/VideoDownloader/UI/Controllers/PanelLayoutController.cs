using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace VideoDownloader.Native;

// The outer panels retain pixel widths; Downloads consumes the remainder.
// This lets both splitters resize adjacent panels without leaving empty space
// when the window grows, or hiding a panel after restoring a wider layout.
internal sealed class PanelLayoutController : IDisposable
{
    internal const double DefaultQueueWidth=380d*4/3;
    internal const double DefaultPreviewWidth=390;
    private readonly Grid _grid;
    private readonly ColumnDefinition _queue;
    private readonly ColumnDefinition _downloads;
    private readonly ColumnDefinition _preview;
    private readonly GridSplitter _leftSplitter;
    private readonly GridSplitter _rightSplitter;
    private readonly Action _save;
    private bool _adjusting;

    internal PanelLayoutController(Window window,Action save)
    {
        T Control<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing layout control: "+name));
        _grid=Control<Grid>("MainPanelsGrid");
        _queue=Control<ColumnDefinition>("QueuePanelColumn");
        _downloads=Control<ColumnDefinition>("DownloadsPanelColumn");
        _preview=Control<ColumnDefinition>("PreviewPanelColumn");
        _leftSplitter=Control<GridSplitter>("QueueDownloadsSplitter");
        _rightSplitter=Control<GridSplitter>("DownloadsPreviewSplitter");
        _save=save;
        _leftSplitter.DragCompleted+=DragCompleted;
        _rightSplitter.DragCompleted+=DragCompleted;
        _grid.SizeChanged+=GridSizeChanged;
    }

    internal double QueueWidth=>_queue.Width.Value;
    internal double PreviewWidth=>_preview.Width.Value;

    internal void Apply(double queueWidth,double previewWidth)
    {
        _queue.Width=new GridLength(Valid(queueWidth,_queue.MinWidth)?queueWidth:DefaultQueueWidth);
        _preview.Width=new GridLength(Valid(previewWidth,_preview.MinWidth)?previewWidth:DefaultPreviewWidth);
        _downloads.Width=new GridLength(1,GridUnitType.Star);
        FitToWindow();
    }

    private static bool Valid(double value,double minimum)=>double.IsFinite(value) && value>=minimum && value<=10_000;
    private void GridSizeChanged(object sender,SizeChangedEventArgs e)=>FitToWindow();
    private void DragCompleted(object sender,DragCompletedEventArgs e)
    {
        FitToWindow();
        _save();
    }

    private void FitToWindow()
    {
        if(_adjusting || _grid.ActualWidth<=0)return;
        double gaps=_grid.ColumnDefinitions[1].ActualWidth+_grid.ColumnDefinitions[3].ActualWidth;
        double available=_grid.ActualWidth-gaps-_downloads.MinWidth;
        double minimum=_queue.MinWidth+_preview.MinWidth;
        if(available<minimum)return; // The Window.MinWidth normally prevents this.
        double queue=Math.Max(_queue.MinWidth,_queue.Width.Value);
        double preview=Math.Max(_preview.MinWidth,_preview.Width.Value);
        if(queue+preview<=available)return;
        double fraction=(available-minimum)/(queue+preview-minimum);
        _adjusting=true;
        try
        {
            _queue.Width=new GridLength(_queue.MinWidth+(queue-_queue.MinWidth)*fraction);
            _preview.Width=new GridLength(_preview.MinWidth+(preview-_preview.MinWidth)*fraction);
        }
        finally {_adjusting=false;}
    }

    public void Dispose()
    {
        _leftSplitter.DragCompleted-=DragCompleted;
        _rightSplitter.DragCompleted-=DragCompleted;
        _grid.SizeChanged-=GridSizeChanged;
    }
}
