using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace VideoDownloader.Native;

// Owns only the subscription window's selected-video view. The main preview
// controller remains the sole owner of the metadata worker and validated cache.
internal sealed class SubscriptionPreviewController : IDisposable
{
    private readonly PreviewController? _previews;
    private readonly Action<string> _openUrl;
    private readonly Action<string> _log;
    private readonly TextBlock _title,_info,_status,_error;
    private readonly TextBox _urlBox;
    private readonly Image _first,_second;
    private readonly Button _open,_copy;
    private readonly Dictionary<string,BitmapImage> _decoded=new(StringComparer.Ordinal);
    private string _selectedUrl="",_knownTitle="",_baseInfo="";
    private PreviewData? _data;
    private bool _final;
    private bool _disposed;

    internal SubscriptionPreviewController(Window window,PreviewController? previews,Action<string> openUrl,Action<string> log)
    {
        T Find<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing subscription preview control: "+name));
        _previews=previews;_openUrl=openUrl;_log=log;
        _title=Find<TextBlock>("SubscriptionPreviewTitle");
        _urlBox=Find<TextBox>("SubscriptionPreviewUrlBox");
        _info=Find<TextBlock>("SubscriptionPreviewInfo");
        _status=Find<TextBlock>("SubscriptionPreviewStatus");
        _error=Find<TextBlock>("SubscriptionPreviewError");
        _first=Find<Image>("SubscriptionPreviewImage");
        _second=Find<Image>("SubscriptionPreviewSecondImage");
        _open=Find<Button>("SubscriptionOpenVideoButton");
        _copy=Find<Button>("SubscriptionCopyVideoUrlButton");
        _open.Click+=OpenUrl;_copy.Click+=CopyUrl;
        if(_previews!=null){_previews.Changed+=Receive;_previews.Failed+=ReceiveFailure;}
        Clear(0);
    }

    internal void Show(SubscriptionItem? item,string sourceTitle,int selectedCount)
    {
        if(_disposed)return;
        if(item==null){Clear(selectedCount);return;}
        bool changed=!string.Equals(_selectedUrl,item.Url,StringComparison.Ordinal);
        if(changed){Clear(0);_selectedUrl=item.Url;}
        _knownTitle=string.IsNullOrWhiteSpace(item.Title) || string.Equals(item.Title,item.Url,StringComparison.Ordinal)
            ? item.Url : item.Title;
        _baseInfo=string.Join(Environment.NewLine,new[]
        {
            "Подписка: "+(sourceTitle.Length>0?sourceTitle:item.SubscriptionId),
            "Состояние: "+item.StateLabel,
            "Обнаружено: "+item.DiscoveredAtUtc.LocalDateTime.ToString("g",CultureInfo.CurrentCulture)
        });
        _title.Text=_knownTitle;_urlBox.Text=item.Url;_info.Text=_baseInfo;
        _open.IsEnabled=true;_copy.IsEnabled=true;
        if(_data!=null)Render(_data,_final);
        if(!changed)return; // A list refresh updates labels without extra work.
        if(_previews==null){_status.Text="Название и адрес доступны; загрузчик превью не подключён.";return;}
        _status.Text="Получаем сведения и изображения…";
        if(_previews.GetCached(item.Url) is {} cached)Render(cached,true);
        _previews.RequestUrl(item.Url);
    }

    private void Clear(int selectedCount)
    {
        _selectedUrl="";_knownTitle="";_baseInfo="";_data=null;_final=false;
        _decoded.Clear();SetImage(_first,null);SetImage(_second,null);
        _title.Text="Выберите видео";_urlBox.Clear();_info.Text="";
        _status.Text=selectedCount>1?$"Выбрано материалов: {selectedCount}. Выберите одно видео для просмотра.":
            "Нажмите на материал в списке, чтобы увидеть сведения и до двух превью.";
        SetError("");_open.IsEnabled=false;_copy.IsEnabled=false;
    }

    private void Receive(PreviewData data,bool isFinal)
    {
        if(_disposed || !string.Equals(_selectedUrl,data.Url,StringComparison.Ordinal))return;
        Render(data,isFinal);
    }

