using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using VideoDownloader.Native;

/// <summary>Real loopback HTTP verifies bounded byte transport, authentication scope and joined cancellation.</summary>
internal static class MediaFrameProxyTests
{
    private static readonly byte[] Media=Enumerable.Range(0,2048).Select(index=>(byte)(index%251)).ToArray();
    private static readonly IReadOnlyDictionary<string,string> Headers=new Dictionary<string,string> {
        ["Authorization"]="Bearer private-frame-fixture",
        ["X-Secret"]="private-header-fixture",
        ["Referer"]="https://frame-fixture.invalid/private?token=private-referrer-fixture",
        ["User-Agent"]="VideoDownloader-FrameFixture",
        ["Cookie"]="injected=must-not-be-forwarded",
    };

    internal static async Task RunAsync()
    {
        await using var source=new Fixture(Media);
        await using var destination=new Fixture(Media);
        // A different hostname avoids relying on extra loopback interface bindings
        // and also verifies that source-only host cookies do not follow redirects.
        source.Redirect=new UriBuilder(destination.Url){Host="localhost"}.Uri;
        using var client=new HttpClient(new SocketsHttpHandler{UseProxy=false}){Timeout=TimeSpan.FromSeconds(5)};
        var cookies=new CookieContainer();
        cookies.Add(source.Url,new Cookie("session","private-cookie-fixture","/media"));
        cookies.Add(source.Url,new Cookie("wrongPath","must-not-be-forwarded","/private"));
        cookies.Add(new Cookie("wrongDomain","must-not-be-forwarded","/","unrelated.invalid"));
        cookies.Add(source.Url,new Cookie("secureOnly","must-not-be-forwarded","/"){Secure=true});

        await RangesAndRequestBounds(source,client,cookies);
        await RedirectScopes(source,destination,client,cookies);
        await ByteLimit(source,client,cookies);
        await IgnoredRange(source,client,cookies);
        await CancellationJoins(source,client,cookies);
        Console.WriteLine("PASS: actual media proxy ranges, cookie/header scope, byte cap, request bounds and joined HTTP cancellation");
    }

    private static async Task RangesAndRequestBounds(Fixture source,HttpClient client,CookieContainer cookies)
    {
        int before=source.Requests.Count;
        await using var proxy=new BoundedMediaProxy(source.Url,Headers,cookies,CancellationToken.None);
        await Task.Delay(50);
        Require(source.Requests.Count==before,"Constructing a bridge must not fetch the media stream");
        using(var request=new HttpRequestMessage(HttpMethod.Get,proxy.Address))
        {
            request.Headers.Range=new RangeHeaderValue(128,255);
            using var response=await client.SendAsync(request);
            var bytes=await response.Content.ReadAsByteArrayAsync();
            Require(response.StatusCode==HttpStatusCode.PartialContent && bytes.SequenceEqual(Media.Skip(128).Take(128)),
                "An exact single range must reach the source and preserve its body");
            Require(response.Content.Headers.ContentRange is {From:128,To:255,Length:2048},
                "The response must retain the source's validated range coordinates");
        }
        var received=source.Requests.Last();
        Require(received.Header("Range")=="bytes=128-255" && received.Header("Authorization")==Headers["Authorization"] &&
            received.Header("Cookie")=="session=private-cookie-fixture" && received.Header("X-Secret")==Headers["X-Secret"] &&
            !received.Text.Contains("must-not-be-forwarded",StringComparison.Ordinal),
            "Only cookies matching the media domain/path/security may accompany its initial request");
        Require(proxy.Failure==null && proxy.ReceivedBytes==128,"A valid response must account for exactly its transferred body bytes");

        using(var head=await client.SendAsync(new HttpRequestMessage(HttpMethod.Head,proxy.Address)))
            Require(head.StatusCode==HttpStatusCode.OK && (await head.Content.ReadAsByteArrayAsync()).Length==0,
                "HEAD must not read or forward a media body");
        Require(proxy.ReceivedBytes==128,"HEAD must leave the frame's body budget intact");
        int validRequests=source.Requests.Count;
        using(var missing=await client.GetAsync(new Uri(proxy.Address,"/unknown-frame-token")))
            Require(missing.StatusCode==HttpStatusCode.NotFound,"The secret path must bind the proxy to this extraction");
        using(var multiple=new HttpRequestMessage(HttpMethod.Get,proxy.Address))
        {
            multiple.Headers.Range=new RangeHeaderValue(0,15);multiple.Headers.Range.Ranges.Add(new RangeItemHeaderValue(32,47));
            using var rejected=await client.SendAsync(multiple);
            Require(rejected.StatusCode==HttpStatusCode.RequestedRangeNotSatisfiable,"Multipart ranges must be rejected before a source request");
        }
        using(var oversized=new HttpRequestMessage(HttpMethod.Get,proxy.Address))
        {
            oversized.Headers.TryAddWithoutValidation("X-Padding",new string('x',9000));
            try
            {
                using var rejected=await client.SendAsync(oversized);
                Require(rejected.StatusCode==HttpStatusCode.BadRequest,"An oversized request must be rejected");
            }
            catch(HttpRequestException)
            {
                // Closing while a large header is still being written may reset
                // the loopback socket instead of delivering its empty 400 body.
            }
        }
        Require(source.Requests.Count==validRequests && proxy.Failure==null,
            "An invalid token, multipart range or oversized header must not fetch media or fail a different valid request");
        using(var subsequent=new HttpRequestMessage(HttpMethod.Get,proxy.Address))
        {
            subsequent.Headers.Range=new RangeHeaderValue(0,0);
            using var response=await client.SendAsync(subsequent);
            Require((await response.Content.ReadAsByteArrayAsync()).SequenceEqual(Media.Take(1)),
                "A rejected local request must not prevent the frame owner from making its next valid request");
        }
    }

