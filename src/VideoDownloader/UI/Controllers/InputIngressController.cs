using System.Windows;
using System.Windows.Threading;

namespace VideoDownloader.Native;

// Owns argument/file/drop/clipboard ingress and its polling lifecycle. Queue
// acceptance and duplicate decisions are supplied by the session owner.
internal sealed class InputIngressController : IDisposable
{
    private readonly Window _window;
    private readonly Action<IEnumerable<string>,bool,bool> _enqueue;
    private readonly Func<bool> _watchClipboard;
    private readonly Action _activate;
    private readonly Action<string> _log;
    private readonly Action<string>? _portableText;
    private readonly Action<string>? _portableFile;
    private readonly DispatcherTimer _clipboardTick=new(){Interval=TimeSpan.FromMilliseconds(950)};
    private bool _disposed;
    internal string LastClipboard { get; set; }="";

    internal InputIngressController(Window window,Action<IEnumerable<string>,bool,bool> enqueue,
        Func<bool> watchClipboard,Action activate,Action<string> log,
        Action<string>? portableText=null,Action<string>? portableFile=null)
    {
        _window=window;_enqueue=enqueue;_watchClipboard=watchClipboard;_activate=activate;_log=log;
        _portableText=portableText;_portableFile=portableFile;
        _clipboardTick.Tick+=OnClipboardTick;
        _window.Drop+=OnWindowDrop;
        _window.DragOver+=OnWindowDragOver;
    }
    internal void Start()
    {
        if(!_disposed)_clipboardTick.Start();
    }
    internal void Stop()=>_clipboardTick.Stop();
    internal void ReceiveArguments(IEnumerable<string> arguments)
    {
        if(_disposed)return;
        var urls=new List<string>();
        foreach(string argument in arguments)
        {
            if(argument.StartsWith("videodownloader://",StringComparison.OrdinalIgnoreCase))
            {
                string payload=Uri.UnescapeDataString(argument["videodownloader://".Length..].TrimStart('/'));
                urls.AddRange(ParseUrls(payload));
            }
            else if(File.Exists(argument) && Path.GetExtension(argument).Equals(".vdqueue",StringComparison.OrdinalIgnoreCase))
                _portableFile?.Invoke(argument);
            else if(File.Exists(argument))urls.AddRange(ReadInputFile(argument));
            else if(PortableQueueCodec.IsPortable(argument))_portableText?.Invoke(argument);
            else urls.AddRange(ParseUrls(argument));
        }
        if(urls.Count>0)_enqueue(urls,false,false);
        _activate();
    }
    internal static IReadOnlyList<string> ParseUrls(string text)
    {
        var found=new HashSet<string>(StringComparer.Ordinal);
        foreach(string token in text.Split([' ','\t','\r','\n'],StringSplitOptions.RemoveEmptyEntries))
        {
            string value=token.Trim();
            if(AppPersistence.ValidUrl(value))found.Add(value);
        }
        return found.ToList();
    }
    internal static IReadOnlyList<string> ReadInputFile(string file)
    {
        if(new FileInfo(file).Length>16*1024*1024)
            throw new InvalidDataException("URL list is larger than 16 MiB.");
        if(Path.GetExtension(file).Equals(".url",StringComparison.OrdinalIgnoreCase))
        {
            return File.ReadLines(file).Where(line=>line.TrimStart().StartsWith("URL=",StringComparison.OrdinalIgnoreCase))
                .SelectMany(line=>ParseUrls(line[(line.IndexOf('=')+1)..])).ToArray();
        }
        return Path.GetExtension(file).Equals(".txt",StringComparison.OrdinalIgnoreCase)
            ? ParseUrls(File.ReadAllText(file)) : [];
    }
    internal void Paste(bool select)
    {
        if(_disposed)return;
        try
        {
            if(Clipboard.ContainsText())
            {
                string text=Clipboard.GetText();
                if(PortableQueueCodec.IsPortable(text))_portableText?.Invoke(text);
                else _enqueue(ParseUrls(text),true,select);
            }
        }
        catch(System.Runtime.InteropServices.COMException error){_log("Clipboard busy: "+error.Message);}
    }
    internal void PollClipboard()
    {
        if(_disposed || !_watchClipboard())return;
        try
        {
            if(!Clipboard.ContainsText())return;
            string text=Clipboard.GetText();
            if(text==LastClipboard)return;
            LastClipboard=text;
            if(PortableQueueCodec.IsPortable(text))return;
            _enqueue(ParseUrls(text),true,false);
        }
        catch(System.Runtime.InteropServices.COMException){}
    }
    private void OnClipboardTick(object? sender,EventArgs args)=>PollClipboard();
    private void OnWindowDragOver(object sender,DragEventArgs args)=>args.Effects=DragDropEffects.Copy;
    private void OnWindowDrop(object sender,DragEventArgs args)
    {
        if(_disposed)return;
        if(args.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files=(string[])(args.Data.GetData(DataFormats.FileDrop)??Array.Empty<string>());
            foreach(string file in files)
            {
                if(Path.GetExtension(file).Equals(".vdqueue",StringComparison.OrdinalIgnoreCase))_portableFile?.Invoke(file);
                else _enqueue(ReadInputFile(file),false,false);
            }
            args.Handled=true;
        }
        else if(args.Data.GetDataPresent(DataFormats.Text))
        {
            string text=args.Data.GetData(DataFormats.Text)?.ToString()??"";
            if(PortableQueueCodec.IsPortable(text))_portableText?.Invoke(text);
            else _enqueue(ParseUrls(text),false,false);
            args.Handled=true;
        }
    }
    public void Dispose()
    {
        if(_disposed)return;
        _disposed=true;
        Stop();
        _clipboardTick.Tick-=OnClipboardTick;
        _window.Drop-=OnWindowDrop;
        _window.DragOver-=OnWindowDragOver;
    }
}
