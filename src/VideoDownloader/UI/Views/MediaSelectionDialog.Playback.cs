using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using Color=System.Windows.Media.Color;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private sealed record PlaybackContext(bool Editable,bool Loaded,bool Collection,double? Duration,string Start,string End,
        bool Manual,string? Primary,string? Audio,bool AudioOnly,string AudioTrack,string AudioLanguages);
    private Func<MediaClipRequest,CancellationToken,Task<MediaClipResult>>? _clipPreview;
    private IMediaClipPlayer? _clipPlayer;
    private MediaClipPreview? _clip;
    private CancellationTokenSource? _clipCancellation;
    private DispatcherTimer? _clipTimer;
    private Button? _clipFirst,_clipLast,_clipPause,_clipStop;
    private Slider? _clipSeek;
    private TextBlock? _clipStatus,_clipRange,_clipPosition;
    private PlaybackContext? _playbackContext;
    private long _clipGeneration;
    private bool _playbackClosed,_clipPlaying,_clipOpened,_clipAutoPlay,_clipUpdatingPosition;

    private void InitializePlayback(StackPanel body,
        Func<MediaClipRequest,CancellationToken,Task<MediaClipResult>>? clipPreview,IMediaClipPlayer? player)
    {
        _clipPreview=clipPreview;_clipPlayer=player??new WpfMediaClipPlayer();
        body.Children.Add(Heading("Посмотреть и послушать фрагмент"));
        body.Children.Add(Note("Предпросмотр воспроизводит до 15 секунд с начала или конца выбранного диапазона. Звук зависит от выбранной дорожки и аудиоустройства Windows. Скачивание запускается отдельно."));
        body.Children.Add(Note("Если выбрано несколько языков, предпросмотр использует первый язык."));
        var view=Named("MediaClipVideo",_clipPlayer.View);
        body.Children.Add(new Border{Background=new SolidColorBrush(Color.FromRgb(13,18,30)),
            BorderBrush=new SolidColorBrush(Color.FromRgb(48,58,85)),BorderThickness=new Thickness(1),
            CornerRadius=new CornerRadius(5),Padding=new Thickness(4),Child=view,Margin=new Thickness(0,4,0,4)});
        _clipRange=Named("MediaClipRangeText",Note("Диапазон предпросмотра ещё не подготовлен."));body.Children.Add(_clipRange);
        _clipSeek=Named("MediaClipSeekSlider",new Slider{Minimum=0,Maximum=1,IsEnabled=false,
            SmallChange=.1,LargeChange=1,IsMoveToPointEnabled=true,Margin=new Thickness(0,4,0,4)});
        body.Children.Add(_clipSeek);
        _clipPosition=Named("MediaClipPositionText",Note(""));body.Children.Add(_clipPosition);
        var actions=new WrapPanel{Orientation=Orientation.Horizontal};
        _clipFirst=Named("PreviewMediaClipStartButton",new Button{Content="Начало фрагмента"});
        _clipLast=Named("PreviewMediaClipEndButton",new Button{Content="Конец фрагмента"});
        _clipPause=Named("PauseMediaClipButton",new Button{Content="Пауза",IsEnabled=false});
        _clipStop=Named("StopMediaClipButton",new Button{Content="Стоп",IsEnabled=false});
        foreach(var button in new[]{_clipFirst,_clipLast,_clipPause,_clipStop})actions.Children.Add(button);
        body.Children.Add(actions);
        _clipStatus=Named("MediaClipPlaybackStatus",Note("Укажите начало и конец фрагмента."));body.Children.Add(_clipStatus);
        _clipFirst.Click+=(_,_)=>StartClip(false);_clipLast.Click+=(_,_)=>StartClip(true);
        _clipPause.Click+=(_,_)=>PauseClip();_clipStop.Click+=(_,_)=>StopClip();
        _clipSeek.ValueChanged+=SeekClip;
        _start.TextChanged+=PlaybackTimeChanged;_end.TextChanged+=PlaybackTimeChanged;
        _mode.SelectionChanged+=PlaybackFormatChanged;_primary.SelectionChanged+=PlaybackFormatChanged;_audio.SelectionChanged+=PlaybackFormatChanged;
        _clipPlayer.Opened+=ClipOpened;_clipPlayer.Ended+=ClipEnded;_clipPlayer.Failed+=ClipFailed;
        _clipTimer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(150)};
        _clipTimer.Tick+=ClipPositionTick;
        Closed+=(_,_)=>ClosePlayback();
    }
    private PlaybackContext CurrentPlaybackContext()
    {
        var options=_options();bool manual=_mode.SelectedIndex==1;
        double? duration=_metadata is {Duration:>0} data && double.IsFinite(data.Duration)?data.Duration:null;
        return new(_editable,_metadata?.FormatsLoaded==true,_metadata?.IsCollection==true,duration,_start.Text,_end.Text,
            manual,manual?(_primary.SelectedItem as FormatChoice)?.Id:null,
            manual?(_audio.SelectedItem as FormatChoice)?.Id:null,options.AudioOnly,options.AudioTrack,options.AudioLanguages);
    }
    private bool TryPlaybackRequest(bool fromEnd,out MediaClipRequest? request,out string explanation)
    {
        request=null;var context=CurrentPlaybackContext();
        explanation=!context.Editable?"Предпросмотр приостановлен: строка недоступна для изменения.":
            !context.Loaded?"Дождитесь метаданных выбранного материала.":context.Collection?"Выберите отдельный материал вместо коллекции.":
            _clipPreview is null?"Воспроизведение предпросмотра недоступно.":"";
        if(explanation.Length>0)return false;
        if(!DownloadSection.TryParse(context.Start,context.End,context.Duration,out var section,out explanation))return false;
        if(section is null){explanation="Укажите начало и конец фрагмента.";return false;}
        if(context.Manual && _primary.SelectedItem is not FormatChoice {Format:not null})
        {explanation="Выберите доступный основной формат.";return false;}
        MediaFormat? primary=context.Manual?(_primary.SelectedItem as FormatChoice)?.Format:null;
        MediaFormat? audio=context.Manual?(_audio.SelectedItem as FormatChoice)?.Format:null;
        if(context.Manual && !string.IsNullOrEmpty(context.Audio) && audio is null)
        {explanation="Выберите доступный аудиоформат.";return false;}
        if(!new DownloadSelection(Row.Url,section,primary,audio,context.Duration).TryValidate(Row.Url,_options(),out explanation))return false;
        request=new(Row.Url,section.StartSeconds,section.EndSeconds,context.Primary,
            string.IsNullOrEmpty(context.Audio)?null:context.Audio,context.Duration,fromEnd,context.AudioOnly,context.AudioTrack,context.AudioLanguages);
        return true;
    }
    private void RefreshPlaybackState()
    {
        if(_playbackClosed || _clipFirst is null)return;
        var context=CurrentPlaybackContext();
        if(context==_playbackContext)return;
        _playbackContext=context;ResetClip();
        bool available=TryPlaybackRequest(false,out _,out string explanation);
        _clipFirst.IsEnabled=_clipLast!.IsEnabled=available;
        _clipStatus!.Text=available?"Нажмите «Начало фрагмента» или «Конец фрагмента». Подготовка не мешает применить выбранный диапазон.":explanation;
    }
    private void PlaybackTimeChanged(object sender,TextChangedEventArgs e)
    {if(!_loading)RefreshPlaybackState();}
    private void PlaybackFormatChanged(object sender,SelectionChangedEventArgs e)
    {if(!_loading)RefreshPlaybackState();}
    private void ResetClip()
    {
        _clipGeneration++;_clipCancellation?.Cancel();_clipCancellation=null;
        _clipTimer?.Stop();_clipPlaying=_clipOpened=_clipAutoPlay=false;
        // Detach the native media graph before deleting its owned local cache.
        try{_clipPlayer?.Clear();}finally{_clip?.Dispose();_clip=null;}
        if(_clipSeek is not null){_clipSeek.IsEnabled=false;SetClipPosition(0);}
        if(_clipPause is not null){_clipPause.IsEnabled=false;_clipPause.Content="Пауза";}
        if(_clipStop is not null)_clipStop.IsEnabled=false;
        if(_clipRange is not null)_clipRange.Text="Диапазон предпросмотра ещё не подготовлен.";
        if(_clipPosition is not null)_clipPosition.Text="";
    }
    private void StartClip(bool fromEnd)
    {
        if(_playbackClosed)return;
        RefreshPlaybackState();
        if(!TryPlaybackRequest(fromEnd,out var request,out string explanation))
        {_clipStatus!.Text=explanation;return;}
        ResetClip();
        var cancel=new CancellationTokenSource();_clipCancellation=cancel;
        _clipStop!.IsEnabled=true;
        _clipStatus!.Text="Готовим короткий фрагмент со звуком… Можно применить выбранные параметры, не дожидаясь готовности.";
        _=LoadClipAsync(request!,_clipGeneration,cancel);
    }
    private bool IsCurrentClip(long generation,CancellationTokenSource cancel)=>!_playbackClosed &&
        !cancel.IsCancellationRequested && generation==_clipGeneration && ReferenceEquals(_clipCancellation,cancel);
    private async Task LoadClipAsync(MediaClipRequest request,long generation,CancellationTokenSource cancel)
    {
        MediaClipPreview? prepared=null;
        try
        {
            var result=await _clipPreview!(request,cancel.Token);prepared=result.Clip;
            if(!IsCurrentClip(generation,cancel))return;
            if(result.Url!=request.Url || result.RequestedStart!=request.StartSeconds || result.RequestedEnd!=request.EndSeconds)
                throw new InvalidDataException("Источник вернул другой фрагмент.");
            if(!result.Success){_clipStatus!.Text=ClipFailure(result.Error);_clipStop!.IsEnabled=false;return;}
            if(!double.IsFinite(prepared!.StartSeconds) || !double.IsFinite(prepared.EndSeconds) ||
                prepared.StartSeconds<request.StartSeconds || prepared.EndSeconds>request.EndSeconds+.001 ||
                prepared.DurationSeconds<=0 || prepared.DurationSeconds>15.001)
                throw new InvalidDataException("Источник вернул некорректный диапазон предпросмотра.");
            _clip=prepared;prepared=null;
            _clipRange!.Text="Предпросмотр: "+DownloadSection.FormatTimestamp(_clip.StartSeconds)+" — "+
                DownloadSection.FormatTimestamp(_clip.EndSeconds)+" · "+(_clip.HasAudio?"со звуком":"источник без звука")+
                (_clip.HasVideo?"":" · только аудио");
            _clipSeek!.Maximum=_clip.DurationSeconds;SetClipPosition(0);
            _clipAutoPlay=true;_clipStatus!.Text="Открываем подготовленный фрагмент…";
            _clipPlayer!.Open(new Uri(_clip.Path,UriKind.Absolute));
        }
        catch(OperationCanceledException) when(cancel.IsCancellationRequested){ }
        catch(Exception error)
        {
            if(IsCurrentClip(generation,cancel))
            {ResetClip();_clipStatus!.Text=ClipFailure(error.Message);}
        }
        finally
        {
            prepared?.Dispose();
            if(ReferenceEquals(_clipCancellation,cancel))_clipCancellation=null;
            cancel.Dispose();
        }
    }
    private void ClipOpened()
    {
        if(_playbackClosed || _clip is null || !_editable)return;
        _clipOpened=true;_clipSeek!.IsEnabled=true;_clipPause!.IsEnabled=true;_clipStop!.IsEnabled=true;
        if(_clipAutoPlay)
        {
            _clipAutoPlay=false;
            try{_clipPlayer!.Play();}catch(Exception){ClipFailed();return;}
            _clipPlaying=true;_clipTimer!.Start();
        }
        _clipStatus!.Text=_clip.HasAudio?"Воспроизводим подготовленный фрагмент со звуком.":"Воспроизводим фрагмент. В выбранном источнике нет звука.";
    }
    private void ClipEnded()
    {
        if(_playbackClosed || _clip is null)return;
        _clipTimer!.Stop();
        try{_clipPlayer!.Stop();_clipPlayer.Position=TimeSpan.Zero;}catch(Exception){ClipFailed();return;}
        _clipPlaying=false;
        _clipPause!.Content="Продолжить";SetClipPosition(0);_clipStatus!.Text="Предпросмотр завершён.";
    }
    private void ClipFailed()
    {
        if(_playbackClosed || _clip is null)return;
        ResetClip();_clipStatus!.Text="Windows не смогла воспроизвести фрагмент. Проверьте поддержку видео и аудиоустройство. Выбранный диапазон можно применить.";
    }
    private void PauseClip()
    {
        if(_playbackClosed || !_editable || !_clipOpened || _clip is null)return;
        try
        {
            if(_clipPlaying){_clipPlayer!.Pause();_clipTimer!.Stop();_clipPause!.Content="Продолжить";_clipStatus!.Text="Предпросмотр на паузе.";}
            else{_clipPlayer!.Play();_clipTimer!.Start();_clipPause!.Content="Пауза";_clipStatus!.Text="Воспроизводим подготовленный фрагмент.";}
        }
        catch(Exception){ClipFailed();return;}
        _clipPlaying=!_clipPlaying;
    }
    private void StopClip()
    {
        if(_playbackClosed || !_editable)return;
        if(_clip is null || !_clipOpened){ResetClip();_clipStatus!.Text="Подготовка предпросмотра отменена.";return;}
        _clipTimer!.Stop();
        try{_clipPlayer!.Stop();_clipPlayer.Position=TimeSpan.Zero;}catch(Exception){ClipFailed();return;}
        _clipPlaying=false;
        _clipPause!.Content="Продолжить";SetClipPosition(0);_clipStatus!.Text="Предпросмотр остановлен.";
    }
    private void SeekClip(object sender,RoutedPropertyChangedEventArgs<double> e)
    {
        if(_clipUpdatingPosition || _playbackClosed || !_editable || !_clipOpened || _clip is null)return;
        double position=Math.Clamp(e.NewValue,0,_clip.DurationSeconds);
        try{_clipPlayer!.Position=TimeSpan.FromSeconds(position);}catch(Exception){ClipFailed();return;}
        SetClipPosition(position);
    }
    private void ClipPositionTick(object? sender,EventArgs e)
    {
        if(_playbackClosed || !_clipOpened || _clip is null)return;
        try{SetClipPosition(Math.Clamp(_clipPlayer!.Position.TotalSeconds,0,_clip.DurationSeconds));}
        catch(Exception){ClipFailed();}
    }
    private void SetClipPosition(double seconds)
    {
        _clipUpdatingPosition=true;
        try{if(_clipSeek is not null)_clipSeek.Value=seconds;}
        finally{_clipUpdatingPosition=false;}
        if(_clip is not null && _clipPosition is not null)
            _clipPosition.Text="Позиция источника: "+DownloadSection.FormatTimestamp(Math.Min(_clip.EndSeconds,_clip.StartSeconds+seconds));
    }
    private static string ClipFailure(string error)
    {
        string detail=JournalSanitizer.Redact(ErrorMessages.ForDisplay(error),multiline:false);
        if(detail.Length>512)detail=detail[..512];
        return detail.Length==0?"Предпросмотр недоступен. Выбранный диапазон можно применить.":"Предпросмотр недоступен: "+detail;
    }
    private void ClosePlayback()
    {
        if(_playbackClosed)return;_playbackClosed=true;
        _start.TextChanged-=PlaybackTimeChanged;_end.TextChanged-=PlaybackTimeChanged;
        _mode.SelectionChanged-=PlaybackFormatChanged;_primary.SelectionChanged-=PlaybackFormatChanged;_audio.SelectionChanged-=PlaybackFormatChanged;
        ResetClip();
        if(_clipTimer is not null){_clipTimer.Tick-=ClipPositionTick;_clipTimer=null;}
        if(_clipPlayer is not null)
        {_clipPlayer.Opened-=ClipOpened;_clipPlayer.Ended-=ClipEnded;_clipPlayer.Failed-=ClipFailed;_clipPlayer.Dispose();}
    }
}
