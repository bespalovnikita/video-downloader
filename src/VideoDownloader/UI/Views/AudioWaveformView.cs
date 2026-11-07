using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Color=System.Windows.Media.Color;
using Cursors=System.Windows.Input.Cursors;
using KeyEventArgs=System.Windows.Input.KeyEventArgs;
using Pen=System.Windows.Media.Pen;
using Point=System.Windows.Point;
using Rect=System.Windows.Rect;

namespace VideoDownloader.Native;

/// <summary>Draws measured PCM bins on their source-time axis; owns no media or network work.</summary>
internal sealed class AudioWaveformView:Canvas
{
    private const double HandleWidth=16;
    private readonly Thumb _handle;
    private readonly bool _isEnd;
    private AudioWaveform? _data;
    private double _boundary,_minimum,_maximum,_dragStart,_dragPixels;
    private bool _editable;
    internal Thumb BoundaryHandle=>_handle;
    internal AudioWaveform? Data=>_data;
    internal double BoundarySeconds=>_boundary;
    internal event Action<double>? BoundaryChanged;

    internal AudioWaveformView(bool isEnd)
    {
        _isEnd=isEnd;Height=112;MinWidth=180;Background=Brushes.Transparent;ClipToBounds=true;
        AutomationProperties.SetName(this,isEnd?"Звуковая волна конца фрагмента":"Звуковая волна начала фрагмента");
        _handle=new Thumb{Width=HandleWidth,Height=23,Focusable=true,Cursor=Cursors.SizeWE};
        AutomationProperties.SetName(_handle,isEnd?"Конец фрагмента на звуковой волне":"Начало фрагмента на звуковой волне");
        var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty,new SolidColorBrush(isEnd?Color.FromRgb(255,190,100):Color.FromRgb(74,204,230)));
        border.SetValue(Border.BorderBrushProperty,Brushes.White);border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(3));
        var text=new FrameworkElementFactory(typeof(TextBlock));text.SetValue(TextBlock.TextProperty,isEnd?"К":"Н");
        text.SetValue(TextBlock.ForegroundProperty,Brushes.Black);text.SetValue(TextBlock.FontWeightProperty,FontWeights.Bold);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty,System.Windows.HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty,System.Windows.VerticalAlignment.Center);
        border.AppendChild(text);_handle.Template=new ControlTemplate(typeof(Thumb)){VisualTree=border};
        Children.Add(_handle);SetTop(_handle,0);
        _handle.DragStarted+=(_,_)=>{if(_editable){_dragStart=_boundary;_dragPixels=0;_handle.Focus();}};
        _handle.DragDelta+=(_,e)=>
        {
            if(!_editable || _data is null)return;
            _dragPixels+=e.HorizontalChange;
            MoveBoundary(_dragStart+_dragPixels/TrackWidth*_data.DurationSeconds);
        };
        _handle.KeyDown+=HandleKeyDown;
        MouseLeftButtonDown+=TrackClicked;
        SizeChanged+=(_,_)=>{PlaceHandle();InvalidateVisual();};
        SetUnavailable();
    }
    private double TrackWidth=>Math.Max(1,ActualWidth-HandleWidth);
    private double Position(double time)=>_data is null?0:HandleWidth/2+
        Math.Clamp((time-_data.StartSeconds)/_data.DurationSeconds,0,1)*TrackWidth;
    internal void SetData(AudioWaveform data,double boundary,double minimum,double maximum,bool editable)
    {
        _data=data;
        _minimum=Math.Ceiling(Math.Max(data.StartSeconds,minimum)*1000-1e-7)/1000;
        _maximum=Math.Floor(Math.Min(data.EndSeconds,maximum)*1000+1e-7)/1000;
        _editable=editable && _maximum>=_minimum;
        _boundary=Math.Clamp(boundary,data.StartSeconds,data.EndSeconds);
        _handle.IsEnabled=_editable;_handle.Visibility=Visibility.Visible;Opacity=editable?1:.6;
        PlaceHandle();InvalidateVisual();
    }
    internal void SetUnavailable()
    {
        _data=null;_editable=false;_handle.IsEnabled=false;_handle.Visibility=Visibility.Collapsed;
        Opacity=.6;InvalidateVisual();
    }
    private void MoveBoundary(double value)
    {
        if(!_editable || _data is null || !double.IsFinite(value))return;
        value=Math.Clamp(Math.Round(value,3,MidpointRounding.AwayFromZero),_minimum,_maximum);
        if(value==_boundary)return;
        _boundary=value;PlaceHandle();InvalidateVisual();BoundaryChanged?.Invoke(value);
    }
    private void HandleKeyDown(object sender,KeyEventArgs e)
    {
        if(!_editable)return;
        double step=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?1:.1;
        switch(e.Key)
        {
            case Key.Left:MoveBoundary(_boundary-step);break;
            case Key.Right:MoveBoundary(_boundary+step);break;
            case Key.Home:MoveBoundary(_minimum);break;
            case Key.End:MoveBoundary(_maximum);break;
            default:return;
        }
        e.Handled=true;
    }
    private void TrackClicked(object sender,MouseButtonEventArgs e)
    {
        if(!_editable || _data is null || !ReferenceEquals(e.OriginalSource,this))return;
        double fraction=Math.Clamp((e.GetPosition(this).X-HandleWidth/2)/TrackWidth,0,1);
        MoveBoundary(_data.StartSeconds+fraction*_data.DurationSeconds);_handle.Focus();e.Handled=true;
    }
    private void PlaceHandle()
    {
        if(_data is null)return;
        SetLeft(_handle,Position(_boundary)-HandleWidth/2);
        _handle.ToolTip=(_isEnd?"Конец: ":"Начало: ")+DownloadSection.FormatTimestamp(_boundary);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var frame=new Rect(HandleWidth/2,25,TrackWidth,56);
        drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(18,26,43)),null,frame);
        drawing.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(80,93,115)),1),
            new Point(frame.Left,frame.Top+frame.Height/2),new Point(frame.Right,frame.Top+frame.Height/2));
        if(_data is null)return;
        var peak=new SolidColorBrush(Color.FromRgb(43,131,154));
        var rms=new SolidColorBrush(Color.FromRgb(87,218,220));
        double binWidth=TrackWidth/_data.Bins.Count,center=frame.Top+frame.Height/2;
        for(int index=0;index<_data.Bins.Count;index++)
        {
            var bin=_data.Bins[index];double x=frame.Left+index*binWidth;
            double outer=Math.Clamp(bin.Peak,0,1)*frame.Height/2,inner=Math.Clamp(bin.Rms,0,1)*frame.Height/2;
            drawing.DrawRectangle(peak,null,new Rect(x,center-outer,Math.Max(.5,binWidth-.5),outer*2));
            drawing.DrawRectangle(rms,null,new Rect(x,center-inner,Math.Max(.5,binWidth-.5),inner*2));
        }
        drawing.DrawLine(new Pen(new SolidColorBrush(_isEnd?Color.FromRgb(255,190,100):Color.FromRgb(74,204,230)),1.5),
            new Point(Position(_boundary),22),new Point(Position(_boundary),frame.Bottom));
        var brush=new SolidColorBrush(Color.FromRgb(183,192,216));
        FormattedText Label(double seconds)=>new(DownloadSection.FormatTimestamp(seconds),CultureInfo.CurrentUICulture,
            System.Windows.FlowDirection.LeftToRight,new Typeface("Segoe UI"),11,brush,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var left=Label(_data.StartSeconds);var right=Label(_data.EndSeconds);
        drawing.DrawText(left,new Point(0,87));drawing.DrawText(right,new Point(Math.Max(0,ActualWidth-right.Width),87));
        var middle=Label((_data.StartSeconds+_data.EndSeconds)/2);
        if(ActualWidth>left.Width+right.Width+middle.Width+30)
            drawing.DrawText(middle,new Point((ActualWidth-middle.Width)/2,87));
    }
}
