using System.Windows;
using System.Windows.Controls;
using Orientation=System.Windows.Controls.Orientation;

namespace VideoDownloader.Native;

internal sealed partial class MediaSelectionDialog
{
    private sealed record WaveformContext(PlaybackContext Playback,string Cookies,bool Available);
    private sealed class WaveformCard(bool isEnd,AudioWaveformView view,Button request,Button cancel,TextBlock status,TextBlock range)
    {
        internal readonly bool IsEnd=isEnd;
        internal readonly AudioWaveformView View=view;
        internal readonly Button Request=request,Cancel=cancel;
        internal readonly TextBlock Status=status,Range=range;
        internal CancellationTokenSource? Cancellation;
        internal long Generation;
    }
    private Func<MediaWaveformRequest,CancellationToken,Task<MediaWaveformResult>>? _waveformPreview;
    private WaveformCard? _waveStart,_waveEnd;
    private WaveformContext? _waveContext;
    private bool _waveformClosed,_waveformEditing;

    private void InitializeWaveforms(StackPanel body,
        Func<MediaWaveformRequest,CancellationToken,Task<MediaWaveformResult>>? preview)
    {
        _waveformPreview=preview;
        body.Children.Add(Heading("Звуковая волна у границ"));
        body.Children.Add(Note("Нажмите «Волна начала» или «Волна конца»: показываем измеренную громкость короткого участка звука вокруг границы. Щелчок по волне или перетаскивание метки меняет только соответствующее поле времени. Стрелки сдвигают метку на 0.1 с, Shift — на 1 с."));
        WaveformCard Card(bool isEnd)
        {
            string name=isEnd?"End":"Start";
            body.Children.Add(Heading(isEnd?"Конец фрагмента":"Начало фрагмента"));
            var actions=new WrapPanel{Orientation=Orientation.Horizontal};
            var request=Named("PreviewMediaWaveform"+name+"Button",new Button{Content=isEnd?"Волна конца":"Волна начала"});
            var cancel=Named("CancelMediaWaveform"+name+"Button",new Button{Content="Отменить волну",IsEnabled=false});
            actions.Children.Add(request);actions.Children.Add(cancel);body.Children.Add(actions);
            var view=Named("MediaWaveform"+name+"View",new AudioWaveformView(isEnd));
            RegisterName("MediaWaveform"+name+"Thumb",view.BoundaryHandle);body.Children.Add(view);
            var range=Named("MediaWaveform"+name+"Range",Note(""));body.Children.Add(range);
            var status=Named("MediaWaveform"+name+"Status",Note("Укажите начало и конец фрагмента."));body.Children.Add(status);
            var card=new WaveformCard(isEnd,view,request,cancel,status,range);
            request.Click+=(_,_)=>StartWaveform(card);cancel.Click+=(_,_)=>CancelWaveform(card,"Подготовка звуковой волны отменена.");
            view.BoundaryChanged+=seconds=>WaveformBoundaryChanged(card,seconds);
            return card;
        }
        _waveStart=Card(false);_waveEnd=Card(true);
        _start.TextChanged+=WaveformTimeChanged;_end.TextChanged+=WaveformTimeChanged;
        _mode.SelectionChanged+=WaveformFormatChanged;_primary.SelectionChanged+=WaveformFormatChanged;_audio.SelectionChanged+=WaveformFormatChanged;
        if(_sectionList is not null)_sectionList.SelectionChanged+=WaveformSectionChanged;
        if(_multipleSections is not null){_multipleSections.Checked+=WaveformSectionModeChanged;_multipleSections.Unchecked+=WaveformSectionModeChanged;}
        if(_clipStop is not null)_clipStop.Click+=WaveformPlaybackStopped;
        Closed+=(_,_)=>CloseWaveforms();
    }
    private WaveformContext CurrentWaveformContext()=>new(CurrentPlaybackContext(),_options().Cookies,
        TryPlaybackRequest(false,out _,out _));
    private void RefreshWaveformState(WaveformCard? preserve=null,bool force=false)
    {
        if(_waveformClosed || _waveStart is null || _waveEnd is null)return;
        var context=CurrentWaveformContext();
        if(!force && context==_waveContext)return;
        _waveContext=context;
        foreach(var card in new[]{_waveStart,_waveEnd})
        {
            var data=ReferenceEquals(card,preserve)?card.View.Data:null;
            bool valid=_waveformPreview is not null && TryPlaybackRequest(card.IsEnd,out _,out _);
            // A Thumb must stay enabled and captured throughout its own drag.
            // This data still describes the same fixed source window; only its
            // marker changes, while every other pending generation is revoked.
            CancelWaveform(card,"",keepView:data is not null && valid);
            card.Request.IsEnabled=valid;
            if(data is not null && valid && UpdateWaveformView(card,data))
            {card.Status.Text="Граница изменена. Волна показывает тот же участок источника; для нового участка нажмите кнопку волны.";continue;}
            card.Status.Text=!_editable?"Звуковая волна недоступна: строка защищена от изменения.":
                _waveformPreview is null?"Звуковая волна недоступна.":
                TryPlaybackRequest(card.IsEnd,out _,out string explanation)?"Нажмите кнопку волны, чтобы подготовить короткий участок.":explanation;
        }
    }
    private void WaveformTimeChanged(object sender,TextChangedEventArgs e)
    {if(!_loading && !_waveformEditing)RefreshWaveformState();}
    private void WaveformFormatChanged(object sender,SelectionChangedEventArgs e)
    {if(!_loading)RefreshWaveformState();}
    private void WaveformSectionChanged(object sender,SelectionChangedEventArgs e)
    {if(!_loading)RefreshWaveformState(force:true);}
    private void WaveformSectionModeChanged(object sender,RoutedEventArgs e)
    {if(!_loading)RefreshWaveformState(force:true);}
    private void WaveformPlaybackStopped(object sender,RoutedEventArgs e)
    {if(_editable && !_waveformClosed)RefreshWaveformState(force:true);}
    private static async Task CancelWaveformRequestAsync(CancellationTokenSource cancellation)
    {
        try{await cancellation.CancelAsync();}catch(ObjectDisposedException){ }catch(AggregateException){ }
    }
    private void CancelWaveform(WaveformCard card,string explanation,bool keepView=false)
    {
        card.Generation++;
        if(card.Cancellation is {} cancellation){card.Cancellation=null;_=CancelWaveformRequestAsync(cancellation);}
        card.Cancel.IsEnabled=false;
        if(!keepView){card.View.SetUnavailable();card.Range.Text="";}
        if(explanation.Length>0)card.Status.Text=explanation;
    }
    private void StartWaveform(WaveformCard card)
    {
        if(_waveformClosed || !_editable)return;
        RefreshWaveformState();
        MediaClipRequest? clip=null;string explanation="";
        if(_waveformPreview is null || !TryPlaybackRequest(card.IsEnd,out clip,out explanation))
        {card.Status.Text=_waveformPreview is null?"Звуковая волна недоступна.":explanation;return;}
        CancelWaveform(card,"");
        var cancellation=new CancellationTokenSource();card.Cancellation=cancellation;card.Cancel.IsEnabled=true;
        card.Status.Text="Готовим короткий участок и измеряем звук…";
        _=LoadWaveformAsync(card,new MediaWaveformRequest(clip!,card.IsEnd),CurrentWaveformContext(),card.Generation,cancellation);
    }
    private bool CurrentWaveform(WaveformCard card,WaveformContext context,long generation,CancellationTokenSource cancellation)=>
        !_waveformClosed && _editable && !cancellation.IsCancellationRequested && ReferenceEquals(card.Cancellation,cancellation) &&
        card.Generation==generation && CurrentWaveformContext()==context;
    private async Task LoadWaveformAsync(WaveformCard card,MediaWaveformRequest request,WaveformContext context,long generation,CancellationTokenSource cancellation)
    {
        try
        {
            var result=await _waveformPreview!(request,cancellation.Token);
            if(!CurrentWaveform(card,context,generation,cancellation))return;
            double boundary=card.IsEnd?request.ClipRequest.EndSeconds:request.ClipRequest.StartSeconds;
            if(result.Url!=Row.Url || result.RequestedSeconds!=boundary)throw new InvalidDataException("Источник вернул другую границу звуковой волны.");
            if(!result.Success){card.Status.Text=ClipFailure(result.Error);return;}
            var data=result.Waveform!;
            if(data.DurationSeconds>15.001 || boundary<data.StartSeconds-.001 || boundary>data.EndSeconds+.001 ||
                context.Playback.Duration is {} duration && data.EndSeconds>duration+.001)
                throw new InvalidDataException("Источник вернул некорректный участок звуковой волны.");
            if(!UpdateWaveformView(card,data))throw new InvalidDataException("Граница звуковой волны недоступна для изменения.");
            card.Status.Text=data.Bins.All(bin=>bin.Peak==0)?"В этом участке есть аудиодорожка, но измеренный звук — тишина.":
                "Щёлкните по волне или перетащите метку, чтобы уточнить "+(card.IsEnd?"конец":"начало")+" фрагмента.";
        }
        catch(OperationCanceledException) when(cancellation.IsCancellationRequested){ }
        catch(Exception error)
        {if(CurrentWaveform(card,context,generation,cancellation)){card.View.SetUnavailable();card.Range.Text="";card.Status.Text=ClipFailure(error.Message);}}
        finally
        {
            if(ReferenceEquals(card.Cancellation,cancellation)){card.Cancellation=null;card.Cancel.IsEnabled=false;}
            cancellation.Dispose();
            if(!_waveformClosed)RefreshWaveformState();
        }
    }
    private bool UpdateWaveformView(WaveformCard card,AudioWaveform data)
    {
        double? duration=CurrentPlaybackContext().Duration;
        if(!DownloadSection.TryParse(_start.Text,_end.Text,duration,out var section,out _) || section is null)return false;
        double boundary=card.IsEnd?section.EndSeconds:section.StartSeconds;
        double minimum=card.IsEnd?section.StartSeconds+.001:0;
        double maximum=card.IsEnd?(duration??data.EndSeconds):section.EndSeconds-.001;
        card.View.SetData(data,boundary,minimum,maximum,_editable);
        card.Range.Text="Участок источника: "+DownloadSection.FormatTimestamp(data.StartSeconds)+" — "+DownloadSection.FormatTimestamp(data.EndSeconds)+
            " · "+(card.IsEnd?"Конец: ":"Начало: ")+DownloadSection.FormatTimestamp(boundary)+
            (data.DecodedEndSeconds<data.EndSeconds-.05?" · звук заканчивается около "+DownloadSection.FormatTimestamp(data.DecodedEndSeconds):"");
        return true;
    }
    private void WaveformBoundaryChanged(WaveformCard card,double seconds)
    {
        if(_waveformClosed || !_editable)return;
        if(CurrentWaveformContext()!=_waveContext){RefreshWaveformState();return;}
        var data=card.View.Data;if(data is null)return;
        _waveformEditing=true;
        try{(card.IsEnd?_end:_start).Text=DownloadSection.FormatTimestamp(seconds);}
        finally{_waveformEditing=false;}
        RefreshWaveformState(card,force:true);
    }
    private void CloseWaveforms()
    {
        if(_waveformClosed)return;_waveformClosed=true;
        _start.TextChanged-=WaveformTimeChanged;_end.TextChanged-=WaveformTimeChanged;
        _mode.SelectionChanged-=WaveformFormatChanged;_primary.SelectionChanged-=WaveformFormatChanged;_audio.SelectionChanged-=WaveformFormatChanged;
        if(_sectionList is not null)_sectionList.SelectionChanged-=WaveformSectionChanged;
        if(_multipleSections is not null){_multipleSections.Checked-=WaveformSectionModeChanged;_multipleSections.Unchecked-=WaveformSectionModeChanged;}
        if(_clipStop is not null)_clipStop.Click-=WaveformPlaybackStopped;
        if(_waveStart is not null)CancelWaveform(_waveStart,"");if(_waveEnd is not null)CancelWaveform(_waveEnd,"");
    }
}
