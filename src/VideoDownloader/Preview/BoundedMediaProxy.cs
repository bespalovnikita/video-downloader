using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace VideoDownloader.Native;

/// <summary>
/// A single-frame, memory-only bridge to one progressive media URL. FFmpeg sees
/// only its unguessable loopback address; credentials and signed URLs stay here.
/// </summary>
internal sealed class BoundedMediaProxy : IAsyncDisposable
{
    private const int MaxRequestBytes=8192;
    private const int MaxRequests=64;
    private const int MaxClients=16;
    private readonly Uri _source;
    private readonly Uri _headerOrigin;
    private readonly IReadOnlyDictionary<string,string> _headers;
    private readonly CookieContainer _cookies;
    private readonly long _maxBytes;
    private readonly TcpListener _listener=new(IPAddress.Loopback,0);
    private readonly CancellationTokenSource _stop;
    private readonly CancellationTokenSource _failed=new();
    private readonly CancellationTokenRegistration _closeOnStop;
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<int,TcpClient> _clients=new();
    private readonly ConcurrentDictionary<int,Task> _handlers=new();
    private readonly SemaphoreSlim _readGate=new(1,1);
    private readonly object _disposeGate=new();
    private readonly string _path="/"+Guid.NewGuid().ToString("N");
    private readonly Task _accept;
    private Exception? _failure;
    private Task? _dispose;
    private long _received;
    private int _sequence;
    private int _requests;
    private byte[]? _buffer;
    private string _bufferType="application/octet-stream";

    internal Uri Address { get; }
    internal Exception? Failure=>Volatile.Read(ref _failure);
    internal CancellationToken FailureToken { get; }
    internal long ReceivedBytes=>Interlocked.Read(ref _received);

    internal BoundedMediaProxy(byte[] media,string contentType,CancellationToken cancellationToken)
        :this(ValidateCompiled(media,contentType),new Dictionary<string,string>(),new CookieContainer(),
            cancellationToken,64L*1024*1024)
    {
        _buffer=media;_bufferType=contentType;
    }
    private static Uri ValidateCompiled(byte[] media,string contentType)
    {
        if(media.Length is <=0 or >16*1024*1024)throw new ArgumentException("Invalid compiled frame media size.",nameof(media));
        if(contentType is not ("video/mp4" or "video/mp2t" or "video/webm"))throw new ArgumentException("Invalid compiled frame media type.",nameof(contentType));
        return new Uri("http://127.0.0.1/compiled-media");
    }

