using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using Brush=System.Windows.Media.Brush;
using Color=System.Windows.Media.Color;
using ColorConverter=System.Windows.Media.ColorConverter;
using SystemColors=System.Windows.SystemColors;

namespace VideoDownloader.Native;

// The resource instance is shared by owner dialogs. DynamicResource consumers
// update in place when Windows changes its Contrast Theme or system colors.
public sealed class WindowThemeResources : ResourceDictionary
{
    private readonly WindowThemeState _state=new();
    public WindowThemeResources()
    {
        this["WindowThemeState"]=_state;
        Apply(false);
    }

    internal void Apply(bool highContrast)
    {
        Brush WindowText()=>SystemColors.WindowTextBrush;
        Brush WindowSurface()=>SystemColors.WindowBrush;
        Brush Highlight()=>SystemColors.HighlightBrush;
        Brush HighlightText()=>SystemColors.HighlightTextBrush;
        (string Key,string Dark,Func<Brush> Contrast)[] palette=
        [
            ("WindowBackgroundBrush","#111318",WindowSurface),("WindowTextBrush","#EEF1F8",WindowText),
            ("PanelBrush","#1B1E25",WindowSurface),("FieldBrush","#252932",WindowSurface),
            ("FieldHoverBrush","#303644",WindowSurface),("AlternateRowBrush","#20242D",WindowSurface),
            ("RowHoverBrush","#252B36",WindowSurface),("MutedBrush","#949CAE",WindowText),
            ("SecondaryTextBrush","#B7C0D8",WindowText),("TransferTextBrush","#B8C1D4",WindowText),
            ("BorderBrush","#343946",WindowText),("HoverBorderBrush","#4B5364",Highlight),
            ("PopupBorderBrush","#3B4251",WindowText),("FocusBrush","#FFFFFF",WindowText),
            ("SelectionBrush","#2F3A63",Highlight),("SelectionTextBrush","#FFFFFF",HighlightText),
            ("AccentBrush","#4F46E5",Highlight),("OnAccentBrush","#EEF1F8",HighlightText),
            ("WarnBrush","#F59E0B",Highlight),("OnWarnBrush","#111318",HighlightText),
            ("DangerBrush","#B42337",Highlight),("OnDangerBrush","#EEF1F8",HighlightText),
            ("OkBrush","#22C55E",WindowText),("DisabledTextBrush","#949CAE",()=>SystemColors.GrayTextBrush),
            ("MediaSurfaceBrush","#0D0F13",WindowSurface),("ProgressBrush","#8997FF",Highlight),
            ("TabIndicatorBrush","#A5B4FC",Highlight),("ValidationBrush","#FA8072",WindowText),
            ("CompletedStateBrush","#4ADE80",WindowText),("QueuedStateBrush","#A7B0C1",WindowText),
            ("ActiveStateBrush","#67D7FD",WindowText),("InterruptedStateBrush","#FBBF24",WindowText),
            ("SkippedStateBrush","#C4B5FD",WindowText),("FailedStateBrush","#FB7185",WindowText),
            ("HeaderWarningBrush","#FFA500",WindowText),("HeaderRunningBrush","#87CEFA",WindowText),
            ("HeaderSuccessBrush","#90EE90",WindowText),("HeaderErrorBrush","#FF6347",WindowText),
            ("HeaderIdleBrush","#D3D3D3",WindowText),
            ("TimelineCanvasBrush","Transparent",WindowSurface),("RangeStartBrush","#4ACCE6",Highlight),
            ("RangeEndBrush","#FFBE64",Highlight),("RangeHandleTextBrush","#000000",HighlightText),
            ("RangeHandleBorderBrush","#FFFFFF",WindowText),
            ("MediaCardBrush","#0D121E",WindowSurface),("MediaCardBorderBrush","#303A55",WindowText)
        ];
        foreach(var entry in palette)
        {
            Brush brush;
            if(highContrast)brush=entry.Contrast();
            else
            {
                var solid=new SolidColorBrush((Color)ColorConverter.ConvertFromString(entry.Dark));
                solid.Freeze();brush=solid;
            }
            this[entry.Key]=brush;
        }
        _state.HighContrast=highContrast;
    }
}

public sealed class WindowThemeState : INotifyPropertyChanged
{
    private bool _highContrast;
    public bool HighContrast
    {
        get=>_highContrast;
        internal set
        {
            if(_highContrast==value)return;
            _highContrast=value;PropertyChanged?.Invoke(this,new PropertyChangedEventArgs(nameof(HighContrast)));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class WindowThemeController : IDisposable
{
    private readonly Window _window;
    private readonly WindowThemeResources _palette;
    private readonly Func<bool> _highContrast;
    private bool _disposed;

    internal WindowThemeController(Window window,Func<bool>? highContrast=null)
    {
        _window=window;_highContrast=highContrast??(()=>SystemParameters.HighContrast);
        _palette=window.Resources.MergedDictionaries.OfType<WindowThemeResources>().FirstOrDefault()??new WindowThemeResources();
        if(!window.Resources.MergedDictionaries.Contains(_palette))window.Resources.MergedDictionaries.Insert(0,_palette);
        Refresh();SystemParameters.StaticPropertyChanged+=SystemSettingsChanged;
    }
    internal void Refresh()
    {
        if(_disposed)return;
        if(!_window.Dispatcher.CheckAccess())
        {
            if(!_window.Dispatcher.HasShutdownStarted)_=_window.Dispatcher.BeginInvoke(Refresh);
            return;
        }
        _palette.Apply(_highContrast());
    }
    private void SystemSettingsChanged(object? sender,PropertyChangedEventArgs args)=>Refresh();
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;SystemParameters.StaticPropertyChanged-=SystemSettingsChanged;
    }
}
