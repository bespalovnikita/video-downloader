using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color=System.Windows.Media.Color;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private sealed class BoundaryFrame(TextBox input,Image image,TextBlock status,bool isEnd)
    {
        internal readonly TextBox Input=input;
        internal readonly Image Image=image;
        internal readonly TextBlock Status=status;
        internal readonly bool IsEnd=isEnd;
        internal long Generation;
        internal CancellationTokenSource? Cancellation;
    }
    private sealed record FrameContext(bool Editable,bool Loaded,bool Collection,double? Duration,string? Format,bool? HasVideo,double? FrameRate);
    private Func<MediaFrameRequest,CancellationToken,Task<MediaFrameResult>>? _framePreview;
    private BoundaryFrame? _startFrame,_endFrame;
    private Button? _refreshFrames;
    private FrameContext? _frameContext;
    private bool _framesClosed;

    private void InitializeFramePreviews(StackPanel body,
        Func<MediaFrameRequest,CancellationToken,Task<MediaFrameResult>>? framePreview)
    {
        _framePreview=framePreview;
        var cards=new Grid{Margin=new Thickness(0,0,0,6)};
        cards.ColumnDefinitions.Add(new ColumnDefinition());cards.ColumnDefinitions.Add(new ColumnDefinition());
        BoundaryFrame Card(string prefix,string label,TextBox input,bool isEnd,int column)
        {
            var image=Named(prefix+"Frame",new Image{Height=165,Stretch=Stretch.Uniform,SnapsToDevicePixels=true});
            var status=Named(prefix+"FrameStatus",Note("Укажите время."));
            status.MinHeight=38;
            var panel=new StackPanel();panel.Children.Add(Note(label));
            panel.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(13,18,30)),
                BorderBrush=new SolidColorBrush(Color.FromRgb(48,58,85)),BorderThickness=new Thickness(1),
                CornerRadius=new CornerRadius(5),Padding=new Thickness(4),Child=image});
            panel.Children.Add(status);
            panel.Margin=new Thickness(column==0?0:6,0,column==0?6:0,0);
            Grid.SetColumn(panel,column);cards.Children.Add(panel);
            return new BoundaryFrame(input,image,status,isEnd);
        }
        _startFrame=Card("MediaSectionStart","Кадр начала",_start,false,0);
        _endFrame=Card("MediaSectionEnd","Кадр перед концом",_end,true,1);
        body.Children.Add(cards);
        var actions=new StackPanel{Orientation=Orientation.Horizontal};
        _refreshFrames=Named("RefreshMediaFramesButton",new Button{Content="Обновить кадры",Margin=new Thickness(0,2,8,4)});
        actions.Children.Add(_refreshFrames);body.Children.Add(actions);
        body.Children.Add(Note("Кадры обновляются после ввода времени. Для конца показывается кадр перед выбранной границей. Предпросмотр требует FFmpeg и не запускает скачивание в очереди."));
        _start.TextChanged+=FrameTimeChanged;_end.TextChanged+=FrameTimeChanged;
        _mode.SelectionChanged+=FrameFormatChanged;_primary.SelectionChanged+=FrameFormatChanged;
        _refreshFrames.Click+=(_,_)=>ScheduleBothFrames();
        Closed+=(_,_)=>CloseFramePreviews();
    }

    private FrameContext CurrentFrameContext()
    {
        var format=_mode.SelectedIndex==1?_primary.SelectedItem as FormatChoice:null;
        double? duration=_metadata is {Duration:>0} data && double.IsFinite(data.Duration)?data.Duration:null;
        return new(_editable,_metadata?.FormatsLoaded==true,_metadata?.IsCollection==true,duration,
            _mode.SelectedIndex==1?format?.Id??"":null,format?.Format?.HasVideo,TimelineFps());
    }
    private void RefreshFrameState()
    {
        if(_startFrame is null || _endFrame is null || _framesClosed)return;
        var context=CurrentFrameContext();
        if(_refreshFrames is not null)_refreshFrames.IsEnabled=context.Editable && context.Loaded && !context.Collection;
        if(context==_frameContext)return;
        _frameContext=context;ScheduleBothFrames();
    }
    private void FrameTimeChanged(object sender,TextChangedEventArgs e)
    {
        if(_framesClosed || _loading)return;
        if(ReferenceEquals(sender,_start))ScheduleFrame(_startFrame);
        if(ReferenceEquals(sender,_end))ScheduleFrame(_endFrame);
    }
    private void FrameFormatChanged(object sender,SelectionChangedEventArgs e)
    {if(!_loading)RefreshFrameState();}
    private void ScheduleBothFrames()
    {ScheduleFrame(_startFrame);ScheduleFrame(_endFrame);}
    private static void CancelFrame(BoundaryFrame frame)
    {
        frame.Generation++;
        frame.Cancellation?.Cancel();frame.Cancellation=null;
        frame.Image.Source=null;frame.Image.ToolTip=null;
    }
    private void ScheduleFrame(BoundaryFrame? frame)
    {
        if(frame is null || _framesClosed)return;
        CancelFrame(frame);
        var context=CurrentFrameContext();
        string text=frame.Input.Text;
        if(string.IsNullOrWhiteSpace(text)){frame.Status.Text="Укажите время "+(frame.IsEnd?"конца.":"начала.");return;}
        if(!context.Editable){frame.Status.Text="Предпросмотр приостановлен: строка недоступна для изменения.";return;}
        if(!DownloadSection.TryParseTimestamp(text,out double seconds))
        {frame.Status.Text="Укажите время в формате ЧЧ:ММ:СС, М:СС или секунд.";return;}
        if(!context.Loaded){frame.Status.Text="Ожидаем метаданные видео…";return;}
        if(context.Collection){frame.Status.Text="Выберите отдельное видео вместо коллекции.";return;}
        if(context.Duration is {} duration && (seconds>duration || !frame.IsEnd && seconds>=duration))
        {frame.Status.Text="Время выходит за длительность видео.";return;}
        if(context.Format is not null && (context.Format.Length==0 || context.HasVideo!=true))
        {frame.Status.Text=context.HasVideo==false?"В аудиоформате нет кадров.":"Выберите доступный видеоформат.";return;}
        if(_framePreview is null){frame.Status.Text="Предпросмотр кадров недоступен.";return;}
        frame.Status.Text="Готовим кадр…";
        var cancel=new CancellationTokenSource();frame.Cancellation=cancel;
        var request=new MediaFrameRequest(Row.Url,seconds,context.Format,context.Duration,frame.IsEnd,context.FrameRate);
        _=LoadFrameAsync(frame,frame.Generation,request,cancel);
    }
    private bool IsCurrentFrame(BoundaryFrame frame,long generation,CancellationTokenSource cancel)=>
        !_framesClosed && !cancel.IsCancellationRequested && frame.Generation==generation && ReferenceEquals(frame.Cancellation,cancel);
    private async Task LoadFrameAsync(BoundaryFrame frame,long generation,MediaFrameRequest request,CancellationTokenSource cancel)
    {
        try
        {
            await Task.Delay(350,cancel.Token);
            if(!IsCurrentFrame(frame,generation,cancel))return;
            frame.Status.Text="Получаем кадр…";
            var result=await _framePreview!(request,cancel.Token);
            if(!IsCurrentFrame(frame,generation,cancel))return;
            if(!string.Equals(result.Url,request.Url,StringComparison.Ordinal) || result.RequestedSeconds!=request.Seconds)
                throw new InvalidDataException("Источник вернул кадр для другого запроса.");
            if(!result.Success)
            {frame.Status.Text=FrameFailure(result.Error);return;}
            var image=await Task.Run(()=>PreviewImage.Decode(result.PngBytes),cancel.Token);
            if(!IsCurrentFrame(frame,generation,cancel))return;
            double sampled=result.SampledSeconds is {} actual && double.IsFinite(actual) && actual>=0 &&
                actual<TimeSpan.MaxValue.TotalSeconds?actual:request.Seconds;
            frame.Image.Source=image;
            frame.Status.Text="Кадр около "+DownloadSection.FormatTimestamp(sampled);
            frame.Image.ToolTip=frame.Status.Text;
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested) { }
        catch(Exception error)
        {if(IsCurrentFrame(frame,generation,cancel))frame.Status.Text=FrameFailure(error.Message);}
        finally
        {
            if(ReferenceEquals(frame.Cancellation,cancel))frame.Cancellation=null;
            cancel.Dispose();
        }
    }
    private static string FrameFailure(string error)
    {
        string detail=JournalSanitizer.Redact(ErrorMessages.ForDisplay(error),multiline:true);
        if(detail.Length>512)detail=detail[..512];
        return detail.Length==0?"Кадр недоступен для выбранного времени.":"Кадр недоступен: "+detail;
    }
    private void CloseFramePreviews()
    {
        _framesClosed=true;
        _start.TextChanged-=FrameTimeChanged;_end.TextChanged-=FrameTimeChanged;
        _mode.SelectionChanged-=FrameFormatChanged;_primary.SelectionChanged-=FrameFormatChanged;
        if(_startFrame is not null)CancelFrame(_startFrame);
        if(_endFrame is not null)CancelFrame(_endFrame);
    }
}
