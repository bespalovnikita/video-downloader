using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace VideoDownloader.Native;

// Keep the user's height separate from the temporary viewport limit. Collapsing
// the journal or visiting another compact panel must not replace that choice.
internal sealed class JournalLayoutController : IDisposable
{
    private const double MinimumHeight=140;
    private const double DownloadViewport=96;
    private readonly Grid _layout,_body;
    private readonly Expander _expander;
    private readonly Thumb _handle;
    private readonly ScrollViewer _controls;
    private double _preferredHeight=220,_minimum,_maximum;
    private bool _arranging,_disposed;

    internal JournalLayoutController(Window window)
    {
        T Control<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing journal layout control: "+name));
        _layout=Control<Grid>("DownloadsContentGrid");
        _body=Control<Grid>("JournalBody");
        _expander=Control<Expander>("JournalExpander");
        _handle=Control<Thumb>("JournalResizeHandle");
        _controls=Control<ScrollViewer>("JournalControlsScrollViewer");
        _layout.SizeChanged+=SizeChanged;
        _expander.SizeChanged+=SizeChanged;
        _expander.Expanded+=Expanded;
        _expander.IsVisibleChanged+=VisibilityChanged;
        _handle.DragDelta+=DragDelta;
        _handle.KeyDown+=KeyDown;
    }

    private void SizeChanged(object sender,SizeChangedEventArgs e)=>Arrange();
    private void Expanded(object sender,RoutedEventArgs e)=>Arrange();
    private void VisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)=>Arrange();

    private void Arrange()
    {
        if(_disposed || _arranging || !_expander.IsExpanded || !_expander.IsVisible || _layout.ActualHeight<=0)return;
        _arranging=true;
        try
        {
            double chrome=Math.Max(0,_expander.ActualHeight-_body.ActualHeight);
            double fixedHeight=_layout.RowDefinitions[0].ActualHeight+_layout.RowDefinitions[2].ActualHeight+chrome;
            double reservedList=Math.Min(DownloadViewport,_layout.ActualHeight/4);
            _maximum=Math.Max(0,_layout.ActualHeight-fixedHeight-reservedList);
            _minimum=Math.Min(MinimumHeight,_maximum);
            double height=Math.Clamp(_preferredHeight,_minimum,_maximum);
            if(Math.Abs(_body.Height-height)>.5)_body.Height=height;
            // On short/narrow windows, wrapping buttons scroll independently
            // instead of consuming the whole journal text viewport.
            double handleHeight=_handle.ActualHeight+_handle.Margin.Top+_handle.Margin.Bottom;
            _controls.MaxHeight=Math.Max(0,Math.Min(height*.6,height-handleHeight-32));
        }
        finally {_arranging=false;}
    }

    private void ChangeHeight(double height)
    {
        if(_disposed || !_expander.IsExpanded || !_expander.IsVisible)return;
        Arrange();
        _preferredHeight=Math.Clamp(height,_minimum,_maximum);
        Arrange();
    }
    private void DragDelta(object sender,DragDeltaEventArgs e)=>ChangeHeight(_body.Height-e.VerticalChange);
    private void KeyDown(object sender,KeyEventArgs e)
    {
        if((Keyboard.Modifiers & (ModifierKeys.Alt|ModifierKeys.Control|ModifierKeys.Windows))!=0)return;
        double step=(Keyboard.Modifiers & ModifierKeys.Shift)!=0?64:16;
        double? height=e.Key switch
        {
            Key.Up=>_body.Height+step,
            Key.Down=>_body.Height-step,
            Key.Home=>_minimum,
            Key.End=>_maximum,
            _=>null
        };
        if(height is null)return;
        ChangeHeight(height.Value);e.Handled=true;
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _layout.SizeChanged-=SizeChanged;
        _expander.SizeChanged-=SizeChanged;
        _expander.Expanded-=Expanded;
        _expander.IsVisibleChanged-=VisibilityChanged;
        _handle.DragDelta-=DragDelta;
        _handle.KeyDown-=KeyDown;
    }
}
