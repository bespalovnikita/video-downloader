using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Binding=System.Windows.Data.Binding;
using Color=System.Windows.Media.Color;

namespace VideoDownloader.Native;

// Edits a single captured row. Metadata is supplied by the main preview owner;
// Frame extraction is delegated to the shared background owner. This view
// never reads disk or starts a queue download.
internal sealed partial class MediaSelectionDialog : Window
{
    private readonly Func<DownloadOptions> _options;
    private readonly Func<DownloadSelection?,string?> _apply;
    private readonly TextBlock _metadataStatus;
    private readonly TextBlock _duration;
    private readonly TextBlock _editState;
    private readonly TextBlock _error;
    private readonly ComboBox _mode;
    private readonly ComboBox _primary;
    private readonly ComboBox _audio;
    private readonly TextBox _start;
    private readonly TextBox _end;
    private readonly Button _applyButton;
    private readonly Button _resetButton;
    private readonly DataGrid _catalog;
    private PreviewData? _metadata;
    private MediaFormat[]? _catalogReference;
    private bool _editable=true;
    private bool _loading;
    private string _wantedPrimary="";
    private string _wantedAudio="";
    internal DownloadItem Row { get; }

    internal MediaSelectionDialog(Window owner,DownloadItem row,Func<DownloadOptions> options,
        Func<DownloadSelection?,string?> apply,
        Func<MediaFrameRequest,CancellationToken,Task<MediaFrameResult>>? framePreview=null,
        Func<MediaClipRequest,CancellationToken,Task<MediaClipResult>>? clipPreview=null,IMediaClipPlayer? clipPlayer=null,
        Func<MediaWaveformRequest,CancellationToken,Task<MediaWaveformResult>>? waveformPreview=null)
    {
        Row=row;_options=options;_apply=apply;
        Name="MediaSelectionDialog";Title="Формат и фрагмент";Owner=owner;
        WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Width=Math.Min(1000,Math.Max(600,SystemParameters.WorkArea.Width-48));
        Height=Math.Min(760,Math.Max(440,SystemParameters.WorkArea.Height-48));
        MinWidth=600;MinHeight=440;
        Background=owner.Background;Foreground=owner.Foreground;FontFamily=owner.FontFamily;
        Resources.MergedDictionaries.Add(owner.Resources);
        NameScope.SetNameScope(this,new NameScope());
        var root=new DockPanel{Margin=new Thickness(16)};
        var footer=new StackPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        _applyButton=Named("ApplyMediaSelectionButton",new Button{Content="Применить",IsDefault=true});
        _resetButton=Named("ResetMediaSelectionButton",new Button{Content="Сбросить параметры строки"});
        if(TryFindResource("GuardedActionButton") is Style guarded)
        {_applyButton.Style=guarded;_resetButton.Style=guarded;}
        var close=Named("CloseMediaSelectionButton",new Button{Content="Закрыть"});
        footer.Children.Add(_resetButton);footer.Children.Add(_applyButton);footer.Children.Add(close);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();
        root.Children.Add(new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto});
        Content=root;