    internal BoundedMediaProxy(Uri mediaUrl,IReadOnlyDictionary<string,string> headers,CookieContainer cookies,
        CancellationToken cancellationToken,long maxBytes=16L*1024*1024,Uri? originalHeaderOrigin=null)
    {
        ArgumentNullException.ThrowIfNull(mediaUrl);
        ArgumentNullException.ThrowIfNull(headers);
        ArgumentNullException.ThrowIfNull(cookies);
        ValidateUrl(mediaUrl);
        originalHeaderOrigin??=mediaUrl;
        if(!originalHeaderOrigin.IsAbsoluteUri || originalHeaderOrigin.Scheme is not ("http" or "https") ||
            originalHeaderOrigin.UserInfo.Length>0 || string.IsNullOrWhiteSpace(originalHeaderOrigin.Host) ||
            originalHeaderOrigin.Scheme=="https" && mediaUrl.Scheme!="https")
            throw new ArgumentException("Invalid media header origin.",nameof(originalHeaderOrigin));
        if(maxBytes<=0)throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if(headers.Count>64)throw new ArgumentException("Too many media request headers.",nameof(headers));
        var accepted=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        int headerCharacters=0;
        foreach(var (name,value) in headers)
        {
            if(name.Length is <1 or >128 || !name.All(IsToken) || value is null || value.Length>8192 ||
                value.Any(character=>character is '\r' or '\n' or '\0' || char.IsControl(character) && character!='\t'))
                throw new ArgumentException("Invalid media request header.",nameof(headers));
            headerCharacters+=name.Length+value.Length;
            if(headerCharacters>32*1024)throw new ArgumentException("Media request headers exceed their size limit.",nameof(headers));
            if(!BlockedHeader(name))accepted[name]=value;
        }
        _source=mediaUrl;_headerOrigin=originalHeaderOrigin;_headers=accepted;_cookies=cookies;_maxBytes=maxBytes;
        FailureToken=_failed.Token;
        _stop=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _http=new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect=false,UseCookies=false,AutomaticDecompression=DecompressionMethods.None,
            MaxResponseHeadersLength=32,ConnectTimeout=TimeSpan.FromSeconds(10),
            // One frame commonly closes the body early. Never leave an unread
            // response draining in the background after its owner has joined.
            MaxResponseDrainSize=0,ResponseDrainTimeout=TimeSpan.Zero,
            MaxConnectionsPerServer=4,UseProxy=!mediaUrl.IsLoopback,
        }){Timeout=Timeout.InfiniteTimeSpan};
        try
        {
            _listener.Start(MaxClients);
            Address=new Uri("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)+_path);
            _closeOnStop=_stop.Token.Register(CloseSockets);
            _accept=AcceptAsync();
        }
        catch
        {
            _listener.Stop();_http.Dispose();_stop.Dispose();_failed.Dispose();_readGate.Dispose();
            throw;
        }
    }

    private static bool IsToken(char character)=>char.IsAsciiLetterOrDigit(character) || "!#$%&'*+-.^_`|~".Contains(character);
    private static bool BlockedHeader(string name)=>name.Equals("Cookie",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Host",StringComparison.OrdinalIgnoreCase) || name.Equals("Range",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Connection",StringComparison.OrdinalIgnoreCase) || name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase) || name.Equals("Proxy-Authorization",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Accept-Encoding",StringComparison.OrdinalIgnoreCase);
    private static bool PublicRedirectHeader(string name)=>name.Equals("User-Agent",StringComparison.OrdinalIgnoreCase) ||
        name.Equals("Accept",StringComparison.OrdinalIgnoreCase) || name.Equals("Accept-Language",StringComparison.OrdinalIgnoreCase);
    private static bool SameOrigin(Uri left,Uri right)=>left.Scheme==right.Scheme && left.Port==right.Port &&
        string.Equals(left.IdnHost,right.IdnHost,StringComparison.OrdinalIgnoreCase);
    private static void ValidateUrl(Uri url)
    {
        if(!url.IsAbsoluteUri || url.Scheme is not ("http" or "https") || url.UserInfo.Length!=0 ||
            string.IsNullOrWhiteSpace(url.Host) || url.AbsoluteUri.Length>32*1024)
            throw new ArgumentException("A progressive HTTP(S) media stream is required.");
        string extension=Path.GetExtension(url.AbsolutePath);
        if(extension.Equals(".m3u8",StringComparison.OrdinalIgnoreCase) || extension.Equals(".mpd",StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Manifest streams do not support bounded frame preview.");
    }

    private async Task AcceptAsync()
    {
        try
        {
            while(!_stop.IsCancellationRequested)
            {
                var client=await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
                if(_clients.Count>=MaxClients){client.Dispose();continue;}
                int id=Interlocked.Increment(ref _sequence);
                _clients[id]=client;
                Task handling=HandleAsync(client);
                _handlers[id]=handling;
                _=handling.ContinueWith(_=>{
                    _handlers.TryRemove(id,out var ignoredTask);_clients.TryRemove(id,out var ignoredClient);
                },CancellationToken.None,TaskContinuationOptions.ExecuteSynchronously,TaskScheduler.Default);
            }
        }
        catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
        catch(ObjectDisposedException) when(_stop.IsCancellationRequested){ }
        catch(SocketException) when(_stop.IsCancellationRequested){ }
        catch(Exception){Fail(new IOException("The bounded media bridge could not accept a frame request."));}
    }

    private sealed record Request(bool Head,RangeHeaderValue? Range);
    private async Task<Request?> ReadRequestAsync(NetworkStream stream)
    {
        byte[] bytes=new byte[MaxRequestBytes];
        int count=0,end=-1;
        while(end<0 && count<bytes.Length)
        {
            int read=await stream.ReadAsync(bytes.AsMemory(count,Math.Min(512,bytes.Length-count)),_stop.Token).ConfigureAwait(false);
            if(read==0)return null;
            count+=read;
            for(int index=Math.Max(0,count-read-3);index<=count-4;index++)
                if(bytes[index]==13 && bytes[index+1]==10 && bytes[index+2]==13 && bytes[index+3]==10){end=index;break;}
        }
        if(end<0 || bytes.AsSpan(0,end).ContainsAnyExceptInRange((byte)9,(byte)126))
        {await ErrorAsync(stream,400).ConfigureAwait(false);return null;}
        string[] lines=Encoding.ASCII.GetString(bytes,0,end).Split("\r\n",StringSplitOptions.None);
        string[] start=lines[0].Split(' ');
        if(start.Length!=3 || start[1]!=_path || start[2] is not ("HTTP/1.0" or "HTTP/1.1"))
        {await ErrorAsync(stream,404).ConfigureAwait(false);return null;}
        if(start[0] is not ("GET" or "HEAD"))
        {await ErrorAsync(stream,405).ConfigureAwait(false);return null;}
        if(Interlocked.Increment(ref _requests)>MaxRequests)
        {Fail(new IOException("The frame requires too many media requests."));return null;}
        RangeHeaderValue? range=null;
        foreach(string line in lines.Skip(1))
        {
            int separator=line.IndexOf(':');
            if(separator<=0 || !line[..separator].All(IsToken))
            {await ErrorAsync(stream,400).ConfigureAwait(false);return null;}
            string name=line[..separator],value=line[(separator+1)..].Trim();
            if(value.Any(character=>char.IsControl(character) && character!='\t'))
            {await ErrorAsync(stream,400).ConfigureAwait(false);return null;}
            if(name.Equals("Range",StringComparison.OrdinalIgnoreCase))
            {
                if(range is not null || !RangeHeaderValue.TryParse(value,out range) || range.Unit!="bytes" || range.Ranges.Count!=1 ||
                    range.Ranges.Single().From is null || range.Ranges.Single().To is {} last && last<range.Ranges.Single().From)
                {await ErrorAsync(stream,416).ConfigureAwait(false);return null;}
            }
            if(name.Equals("Transfer-Encoding",StringComparison.OrdinalIgnoreCase) ||
                name.Equals("Content-Length",StringComparison.OrdinalIgnoreCase) && value!="0")
            {await ErrorAsync(stream,400).ConfigureAwait(false);return null;}
        }
        return new(start[0]=="HEAD",range);
    }

    private async Task<HttpResponseMessage> SendAsync(Request incoming)
    {
        Uri target=_source;
        bool publicHeadersOnly=!SameOrigin(_headerOrigin,target);
        for(int redirect=0;redirect<=3;redirect++)
        {
            using var request=new HttpRequestMessage(incoming.Head?HttpMethod.Head:HttpMethod.Get,target);
            request.Headers.Range=incoming.Range;
            request.Headers.TryAddWithoutValidation("Accept-Encoding","identity");
            foreach(var (name,value) in _headers)
                if(!publicHeadersOnly || PublicRedirectHeader(name))request.Headers.TryAddWithoutValidation(name,value);
            string cookie=_cookies.GetCookieHeader(target);
            if(cookie.Length>32*1024)throw new IOException("The media cookie header exceeds its size limit.");
            if(cookie.Length>0)request.Headers.TryAddWithoutValidation("Cookie",cookie);
            var response=await _http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,_stop.Token).ConfigureAwait(false);
            if(response.Headers.TryGetValues("Set-Cookie",out var receivedCookies))
                foreach(string receivedCookie in receivedCookies)
                    try{_cookies.SetCookies(target,receivedCookie);}catch(CookieException){ }
            if(response.StatusCode is not (HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect))return response;
            try
            {
                if(redirect==3 || response.Headers.Location is not {} location)
                    throw new IOException("The media stream redirected too many times or without a destination.");
                Uri next=location.IsAbsoluteUri?location:new Uri(target,location);
                try{ValidateUrl(next);}catch(ArgumentException){throw new IOException("The media stream redirected to an unsupported destination.");}
                if(target.Scheme=="https" && next.Scheme!="https")throw new IOException("The media stream redirected to an insecure destination.");
                publicHeadersOnly|=!SameOrigin(_headerOrigin,next);
                target=next;
            }
            finally {response.Dispose();}
        }
        throw new IOException("The media stream redirected too many times.");
    }

    private static void ValidateResponse(Request request,HttpResponseMessage response)
    {
        if(!response.IsSuccessStatusCode)throw new IOException("Media frame HTTP "+((int)response.StatusCode).ToString(CultureInfo.InvariantCulture)+".");
        var requested=request.Range?.Ranges.Single();
        var contentRange=response.Content.Headers.ContentRange;
        if(requested?.From is >0 && response.StatusCode!=HttpStatusCode.PartialContent)
            throw new IOException("The media server does not support seeking to this frame.");
        if(response.StatusCode==HttpStatusCode.PartialContent)
        {
            if(contentRange is null || !contentRange.HasRange || !contentRange.Unit.Equals("bytes",StringComparison.OrdinalIgnoreCase) ||
                contentRange.From!=(requested?.From??0) || requested?.To is {} last && contentRange.To>last ||
                response.Content.Headers.ContentLength is {} length && length!=contentRange.To-contentRange.From+1)
                throw new IOException("The media server returned an invalid byte range.");
        }
        string type=response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant()??"";
        if(type.Contains("mpegurl",StringComparison.Ordinal) || type.Contains("dash",StringComparison.Ordinal) ||
            type.Contains("xml",StringComparison.Ordinal) || type.Contains("json",StringComparison.Ordinal) ||
            type.StartsWith("text/",StringComparison.Ordinal) || type.StartsWith("audio/",StringComparison.Ordinal))
            throw new IOException("This stream does not support progressive video frame preview.");
        if(response.Content.Headers.ContentEncoding.Any(encoding=>!encoding.Equals("identity",StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Encoded media responses do not support bounded frame seeking.");
    }

    private async Task HandleAsync(TcpClient client)
    {
        using(client)
        {
            try
            {
                using var stream=client.GetStream();
                var request=await ReadRequestAsync(stream).ConfigureAwait(false);
                if(request is null)return;
                if(_buffer is {} buffered)
                {await SendBufferAsync(stream,request,buffered).ConfigureAwait(false);return;}
                using var response=await SendAsync(request).ConfigureAwait(false);
                ValidateResponse(request,response);
                var headers=new StringBuilder("HTTP/1.1 ").Append((int)response.StatusCode).Append(" Media\r\nConnection: close\r\nCache-Control: no-store\r\n");
                if(response.Content.Headers.ContentLength is {} length)headers.Append("Content-Length: ").Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
                if(response.Content.Headers.ContentRange is {} range)headers.Append("Content-Range: ").Append(range).Append("\r\n");
                if(response.StatusCode==HttpStatusCode.PartialContent || response.Headers.AcceptRanges.Contains("bytes"))headers.Append("Accept-Ranges: bytes\r\n");
                headers.Append("Content-Type: ").Append(response.Content.Headers.ContentType?.MediaType??"application/octet-stream").Append("\r\n\r\n");
                if(!await WriteAsync(stream,Encoding.ASCII.GetBytes(headers.ToString())).ConfigureAwait(false) || request.Head)return;
                using var input=await response.Content.ReadAsStreamAsync(_stop.Token).ConfigureAwait(false);
                byte[] buffer=new byte[8192];
                while(true)
                {
                    int read;
                    await _readGate.WaitAsync(_stop.Token).ConfigureAwait(false);
                    try
                    {
                        int wanted=(int)Math.Min(buffer.Length,_maxBytes-ReceivedBytes+1);
                        read=await input.ReadAsync(buffer.AsMemory(0,wanted),_stop.Token).ConfigureAwait(false);
                        if(Interlocked.Add(ref _received,read)>_maxBytes)
                            throw new IOException("The frame exceeds its media transfer limit ("+_maxBytes.ToString(CultureInfo.InvariantCulture)+" bytes).");
                    }
                    finally{_readGate.Release();}
                    if(read==0 || !await WriteAsync(stream,buffer.AsMemory(0,read)).ConfigureAwait(false))return;
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){ }
            catch(ObjectDisposedException) when(_stop.IsCancellationRequested){ }
            catch(Exception) when(_stop.IsCancellationRequested){ }
            catch(IOException error) when(!_stop.IsCancellationRequested){Fail(error);}
            catch(Exception error) when(!_stop.IsCancellationRequested)
            {Fail(new IOException("The media stream could not provide this frame ("+error.GetType().Name+")."));}
        }
    }

    private async Task SendBufferAsync(NetworkStream stream,Request request,byte[] media)
    {
        var range=request.Range?.Ranges.Single();
        long first=range?.From??0,last=Math.Min(range?.To??media.Length-1,media.Length-1);
        if(first>=media.Length || first>last){await ErrorAsync(stream,416).ConfigureAwait(false);return;}
        long length=last-first+1;
        int status=range is null?200:206;
        var headers=new StringBuilder("HTTP/1.1 ").Append(status).Append(" Media\r\nConnection: close\r\nCache-Control: no-store\r\nAccept-Ranges: bytes\r\nContent-Length: ")
            .Append(length.ToString(CultureInfo.InvariantCulture)).Append("\r\nContent-Type: ").Append(_bufferType).Append("\r\n");
        if(range is not null)headers.Append("Content-Range: bytes ").Append(first).Append('-').Append(last).Append('/').Append(media.Length).Append("\r\n");
        headers.Append("\r\n");
        if(!await WriteAsync(stream,Encoding.ASCII.GetBytes(headers.ToString())).ConfigureAwait(false) || request.Head)return;
        if(Interlocked.Add(ref _received,length)>_maxBytes)throw new IOException("The frame decoder read too much compiled media.");
        await WriteAsync(stream,media.AsMemory((int)first,(int)length)).ConfigureAwait(false);
    }

    private async Task<bool> WriteAsync(NetworkStream stream,ReadOnlyMemory<byte> bytes)
    {
        try{await stream.WriteAsync(bytes,_stop.Token).ConfigureAwait(false);return true;}
        // FFmpeg may close its media input as soon as one frame is decoded.
        catch(IOException){return false;}
        catch(SocketException){return false;}
    }
    private Task ErrorAsync(NetworkStream stream,int status)=>WriteAsync(stream,Encoding.ASCII.GetBytes(
        "HTTP/1.1 "+status.ToString(CultureInfo.InvariantCulture)+" Unavailable\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    private void Fail(Exception error)
    {
        if(Interlocked.CompareExchange(ref _failure,error,null) is not null)return;
        try{_failed.Cancel();}catch(AggregateException){ }
        try{_stop.Cancel();}catch(AggregateException){ }
    }
    private void CloseSockets()
    {
        _listener.Stop();
        foreach(var client in _clients.Values)client.Dispose();
    }
    public ValueTask DisposeAsync()
    {
        lock(_disposeGate)return new(_dispose??=DisposeCoreAsync());
    }
    private async Task DisposeCoreAsync()
    {
        try{_stop.Cancel();}catch(AggregateException){ }
        CloseSockets();
        await _accept.ConfigureAwait(false);
        await Task.WhenAll(_handlers.Values.ToArray()).ConfigureAwait(false);
        _closeOnStop.Dispose();_http.Dispose();_readGate.Dispose();_stop.Dispose();_failed.Dispose();
    }
}
