using System.ComponentModel;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Point=System.Windows.Point;

namespace VideoDownloader.Native;

// Owned dialogs use the active monitor's device-independent work area. The
// original minimums remain preferences, rather than making Close unreachable
// when Windows uses a large display scale or a shorter work area.
internal sealed class OwnedWindowLayout:IDisposable
{
    private static readonly DependencyProperty ControllerProperty=DependencyProperty.RegisterAttached(
        "Controller",typeof(OwnedWindowLayout),typeof(OwnedWindowLayout));
    private readonly Window _window,_owner;
    private readonly double _minWidth,_minHeight,_maxWidth,_maxHeight;
    private bool _disposed,_refreshPending,_clamping,_readWorkArea;
    private Rect? _availableOverride;

    private OwnedWindowLayout(Window window,Window owner)
    {
        _window=window;_owner=owner;
        _minWidth=window.MinWidth;_minHeight=window.MinHeight;_maxWidth=window.MaxWidth;_maxHeight=window.MaxHeight;
        window.SourceInitialized+=Initialized;window.Loaded+=Loaded;window.DpiChanged+=DpiChanged;window.SizeChanged+=SizeChanged;window.Closed+=Closed;
        SystemParameters.StaticPropertyChanged+=SystemSettingsChanged;
        Clamp(WorkArea(owner),center:true);
    }
    internal static OwnedWindowLayout Attach(Window window,Window owner)
    {
        if(window.GetValue(ControllerProperty) is OwnedWindowLayout existing)return existing;
        var layout=new OwnedWindowLayout(window,owner);window.SetValue(ControllerProperty,layout);return layout;
    }
    internal static Rect DeviceBoundsToDips(Rect bounds,Matrix fromDevice)=>new(
        fromDevice.Transform(bounds.TopLeft),fromDevice.Transform(bounds.BottomRight));
    private static Rect WorkArea(Window anchor)
    {
        var handle=new WindowInteropHelper(anchor).Handle;
        if(handle!=IntPtr.Zero && PresentationSource.FromVisual(anchor)?.CompositionTarget is {} target)
        {
            var pixels=System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
            return DeviceBoundsToDips(new Rect(pixels.Left,pixels.Top,pixels.Width,pixels.Height),target.TransformFromDevice);
        }
        return SystemParameters.WorkArea;
    }
    internal void Refresh(Rect? available=null)
    {_availableOverride=available;Clamp(available??WorkArea(_window),center:false);}
    private void Initialized(object? sender,EventArgs e)=>Clamp(WorkArea(_owner),center:true);
    private void Loaded(object sender,RoutedEventArgs e)=>Clamp(WorkArea(_window),center:true);
    private void DpiChanged(object sender,System.Windows.DpiChangedEventArgs e)=>ScheduleRefresh();
    private void SizeChanged(object sender,System.Windows.SizeChangedEventArgs e)
    {if(!_clamping)ScheduleRefresh(readWorkArea:false);}
    private void SystemSettingsChanged(object? sender,PropertyChangedEventArgs e)
    {
        if(string.IsNullOrEmpty(e.PropertyName) || e.PropertyName is nameof(SystemParameters.WorkArea) or
            nameof(SystemParameters.PrimaryScreenWidth) or nameof(SystemParameters.PrimaryScreenHeight) or
            nameof(SystemParameters.VirtualScreenWidth) or nameof(SystemParameters.VirtualScreenHeight))ScheduleRefresh();
    }
    private void ScheduleRefresh(bool readWorkArea=true)
    {
        if(_disposed || _window.Dispatcher.HasShutdownStarted)return;
        _readWorkArea|=readWorkArea;
        if(_refreshPending)return;
        _refreshPending=true;
        _=_window.Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>
        {
            _refreshPending=false;bool read=_readWorkArea;_readWorkArea=false;
            if(!_disposed)
            {
                if(read)_availableOverride=null;
                Clamp(_availableOverride??WorkArea(_window),center:false);
            }
        }));
    }
    private void Clamp(Rect area,bool center)
    {
        if(_disposed || _clamping || area.IsEmpty || !double.IsFinite(area.Width) || !double.IsFinite(area.Height) || area.Width<1 || area.Height<1)return;
        _clamping=true;
        try
        {
            double width=double.IsFinite(_window.Width)?_window.Width:Math.Max(_minWidth,_window.ActualWidth);
            double height=double.IsFinite(_window.Height)?_window.Height:Math.Max(_minHeight,_window.ActualHeight);
            double minWidth=Math.Min(_minWidth,area.Width),minHeight=Math.Min(_minHeight,area.Height);
            // Reset temporary constraints before switching between monitors,
            // so a previous small monitor cannot cap the next monitor's bounds.
            _window.MinWidth=0;_window.MinHeight=0;
            _window.MaxWidth=Math.Min(_maxWidth,area.Width);_window.MaxHeight=Math.Min(_maxHeight,area.Height);
            _window.MinWidth=minWidth;_window.MinHeight=minHeight;
            width=Math.Clamp(width,minWidth,_window.MaxWidth);height=Math.Clamp(height,minHeight,_window.MaxHeight);
            if(_window.SizeToContent is SizeToContent.Manual or SizeToContent.Height)_window.Width=width;
            if(_window.SizeToContent is SizeToContent.Manual or SizeToContent.Width)_window.Height=height;
            if(_window.SizeToContent is SizeToContent.Height or SizeToContent.WidthAndHeight)
                height=Math.Clamp(_window.ActualHeight>0?_window.ActualHeight:height,minHeight,_window.MaxHeight);
            if(_window.SizeToContent is SizeToContent.Width or SizeToContent.WidthAndHeight)
                width=Math.Clamp(_window.ActualWidth>0?_window.ActualWidth:width,minWidth,_window.MaxWidth);
            double left=center?_owner.Left+(_owner.ActualWidth-width)/2:_window.Left;
            double top=center?_owner.Top+(_owner.ActualHeight-height)/2:_window.Top;
            if(!double.IsFinite(left))left=area.Left+(area.Width-width)/2;
            if(!double.IsFinite(top))top=area.Top+(area.Height-height)/2;
            _window.WindowStartupLocation=WindowStartupLocation.Manual;
            _window.Left=Math.Clamp(left,area.Left,Math.Max(area.Left,area.Right-width));
            _window.Top=Math.Clamp(top,area.Top,Math.Max(area.Top,area.Bottom-height));
        }
        finally{_clamping=false;}
    }
    private void Closed(object? sender,EventArgs e)=>Dispose();
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _window.SourceInitialized-=Initialized;_window.Loaded-=Loaded;_window.DpiChanged-=DpiChanged;_window.SizeChanged-=SizeChanged;_window.Closed-=Closed;
        SystemParameters.StaticPropertyChanged-=SystemSettingsChanged;
        _window.ClearValue(ControllerProperty);
    }
}
