using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using MouseWheelEventArgs = System.Windows.Input.MouseWheelEventArgs;

namespace VideoDownloader.Native;

// Owns the independent prefetch worker and its displayed selection. Queue and
// session orchestration supply rows and checkpoint/log actions explicitly.
internal sealed class PreviewController : IDisposable
{
    private readonly Window _window;
    private readonly Func<IEnumerable<DownloadItem>> _rows;
    private readonly Action _save;
    private readonly Action<string> _log;
    private readonly PreviewService _service;
    private readonly TextBox _cookiesBox;
    private readonly TextBlock _title;
    private readonly TextBlock _metadata;
    private readonly TextBlock _formats;
    private readonly Image _image;
    private readonly FrameworkElement _imageHost;
    private readonly TextBlock _imageCounter;
    private readonly Button _previousImage;
    private readonly Button _nextImage;
    private readonly DispatcherTimer _galleryTimer=new(){Interval=TimeSpan.FromMilliseconds(900)};
    private readonly List<string> _gallery=new();
    private readonly Dictionary<string,BitmapImage> _decoded=new(StringComparer.Ordinal);
    private int _galleryIndex;
    private bool _hovering;
    private string _selectedUrl="";
    private string _selectedKnownTitle="";
    private string _lastReceivedUrl="";
    private string _lastReceivedTitle="";
    private string _cookies;
    private volatile bool _disposed;

    internal string SelectedUrl
    {
        get=>_selectedUrl;
        set
        {
            if(string.Equals(_selectedUrl,value,StringComparison.Ordinal))return;
            _selectedUrl=value;
            _selectedKnownTitle="";
            ClearGallery();
        }
    }
    internal string CookieSnapshot=>Volatile.Read(ref _cookies);

