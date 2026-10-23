using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDownloader.Native;

internal static class DependencyInstallTests
{
    internal static async Task RunAsync()
    {
        string root=Path.Combine(Path.GetTempPath(),"vd-dependency-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var before=Directory.GetDirectories(Path.GetTempPath(),"vd-ffmpeg-*").ToHashSet();
        await using var server=new PackageServer();
        try
        {
            foreach(string endpoint in new[]{"headers","body"})
            {
                var watch=Stopwatch.StartNew();
                bool timedOut=false;
                try
                {
                    await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+endpoint),root,
                        TimeSpan.FromSeconds(1),CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(8));
                }
                catch(TimeoutException error) {timedOut=error.Message.Contains("FFmpeg",StringComparison.Ordinal);}
                Require(timedOut && watch.Elapsed<TimeSpan.FromSeconds(5),"The package deadline must cover "+endpoint);
                if(endpoint=="body")Require(server.BodyHeadersSent,"The body test must receive headers before it times out");
                Require(!File.Exists(Path.Combine(root,"ffmpeg.exe")),"Timeout must not install a partial package");
            }
            using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
            {
                bool cancelled=false;
                try {await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"body"),root,TimeSpan.FromSeconds(5),cancel.Token);}
                catch(OperationCanceledException) when(cancel.IsCancellationRequested) {cancelled=true;}
                Require(cancelled,"Explicit cancellation must remain distinct from a download timeout");
            }
            await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"ok"),root,TimeSpan.FromSeconds(5),CancellationToken.None);
            Require(File.ReadAllText(Path.Combine(root,"ffmpeg.exe"))=="ffmpeg fixture" &&
                File.ReadAllText(Path.Combine(root,"ffprobe.exe"))=="ffprobe fixture","A retry must install both tools after a timeout");
            Require(!Directory.GetDirectories(Path.GetTempPath(),"vd-ffmpeg-*").Except(before).Any(),
                "Successful, cancelled and timed-out installs must remove their scratch folders");
            Console.WriteLine("PASS: dependency package header/body deadlines, cancellation, retry and cleanup");
        }
        finally {Directory.Delete(root,true);}
    }

    private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}

    private sealed class PackageServer : IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly List<Task> _requests=new();
        private readonly Task _accept;
        private readonly byte[] _zip;
        public string BaseUrl {get;}
        public bool BodyHeadersSent {get;private set;}
        public PackageServer()
        {
            using var buffer=new MemoryStream();
            using(var zip=new ZipArchive(buffer,ZipArchiveMode.Create,true))
                foreach(string tool in new[]{"ffmpeg","ffprobe"})
                {
                    using var writer=new StreamWriter(zip.CreateEntry("fixture/bin/"+tool+".exe").Open());
                    writer.Write(tool+" fixture");
                }
            _zip=buffer.ToArray();
            _listener.Start();BaseUrl="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/";
            _accept=AcceptAsync();
        }
        private async Task AcceptAsync()
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _requests.Add(ServeAsync(client));
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
        }
        private async Task ServeAsync(TcpClient client)
        {
            using(client)
            try
            {
                using var stream=client.GetStream();
                using var reader=new StreamReader(stream,Encoding.ASCII,leaveOpen:true);
                string request=await reader.ReadLineAsync(_stop.Token)??"";
                while(await reader.ReadLineAsync(_stop.Token) is {Length:>0}){}
                if(request.Contains("/headers ",StringComparison.Ordinal))
                {await Task.Delay(Timeout.InfiniteTimeSpan,_stop.Token);return;}
                bool body=request.Contains("/body ",StringComparison.Ordinal);
                byte[] header=Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {_zip.Length}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header,_stop.Token);
                if(body) {BodyHeadersSent=true;await Task.Delay(Timeout.InfiniteTimeSpan,_stop.Token);}
                else await stream.WriteAsync(_zip,_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException){} // a client can cancel before the headers arrive
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();
            await _accept;
            await Task.WhenAll(_requests);
            _stop.Dispose();
        }
    }
}