    private static async Task RedirectScopes(Fixture source,Fixture destination,HttpClient client,CookieContainer cookies)
    {
        await using var proxy=new BoundedMediaProxy(new Uri(source.Url,"/redirect"),Headers,cookies,CancellationToken.None);
        var result=await client.GetByteArrayAsync(proxy.Address);
        Require(result.SequenceEqual(Media),"An allowed HTTP redirect must preserve the requested stream");
        var redirected=destination.Requests.Last();
        Require(redirected.Header("User-Agent")==Headers["User-Agent"] && redirected.Header("Authorization")=="" &&
            redirected.Header("X-Secret")=="" && redirected.Header("Referer")=="" && redirected.Header("Cookie")=="",
            "Cross-origin redirects must strip authorization, custom secrets and source-scoped cookies");
        Require(proxy.Failure==null,"A safe redirect must not leave a bridge failure");
    }

    private static async Task ByteLimit(Fixture source,HttpClient client,CookieContainer cookies)
    {
        await using var proxy=new BoundedMediaProxy(source.Url,Headers,cookies,CancellationToken.None,128);
        try{await client.GetByteArrayAsync(proxy.Address);}catch(HttpRequestException){ }
        Require(proxy.Failure is IOException && proxy.Failure.Message.Contains("128",StringComparison.Ordinal) &&
            proxy.FailureToken.IsCancellationRequested && proxy.ReceivedBytes==129,
            "A capped response must stop after one overflow-detection byte and signal its FFmpeg owner");
        Require(proxy.Failure?.Message.Contains("private-",StringComparison.Ordinal)!=true,"A transfer-limit error must not reveal credentials");
    }

    private static async Task IgnoredRange(Fixture source,HttpClient client,CookieContainer cookies)
    {
        await using var proxy=new BoundedMediaProxy(new Uri(source.Url,"/ignore-range"),Headers,cookies,CancellationToken.None);
        using var request=new HttpRequestMessage(HttpMethod.Get,proxy.Address);
        request.Headers.Range=new RangeHeaderValue(128,null);
        try{using var response=await client.SendAsync(request);}catch(HttpRequestException){ }
        Require(proxy.Failure is IOException && proxy.Failure.Message.Contains("seeking",StringComparison.Ordinal) && proxy.ReceivedBytes==0,
            "A nonzero seek ignored by the source must fail before the proxy reads a sequential/full body");
    }

