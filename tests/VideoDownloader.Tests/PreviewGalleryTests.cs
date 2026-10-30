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

internal static class PreviewGalleryTests
{
    private const string Prefix="https://preview-gallery.invalid/";

    internal static bool TryRunFixture(string[] args)
    {
        string url=args.LastOrDefault()??"";
        if(!url.StartsWith(Prefix,StringComparison.Ordinal))return false;
        Require(new[]{"--ignore-config","--dump-single-json","--skip-download"}.All(args.Contains),
            "Gallery acquisition must only request metadata, never download media");
        int cookie=Array.IndexOf(args,"--cookies");
        Require(cookie>=0 && cookie+1<args.Length,"The gallery fixture needs its metadata control file");
        using var control=JsonDocument.Parse(File.ReadAllText(args[cookie+1]));
        File.AppendAllText(control.RootElement.GetProperty("Calls").GetString()!,url+Environment.NewLine);
        Console.WriteLine(control.RootElement.GetProperty("Metadata").GetRawText());
        return true;
    }

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-preview-gallery-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var images=Enumerable.Range(0,24).Select(Image).ToArray();
        using var stop=new CancellationTokenSource();
        var listener=new TcpListener(IPAddress.Loopback,0);listener.Start();
        string address="http://127.0.0.1:"+((IPEndPoint)listener.LocalEndpoint).Port;
        var requests=new ConcurrentQueue<string>();
        var responses=new List<Task>();
        var gate=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gateStarted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var server=Task.Run(async()=>
        {
            while(!stop.IsCancellationRequested)
            {
                var client=await listener.AcceptTcpClientAsync(stop.Token);
                responses.Add(Task.Run(async()=>
                {
                    using(client)
                    {
                        var stream=client.GetStream();byte[] buffer=new byte[4096];
                        int read=await stream.ReadAsync(buffer,stop.Token);
                        string target=Encoding.ASCII.GetString(buffer,0,read).Split('\r')[0].Split(' ')[1];
                        requests.Enqueue(target);
                        byte[] bytes=target=="/bad.png"?new byte[]{0x89,0x50,0x4e,0x47,0x0d,0x0a,0x1a,0x0a}:
                            target=="/duplicate.png"?images[0]:
                            target=="/hold.png"?images[20]:target=="/stall.png"?images[21]:
                            images[int.Parse(target.Split('/').Last().Split('.')[0])];
                        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"),stop.Token);
                        if(target=="/hold.png") {gateStarted.TrySetResult();await gate.Task.WaitAsync(stop.Token);}
                        if(target=="/stall.png") {await Task.Delay(Timeout.Infinite,stop.Token);return;}
                        await stream.WriteAsync(bytes,stop.Token);
                    }
                }));
            }
        });
        string executable=Path.Combine(AppContext.BaseDirectory,typeof(PreviewGalleryTests).Assembly.GetName().Name+".exe");
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        object Metadata(string title,params string[] paths)=>new{title,uploader="Gallery fixture",thumbnails=paths.Select((path,index)=>
            new{url=address+path,width=1000-index,height=1000-index}).ToArray()};
        string Control(string name,object metadata)
        {
            string path=Path.Combine(root,name+".json");
            File.WriteAllText(path,JsonSerializer.Serialize(new{Calls=Path.Combine(root,name+".calls"),Metadata=metadata}));
            return path;
        }
        static TaskCompletionSource<PreviewData> Completion()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
        string Cache(string url)=>Path.Combine(storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
        void WriteCache(string url,string title,DateTime savedAtUtc,string error,string cover="")=>File.WriteAllText(Cache(url),JsonSerializer.Serialize(new {
            CacheVersion=1,PreviewFetchVersion=3,Url=url,SavedAtUtc=savedAtUtc,Success=true,ThumbnailError=error,
            Title=title,Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
            ThumbnailBase64=cover,ThumbnailsBase64=cover.Length==0?Array.Empty<string>():new[]{cover}
        }));
        try
        {
            var ready=Completion();var updates=new ConcurrentQueue<PreviewData>();int finalCalls=0;
            string[] candidates=["/bad.png","/duplicate.png","/image/0.png","/image/0.png",..Enumerable.Range(1,20).Select(index=>$"/image/{index}.png")];
            string control=Control("fifteen",Metadata("Fifteen images",candidates));
            using(var service=new PreviewService(executable,storage,()=>control,value=>{Interlocked.Increment(ref finalCalls);ready.TrySetResult(value);},_=>{},updated:updates.Enqueue))
            {
                service.Request(Prefix+"fifteen");
                var gallery=await ready.Task.WaitAsync(TimeSpan.FromSeconds(15));
                Require(gallery.ThumbnailsBase64.Length==15 && gallery.ThumbnailsBase64.Distinct(StringComparer.Ordinal).Count()==15,
                    "Only fifteen valid distinct site images may enter the gallery; invalid images and identical bytes do not consume slots");
                Require(gallery.ThumbnailBase64==gallery.ThumbnailsBase64[0] &&
                    Convert.FromBase64String(gallery.ThumbnailBase64).SequenceEqual(images[0]),"The legacy cover must remain the first valid gallery image");
                Require(requests.Count(path=>path=="/image/0.png")==1 && requests.Contains("/bad.png") && !requests.Contains("/image/15.png"),
                    "Exact duplicate URLs must be skipped, fallback must work, and the worker must stop once fifteen images are accepted");
                Require(updates.Any(value=>value.Url==gallery.Url && value.Title==gallery.Title) && finalCalls==1,
                    "Progress must retain exact metadata identity while the final callback fires once");
                await Task.Delay(100);service.Request(gallery.Url,true);await Task.Delay(100);
                Require(File.ReadAllLines(Path.Combine(root,"fifteen.calls")).Length==1,"A completed gallery must not launch another metadata worker");
            }

            var partial=Completion();var complete=Completion();
            control=Control("progress",Metadata("Progressive gallery","/image/1.png","/hold.png"));
            using(var service=new PreviewService(executable,storage,()=>control,value=>complete.TrySetResult(value),_=>{},
                updated:value=>{if(value.ThumbnailBase64.Length>0)partial.TrySetResult(value);}))
            {
                string url=Prefix+"progress";service.Request(url);
                var first=await partial.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await gateStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Require(first.Url==url && first.Title=="Progressive gallery" && !complete.Task.IsCompleted,
                    "The first image and title must reach the UI while a later image body is still waiting");
                service.Request(url,true);service.Request(url,true);
                Require(service.Get(url)?.ThumbnailBase64==first.ThumbnailBase64,"An in-flight gallery must remain immediately readable");
                gate.TrySetResult();
                Require((await complete.Task.WaitAsync(TimeSpan.FromSeconds(5))).ThumbnailsBase64.Length==2 &&
                    File.ReadAllLines(Path.Combine(root,"progress.calls")).Length==1,"Priority requests during gallery loading must reuse its one metadata child");
            }

            string legacyUrl=Prefix+"legacy";
            File.WriteAllText(Cache(legacyUrl),JsonSerializer.Serialize(new{CacheVersion=1,PreviewFetchVersion=2,Url=legacyUrl,SavedAtUtc=DateTime.UtcNow,
                Title="Legacy title",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
                ThumbnailBase64=Convert.ToBase64String(images[0])}));
            ready=Completion();control=Control("legacy",Metadata("Upgraded title","/image/2.png","/image/3.png"));
            using(var service=new PreviewService(executable,storage,()=>control,value=>ready.TrySetResult(value),_=>{}))
            {
                Require(service.Get(legacyUrl)?.Title=="Legacy title","A legacy single-image cache must remain usable before gallery upgrade");
                service.Request(legacyUrl);service.Request(legacyUrl);
                var upgraded=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(upgraded.Title=="Upgraded title" && upgraded.ThumbnailsBase64.Length==2 && File.ReadAllLines(Path.Combine(root,"legacy.calls")).Length==1,
                    "A legacy cache must upgrade exactly once to the new gallery without losing metadata");
            }
            var failures=new ConcurrentQueue<string>();
            using(var restarted=new PreviewService("must-not-run",storage,()=>"",_=>throw new Exception("A valid gallery must not fetch again"),_=>{},failed:(url,error)=>failures.Enqueue(error)))
            {
                Require(restarted.Get(legacyUrl)?.ThumbnailsBase64.Length==2,"Gallery images must survive a service restart");
                restarted.Request(legacyUrl);await Task.Delay(100);
                Require(failures.IsEmpty,"A current gallery cache must suppress metadata acquisition after restart");
                string different=Prefix+"Legacy";File.Copy(Cache(legacyUrl),Cache(different));
                Require(restarted.Get(different)==null && !File.Exists(Cache(different)),"Disk gallery identity must use the exact URL, including case");
            }
            ready=Completion();int coldReadyCalls=0;
            using(var cold=new PreviewService("must-not-run",storage,()=>throw new Exception("Disk cache delivery must not read download credentials"),
                value=>{Interlocked.Increment(ref coldReadyCalls);ready.TrySetResult(value);},_=>{},failed:(url,error)=>failures.Enqueue(error)))
            {
                Require(cold.Peek(legacyUrl)==null,"A memory-only cache lookup must not load a cold disk gallery");
                cold.Request(legacyUrl);cold.Request(legacyUrl,true);
                var cached=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await Task.Delay(100);
                Require(cached.Title=="Upgraded title" && cached.ThumbnailsBase64.Length==2 && cold.Peek(legacyUrl)?.Title==cached.Title &&
                    coldReadyCalls==1 && failures.IsEmpty,"A cold cache request must deliver and warm its gallery once, without a metadata child");
            }
            foreach(bool warmMemory in new[]{false,true})
            {
                string name=warmMemory?"retry-warm":"retry-cold",url=Prefix+name;
                string title="Known title survives a failed cover";
                WriteCache(url,title,DateTime.UtcNow.AddMinutes(-2),"HTTP Error 503: temporary thumbnail failure");
                int imageIndex=warmMemory?7:6;
                string imagePath=$"/image/{imageIndex}.png";
                int before=requests.Count(path=>path==imagePath);
                control=Control(name,Metadata("",imagePath));
                ready=Completion();
                using var retry=new PreviewService(executable,storage,()=>control,value=>ready.TrySetResult(value),_=>{});
                if(warmMemory)
                    Require(retry.Get(url)?.Title==title,"The failed-cover cache must remain immediately usable for its known title");
                else Require(retry.Peek(url)==null,"A cold failed-cover retry must begin without a memory cache entry");
                retry.Request(url);retry.Request(url);
                var recovered=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(recovered.Title==title && recovered.ThumbnailsBase64.Length==1 &&
                        Convert.FromBase64String(recovered.ThumbnailBase64).SequenceEqual(images[imageIndex]),
                    "An expired failed-cover result must retry acquisition and retain useful metadata: "+name);
                Require(File.ReadAllLines(Path.Combine(root,name+".calls")).Length==1 && requests.Count(path=>path==imagePath)==before+1,
                    "A failed-cover retry must execute one actual metadata child and one actual HTTP image request: "+name);
                Require(retry.Peek(url)?.ThumbnailBase64==recovered.ThumbnailBase64,
                    "A recovered thumbnail must replace the failed-cover memory cache immediately");
            }

            string recentFailure=Prefix+"recent-thumbnail-failure",noCandidates=Prefix+"no-thumbnail-candidates",
                partialCover=Prefix+"partial-gallery-with-cover";
            WriteCache(recentFailure,"Fresh failed cover",DateTime.UtcNow,"HTTP Error 503: thumbnail failure");
            WriteCache(noCandidates,"Source has no cover",DateTime.UtcNow.AddHours(-2),"");
            WriteCache(partialCover,"Usable partial gallery",DateTime.UtcNow.AddHours(-2),"Thumbnail timed out",Convert.ToBase64String(images[8]));
            byte[] recentBytes=File.ReadAllBytes(Cache(recentFailure)),noCandidateBytes=File.ReadAllBytes(Cache(noCandidates));
            byte[] partialBytes=File.ReadAllBytes(Cache(partialCover));
            string suppressionControl=Control("retry-suppression",Metadata("Must not fetch","/image/8.png"));
            var delivered=new ConcurrentQueue<string>();var suppressedFailures=new ConcurrentQueue<string>();ready=Completion();
            int requestsBeforeSuppression=requests.Count;
            using(var suppressed=new PreviewService(executable,storage,()=>suppressionControl,value=>
                {delivered.Enqueue(value.Url);if(value.Url==legacyUrl)ready.TrySetResult(value);},_=>{},
                failed:(url,error)=>suppressedFailures.Enqueue(url+": "+error)))
            {
                Require(suppressed.Get(recentFailure)?.Title=="Fresh failed cover" && suppressed.Get(noCandidates)?.Title=="Source has no cover" &&
                        suppressed.Get(partialCover)?.ThumbnailBase64==Convert.ToBase64String(images[8]),
                    "Fresh failures, sources without images and useful partial galleries must expose their cached metadata immediately");
                suppressed.Request(recentFailure);suppressed.Request(noCandidates);suppressed.Request(partialCover);
                // This valid disk gallery is a FIFO barrier: once its callback
                // arrives, the single worker has finished any wrongly admitted
                // retries queued by the three requests above.
                suppressed.Request(legacyUrl);
                await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(delivered.SequenceEqual(new[]{legacyUrl}) && suppressedFailures.IsEmpty &&
                        !File.Exists(Path.Combine(root,"retry-suppression.calls")) && requests.Count==requestsBeforeSuppression,
                    "Fresh failed covers must be throttled; sources without images and galleries with a usable cover must keep their normal cache without retrying");
                Require(File.ReadAllBytes(Cache(recentFailure)).SequenceEqual(recentBytes) &&
                        File.ReadAllBytes(Cache(noCandidates)).SequenceEqual(noCandidateBytes) &&
                        File.ReadAllBytes(Cache(partialCover)).SequenceEqual(partialBytes),
                    "Suppressed requests must not rewrite failed-cover, no-image or usable partial-gallery cache records");
            }

            string unavailable=Prefix+"legacy-upgrade-unavailable";
            File.WriteAllText(Cache(unavailable),JsonSerializer.Serialize(new{CacheVersion=1,PreviewFetchVersion=2,Url=unavailable,SavedAtUtc=DateTime.UtcNow,
                Title="Usable legacy cover",Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
                ThumbnailBase64=Convert.ToBase64String(images[0])}));
            byte[] beforeFailedUpgrade=File.ReadAllBytes(Cache(unavailable));ready=Completion();
            using(var failedUpgrade=new PreviewService("must-not-run",storage,()=>"",value=>ready.TrySetResult(value),_=>{},failed:(url,error)=>failures.Enqueue(error)))
            {
                var old=failedUpgrade.Get(unavailable)!;failedUpgrade.Request(unavailable);
                var retained=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(retained.Title==old.Title && retained.ThumbnailBase64==old.ThumbnailBase64 &&
                    failedUpgrade.Get(unavailable)?.ThumbnailBase64==old.ThumbnailBase64 && failures.IsEmpty &&
                    File.ReadAllBytes(Cache(unavailable)).SequenceEqual(beforeFailedUpgrade),
                    "A failed legacy-gallery upgrade must retain its valid title, image and original disk record without reporting total preview failure");
            }

            ready=Completion();var logs=new ConcurrentQueue<string>();
            control=Control("timeout",Metadata("Partial after timeout","/image/4.png","/stall.png"));
            using(var service=new PreviewService(executable,storage,()=>control,value=>ready.TrySetResult(value),logs.Enqueue,
                requestTimeout:TimeSpan.FromSeconds(8),thumbnailTimeout:TimeSpan.FromMilliseconds(500)))
            {
                service.Request(Prefix+"timeout");
                var timed=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(timed.ThumbnailsBase64.Length==1 && timed.Title=="Partial after timeout" && logs.Any(line=>line.Contains("Thumbnail timed out")),
                    "The shared body deadline must retain already received gallery images and metadata");
                ready=Completion();control=Control("after-timeout",Metadata("After stalled gallery","/image/5.png"));
                service.Request(Prefix+"after-timeout");
                var recovered=await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
                Require(recovered.Url==Prefix+"after-timeout" && recovered.ThumbnailsBase64.Length==1,
                    "A stalled gallery must release the single worker for the next metadata request");
            }
            MemoryBudget(Path.Combine(root,"memory-budget"));
            Require(!File.Exists(storage.HistoryPath),"Gallery acquisition must never record a successful media download");
            Console.WriteLine("PASS: native preview galleries; fifteen-image limit, fallback, duplicate suppression, progressive delivery, legacy upgrade, failed-cover retry/throttle, exact cache identity and body deadline");
        }
        finally
        {
            gate.TrySetResult();stop.Cancel();listener.Stop();
            try{await server;}catch(OperationCanceledException){}
            try{await Task.WhenAll(responses);}catch(OperationCanceledException){}catch(IOException){}
            Directory.Delete(root,true);
        }
    }

