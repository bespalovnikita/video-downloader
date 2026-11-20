using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Binding=System.Windows.Data.Binding;
using Color=System.Windows.Media.Color;
using Control=System.Windows.Controls.Control;

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
    private readonly TextBlock _startError;
    private readonly TextBlock _endError;
    private readonly TextBlock _formatError;
    private readonly Expander _formatsExpander;
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
        Resources.MergedDictionaries.Add(owner.Resources);
        SetResourceReference(BackgroundProperty,"WindowBackgroundBrush");
        SetResourceReference(ForegroundProperty,"WindowTextBrush");FontFamily=owner.FontFamily;
        NameScope.SetNameScope(this,new NameScope());
        var root=new DockPanel{Margin=new Thickness(16)};
        var footer=new StackPanel{Margin=new Thickness(0,8,0,0)};
        _error=Named("MediaSelectionError",ValidationNote());
        AutomationProperties.SetLiveSetting(_error,AutomationLiveSetting.Assertive);
        footer.Children.Add(_error);
        InitializeDiscardPrompt(footer);
        var footerActions=new WrapPanel{Orientation=System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment=System.Windows.HorizontalAlignment.Right,Margin=new Thickness(0,12,0,0)};
        _applyButton=Named("ApplyMediaSelectionButton",new Button{Content="Применить",IsDefault=true});
        _resetButton=Named("ResetMediaSelectionButton",new Button{Content="Сбросить параметры строки"});
        if(TryFindResource("GuardedActionButton") is Style guarded)
        {_applyButton.Style=guarded;_resetButton.Style=guarded;}
        var close=Named("CloseMediaSelectionButton",new Button{Content="Закрыть"});
        footerActions.Children.Add(_resetButton);footerActions.Children.Add(_applyButton);footerActions.Children.Add(close);
        footer.Children.Add(footerActions);
        DockPanel.SetDock(footer,Dock.Bottom);root.Children.Add(footer);
        var body=new StackPanel();
        root.Children.Add(Named("MediaSelectionScrollViewer",new ScrollViewer{Content=body,VerticalScrollBarVisibility=ScrollBarVisibility.Auto}));
        Content=root;

        body.Children.Add(new TextBlock{Text=row.Title.Length>0?row.Title:"Выбранный материал",FontSize=17,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap});
        body.Children.Add(Named("MediaSelectionUrl",new TextBox{Text=row.Url,IsReadOnly=true,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,6,0,8)}));
        body.Children.Add(Note("Параметры относятся только к этой строке очереди. Скачивание запускается отдельно. Готовый файл и история сохраняются при изменении выбора."));
        _editState=Named("MediaSelectionEditState",Note(""));body.Children.Add(_editState);
        _metadataStatus=Named("MediaCatalogStatus",Note("Получаем список форматов…"));body.Children.Add(_metadataStatus);
        _duration=Named("MediaDurationText",Note("Длительность пока неизвестна."));body.Children.Add(_duration);

        body.Children.Add(Heading("Формат скачивания"));
        _mode=Named("MediaFormatModeCombo",new ComboBox{Margin=new Thickness(0,4,0,8)});
        AutomationProperties.SetName(_mode,"Способ выбора формата");
        _mode.Items.Add("Автоматически по общим настройкам");_mode.Items.Add("Выбрать точные форматы");
        body.Children.Add(_mode);

        // Build the expert controls before the preview handlers subscribe to
        // them, but keep them after the fragment editor and collapsed in auto.
        var formats=new StackPanel{Margin=new Thickness(8,4,8,8)};
        formats.Children.Add(Note("Точные форматы имеют приоритет над качеством и кодеком очереди. Для обычной обрезки оставьте автоматический выбор."));
        formats.Children.Add(Heading("Доступные форматы источника"));
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
        formats.Children.Add(_catalog);
        formats.Children.Add(Note("Размер относится к полному потоку, а не к выбранному фрагменту. «Неизвестно» означает, что источник не сообщил размер; знак ≈ обозначает оценку. Размер готового файла после объединения или обрезки может отличаться."));
        formats.Children.Add(Heading("Основной формат — видео, видео со звуком или аудио"));
        _primary=Named("PrimaryMediaFormatCombo",FormatCombo());formats.Children.Add(_primary);
        AutomationProperties.SetName(_primary,"Основной формат источника");
        formats.Children.Add(Heading("Отдельное аудио — для основного видео без звука"));
        _audio=Named("AudioMediaFormatCombo",FormatCombo());formats.Children.Add(_audio);
        AutomationProperties.SetName(_audio,"Отдельный аудиоформат источника");
        _formatError=Named("MediaFormatError",ValidationNote());formats.Children.Add(_formatError);
        _formatsExpander=Named("MediaFormatsExpander",new Expander{Header="Формат: расширенные настройки и каталог источника",Content=formats,
            Margin=new Thickness(0,10,0,0),IsExpanded=row.Selection?.Format is not null});

        body.Children.Add(Heading("Фрагмент"));
        body.Children.Add(Note("Пустые поля — весь материал. Укажи начало и конец в формате HH:MM:SS; допустимы доли секунды. Для плейлиста сначала добавь отдельные материалы в очередь."));
        var range=new Grid{Margin=new Thickness(0,5,0,8)};
        range.ColumnDefinitions.Add(new ColumnDefinition());range.ColumnDefinitions.Add(new ColumnDefinition());
        var startPanel=new StackPanel{Margin=new Thickness(0,0,6,0)};
        startPanel.Children.Add(Note("Начало"));_start=Named("MediaSectionStartBox",new TextBox{ToolTip="Например: 00:02:15"});startPanel.Children.Add(_start);
        AutomationProperties.SetName(_start,"Начало фрагмента");
        _startError=Named("MediaSectionStartError",ValidationNote());startPanel.Children.Add(_startError);
        var endPanel=new StackPanel{Margin=new Thickness(6,0,0,0)};
        endPanel.Children.Add(Note("Конец"));_end=Named("MediaSectionEndBox",new TextBox{ToolTip="Например: 00:05:40"});endPanel.Children.Add(_end);
        AutomationProperties.SetName(_end,"Конец фрагмента");
        _endError=Named("MediaSectionEndError",ValidationNote());endPanel.Children.Add(_endError);
        Grid.SetColumn(endPanel,1);range.Children.Add(startPanel);range.Children.Add(endPanel);body.Children.Add(range);
        InitializeSectionList(body,range,row.Selection);
        InitializeTimeline(body);
        InitializeFramePreviews(body,framePreview);
        InitializePlayback(body,clipPreview,clipPlayer);
        InitializeWaveforms(body,waveformPreview);
        body.Children.Add(_formatsExpander);

        var initial=row.Selection;
        _wantedPrimary=initial?.Format?.Id??"";_wantedAudio=initial?.AudioFormat?.Id??"";
        _mode.SelectedIndex=initial?.Format==null?0:1;
        _start.Text=initial?.EffectiveSections.FirstOrDefault() is {} section?DownloadSection.FormatTimestamp(section.StartSeconds):"";
        _end.Text=initial?.EffectiveSections.FirstOrDefault() is {} endSection?DownloadSection.FormatTimestamp(endSection.EndSeconds):"";
        PopulateFormats([]);
        _mode.SelectionChanged+=(_,_)=>
        {
            ClearValidation();RefreshEnabled();
            if(_mode.SelectedIndex==1)RevealFormats(_primary);
        };
        _primary.SelectionChanged+=(_,_)=>{if(!_loading)_wantedPrimary=(_primary.SelectedItem as FormatChoice)?.Id??"";};
        _audio.SelectionChanged+=(_,_)=>{if(!_loading)_wantedAudio=(_audio.SelectedItem as FormatChoice)?.Id??"";};
        _start.TextChanged+=(_,_)=>ClearValidation();_end.TextChanged+=(_,_)=>ClearValidation();
        _primary.SelectionChanged+=(_,_)=>ClearValidation();_audio.SelectionChanged+=(_,_)=>ClearValidation();
        _applyButton.Click+=(_,_)=>Apply();
        _resetButton.Click+=(_,_)=>Commit(null);
        close.Click+=(_,_)=>RequestEditorClose();
        RefreshEnabled();
        RefreshFrameState();
        RefreshTimelineState();
        RefreshPlaybackState();
        RefreshWaveformState();
        InitializeEditorCloseGuard();
    }

    private T Named<T>(string name,T control) where T:FrameworkElement
    {control.Name=name;RegisterName(name,control);return control;}
    private static TextBlock Note(string text)
    {
        var note=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,3,0,3)};
        note.SetResourceReference(TextBlock.ForegroundProperty,"SecondaryTextBrush");return note;
    }
    private static TextBlock Heading(string text)=>new(){Text=text,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,3)};
    private static TextBlock ValidationNote()
    {
        var note=new TextBlock{TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,4,0,2),Visibility=Visibility.Collapsed};
        note.SetResourceReference(TextBlock.ForegroundProperty,"ValidationBrush");return note;
    }
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
        ClearValidation();
        if(!_editable){ShowValidationError("Эта строка недоступна для изменения.");return;}
        double? duration=_metadata is {Duration:>0} data && double.IsFinite(data.Duration)?data.Duration:null;
        if(!TryReadSections(duration,out var sections,out string error))
        {ShowSectionValidationError(error);return;}
        var section=sections.FirstOrDefault();
        bool manual=_mode.SelectedIndex==1;
        if((manual || section!=null) && _metadata?.FormatsLoaded!=true)
        {ShowValidationError("Дождитесь получения метаданных выбранного материала.",_mode);return;}
        if((manual || section!=null) && _metadata?.IsCollection==true)
        {ShowValidationError("Формат и фрагмент выбираются для отдельного материала, а не для коллекции.",manual?_mode:_start,manual?null:_startError);return;}
        MediaFormat? primary=null,audio=null;
        if(manual)
        {
            if(_primary.SelectedItem is not FormatChoice {Format:{} selected})
            {ShowValidationError("Выберите доступный основной формат из списка источника.",_primary,_formatError);return;}
            primary=selected;
            if(_audio.SelectedItem is FormatChoice choice)
            {
                if(choice.Id.Length>0 && choice.Format==null)
                {ShowValidationError("Сохранённый аудиоформат отсутствует в текущем списке. Выберите доступный формат.",_audio,_formatError);return;}
                audio=choice.Format;
            }
        }
        var selection=manual || section!=null ? new DownloadSelection(Row.Url,section,primary,audio,duration)
            {Sections=sections.Length>1?new DownloadSectionSet(sections):null,FrameRate=manual?null:TimelineFps(),
                FragmentOutputMode=_mergeDraft?"Merged":"Separate",AllowMergeReencode=_mergeDraft && _mergeReencodeDraft} : null;
        if(selection!=null && !selection.TryValidate(Row.Url,_options(),out error))
        {
            // The domain validator remains authoritative. Pick the related
            // control only to make its explanation reachable and actionable.
            if(manual)
                ShowValidationError(error,primary?.IsVideoOnly==true && audio is null || audio is not null && primary?.IsVideoOnly!=true?_audio:_primary,_formatError);
            else ShowValidationError(error,_mergeDraft && _mergeSections is not null?_mergeSections:_start);
            return;
        }
        Commit(selection);
    }
    private void Commit(DownloadSelection? selection)
    {
        if(!_editable){ShowValidationError("Эта строка недоступна для изменения.");return;}
        string? error=_apply(selection);
        if(!string.IsNullOrEmpty(error)){ShowValidationError(error);return;}
        Close();
    }

    private void ClearValidation()
    {
        foreach(var note in new[]{_error,_startError,_endError,_formatError})
        {note.Text="";note.Visibility=Visibility.Collapsed;}
        foreach(var field in new Control[]{_start,_end,_primary,_audio})
        {field.ClearValue(Control.BorderBrushProperty);AutomationProperties.SetHelpText(field,"");}
    }
    private void RevealFormats(FrameworkElement target)
    {
        _formatsExpander.IsExpanded=true;
        UpdateLayout();
        target.BringIntoView();UpdateLayout();
    }
    private void ShowValidationError(string message,Control? field=null,TextBlock? inline=null)
    {
        string display=ErrorMessages.ForDisplay(message);
        _error.Text=display;_error.Visibility=Visibility.Visible;
        if(inline is not null){inline.Text=display;inline.Visibility=Visibility.Visible;}
        if(field is null)return;
        if(ReferenceEquals(field,_primary) || ReferenceEquals(field,_audio))RevealFormats(field);
        field.SetResourceReference(Control.BorderBrushProperty,"ValidationBrush");AutomationProperties.SetHelpText(field,display);
        UpdateLayout();field.BringIntoView();UpdateLayout();field.Focus();
        if(field is TextBox text)text.SelectAll();
    }
    private void ShowSectionValidationError(string message)
    {
        if(_multipleSections?.IsChecked==true && _sectionDrafts.Count==0)
        {ShowValidationError(message,_addSection);return;}
        bool invalidStart=!DownloadSection.TryParseTimestamp(_start.Text,out _);
        ShowValidationError(message,invalidStart?_start:_end,invalidStart?_startError:_endError);
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