    private static async Task CancellationJoins(Fixture source,HttpClient client,CookieContainer cookies)
    {
        using var stop=new CancellationTokenSource();
        var proxy=new BoundedMediaProxy(new Uri(source.Url,"/stall"),Headers,cookies,stop.Token);
        try
        {
            var transfer=client.GetByteArrayAsync(proxy.Address);
            await source.Stalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var watch=Stopwatch.StartNew();stop.Cancel();
            await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
            try{await transfer.WaitAsync(TimeSpan.FromSeconds(2));}catch(HttpRequestException){ }
            await source.StalledDisconnected.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(watch.Elapsed<TimeSpan.FromSeconds(2) && proxy.Failure==null,
                "Cancellation must close source/client sockets and join handlers before returning, without recording a source failure; elapsed_ms="+
                watch.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture)+" failure="+(proxy.Failure?.ToString()??"none"));
        }
        finally{await proxy.DisposeAsync();}
    }

    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Media frame proxy: "+message);}

    private sealed record ReceivedRequest(string Text)
    {
        internal string Header(string name)=>Text.Split("\r\n").FirstOrDefault(line=>line.StartsWith(name+":",StringComparison.OrdinalIgnoreCase))?
            [(name.Length+1)..].Trim()??"";
    }
    private sealed class Fixture:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _handlers=[];
        private readonly ConcurrentDictionary<TcpClient,byte> _clients=[];
        private readonly byte[] _media;
        private readonly Task _accept;
        internal Uri Url { get; }
        internal Uri? Redirect { get; set; }
        internal ConcurrentQueue<ReceivedRequest> Requests { get; }=[];
        internal TaskCompletionSource Stalled { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource StalledDisconnected { get; }=new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Fixture(byte[] media)
        {
            _media=media;_listener.Start();
            Url=new Uri("http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture)+"/media");
            _accept=AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _clients[client]=0;_handlers.Add(ServeAsync(client));
                }
            }
            catch(Exception) when(_stop.IsCancellationRequested){ }
        }
        private async Task ServeAsync(TcpClient client)
        {
            bool stalled=false;
            using(client)
            try
            {
                using var stream=client.GetStream();
                var raw=new List<byte>();
                byte[] character=new byte[1];
                while(raw.Count<8192)
                {
                    if(await stream.ReadAsync(character,_stop.Token)==0)return;
                    raw.Add(character[0]);
                    if(raw.Count>=4 && raw[^4]==13 && raw[^3]==10 && raw[^2]==13 && raw[^1]==10)break;
                }
                string text=Encoding.ASCII.GetString(raw.ToArray());
                var received=new ReceivedRequest(text);Requests.Enqueue(received);
                string[] first=text.Split("\r\n")[0].Split(' ');
                string path=first[1];
                if(path=="/redirect")
                {
                    await WriteAsync(stream,"HTTP/1.1 302 Found\r\nLocation: "+Redirect+"\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");return;
                }
                if(path=="/stall")
                {
                    stalled=true;
                    await WriteAsync(stream,"HTTP/1.1 200 OK\r\nContent-Type: video/mp4\r\nContent-Length: 100000\r\nConnection: close\r\n\r\n");
                    Stalled.TrySetResult();
                    await stream.ReadAsync(character,_stop.Token);return;
                }
                long start=0,end=_media.Length-1;
                bool partial=received.Header("Range") is {Length:>0} && path!="/ignore-range";
                if(partial)
                {
                    var range=RangeHeaderValue.Parse(received.Header("Range")).Ranges.Single();
                    start=range.From??0;end=range.To??end;
                }
                string headers="HTTP/1.1 "+(partial?"206":"200")+" Media\r\nContent-Type: video/mp4\r\nContent-Length: "+
                    (end-start+1).ToString(CultureInfo.InvariantCulture)+"\r\nAccept-Ranges: bytes\r\nConnection: close\r\n"+
                    (partial?"Content-Range: bytes "+start.ToString(CultureInfo.InvariantCulture)+"-"+end.ToString(CultureInfo.InvariantCulture)+"/"+
                        _media.Length.ToString(CultureInfo.InvariantCulture)+"\r\n":"")+"\r\n";
                await WriteAsync(stream,headers);
                if(first[0]!="HEAD")await stream.WriteAsync(_media.AsMemory((int)start,(int)(end-start+1)),_stop.Token);
            }
            catch(Exception error) when(_stop.IsCancellationRequested || error is IOException or SocketException){ }
            finally
            {
                if(stalled)StalledDisconnected.TrySetResult();
                _clients.TryRemove(client,out _);
            }
        }
        private Task WriteAsync(NetworkStream stream,string text)=>stream.WriteAsync(Encoding.ASCII.GetBytes(text),_stop.Token).AsTask();
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();foreach(var client in _clients.Keys)client.Dispose();
            await _accept;await Task.WhenAll(_handlers.ToArray());_stop.Dispose();
        }
    }
}
