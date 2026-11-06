using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Color=System.Windows.Media.Color;
using Point=System.Windows.Point;
using Rect=System.Windows.Rect;

namespace VideoDownloader.Native;

/// <summary>A range-only timeline. It edits values and never performs media work.</summary>
internal sealed class FragmentTimeline : Canvas
{
    private const double HandleWidth=16;
    private const double TrackTop=23;
    private readonly Thumb _startHandle,_endHandle;
    private double _duration=1,_start,_end=1;
    private double? _fps;
    private bool _editable,_dragEnd,_hasDuration;
    private double _dragStart,_dragPixels;
    internal Thumb StartHandle=>_startHandle;
    internal Thumb EndHandle=>_endHandle;
    internal double StartSeconds=>_start;
    internal double EndSeconds=>_end;
    internal double DurationSeconds=>_duration;
    internal bool ActiveIsEnd {get;private set;}
    internal event Action<double,double>? RangeChanged;

    internal FragmentTimeline()
    {
        Height=65;MinWidth=120;Background=Brushes.Transparent;ClipToBounds=false;
        _startHandle=Handle("MediaSectionStartThumb","Начало фрагмента",Color.FromRgb(74,204,230),false);
        _endHandle=Handle("MediaSectionEndThumb","Конец фрагмента",Color.FromRgb(255,190,100),true);
        Children.Add(_startHandle);Children.Add(_endHandle);
        SizeChanged+=(_,_)=>PlaceHandles();
        MouseLeftButtonDown+=TrackClicked;
        SetRange(1,0,1,null,false);
    }
    private Thumb Handle(string name,string label,Color color,bool isEnd)
    {
        var thumb=new Thumb{Name=name,Width=HandleWidth,Height=32,Focusable=true,Cursor=System.Windows.Input.Cursors.SizeWE};
        AutomationProperties.SetName(thumb,label);
        var border=new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.BackgroundProperty,new SolidColorBrush(color));
        border.SetValue(Border.BorderBrushProperty,Brushes.White);
        border.SetValue(Border.BorderThicknessProperty,new Thickness(1));
        border.SetValue(Border.CornerRadiusProperty,new CornerRadius(4));
        var text=new FrameworkElementFactory(typeof(TextBlock));
        text.SetValue(TextBlock.TextProperty,isEnd?"К":"Н");text.SetValue(TextBlock.ForegroundProperty,Brushes.Black);
        text.SetValue(TextBlock.FontWeightProperty,FontWeights.Bold);
        text.SetValue(FrameworkElement.HorizontalAlignmentProperty,System.Windows.HorizontalAlignment.Center);
        text.SetValue(FrameworkElement.VerticalAlignmentProperty,System.Windows.VerticalAlignment.Center);
        border.AppendChild(text);thumb.Template=new ControlTemplate(typeof(Thumb)){VisualTree=border};
        thumb.GotKeyboardFocus+=(_,_)=>{ActiveIsEnd=isEnd;SetZIndex(thumb,2);SetZIndex(isEnd?_startHandle:_endHandle,1);};
        thumb.DragStarted+=(_,_)=>
        {
            if(!_editable)return;
            _dragEnd=isEnd;ActiveIsEnd=isEnd;_dragStart=isEnd?_end:_start;_dragPixels=0;thumb.Focus();
        };
        thumb.DragDelta+=(_,e)=>
        {
            if(!_editable)return;
            _dragPixels+=e.HorizontalChange;
            SetBoundary(_dragEnd,_dragStart+_dragPixels/TrackWidth*_duration,true);
        };
        thumb.KeyDown+=(_,e)=>
        {
            if(!_editable)return;
            int count=(Keyboard.Modifiers&ModifierKeys.Shift)!=0?10:1;
            switch(e.Key)
            {
                case Key.Left: Nudge(isEnd,-count);break;
                case Key.Right: Nudge(isEnd,count);break;
                case Key.PageDown: Nudge(isEnd,-10);break;
                case Key.PageUp: Nudge(isEnd,10);break;
                case Key.Home: SetBoundary(isEnd,isEnd?_start+FragmentTimelineTiming.MinimumRange:0,true);break;
                case Key.End: SetBoundary(isEnd,isEnd?_duration:_end-FragmentTimelineTiming.MinimumRange,true);break;
                default:return;
            }
            e.Handled=true;
        };
        return thumb;
    }
    private double TrackWidth=>Math.Max(1,ActualWidth-HandleWidth);
    private double Position(double seconds)=>HandleWidth/2+seconds/_duration*TrackWidth;
    internal void SetRange(double duration,double start,double end,double? fps,bool editable)
    {
        if(!FragmentTimelineTiming.KnownDuration(duration))throw new ArgumentOutOfRangeException(nameof(duration));
        _duration=FragmentTimelineTiming.LastMillisecond(duration);
        _start=Math.Clamp(start,0,Math.Max(0,_duration-FragmentTimelineTiming.MinimumRange));
        _end=Math.Clamp(end,_start+FragmentTimelineTiming.MinimumRange,_duration);
        _fps=FragmentTimelineTiming.KnownFps(fps)?fps:null;_editable=editable;
        _hasDuration=true;_startHandle.Visibility=_endHandle.Visibility=Visibility.Visible;
        _startHandle.IsEnabled=_endHandle.IsEnabled=editable;
        Opacity=editable?1:.55;
        PlaceHandles();InvalidateVisual();
    }
    internal void SetUnavailable()
    {
        _hasDuration=false;_editable=false;
        _startHandle.IsEnabled=_endHandle.IsEnabled=false;
        _startHandle.Visibility=_endHandle.Visibility=Visibility.Collapsed;
        Opacity=.55;InvalidateVisual();
    }
    internal void FocusBoundary(bool isEnd)
    {
        ActiveIsEnd=isEnd;
        if(_editable)(isEnd?_endHandle:_startHandle).Focus();
    }
    internal void Nudge(bool isEnd,int frames)
    {
        if(!_editable)return;
        double minimum=isEnd?_start+FragmentTimelineTiming.MinimumRange:0;
        double maximum=isEnd?_duration:_end-FragmentTimelineTiming.MinimumRange;
        SetBoundary(isEnd,FragmentTimelineTiming.Step(isEnd?_end:_start,frames,_fps,minimum,maximum),true);
        FocusBoundary(isEnd);
    }
    private void SetBoundary(bool isEnd,double seconds,bool notify)
    {
        if(!_editable || !double.IsFinite(seconds))return;
        seconds=FragmentTimelineTiming.Milliseconds(seconds);
        if(isEnd)_end=Math.Clamp(seconds,_start+FragmentTimelineTiming.MinimumRange,_duration);
        else _start=Math.Clamp(seconds,0,_end-FragmentTimelineTiming.MinimumRange);
        ActiveIsEnd=isEnd;PlaceHandles();InvalidateVisual();
        if(notify)RangeChanged?.Invoke(_start,_end);
    }
    private void TrackClicked(object sender,MouseButtonEventArgs e)
    {
        if(!_editable || !ReferenceEquals(e.OriginalSource,this))return;
        double seconds=Math.Clamp((e.GetPosition(this).X-HandleWidth/2)/TrackWidth,0,1)*_duration;
        bool isEnd=Math.Abs(seconds-_end)<Math.Abs(seconds-_start) ||
            Math.Abs(seconds-_end)==Math.Abs(seconds-_start) && ActiveIsEnd;
        SetBoundary(isEnd,seconds,true);FocusBoundary(isEnd);e.Handled=true;
    }
    private void PlaceHandles()
    {
        SetLeft(_startHandle,Position(_start)-HandleWidth/2);SetTop(_startHandle,12);
        SetLeft(_endHandle,Position(_end)-HandleWidth/2);SetTop(_endHandle,12);
        _startHandle.ToolTip="Начало: "+DownloadSection.FormatTimestamp(_start);
        _endHandle.ToolTip="Конец: "+DownloadSection.FormatTimestamp(_end);
    }
    protected override void OnRender(DrawingContext drawing)
    {
        base.OnRender(drawing);
        var track=new Rect(HandleWidth/2,TrackTop,TrackWidth,10);
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(39,49,72)),null,track,4,4);
        if(!_hasDuration)return;
        drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(40,132,164)),null,
            new Rect(Position(_start),TrackTop,Math.Max(0,Position(_end)-Position(_start)),10),3,3);
        var color=new SolidColorBrush(Color.FromRgb(183,192,216));
        FormattedText Label(string text)=>new(text,CultureInfo.CurrentUICulture,System.Windows.FlowDirection.LeftToRight,
            new Typeface("Segoe UI"),11,color,VisualTreeHelper.GetDpi(this).PixelsPerDip);
        drawing.DrawText(Label("00:00:00"),new Point(0,46));
        var end=Label(DownloadSection.FormatTimestamp(_duration));
        drawing.DrawText(end,new Point(Math.Max(0,ActualWidth-end.Width),46));
    }
}
