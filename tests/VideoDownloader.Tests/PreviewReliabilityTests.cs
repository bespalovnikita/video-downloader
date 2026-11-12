using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class PreviewReliabilityTests
{
    // A real 2x2 RGB PNG. The production thumbnail decoder validates it; these
    // tests exercise HTTP scheduling without replacing raster validation.
    private static readonly byte[] Image=Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAAEElEQVR4nGPQrH8JRAwQCgAo6gZF37A0BAAAAABJRU5ErkJggg==");

    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-preview-reliability-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        PreviewService? owner=null;
        try
        {
            await using var server=new ImageServer(Image);
            var storage=new AppPersistence(Path.Combine(root,"profile"));
            string calls=Path.Combine(root,"metadata.calls");
            // Reuse the existing executable metadata fixture, with its actual
            // child process and production PreviewService/HTTP/cache paths.
            string Url(string name)=>"https://preview-gallery.invalid/reliability-"+name;
            string Control(string name,params string[] paths)
            {
                string path=Path.Combine(root,name+".json");
                File.WriteAllText(path,JsonSerializer.Serialize(new
                {
                    Calls=calls,Metadata=new
                    {
                        title="Reliable "+name,uploader="Loopback image fixture",
                        thumbnails=paths.Select((item,index)=>new{url=server.Address+item,width=1000-index,height=1000-index}).ToArray()
                    }
                }));
                return path;
            }
            string executable=Path.Combine(AppContext.BaseDirectory,typeof(PreviewReliabilityTests).Assembly.GetName().Name+".exe");
            string control="";
            var completed=new ConcurrentDictionary<string,TaskCompletionSource<PreviewData>>(StringComparer.Ordinal);
            var delivered=new ConcurrentQueue<string>();
            var logs=new ConcurrentQueue<string>();
            Task<PreviewData> Result(string url)=>completed.GetOrAdd(url,_=>new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            using var service=new PreviewService(executable,storage,()=>control,value=>
            {
                delivered.Enqueue(value.Url);
                completed.GetOrAdd(value.Url,_=>new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult(value);
            },logs.Enqueue,requestTimeout:TimeSpan.FromSeconds(8),thumbnailTimeout:TimeSpan.FromSeconds(3));
            owner=service;

            foreach(string phase in new[]{"headers","body"})
            {
                string url=Url(phase);
                string stalled="/"+phase+"-stall.png",fallback="/"+phase+"-valid.png";
                control=Control(phase,stalled,fallback);
                service.Request(url);
                var result=await Result(url).WaitAsync(TimeSpan.FromSeconds(12));
                Require(result.Title=="Reliable "+phase && result.ThumbnailsBase64.Length==1 &&
                        Convert.FromBase64String(result.ThumbnailBase64).SequenceEqual(Image),
                    "A stalled first candidate must not hide the actual valid PNG supplied later for the same video ("+phase+").");
                Require(server.Requests.Count(path=>path==stalled)==1 && server.Requests.Count(path=>path==fallback)==1 &&
                        logs.Any(line=>line.Contains("video="+url) && line.Contains("image="+server.Address+stalled) &&
                            line.Contains("stage="+phase) && line.Contains("candidate=1/2") && line.Contains("candidate_timeout_ms=1000")),
                    "A candidate timeout must be bounded and diagnosed, then advance once without retrying that URL.");

                int before=server.Requests.Count;
                string barrier=Url(phase+"-barrier");
                control=Control(phase+"-barrier","/"+phase+"-barrier.png");
                service.Request(url,true);service.Request(barrier);
                await Result(barrier).WaitAsync(TimeSpan.FromSeconds(12));
                Require(File.ReadAllLines(calls).Count(line=>line==url)==1 && server.Requests.Count==before+1 &&
                        service.Get(url)?.ThumbnailBase64==result.ThumbnailBase64,
                    "The recovered cover must be a normal cache hit: no extra metadata child or HTTP request before the worker barrier.");
            }

            string slowLast=Url("slow-last");
            control=Control("slow-last","/quick-denied.png","/slow-valid.png");
            service.Request(slowLast);
            var slowResult=await Result(slowLast).WaitAsync(TimeSpan.FromSeconds(12));
            Require(slowResult.Title=="Reliable slow-last" &&
                    Convert.FromBase64String(slowResult.ThumbnailBase64).SequenceEqual(Image) &&
                    server.Requests.Count(path=>path=="/quick-denied.png")==1 && server.Requests.Count(path=>path=="/slow-valid.png")==1,
                "After a quick HTTP failure the final candidate must keep the remaining gallery budget, even when it exceeds the earlier candidate deadline.");

            string cancelled=Url("cancelled");
            control=Control("cancelled","/cancel-body-stall.png","/cancel-valid.png");
            service.Request(cancelled);
            await server.Started("/cancel-body-stall.png").WaitAsync(TimeSpan.FromSeconds(8));
            service.Dispose();
            await service.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Require(!server.Requests.Contains("/cancel-valid.png") && !delivered.Contains(cancelled),
                "Disposing the owner must join the worker and cancel HTTP, without starting the next candidate or publishing a late result.");
            Require(!File.Exists(storage.HistoryPath),"Cover recovery must not record a successful media download.");
            Console.WriteLine("PASS: preview candidate deadlines; real same-video PNG after stalled headers/body, remaining budget for a slow final candidate, cache hits without network, and joined cancellation without fallback");
        }
        finally
        {
            if(owner is not null){owner.Dispose();await owner.Completion.WaitAsync(TimeSpan.FromSeconds(5));}
            Directory.Delete(root,true);
        }
    }

    private static void Require(bool value,string message)
    {if(!value)throw new InvalidOperationException("Preview reliability test: "+message);}

    private sealed class ImageServer:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly ConcurrentBag<Task> _responses=[];
        private readonly ConcurrentDictionary<string,TaskCompletionSource> _started=new(StringComparer.Ordinal);
        private readonly Task _accept;
        private readonly byte[] _image;
        internal readonly ConcurrentQueue<string> Requests=new();
        internal string Address {get;}

        internal ImageServer(byte[] image)
        {
            _image=image;_listener.Start();
            Address="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port;
            _accept=AcceptAsync();
        }
        internal Task Started(string path)=>_started.GetOrAdd(path,_=>new(TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        private async Task AcceptAsync()
        {
            try
            {
                while(true)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _responses.Add(RespondAsync(client));
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
        }
        private async Task RespondAsync(TcpClient client)
        {
            using(client)
            try
            {
                var stream=client.GetStream();var request=new StringBuilder();byte[] buffer=new byte[1024];
                while(!request.ToString().Contains("\r\n\r\n",StringComparison.Ordinal))
                {
                    int count=await stream.ReadAsync(buffer,_stop.Token);
                    if(count==0)return;
                    request.Append(Encoding.ASCII.GetString(buffer,0,count));
                    if(request.Length>8192)throw new InvalidDataException("Fixture request exceeds its header limit.");
                }
                string path=request.ToString().Split('\r')[0].Split(' ')[1];
                Requests.Enqueue(path);
                _started.GetOrAdd(path,_=>new(TaskCreationOptions.RunContinuationsAsynchronously)).TrySetResult();
                if(path=="/quick-denied.png")
                {
                    await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"),_stop.Token);
                    return;
                }
                if(path=="/headers-stall.png")await Task.Delay(Timeout.Infinite,_stop.Token);
                if(path=="/slow-valid.png")await Task.Delay(1800,_stop.Token);
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: image/png\r\nContent-Length: "+
                    _image.Length+"\r\nConnection: close\r\n\r\n"),_stop.Token);
                if(path.EndsWith("body-stall.png",StringComparison.Ordinal))await Task.Delay(Timeout.Infinite,_stop.Token);
                await stream.WriteAsync(_image,_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException){} // Client cancellation closes its owned HTTP request.
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();
            await _accept;await Task.WhenAll(_responses);_stop.Dispose();
        }
    }
}