        body.Children.Add(new TextBlock{Text=row.Title.Length>0?row.Title:"Выбранный материал",FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        body.Children.Add(Named("MediaSelectionUrl",new TextBox{Text=row.Url,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,8)}));
        body.Children.Add(Note("Параметры относятся только к этой строке очереди. Скачивание запускается отдельно. Готовый файл и история сохраняются при изменении выбора."));
        _editState=Named("MediaSelectionEditState",Note(""));body.Children.Add(_editState);
        _metadataStatus=Named("MediaCatalogStatus",Note("Получаем список форматов…"));body.Children.Add(_metadataStatus);
        _duration=Named("MediaDurationText",Note("Длительность пока неизвестна."));body.Children.Add(_duration);

        body.Children.Add(Heading("Доступные форматы источника"));
        _catalog=Named("MediaFormatsGrid",new DataGrid{IsReadOnly=true,AutoGenerateColumns=false,Height=210,MinHeight=120,
            CanUserAddRows=false,CanUserDeleteRows=false,EnableRowVirtualization=true,EnableColumnVirtualization=true,
            SelectionMode=DataGridSelectionMode.Single,Margin=new Thickness(0,4,0,8)});
        foreach(var (header,path,width) in new (string,string,double)[]{
            ("ID","Id",85),("Тип","Kind",100),("Разрешение","Resolution",105),("кадр/с","Fps",65),
            ("HDR","DynamicRange",80),("Видеокодек","VideoCodec",120),("Аудиокодек","AudioCodec",100),
            ("Язык","Language",70),("Размер","Size",110)})
        {
            var column=new DataGridTextColumn{Header=header,Binding=new Binding(path),Width=width};
            if(TryFindResource("GridTextStyle") is Style style)column.ElementStyle=style;
            _catalog.Columns.Add(column);
        }
        body.Children.Add(_catalog);
        body.Children.Add(Note("Размер относится к полному потоку, а не к выбранному фрагменту. «Неизвестно» означает, что источник не сообщил размер; знак ≈ обозначает оценку. Размер готового файла после объединения или обрезки может отличаться."));

        body.Children.Add(Heading("Выбор форматов для скачивания"));
        _mode=Named("MediaFormatModeCombo",new ComboBox{Margin=new Thickness(0,4,0,8)});
        _mode.Items.Add("Автоматически по общим настройкам");_mode.Items.Add("Выбрать точные форматы");
        body.Children.Add(_mode);
        body.Children.Add(Note("Точные форматы имеют приоритет над Quality и Codec. Совместимость с контейнером, MP3 и выбором аудиодорожки проверяется при применении."));
        body.Children.Add(Heading("Основной формат — видео, видео со звуком или аудио"));
        _primary=Named("PrimaryMediaFormatCombo",FormatCombo());body.Children.Add(_primary);
        body.Children.Add(Heading("Отдельное аудио — для основного видео без звука"));
        _audio=Named("AudioMediaFormatCombo",FormatCombo());body.Children.Add(_audio);

        body.Children.Add(Heading("Фрагмент"));
        body.Children.Add(Note("Пустые поля — весь материал. Укажи начало и конец в формате HH:MM:SS; допустимы доли секунды. Для плейлиста сначала добавь отдельные материалы в очередь."));
        var range=new Grid{Margin=new Thickness(0,5,0,8)};
        range.ColumnDefinitions.Add(new ColumnDefinition());range.ColumnDefinitions.Add(new ColumnDefinition());
        var startPanel=new StackPanel{Margin=new Thickness(0,0,6,0)};
        startPanel.Children.Add(Note("Начало"));_start=Named("MediaSectionStartBox",new TextBox{ToolTip="Например: 00:02:15"});startPanel.Children.Add(_start);
        var endPanel=new StackPanel{Margin=new Thickness(6,0,0,0)};
        endPanel.Children.Add(Note("Конец"));_end=Named("MediaSectionEndBox",new TextBox{ToolTip="Например: 00:05:40"});endPanel.Children.Add(_end);
        Grid.SetColumn(endPanel,1);range.Children.Add(startPanel);range.Children.Add(endPanel);body.Children.Add(range);
        InitializeSectionList(body,range,row.Selection);
        InitializeTimeline(body);
        InitializeFramePreviews(body,framePreview);
        InitializePlayback(body,clipPreview,clipPlayer);
        InitializeWaveforms(body,waveformPreview);
        _error=Named("MediaSelectionError",new TextBlock{Foreground=Brushes.Salmon,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,8,0,0)});
        body.Children.Add(_error);

