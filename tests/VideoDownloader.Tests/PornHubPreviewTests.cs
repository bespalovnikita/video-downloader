using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using VideoDownloader.Native;

internal static class PornHubPreviewTests
{
    private const string OrdinaryPrefix="https://pornhub-preview-fixture.invalid/";
    private static readonly HashSet<string> FixtureKeys=new(StringComparer.Ordinal)
    {
        "phvdpreviewfixturecold","phvdpreviewfixturewarm","phvdpreviewfixturecover","phvdpreviewfixtureabsence",
        "phvdpreviewfixturefresherror","phvdpreviewfixtureexpirederror"
    };

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        bool watch=Uri.TryCreate(url,UriKind.Absolute,out var address) && address.Scheme=="https" &&
            address.Host is "www.pornhub.com" or "rt.pornhub.com" && address.AbsolutePath=="/view_video.php" &&
            FixtureKeys.Contains(address.Query.TrimStart('?').Split('&')[0].Replace("viewkey=","",StringComparison.Ordinal));
        if(!watch && !url.StartsWith(OrdinaryPrefix,StringComparison.Ordinal))return false;
        Require(new[]{"--ignore-config","--dump-single-json","--skip-download"}.All(args.Contains),
            "The preview fixture may only be used for metadata extraction.");
        int cookies=Array.IndexOf(args,"--cookies");
        Require(cookies>=0 && cookies+1<args.Length,"The preview fixture needs its private control file.");
        using var control=JsonDocument.Parse(File.ReadAllText(args[cookies+1]));
        bool barrier=url.StartsWith(OrdinaryPrefix+"barrier-",StringComparison.Ordinal);
        if(!barrier)Require(url==control.RootElement.GetProperty("ExpectedUrl").GetString(),
            "The child must receive the exact original URL, including its subdomain and query case.");
        if(watch)
        {
            int plugin=Array.IndexOf(args,"--plugin-dirs"),extractor=Array.IndexOf(args,"--use-extractors");
            Require(args.Count(argument=>argument=="--no-plugin-dirs")==1 && plugin>=0 && plugin+1<args.Length &&
                args[plugin+1]==PornHubChannel.PluginDirectory && extractor>=0 && extractor+1<args.Length &&
                args[extractor+1]=="videodownloader:pornhub:preview",
                "A supported watch preview must select precisely the packaged preview extractor.");
        }
        else Require(!args.Contains("--use-extractors") && !args.Contains("--plugin-dirs") && !args.Contains("--no-plugin-dirs"),
            "Ordinary sites must retain the normal extractor selection.");
        File.AppendAllText(control.RootElement.GetProperty("Calls").GetString()!,JsonSerializer.Serialize(new
        {
            Url=url,Arguments=args,ProcessId=Environment.ProcessId,MetadataOnly=true
        })+Environment.NewLine,Encoding.UTF8);
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            title=barrier?"Worker barrier":control.RootElement.GetProperty("Title").GetString(),
            extractor=watch?"PornHub":"PreviewFixture",duration=1,
            thumbnail=barrier?"":control.RootElement.GetProperty("ImageUrl").GetString()
        }));
        return true;
    }

    internal static async Task RunAsync()
    {
        Routing();
        string root=Path.Combine(Path.GetTempPath(),"vd-pornhub-preview-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] image=CreatePng();
            await using var server=new ImageServer(image);
            await CacheCase(root,server,image,"cold",Watch("cold"),3,cover:false,warm:false,reextract:true);
            await CacheCase(root,server,image,"warm",Watch("warm","rt")+"&Proof=ExactCase",3,cover:false,warm:true,reextract:true);
            await CacheCase(root,server,image,"ordinary",OrdinaryPrefix+"ordinary",3,cover:false,warm:true,reextract:false);
            await CacheCase(root,server,image,"cover",Watch("cover"),3,cover:true,warm:true,reextract:false);
            await CacheCase(root,server,image,"absence",Watch("absence"),4,cover:false,warm:true,reextract:false);
            await CacheCase(root,server,image,"fresherror",Watch("fresherror"),3,cover:false,warm:true,reextract:false,
                error:"HTTP 403 thumbnail failure",fresh:true);
            await CacheCase(root,server,image,"expirederror",Watch("expirederror"),3,cover:false,warm:true,reextract:true,
                error:"HTTP 403 thumbnail failure");
            await PackagedExtractorDiscovery();
            Console.WriteLine("PASS: Pornhub preview routing, cold/warm empty-v3 recovery with real HTTP PNGs, usable-v3 and genuine-v4 cache reuse, exact URL identity, packaged extractor discovery and no downloads/history");
        }
        finally{Directory.Delete(root,true);}
    }

    private static string Watch(string name,string subdomain="www")=>
        "https://"+subdomain+".pornhub.com/view_video.php?viewkey=phvdpreviewfixture"+name;

    private static void Routing()
    {
        foreach(string url in new[]
        {
            "https://www.pornhub.com/view_video.php?viewkey=6a0a221dc5b27",
            "https://rt.pornhub.com/view_video.php?viewkey=6a0a221dc5b27",
            "https://WWW.pornhub.com/view_video.php?viewkey=phabc123&Proof=ExactCase#t=1",
            "http://www.pornhubpremium.com/view_video.php?viewkey=12345",
            "https://pornhub.net/view_video.php?viewkey=abc123"
        })Require(PornHubPreview.Supports(url),"Supported explicit watch-page route was missed: "+url);
        foreach(string url in new[]
        {
            "https://www.pornhub.com/model/dolly-rud","https://www.pornhub.com/login",
            "https://evilpornhub.com/view_video.php?viewkey=abc","https://pornhub.com.evil.invalid/view_video.php?viewkey=abc",
            "file:///view_video.php?viewkey=abc","ftp://www.pornhub.com/view_video.php?viewkey=abc",
            "https://user:password@www.pornhub.com/view_video.php?viewkey=abc",
            "https://www.pornhub.com:444/view_video.php?viewkey=abc","https://www.pornhub.com:443/view_video.php?viewkey=abc",
            "https://www.pornhub.com/view_video.php?viewkey=","https://www.pornhub.com/view_video.php?viewkey=bad-key",
            "https://www.pornhub.com/view_video.php?viewkey=%61bc","https://www.pornhub.com/view_video.php?viewkey=ABC",
            "https://www.pornhub.com/view_video.php?other=x&viewkey=abc","https://www.pornhub.com/%76iew_video.php?viewkey=abc",
            "HTTPS://www.pornhub.com/view_video.php?viewkey=abc","https://www.PornHub.com/view_video.php?viewkey=abc",
            "https://www.pornhub.com/other/view_video.php?viewkey=abc","https://www.pornhub.com/view_video.php.evil?viewkey=abc"
        })Require(!PornHubPreview.Supports(url),"An unsupported route must retain the normal extractor path: "+url);
    }

    private static async Task CacheCase(string root,ImageServer server,byte[] image,string name,string url,
        int version,bool cover,bool warm,bool reextract,string error="",bool fresh=false)
    {
        string directory=Path.Combine(root,name);Directory.CreateDirectory(directory);
        var storage=new AppPersistence(Path.Combine(directory,"profile"));Directory.CreateDirectory(storage.PreviewDir);
        string cache=Path.Combine(storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        string encoded=cover?Convert.ToBase64String(image):"";
        string oldTitle="Cached "+name,freshTitle="Fresh "+name;
        var saved=fresh?DateTime.UtcNow:DateTime.UtcNow.AddMinutes(-2);
        File.WriteAllText(cache,JsonSerializer.Serialize(new
        {
            CacheVersion=1,PreviewFetchVersion=version,Url=url,SavedAtUtc=saved,Success=true,ThumbnailError=error,
            Title=oldTitle,Uploader="",PlaylistTitle="",Duration=1,Extractor="PornHub",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
            ThumbnailBase64=encoded,ThumbnailsBase64=cover?new[]{encoded}:Array.Empty<string>(),FormatsLoaded=true
        }));
        string calls=Path.Combine(directory,"calls.jsonl"),control=Path.Combine(directory,"control.json");
        File.WriteAllText(control,JsonSerializer.Serialize(new
        {
            ExpectedUrl=url,Calls=calls,Title=freshTitle,ImageUrl=server.Address+"/"+name+".png"
        }));
        var results=new ConcurrentDictionary<string,PreviewData>(StringComparer.Ordinal);
        var barrier=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failures=new ConcurrentQueue<string>();
        string barrierUrl=OrdinaryPrefix+"barrier-"+name;
        int beforeImages=server.Requests.Count;
        var service=new PreviewService(Executable(),storage,()=>control,data=>
        {
            results[data.Url]=data;if(data.Url==barrierUrl)barrier.TrySetResult();
        },_=>{},failed:(failed,error)=>failures.Enqueue(failed+": "+error));
        try
        {
            if(warm)Require(service.Get(url) is {} previous && previous.Title==oldTitle && previous.ThumbnailBase64==encoded,
                "The warm phase must load the real persisted v3/v4 cache through the production decoder.");
            service.Request(url,true);service.Request(barrierUrl);
            await barrier.Task.WaitAsync(TimeSpan.FromSeconds(12));
            var current=service.Get(url);
            Require(failures.IsEmpty && current is not null && current.Url==url,
                "The original URL and current metadata must survive cache recovery: "+string.Join("; ",failures));
            Require(current!.Title==(reextract?freshTitle:oldTitle) &&
                current.ThumbnailBase64==(reextract?Convert.ToBase64String(image):encoded) &&
                server.Requests.Count==beforeImages+(reextract?1:0),
                "Only an empty old known-watch result may re-extract and fetch the real PNG: "+name);
            if(reextract)
            {
                var decoded=PreviewImage.Decode(Convert.FromBase64String(current.ThumbnailBase64));
                Require(decoded.PixelWidth==2 && decoded.PixelHeight==2,"The recovered HTTP cover must be a real decoded 2x2 raster.");
            }
            var rows=File.ReadAllLines(calls,Encoding.UTF8).Where(line=>!string.IsNullOrWhiteSpace(line))
                .Select(line=>JsonDocument.Parse(line)).ToArray();
            try
            {
                Require(rows.Select(row=>row.RootElement.GetProperty("Url").GetString()).SequenceEqual(reextract?new[]{url,barrierUrl}:new[]{barrierUrl}) &&
                    rows.All(row=>row.RootElement.GetProperty("MetadataOnly").GetBoolean() && row.RootElement.GetProperty("ProcessId").GetInt32()!=Environment.ProcessId),
                    "The worker barrier must prove exact one-time recovery or no metadata child, with no media download: "+name);
            }
            finally{foreach(var row in rows)row.Dispose();}
            using(var document=JsonDocument.Parse(File.ReadAllText(cache)))
                Require(document.RootElement.GetProperty("PreviewFetchVersion").GetInt32()==(reextract?PreviewService.CurrentFetchVersion:version) &&
                    document.RootElement.GetProperty("Url").GetString()==url,
                    "Recovered results must persist v4 under the unchanged original cache identity; cache hits retain their version.");
            Require(!File.Exists(storage.HistoryPath),"Preview extraction and cache migration cannot manufacture successful-download history.");
        }
        finally{service.Dispose();await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
    }

    private static async Task PackagedExtractorDiscovery()
    {
        string executable=Path.Combine(AppContext.BaseDirectory,"yt-dlp.exe");
        Require(File.Exists(executable),"The actual bundled yt-dlp executable must be available.");
        var start=new ProcessStartInfo(executable)
        {
            UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true
        };
        foreach(string arg in new[]{"--ignore-config","--no-plugin-dirs","--plugin-dirs",PornHubChannel.PluginDirectory,
            "--use-extractors","videodownloader:pornhub:preview","--verbose","--skip-download","vdph-test:invalid"})start.ArgumentList.Add(arg);
        start.Environment["PYTHONDONTWRITEBYTECODE"]="1";
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var child=ManagedChildProcess.Start(start);
        var output=await BoundedProcessOutput.ReadAsync(child,128*1024,256*1024,deadline.Token);
        string error=Encoding.UTF8.GetString(output.StandardError);
        Require(child.Process.ExitCode!=0 && error.Split('\n').Any(line=>line.StartsWith("[debug] Extractor Plugins:",StringComparison.Ordinal) &&
                line.Contains("VideoDownloaderPornHubPreviewIE",StringComparison.Ordinal)) &&
            error.Contains("No suitable extractor",StringComparison.Ordinal) && !error.Contains("Downloading webpage",StringComparison.Ordinal),
            "The real packaged executable must discover the preview plugin and reject an unsupported non-network URL.");
    }

    private static string Executable()=>Path.Combine(AppContext.BaseDirectory,"VideoDownloader.Tests.exe");
    private static byte[] CreatePng()
    {
        var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,
            new byte[]{17,85,169,20,88,172,23,91,175,26,94,178},6);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }
    private static void Require(bool value,string reason)
    {if(!value)throw new InvalidOperationException("Pornhub preview regression failed: "+reason);}

    private sealed class ImageServer:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _responses=[];
        private readonly Task _accept;
        private readonly byte[] _bytes;
        internal readonly ConcurrentQueue<string> Requests=new();
        internal string Address {get;}
        internal ImageServer(byte[] bytes)
        {
            _bytes=bytes;_listener.Start();Address="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept=Accept();
        }
        private async Task Accept()
        {
            try
            {
                while(true)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _responses.Add(Respond(client));
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(SocketException) when(_stop.IsCancellationRequested){}
        }
        private async Task Respond(TcpClient client)
        {
            using(client)
            try
            {
                var stream=client.GetStream();var header=new StringBuilder();byte[] buffer=new byte[1024];
                while(!header.ToString().Contains("\r\n\r\n",StringComparison.Ordinal))
                {
                    int read=await stream.ReadAsync(buffer,_stop.Token);if(read==0)return;
                    header.Append(Encoding.ASCII.GetString(buffer,0,read));
                    if(header.Length>8192)throw new InvalidDataException("Fixture HTTP header limit exceeded.");
                }
                Requests.Enqueue(header.ToString().Split('\r')[0].Split(' ')[1]);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: "+
                    _bytes.Length+"\r\nConnection: close\r\n\r\n"),_stop.Token);
                await stream.WriteAsync(_bytes,_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException){}
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();await _accept;await Task.WhenAll(_responses);_stop.Dispose();
        }
    }
}
