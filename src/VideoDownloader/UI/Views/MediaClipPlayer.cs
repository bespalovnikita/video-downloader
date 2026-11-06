using System.Windows;
using System.Windows.Controls;

namespace VideoDownloader.Native;

// The dialog owns the prepared local file. Clear releases the Windows media
// graph before that file is disposed; playback never receives a remote URL.
internal interface IMediaClipPlayer:IDisposable
{
    FrameworkElement View {get;}
    event Action? Opened;
    event Action? Ended;
    event Action? Failed;
    TimeSpan Position {get;set;}
    void Open(Uri source);
    void Play();
    void Pause();
    void Stop();
    void Clear();
}

internal sealed class WpfMediaClipPlayer:IMediaClipPlayer
{
    private readonly MediaElement _media=new(){LoadedBehavior=MediaState.Manual,UnloadedBehavior=MediaState.Manual,
        Stretch=System.Windows.Media.Stretch.Uniform,Height=220,Volume=1,IsMuted=false};
    private bool _disposed;
    public FrameworkElement View=>_media;
    public event Action? Opened;
    public event Action? Ended;
    public event Action? Failed;
    internal WpfMediaClipPlayer()
    {_media.MediaOpened+=MediaOpened;_media.MediaEnded+=MediaEnded;_media.MediaFailed+=MediaFailed;}
    private void MediaOpened(object sender,RoutedEventArgs e)=>Opened?.Invoke();
    private void MediaEnded(object sender,RoutedEventArgs e)=>Ended?.Invoke();
    private void MediaFailed(object? sender,ExceptionRoutedEventArgs e)=>Failed?.Invoke();
    public TimeSpan Position {get=>_media.Position;set=>_media.Position=value;}
    public void Open(Uri source)
    {
        if(_disposed)throw new ObjectDisposedException(nameof(WpfMediaClipPlayer));
        if(!source.IsFile)throw new ArgumentException("Playback requires a prepared local clip.",nameof(source));
        Clear();_media.Source=source;
    }
    public void Play()=>_media.Play();
    public void Pause()=>_media.Pause();
    public void Stop()=>_media.Stop();
    public void Clear()
    {
        // An unavailable Windows codec/device must not prevent graph release.
        try{_media.Stop();}catch(InvalidOperationException){ }
        try{_media.Close();}catch(InvalidOperationException){ }
        _media.Source=null;
    }
    public void Dispose()
    {
        if(_disposed)return;_disposed=true;
        _media.MediaOpened-=MediaOpened;_media.MediaEnded-=MediaEnded;_media.MediaFailed-=MediaFailed;
        Clear();Opened=null;Ended=null;Failed=null;
    }
}
