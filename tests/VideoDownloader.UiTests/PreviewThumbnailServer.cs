using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace VideoDownloader.UiTests;

// Real HTTP bytes for cold-ingress tests. Metadata comes from the native child;
// PreviewService must independently fetch and validate this actual PNG.
internal sealed class PreviewThumbnailServer : IDisposable
{
    internal const int Width=32,Height=24;
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop=new();
    private readonly ConcurrentBag<Task> _responses=new();
    private readonly byte[] _png;
    private readonly Task _server;
    private int _requests;
    internal string Url {get;}
    internal string Encoded=>Convert.ToBase64String(_png);
    internal int Requests=>Volatile.Read(ref _requests);
    internal ConcurrentQueue<string> Errors {get;}=new();

    internal PreviewThumbnailServer(byte color=24)
    {
        var pixels=Enumerable.Range(0,Width*Height).SelectMany(_=>new byte[]{color,96,208,255}).ToArray();
        var bitmap=BitmapSource.Create(Width,Height,96,96,PixelFormats.Bgra32,null,pixels,Width*4);
        var encoder=new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using(var output=new MemoryStream()){encoder.Save(output);_png=output.ToArray();}
        _listener.Start();
        Url="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/preview.png";
        _server=Task.Run(async()=>
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _responses.Add(Respond(client));
                }
            }
            catch(Exception error) when(error is OperationCanceledException or SocketException && _stop.IsCancellationRequested){}
        });
    }

    private async Task Respond(TcpClient client)
    {
        using(client)
        try
        {
            var stream=client.GetStream();
            using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,leaveOpen:true);
            string? request=await reader.ReadLineAsync(_stop.Token);
            if(request!="GET /preview.png HTTP/1.1")throw new InvalidDataException("Unexpected image request: "+request);
            int headers=0;
            while(await reader.ReadLineAsync(_stop.Token) is {Length:>0})
                if(++headers>100)throw new InvalidDataException("Too many image request headers.");
            Interlocked.Increment(ref _requests);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: {_png.Length}\r\nConnection: close\r\n\r\n"),_stop.Token);
            await stream.WriteAsync(_png,_stop.Token);
            await stream.FlushAsync(_stop.Token);
        }
        catch(Exception error) when(error is IOException or SocketException or OperationCanceledException)
        {
            if(!_stop.IsCancellationRequested)Errors.Enqueue(error.Message);
        }
    }

    public void Dispose()
    {
        _stop.Cancel();_listener.Stop();
        try {_server.GetAwaiter().GetResult();}catch(OperationCanceledException){}
        Task.WhenAll(_responses.ToArray()).GetAwaiter().GetResult();
        _stop.Dispose();
    }
}