    private static void MemoryBudget(string root)
    {
        var storage=new AppPersistence(root);
        // Deliberately bypass raster validation here: this isolates encoded
        // cache retention from image decoding, which the HTTP cases test above.
        string image=Convert.ToBase64String(new byte[1_000_000]);
        string Url(int index)=>Prefix+"memory-"+index;
        string Cache(int index)=>Path.Combine(storage.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Url(index)))).ToLowerInvariant()+".json");
        void Write(int index,string title)=>File.WriteAllText(Cache(index),JsonSerializer.Serialize(new{
            CacheVersion=1,PreviewFetchVersion=3,Url=Url(index),SavedAtUtc=DateTime.UtcNow,Title=title,
            Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,Codecs="",DynamicRange="",
            ThumbnailBase64=image,ThumbnailsBase64=new[]{image}
        }));
        using var service=new PreviewService("must-not-run",storage,()=>"",_=>throw new Exception("Cache reads must not start metadata"),_=>{},thumbnailValidator:_=>true);
        for(int index=0;index<14;index++)
        {
            Write(index,"Memory "+index);
            Require(service.Get(Url(index))?.Title=="Memory "+index,"A current gallery must load into the bounded memory cache");
        }
        Write(0,"Reloaded oldest");Write(13,"Replaced newest disk record");
        Require(service.Get(Url(0))?.Title=="Reloaded oldest",
            "After the gallery memory budget is exceeded, an old entry must be evicted and reload its current disk record");
        Require(service.Get(Url(13))?.Title=="Memory 13",
            "Memory eviction must retain the recently used gallery instead of clearing every cached image");
    }

    private static byte[] Image(int index)
    {
        var bitmap=BitmapSource.Create(2,2,96,96,PixelFormats.Bgr24,null,
            Enumerable.Repeat(new byte[]{(byte)(index*7),(byte)(index*11),(byte)(index*3)},4).SelectMany(value=>value).ToArray(),6);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream=new MemoryStream();encoder.Save(stream);return stream.ToArray();
    }
    private static void Require(bool condition,string error)
    {
        if(!condition)throw new InvalidOperationException("Preview gallery test failed: "+error);
    }
}