    internal PreviewController(Window window,AppPersistence storage,string executable,
        Func<IEnumerable<DownloadItem>> rows,Action save,Action<string> log)
    {
        _window=window;_rows=rows;_save=save;_log=log;
        T Control<T>(string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing preview control: "+name));
        _cookiesBox=Control<TextBox>("CookiesBox");
        _title=Control<TextBlock>("PreviewTitle");
        _metadata=Control<TextBlock>("PreviewMeta");
        _formats=Control<TextBlock>("PreviewFormats");
        _image=Control<Image>("PreviewImage");
        _imageHost=Control<FrameworkElement>("PreviewImageHost");
        _imageCounter=Control<TextBlock>("PreviewImageCounter");
        _previousImage=Control<Button>("PreviewPreviousButton");
        _nextImage=Control<Button>("PreviewNextButton");
        _imageHost.MouseEnter+=EnterGallery;
        _imageHost.MouseLeave+=LeaveGallery;
        _imageHost.PreviewMouseWheel+=WheelGallery;
        _imageHost.IsVisibleChanged+=GalleryVisibilityChanged;
        _previousImage.Click+=PreviousGalleryImage;
        _nextImage.Click+=NextGalleryImage;
        _galleryTimer.Tick+=GalleryTick;
        ClearGallery();
        _cookies=_cookiesBox.Text;
        _cookiesBox.TextChanged+=CookiesChanged;
        _service=new PreviewService(executable,storage,()=>CookieSnapshot,
            data=>Dispatch(()=>ReceiveReady(data)),
            message=>Dispatch(()=>_log(message)),
            failed:(url,error)=>Dispatch(()=>ReceiveFailure(url,error)),
            updated:data=>Dispatch(()=>ReceiveReady(data)));
    }

    private void CookiesChanged(object sender,TextChangedEventArgs e)=>
        Volatile.Write(ref _cookies,_cookiesBox.Text);

    private void Dispatch(Action action)
    {
        if(_disposed || _window.Dispatcher.HasShutdownStarted)return;
        _=_window.Dispatcher.BeginInvoke(()=>{if(!_disposed)action();});
    }

    internal PreviewData? GetCached(string url)=>_service.Peek(url);

    internal void RequestRow(DownloadItem row,bool priority=false)
    {
        if(_disposed)return;
        var cached=_service.Peek(row.Url);
        if(cached!=null)ApplyTitle(cached);
        _service.Request(row.Url,priority);
    }

    // Subscription discovery warms the same worker/cache before a row joins
    // the download queue. It never changes the user's selected preview.
    internal void PrefetchUrls(IEnumerable<string> urls)
    {
        if(_disposed)return;
        var requested=urls.Where(AppPersistence.ValidUrl).Distinct(StringComparer.Ordinal).ToArray();
        _=Task.Run(()=>
        {
            foreach(string url in requested)
            {
                if(_disposed)return;
                try
                {
                    var cached=_service.Peek(url);
                    if(cached!=null)Dispatch(()=>{if(ApplyTitle(cached))_save();});
                    _service.Request(url);
                }
                catch(ObjectDisposedException) when(_disposed){return;}
                catch(Exception error){Dispatch(()=>_log("Preview prefetch failed: "+error.Message));}
            }
        });
    }

    private bool ApplyTitle(PreviewData data)
    {
        if(string.IsNullOrWhiteSpace(data.Title) || string.Equals(data.Title.Trim(),data.Url,StringComparison.Ordinal))return false;
        string title=data.Title.Trim();
        bool changed=false;
        foreach(var row in _rows().Distinct().Where(row=>string.Equals(row.Url,data.Url,StringComparison.Ordinal)))
            if(!string.Equals(row.Title,title,StringComparison.Ordinal)){row.Title=title;changed=true;}
        return changed;
    }

    private string DisplayTitle(string url,string title,string fallback)
    {
        if(!string.IsNullOrWhiteSpace(title) && !string.Equals(title.Trim(),url,StringComparison.Ordinal))return title.Trim();
        string? known=_rows().Where(row=>string.Equals(row.Url,url,StringComparison.Ordinal))
            .Select(row=>row.Title).FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value,url,StringComparison.Ordinal));
        if(known!=null)return known;
        return string.Equals(SelectedUrl,url,StringComparison.Ordinal) && _selectedKnownTitle.Length>0?_selectedKnownTitle:fallback;
    }

    // Subscription discovery can enrich an existing row before full metadata
    // succeeds. Refresh only the current matching title, leaving image/details.
    internal void RefreshKnownTitle(DownloadItem row)
    {
        if(!_disposed && string.Equals(SelectedUrl,row.Url,StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(row.Title) && !string.Equals(row.Title,row.Url,StringComparison.Ordinal))
        {
            _selectedKnownTitle=row.Title;
            _title.Text=row.Title;
        }
    }

    internal void Show(string url,string? knownTitle=null)
    {
        if(_disposed || !AppPersistence.ValidUrl(url))return;
        SelectedUrl=url;
        if(!string.IsNullOrWhiteSpace(knownTitle) && !string.Equals(knownTitle.Trim(),url,StringComparison.Ordinal))
            _selectedKnownTitle=knownTitle.Trim();
        var cached=_service.Peek(url);
        if(cached!=null){ReceiveReady(cached);_service.Request(url,true);return;}
        _title.Text=DisplayTitle(url,"","Получаем метаданные…");
        _metadata.Text=url;
        _metadata.ToolTip=null;
        _formats.Text="";
        _service.Request(url,true);
    }

    internal void ReceiveFailure(string url,string error)
    {
        if(_disposed || !string.Equals(SelectedUrl,url,StringComparison.Ordinal))return;
        _title.Text=DisplayTitle(url,"","Не удалось получить метаданные");
        _metadata.Text=ErrorMessages.ForDisplay(error);
        _metadata.ToolTip=error;
        if(_gallery.Count==0){_formats.Text="";ClearGallery();}
    }

    internal void ReceiveReady(PreviewData data)
    {
        if(_disposed)return;
        string receivedTitle=data.Title.Trim();
        if(!string.Equals(_lastReceivedUrl,data.Url,StringComparison.Ordinal) ||
           !string.Equals(_lastReceivedTitle,receivedTitle,StringComparison.Ordinal))
        {
            _lastReceivedUrl=data.Url;_lastReceivedTitle=receivedTitle;
            if(ApplyTitle(data))_save();
        }
        if(!string.Equals(SelectedUrl,data.Url,StringComparison.Ordinal))return;
        if(receivedTitle.Length>0 && !string.Equals(receivedTitle,data.Url,StringComparison.Ordinal))_selectedKnownTitle=receivedTitle;
        _title.Text=DisplayTitle(data.Url,data.Title,"Название пока недоступно");
        var summary=new List<string>();
        if(data.PlaylistTitle.Length>0)summary.Add("Playlist: "+data.PlaylistTitle);
        if(data.Uploader.Length>0)summary.Add(data.Uploader);
        if(data.Duration>0)summary.Add(TimeSpan.FromSeconds(data.Duration).ToString(@"hh\:mm\:ss"));
        if(data.Extractor.Length>0)summary.Add(data.Extractor);
        _metadata.Text=string.Join(" · ",summary);
        _metadata.ToolTip=null;
        _formats.Text=$"{(data.MaxHeight>0?data.MaxHeight+"p ":"")}{data.MaxFps:0} FPS · {data.Codecs} · {data.DynamicRange}";
        SetGallery(data);
    }

    private void ClearGallery()
    {
        _galleryTimer.Stop();
        _gallery.Clear();_decoded.Clear();_galleryIndex=0;
        _image.Source=null;
        UpdateGalleryControls();
    }

    private void SetGallery(PreviewData data)
    {
        var images=(data.ThumbnailsBase64??[]).Where(value=>!string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal).Take(15).ToArray();
        if(images.Length==0 && !string.IsNullOrWhiteSpace(data.ThumbnailBase64))images=[data.ThumbnailBase64];
        if(!_gallery.SequenceEqual(images,StringComparer.Ordinal))
        {
            string? current=_gallery.Count>0?_gallery[_galleryIndex]:null;
            _gallery.Clear();_gallery.AddRange(images);
            _galleryIndex=current==null?0:Math.Max(0,_gallery.IndexOf(current));
            foreach(string stale in _decoded.Keys.Where(key=>!_gallery.Contains(key,StringComparer.Ordinal)).ToArray())
                _decoded.Remove(stale);
        }
        RenderGalleryImage();
        UpdateGalleryTimer();
    }

    private void RenderGalleryImage()
    {
        while(_gallery.Count>0)
        {
            _galleryIndex=Math.Clamp(_galleryIndex,0,_gallery.Count-1);
            string encoded=_gallery[_galleryIndex];
            try
            {
                if(!_decoded.TryGetValue(encoded,out var decoded))
                {
                    decoded=PreviewImage.Decode(Convert.FromBase64String(encoded));
                    _decoded.Add(encoded,decoded);
                }
                _image.Source=decoded;
                UpdateGalleryControls();
                return;
            }
            catch(Exception error)
            {
                _log("Thumbnail decode failed: "+error.Message);
                _gallery.RemoveAt(_galleryIndex);_decoded.Remove(encoded);
            }
        }
        _image.Source=null;
        UpdateGalleryControls();
    }

    private void UpdateGalleryControls()
    {
        _imageCounter.Text=_gallery.Count==0?ErrorMessages.Text("Нет превью","No previews"):$"{_galleryIndex+1} / {_gallery.Count}";
        _previousImage.IsEnabled=_nextImage.IsEnabled=_gallery.Count>1;
    }

    private void MoveGallery(int offset)
    {
        if(_disposed || _gallery.Count<2)return;
        _galleryIndex=(_galleryIndex+offset+_gallery.Count)%_gallery.Count;
        RenderGalleryImage();
    }

    private void UpdateGalleryTimer(bool restart=false)
    {
        if(_disposed || !_hovering || !_imageHost.IsVisible || _gallery.Count<2){_galleryTimer.Stop();return;}
        if(restart)_galleryTimer.Stop();
        if(!_galleryTimer.IsEnabled)_galleryTimer.Start();
    }

    private void EnterGallery(object sender,MouseEventArgs e)
    {
        if(_disposed)return;
        _hovering=true;UpdateGalleryTimer(true);
    }
    private void LeaveGallery(object sender,MouseEventArgs e)
    {
        _hovering=false;_galleryTimer.Stop();
    }
    private void GalleryVisibilityChanged(object sender,DependencyPropertyChangedEventArgs e)
    {
        if(_imageHost.IsVisible)return;
        _hovering=false;_galleryTimer.Stop();
    }
    private void GalleryTick(object? sender,EventArgs e)
    {
        if(_disposed || !_hovering || !_imageHost.IsVisible || _gallery.Count<2){_galleryTimer.Stop();return;}
        MoveGallery(1);
    }
    private void WheelGallery(object sender,MouseWheelEventArgs e)
    {
        if(_disposed || _gallery.Count<2 || e.Delta==0)return;
        MoveGallery(e.Delta>0?-1:1);UpdateGalleryTimer(true);e.Handled=true;
    }
    private void PreviousGalleryImage(object sender,RoutedEventArgs e)
    {
        MoveGallery(-1);UpdateGalleryTimer(true);
    }
    private void NextGalleryImage(object sender,RoutedEventArgs e)
    {
        MoveGallery(1);UpdateGalleryTimer(true);
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _galleryTimer.Stop();
        _galleryTimer.Tick-=GalleryTick;
        _imageHost.MouseEnter-=EnterGallery;
        _imageHost.MouseLeave-=LeaveGallery;
        _imageHost.PreviewMouseWheel-=WheelGallery;
        _imageHost.IsVisibleChanged-=GalleryVisibilityChanged;
        _previousImage.Click-=PreviousGalleryImage;
        _nextImage.Click-=NextGalleryImage;
        _gallery.Clear();_decoded.Clear();
        _cookiesBox.TextChanged-=CookiesChanged;
        _service.Dispose();
    }
}
