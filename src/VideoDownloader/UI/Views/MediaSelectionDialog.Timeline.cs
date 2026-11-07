using System.Windows;
using System.Windows.Controls;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private FragmentTimeline? _timeline;
    private TextBlock? _timelineStatus,_stepStatus;
    private Button? _startPreviousFrame,_startNextFrame,_endPreviousFrame,_endNextFrame,_focusStart,_focusEnd;
    private bool _syncingTimeline;

    private void InitializeTimeline(StackPanel body)
    {
        _timeline=Named("MediaSectionTimeline",new FragmentTimeline());
        RegisterName("MediaSectionStartThumb",_timeline.StartHandle);RegisterName("MediaSectionEndThumb",_timeline.EndHandle);
        body.Children.Add(_timeline);
        _timelineStatus=Named("MediaSectionTimelineStatus",Note("Для шкалы нужна длительность видео."));body.Children.Add(_timelineStatus);
        var actions=new Grid();actions.ColumnDefinitions.Add(new ColumnDefinition());actions.ColumnDefinitions.Add(new ColumnDefinition());
        StackPanel Steps(bool isEnd,int column)
        {
            var panel=new StackPanel{Orientation=Orientation.Horizontal,Margin=new Thickness(column==0?0:6,3,column==0?6:0,3)};
            Button Add(string name,string caption,Action action)
            {
                var button=Named(name,new Button{Content=caption,MinWidth=48,Margin=new Thickness(0,0,5,0)});
                button.Click+=(_,_)=>action();panel.Children.Add(button);return button;
            }
            if(isEnd)
            {
                _focusEnd=Add("MediaSectionFocusEndButton","К концу",()=>FocusTimelineBoundary(true));
                _endPreviousFrame=Add("MediaSectionEndPreviousFrameButton","− кадр",()=>StepTimelineBoundary(true,-1));
                _endNextFrame=Add("MediaSectionEndNextFrameButton","+ кадр",()=>StepTimelineBoundary(true,1));
            }
            else
            {
                _focusStart=Add("MediaSectionFocusStartButton","К началу",()=>FocusTimelineBoundary(false));
                _startPreviousFrame=Add("MediaSectionStartPreviousFrameButton","− кадр",()=>StepTimelineBoundary(false,-1));
                _startNextFrame=Add("MediaSectionStartNextFrameButton","+ кадр",()=>StepTimelineBoundary(false,1));
            }
            Grid.SetColumn(panel,column);return panel;
        }
        actions.Children.Add(Steps(false,0));actions.Children.Add(Steps(true,1));body.Children.Add(actions);
        _stepStatus=Named("MediaSectionStepStatus",Note(""));body.Children.Add(_stepStatus);
        body.Children.Add(Note("Перетащите границы или нажмите на шкалу возле нужной границы. Стрелки ←/→ сдвигают выбранную границу на шаг, Shift — на 10 шагов; Home/End переходят к допустимому краю."));
        _timeline.RangeChanged+=TimelineRangeChanged;
        _start.TextChanged+=TimelineTextChanged;_end.TextChanged+=TimelineTextChanged;
        _mode.SelectionChanged+=TimelineFormatChanged;_primary.SelectionChanged+=TimelineFormatChanged;
        Closed+=(_,_)=>CloseTimeline();
    }
    private void TimelineTextChanged(object sender,TextChangedEventArgs e)
    {if(!_syncingTimeline && !_loading)RefreshTimelineState();}
    private void TimelineFormatChanged(object sender,SelectionChangedEventArgs e)
    {if(!_loading)RefreshTimelineState();}
    private double? TimelineFps()
    {
        if(_mode.SelectedIndex==1)
        {
            var format=(_primary.SelectedItem as FormatChoice)?.Format;
            return format?.HasVideo==true && FragmentTimelineTiming.KnownFps(format.Fps)?format.Fps:null;
        }
        var video=_metadata?.Formats.Where(format=>format.HasVideo).ToArray()??[];
        if(video.Length==0 || video.Any(format=>!FragmentTimelineTiming.KnownFps(format.Fps)))return null;
        var values=video.Select(format=>format.Fps).Distinct().Take(2).ToArray();
        return values.Length==1?values[0]:null;
    }
    private bool TimelineRange(out double start,out double end,out double? duration)
    {
        duration=_metadata is {Duration:>0} data && FragmentTimelineTiming.KnownDuration(data.Duration)?data.Duration:null;
        start=0;end=duration is {} known?FragmentTimelineTiming.LastMillisecond(known):0;
        bool startValid=string.IsNullOrWhiteSpace(_start.Text) || DownloadSection.TryParseTimestamp(_start.Text,out start);
        bool endValid=string.IsNullOrWhiteSpace(_end.Text) || DownloadSection.TryParseTimestamp(_end.Text,out end);
        return startValid && endValid && end>start && (duration is null || end<=duration);
    }
    private void RefreshTimelineState()
    {
        if(_timeline is null || _timelineStatus is null || _stepStatus is null || _framesClosed)return;
        var fps=TimelineFps();
        bool range=TimelineRange(out double start,out double end,out double? duration);
        bool individual=_metadata is {FormatsLoaded:true,IsCollection:false};
        bool enabled=_editable && individual && duration is not null;
        if(duration is {} total && individual)
            _timeline.SetRange(total,range?start:0,range?end:total,fps,enabled);
        else _timeline.SetUnavailable();
        bool step=_editable && individual && range;
        foreach(var button in new[]{_startPreviousFrame,_startNextFrame,_endPreviousFrame,_endNextFrame})
            if(button is not null)button.IsEnabled=step || enabled;
        if(_focusStart is not null)_focusStart.IsEnabled=enabled;
        if(_focusEnd is not null)_focusEnd.IsEnabled=enabled;
        string suffix=FragmentTimelineTiming.KnownFps(fps)?" кадр":" 0.1 с";
        foreach(var button in new[]{_startPreviousFrame,_endPreviousFrame})if(button is not null)button.Content="−"+suffix;
        foreach(var button in new[]{_startNextFrame,_endNextFrame})if(button is not null)button.Content="+"+suffix;
        _stepStatus.Text=FragmentTimelineTiming.KnownFps(fps)
            ?"Шаг: один кадр по сетке "+fps!.Value.ToString("0.###",System.Globalization.CultureInfo.InvariantCulture)+" кадр/с. Поля времени округляются до миллисекунд."
            :"Частота кадров выбранного видео неизвестна или различается между форматами; шаг — 0.1 секунды.";
        _timelineStatus.Text=!_editable?"Шкала недоступна: строка защищена от изменения.":!individual
            ?"Дождитесь метаданных отдельного видео.":duration is null
            ?"Источник не сообщил длительность: задайте диапазон полями времени; пошаговая настройка остаётся доступной."
            :!range?"Исправьте диапазон в полях или задайте его перетаскиванием границ."
            :string.IsNullOrWhiteSpace(_start.Text) && string.IsNullOrWhiteSpace(_end.Text)
            ?"Весь материал · "+DownloadSection.FormatTimestamp(end-start)+". Движение границы задаст явный фрагмент."
            :"Длительность фрагмента: "+DownloadSection.FormatTimestamp(end-start);
    }
    private void TimelineRangeChanged(double start,double end)
    {
        if(!_editable || _framesClosed)return;
        _syncingTimeline=true;
        try {_start.Text=DownloadSection.FormatTimestamp(start);_end.Text=DownloadSection.FormatTimestamp(end);}
        finally {_syncingTimeline=false;}
        RefreshTimelineState();
    }
    private void FocusTimelineBoundary(bool isEnd)
    {
        if(!_editable || _framesClosed || _metadata is not {FormatsLoaded:true,IsCollection:false} || _timeline is null ||
            !FragmentTimelineTiming.KnownDuration(_metadata.Duration))return;
        // Explicit navigation also makes the full range's implicit boundaries
        // available to the existing two frame cards.
        TimelineRangeChanged(_timeline.StartSeconds,_timeline.EndSeconds);
        _timeline.FocusBoundary(isEnd);
    }
    private void StepTimelineBoundary(bool isEnd,int direction)
    {
        if(!_editable || _framesClosed || _metadata is not {FormatsLoaded:true,IsCollection:false})return;
        if(FragmentTimelineTiming.KnownDuration(_metadata.Duration))
        {_timeline?.Nudge(isEnd,direction);return;}
        if(!TimelineRange(out double start,out double end,out _))return;
        double minimum=isEnd?start+FragmentTimelineTiming.MinimumRange:0;
        double maximum=isEnd?TimeSpan.MaxValue.TotalSeconds-.001:end-FragmentTimelineTiming.MinimumRange;
        double stepped=FragmentTimelineTiming.Step(isEnd?end:start,direction,TimelineFps(),minimum,maximum);
        TimelineRangeChanged(isEnd?start:stepped,isEnd?stepped:end);
    }
    private void CloseTimeline()
    {
        if(_timeline is not null)_timeline.RangeChanged-=TimelineRangeChanged;
        _start.TextChanged-=TimelineTextChanged;_end.TextChanged-=TimelineTextChanged;
        _mode.SelectionChanged-=TimelineFormatChanged;_primary.SelectionChanged-=TimelineFormatChanged;
    }
}