        var initial=row.Selection;
        _wantedPrimary=initial?.Format?.Id??"";_wantedAudio=initial?.AudioFormat?.Id??"";
        _mode.SelectedIndex=initial?.Format==null?0:1;
        _start.Text=initial?.EffectiveSections.FirstOrDefault() is {} section?DownloadSection.FormatTimestamp(section.StartSeconds):"";
        _end.Text=initial?.EffectiveSections.FirstOrDefault() is {} endSection?DownloadSection.FormatTimestamp(endSection.EndSeconds):"";
        PopulateFormats([]);
        _mode.SelectionChanged+=(_,_)=>RefreshEnabled();
        _primary.SelectionChanged+=(_,_)=>{if(!_loading)_wantedPrimary=(_primary.SelectedItem as FormatChoice)?.Id??"";};
        _audio.SelectionChanged+=(_,_)=>{if(!_loading)_wantedAudio=(_audio.SelectedItem as FormatChoice)?.Id??"";};
        _applyButton.Click+=(_,_)=>Apply();
        _resetButton.Click+=(_,_)=>Commit(null);
        close.Click+=(_,_)=>Close();
        PreviewKeyDown+=(_,e)=>{if(e.Key==System.Windows.Input.Key.Escape){e.Handled=true;Close();}};
        RefreshEnabled();
        RefreshFrameState();
        RefreshTimelineState();
        RefreshPlaybackState();
        RefreshWaveformState();
    }

    private T Named<T>(string name,T control) where T:FrameworkElement
    {control.Name=name;RegisterName(name,control);return control;}
    private static TextBlock Note(string text)=>new(){Text=text,Foreground=new SolidColorBrush(Color.FromRgb(183,192,216)),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};
    private static TextBlock Heading(string text)=>new(){Text=text,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,3)};
    private static ComboBox FormatCombo()
    {
        var text=new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty,new Binding(nameof(FormatChoice.Label)));
        text.SetValue(TextBlock.TextWrappingProperty,TextWrapping.Wrap);text.SetValue(FrameworkElement.MaxWidthProperty,850d);
        return new ComboBox{ItemTemplate=new DataTemplate{VisualTree=text},SelectedValuePath=nameof(FormatChoice.Id),
            MinHeight=36,Margin=new Thickness(0,3,0,6)};
    }

    internal void SetEditable(bool editable)
    {
        _editable=editable;
        _editState.Text=editable?"":"Изменение недоступно: строка выполняется, удаляется либо файл настроек защищён от записи.";
        RefreshEnabled();
        RefreshFrameState();
        RefreshTimelineState();
        RefreshPlaybackState();
        RefreshWaveformState();
    }
    private void RefreshEnabled()
    {
        _mode.IsEnabled=_editable;
        _primary.IsEnabled=_audio.IsEnabled=_editable && _mode.SelectedIndex==1;
        _start.IsEnabled=_end.IsEnabled=_editable;
        _applyButton.IsEnabled=_resetButton.IsEnabled=_editable;
        RefreshSectionControls();
    }

    internal void ReceiveMetadata(PreviewData data)
    {
        if(!string.Equals(data.Url,Row.Url,StringComparison.Ordinal))return;
        _metadata=data;
        _duration.Text=double.IsFinite(data.Duration) && data.Duration>0
            ? "Длительность источника: "+DownloadSection.FormatTimestamp(data.Duration) : "Источник не сообщил длительность.";
        _metadataStatus.Text=!data.FormatsLoaded?"Получаем список форматов…":data.IsCollection
            ? "Это коллекция. Для точного формата или фрагмента добавь отдельный материал в очередь."
            : data.Formats.Length==0?"Метаданные получены. Источник не предоставил список отдельных форматов."
            : data.FormatsTruncated?$"Показано последних {data.Formats.Length} форматов: список источника больше, часть ранних форматов скрыта."
            : $"Доступно форматов: {data.Formats.Length}.";
        if(!ReferenceEquals(_catalogReference,data.Formats))
        {
            _catalogReference=data.Formats;
            _catalog.ItemsSource=data.Formats.Select(format=>new FormatRow(format)).ToArray();
            PopulateFormats(data.Formats);
        }
        RefreshFrameState();
        RefreshTimelineState();
        RefreshPlaybackState();
        RefreshWaveformState();
    }
    internal void ReceiveMetadataFailure(string error)
    {
        _metadataStatus.Text="Не удалось обновить метаданные: "+error;
    }
    private void PopulateFormats(MediaFormat[] formats)
    {
        _loading=true;
        try
        {
            var primary=formats.Select(format=>new FormatChoice(format.Id,format.DisplayName,format)).ToList();
            if(_wantedPrimary.Length>0 && primary.All(choice=>choice.Id!=_wantedPrimary))
                primary.Insert(0,new FormatChoice(_wantedPrimary,"Сохранённый ID "+_wantedPrimary+" — пока не подтверждён",null));
            _primary.ItemsSource=primary;_primary.SelectedItem=primary.FirstOrDefault(choice=>choice.Id==_wantedPrimary);
            var audio=formats.Where(format=>format.IsAudioOnly).Select(format=>new FormatChoice(format.Id,format.DisplayName,format)).ToList();
            audio.Insert(0,new FormatChoice("","Без отдельного аудиоформата",null));
            if(_wantedAudio.Length>0 && audio.All(choice=>choice.Id!=_wantedAudio))
                audio.Add(new FormatChoice(_wantedAudio,"Сохранённый ID "+_wantedAudio+" — пока не подтверждён",null));
            _audio.ItemsSource=audio;_audio.SelectedItem=audio.FirstOrDefault(choice=>choice.Id==_wantedAudio)??audio[0];
        }
        finally{_loading=false;}
    }

    private void Apply()
    {
        _error.Text="";
        if(!_editable){_error.Text="Эта строка недоступна для изменения.";return;}
        double? duration=_metadata is {Duration:>0} data && double.IsFinite(data.Duration)?data.Duration:null;
        if(!TryReadSections(duration,out var sections,out string error))
        {_error.Text=ErrorMessages.ForDisplay(error);return;}
        var section=sections.FirstOrDefault();
        bool manual=_mode.SelectedIndex==1;
        if((manual || section!=null) && _metadata?.FormatsLoaded!=true)
        {_error.Text="Дождитесь получения метаданных выбранного материала.";return;}
        if((manual || section!=null) && _metadata?.IsCollection==true)
        {_error.Text="Формат и фрагмент выбираются для отдельного материала, а не для коллекции.";return;}
        MediaFormat? primary=null,audio=null;
        if(manual)
        {
            if(_primary.SelectedItem is not FormatChoice {Format:{} selected})
            {_error.Text="Выберите доступный основной формат из списка источника.";return;}
            primary=selected;
            if(_audio.SelectedItem is FormatChoice choice)
            {
                if(choice.Id.Length>0 && choice.Format==null)
                {_error.Text="Сохранённый аудиоформат отсутствует в текущем списке. Выберите доступный формат.";return;}
                audio=choice.Format;
            }
        }
        var selection=manual || section!=null ? new DownloadSelection(Row.Url,section,primary,audio,duration)
            {Sections=sections.Length>1?new DownloadSectionSet(sections):null,
                FragmentOutputMode=_mergeDraft?"Merged":"Separate",AllowMergeReencode=_mergeDraft && _mergeReencodeDraft} : null;
        if(selection!=null && !selection.TryValidate(Row.Url,_options(),out error)){_error.Text=ErrorMessages.ForDisplay(error);return;}
        Commit(selection);
    }
    private void Commit(DownloadSelection? selection)
    {
        if(!_editable){_error.Text="Эта строка недоступна для изменения.";return;}
        string? error=_apply(selection);
        if(!string.IsNullOrEmpty(error)){_error.Text=ErrorMessages.ForDisplay(error);return;}
        Close();
    }

    private sealed record FormatChoice(string Id,string Label,MediaFormat? Format);
    private sealed class FormatRow(MediaFormat format)
    {
        public string Id=>format.Id;
        public string Kind=>format.HasVideo?(format.HasAudio?"Видео + звук":"Видео"):"Аудио";
        public string Resolution=>format.Width>0 && format.Height>0?$"{format.Width}×{format.Height}":"—";
        public string Fps=>format.Fps>0?format.Fps.ToString("0.###",CultureInfo.InvariantCulture):"—";
        public string DynamicRange=>string.IsNullOrWhiteSpace(format.DynamicRange)?"Неизвестно":format.DynamicRange;
        public string VideoCodec=>format.HasVideo?format.VideoCodec:"—";
        public string AudioCodec=>format.HasAudio?format.AudioCodec:"—";
        public string Language=>string.IsNullOrWhiteSpace(format.Language)?"—":format.Language;
        public string Size=>format.EstimatedSizeBytes is >0?$"{(format.SizeIsApproximate?"≈ ":"")}{DisplayValueConverter.Bytes(format.EstimatedSizeBytes.Value)}":"Неизвестно";
    }
}