    private void Render(PreviewData data,bool isFinal)
    {
        _data=data;_final=isFinal;SetError("");
        _title.Text=!string.IsNullOrWhiteSpace(data.Title) && !string.Equals(data.Title,data.Url,StringComparison.Ordinal)
            ? data.Title : _knownTitle;
        var details=new List<string>{_baseInfo};
        if(data.Uploader.Length>0)details.Add("Автор: "+data.Uploader);
        if(data.PlaylistTitle.Length>0)details.Add("Плейлист: "+data.PlaylistTitle);
        if(double.IsFinite(data.Duration) && data.Duration>0 && data.Duration<TimeSpan.MaxValue.TotalSeconds)
        {
            var duration=TimeSpan.FromSeconds(data.Duration);
            details.Add("Длительность: "+duration.ToString(duration.Days>0?@"d\.hh\:mm\:ss":@"hh\:mm\:ss"));
        }
        var formats=new List<string>();
        if(data.MaxHeight>0)formats.Add(data.MaxHeight+"p");
        if(double.IsFinite(data.MaxFps) && data.MaxFps>0)formats.Add(data.MaxFps.ToString("0.#",CultureInfo.CurrentCulture)+" FPS");
        if(data.Codecs.Length>0)formats.Add(data.Codecs);
        if(data.DynamicRange.Length>0)formats.Add(data.DynamicRange);
        if(formats.Count>0)details.Add("Форматы: "+string.Join(" · ",formats));
        if(data.Extractor.Length>0)details.Add("Источник: "+data.Extractor);
        _info.Text=string.Join(Environment.NewLine,details);

        var images=new List<(string Encoded,BitmapImage Image)>();
        var candidates=(data.ThumbnailsBase64??[]).Prepend(data.ThumbnailBase64)
            .Where(value=>!string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.Ordinal).Take(15);
        foreach(string candidate in candidates)
        {
            try
            {
                if(!_decoded.TryGetValue(candidate,out var image))image=PreviewImage.Decode(Convert.FromBase64String(candidate));
                images.Add((candidate,image));
                if(images.Count==2)break;
            }
            catch(Exception error)
            {
                _log("Не удалось показать изображение подписки: "+error.Message);
            }
        }
        _decoded.Clear();
        foreach(var image in images)_decoded[image.Encoded]=image.Image;
        SetImage(_first,images.Count>0?images[0].Image:null);
        SetImage(_second,images.Count>1?images[1].Image:null);
        _status.Text=images.Count switch
        {
            2=>"Показаны два превью.",
            1=>isFinal?"Доступно одно превью.":"Первое превью готово. Получаем остальные изображения…",
            _=>isFinal?"Изображения пока недоступны. Текстовые сведения сохранены.":"Получаем изображения…"
        };
    }

    private static void SetImage(Image target,BitmapImage? image)
    {
        target.Source=image;target.Visibility=image==null?Visibility.Collapsed:Visibility.Visible;
    }

    private void ReceiveFailure(string url,string error)
    {
        if(_disposed || !string.Equals(_selectedUrl,url,StringComparison.Ordinal))return;
        _status.Text="Не удалось обновить метаданные. Известные сведения и изображения сохранены.";
        SetError(ErrorMessages.ForDisplay(error),error);
    }

    private void SetError(string message,string? original=null)
    {
        _error.Text=message;_error.ToolTip=original;
        _error.Visibility=message.Length==0?Visibility.Collapsed:Visibility.Visible;
    }

    private void OpenUrl(object sender,RoutedEventArgs e)=>RunAction(()=>_openUrl(_selectedUrl));
    private void CopyUrl(object sender,RoutedEventArgs e)=>RunAction(()=>Clipboard.SetText(_selectedUrl));
    private void RunAction(Action action)
    {
        if(_disposed || _selectedUrl.Length==0)return;
        try { action(); }
        catch(Exception error){SetError(ErrorMessages.ForException(error),error.Message);}
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        if(_previews!=null){_previews.Changed-=Receive;_previews.Failed-=ReceiveFailure;}
        _open.Click-=OpenUrl;_copy.Click-=CopyUrl;
        Clear(0);
    }
}
