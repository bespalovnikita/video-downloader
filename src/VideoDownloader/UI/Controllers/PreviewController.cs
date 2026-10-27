using System.Windows;
using System.Windows.Controls;

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
    private string _cookies;
    private volatile bool _disposed;

    internal string SelectedUrl { get; set; }="";
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
        _cookies=_cookiesBox.Text;
        _cookiesBox.TextChanged+=CookiesChanged;
        _service=new PreviewService(executable,storage,()=>CookieSnapshot,
            data=>Dispatch(()=>ReceiveReady(data)),
            message=>Dispatch(()=>_log(message)),
            failed:(url,error)=>Dispatch(()=>ReceiveFailure(url,error)));
    }

    private void CookiesChanged(object sender,TextChangedEventArgs e)=>
        Volatile.Write(ref _cookies,_cookiesBox.Text);

    private void Dispatch(Action action)
    {
        if(_disposed || _window.Dispatcher.HasShutdownStarted)return;
        _=_window.Dispatcher.BeginInvoke(()=>{if(!_disposed)action();});
    }

    internal PreviewData? GetCached(string url)=>_service.Get(url);

    internal void RequestRow(DownloadItem row,bool priority=false)
    {
        if(_disposed)return;
        var cached=_service.Get(row.Url);
        if(cached!=null)ApplyTitle(cached);
        else _service.Request(row.Url,priority);
    }

    private void ApplyTitle(PreviewData data)
    {
        if(string.IsNullOrWhiteSpace(data.Title) || string.Equals(data.Title.Trim(),data.Url,StringComparison.Ordinal))return;
        foreach(var row in _rows().Distinct().Where(row=>string.Equals(row.Url,data.Url,StringComparison.Ordinal)))
            row.Title=data.Title.Trim();
    }

    private string DisplayTitle(string url,string title,string fallback)
    {
        if(!string.IsNullOrWhiteSpace(title) && !string.Equals(title.Trim(),url,StringComparison.Ordinal))return title.Trim();
        return _rows().Where(row=>string.Equals(row.Url,url,StringComparison.Ordinal))
            .Select(row=>row.Title).FirstOrDefault(value=>!string.IsNullOrWhiteSpace(value) &&
                !string.Equals(value,url,StringComparison.Ordinal))??fallback;
    }

    // Subscription discovery can enrich an existing row before full metadata
    // succeeds. Refresh only the current matching title, leaving image/details.
    internal void RefreshKnownTitle(DownloadItem row)
    {
        if(!_disposed && string.Equals(SelectedUrl,row.Url,StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(row.Title) && !string.Equals(row.Title,row.Url,StringComparison.Ordinal))
            _title.Text=row.Title;
    }

    internal void Show(string url)
    {
        if(_disposed || !AppPersistence.ValidUrl(url))return;
        SelectedUrl=url;
        var cached=_service.Get(url);
        if(cached!=null){ReceiveReady(cached);return;}
        _title.Text=DisplayTitle(url,"","Получаем метаданные…");
        _metadata.Text=url;
        _formats.Text="";
        _image.Source=null;
        _service.Request(url,true);
    }

    internal void ReceiveFailure(string url,string error)
    {
        if(_disposed || !string.Equals(SelectedUrl,url,StringComparison.Ordinal))return;
        _title.Text=DisplayTitle(url,"","Не удалось получить метаданные");
        _metadata.Text=error;
        _formats.Text="";
        _image.Source=null;
    }

    internal void ReceiveReady(PreviewData data)
    {
        if(_disposed)return;
        ApplyTitle(data);
        _save();
        if(!string.Equals(SelectedUrl,data.Url,StringComparison.Ordinal))return;
        _title.Text=DisplayTitle(data.Url,data.Title,"Название пока недоступно");
        var summary=new List<string>();
        if(data.PlaylistTitle.Length>0)summary.Add("Playlist: "+data.PlaylistTitle);
        if(data.Uploader.Length>0)summary.Add(data.Uploader);
        if(data.Duration>0)summary.Add(TimeSpan.FromSeconds(data.Duration).ToString(@"hh\:mm\:ss"));
        if(data.Extractor.Length>0)summary.Add(data.Extractor);
        _metadata.Text=string.Join(" · ",summary);
        _formats.Text=$"{(data.MaxHeight>0?data.MaxHeight+"p ":"")}{data.MaxFps:0} FPS · {data.Codecs} · {data.DynamicRange}";
        if(data.ThumbnailBase64.Length==0){_image.Source=null;return;}
        try { _image.Source=PreviewImage.Decode(Convert.FromBase64String(data.ThumbnailBase64)); }
        catch(Exception error){_log("Thumbnail decode failed: "+error.Message);_image.Source=null;}
    }

    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        _cookiesBox.TextChanged-=CookiesChanged;
        _service.Dispose();
    }
}
