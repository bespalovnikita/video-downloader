using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class AppUpdateClientTests
{
    internal static async Task RunAsync()
    {
        const string version="99.0.0";
        string prefix="https://github.com/bespalovnikita/video-downloader/releases/download/v"+version+"/";
        var release=new AppUpdateRelease(version,new Uri(prefix+"VideoDownloader-Windows-x64.zip"),new Uri(prefix+"SHA256SUMS.txt"));
        string root=Path.Combine(Path.GetTempPath(),"vd-app-update-client-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string current=Path.Combine(root,"installed");Directory.CreateDirectory(current);
            byte[] oldZip=MakePackage(current,"2.0.0","old");
            string next=Path.Combine(root,"next");Directory.CreateDirectory(next);
            byte[] archive=MakePackage(next,version,"new");
            string checksum=Convert.ToHexString(SHA256.HashData(archive)).ToLowerInvariant()+"  VideoDownloader-Windows-x64.zip\n";
            string metadata=JsonSerializer.Serialize(new{tag_name="v"+version,draft=false,prerelease=false,assets=new[]{
                new{name="VideoDownloader-Windows-x64.zip",browser_download_url=release.ArchiveUri.AbsoluteUri,size=archive.Length},
                new{name="SHA256SUMS.txt",browser_download_url=release.ChecksumsUri.AbsoluteUri,size=checksum.Length}}});
            using(var service=new AppUpdateClient(new Handler(request=>
            {
                Require(request.Headers.UserAgent.Count>0,"GitHub requests require an application User-Agent");
                return Reply(request.RequestUri!.Host=="api.github.com"?Encoding.UTF8.GetBytes(metadata):
                    request.RequestUri==release.ChecksumsUri?Encoding.UTF8.GetBytes(checksum):archive);
            })))
            {
                var found=await service.CheckAsync(new Version(2,0,0),CancellationToken.None);
                Require(found?.Version==version,"A newer stable Windows release must be discovered");
                Require(await service.CheckAsync(new Version(99,0,0),CancellationToken.None)==null,"The installed version must not be offered again");
                using var prepared=await service.PrepareAsync(found!,current,CancellationToken.None);
                Require(File.ReadAllText(Path.Combine(current,"VideoDownloader.exe"))=="old","Preparation must not replace running application files");
                Require(File.ReadAllText(Path.Combine(prepared.StageDirectory,"VideoDownloader.exe"))=="new","A verified archive must produce a complete stage");
            }
            using(var service=new AppUpdateClient(new Handler(request=>Reply(request.RequestUri==release.ChecksumsUri?
                Encoding.UTF8.GetBytes(new string('0',64)+"  VideoDownloader-Windows-x64.zip\n"):archive))))
                await RefusesAsync(()=>service.PrepareAsync(release,current,CancellationToken.None),"A mismatching archive hash must fail before extraction");
            Require(File.ReadAllText(Path.Combine(current,"VideoDownloader.exe"))=="old","A rejected update must retain the installed package");
            foreach(string badMetadata in new[]{metadata.Replace("\"draft\":false","\"draft\":true"),
                metadata.Replace("\"prerelease\":false","\"prerelease\":true"),
                metadata.Replace("bespalovnikita/video-downloader/releases/download","attacker/other/releases/download")})
            {
                using var service=new AppUpdateClient(new Handler(_=>Reply(Encoding.UTF8.GetBytes(badMetadata))));
                await RefusesAsync(()=>service.CheckAsync(new Version(2,0,0),CancellationToken.None),"Only fixed-repository stable assets may be accepted");
            }
            using(var service=new AppUpdateClient(new Handler(_=>new HttpResponseMessage(HttpStatusCode.Redirect)
                {Headers={Location=new Uri("https://example.com/package.zip")}})))
                await RefusesAsync(()=>service.CheckAsync(new Version(2,0,0),CancellationToken.None),"An external redirect must be rejected");
            using(var service=new AppUpdateClient(new Handler(_=>
            {
                var response=Reply("{}"u8.ToArray());response.Content.Headers.ContentLength=2*1024*1024;return response;
            })))
                await RefusesAsync(()=>service.CheckAsync(new Version(2,0,0),CancellationToken.None),"Oversized API responses must be rejected before reading");
            foreach(bool stallHeaders in new[]{true,false})
            {
                using var service=new AppUpdateClient(new StallHandler(stallHeaders),TimeSpan.FromMilliseconds(150));
                var watch=Stopwatch.StartNew();
                await RefusesAsync(()=>service.CheckAsync(new Version(2,0,0),CancellationToken.None),"The check deadline must cover headers and body");
                Require(watch.Elapsed<TimeSpan.FromSeconds(3),"The update timeout must remain bounded");
            }
            using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(100)))
            using(var service=new AppUpdateClient(new StallHandler(false),TimeSpan.FromSeconds(5)))
            {
                bool cancelled=false;
                try {await service.CheckAsync(new Version(2,0,0),cancel.Token);}
                catch(OperationCanceledException) when(cancel.IsCancellationRequested){cancelled=true;}
                Require(cancelled,"Closing the owner must cancel an in-flight response body");
            }
            using(var self=Process.GetCurrentProcess())
            {
                long started=self.StartTime.ToUniversalTime().Ticks;
                await AppUpdateBootstrap.WaitForOwnerAsync(self.Id,started-1,TimeSpan.FromSeconds(1),CancellationToken.None);
                bool timeout=false;
                try {await AppUpdateBootstrap.WaitForOwnerAsync(self.Id,started,TimeSpan.FromMilliseconds(100),CancellationToken.None);}
                catch(TimeoutException){timeout=true;}
                Require(timeout && !self.HasExited,"An updater must wait for its actual owner and never terminate it");
            }
            Console.WriteLine("PASS: application update stable source, verified download, header/body bounds, cancellation and owner lifetime");
        }
        finally {Directory.Delete(root,true);}
    }
    private static byte[] MakePackage(string directory,string version,string content)
    {
        Directory.CreateDirectory(Path.Combine(directory,"ui"));
        foreach(string name in new[]{"VideoDownloader.exe","yt-dlp.exe","ui/MainWindow.xaml"})File.WriteAllText(Path.Combine(directory,name),content);
        var files=new[]{"VideoDownloader.exe","yt-dlp.exe","ui/MainWindow.xaml"}.Select(name=>new AppPackageFile(name,
            new FileInfo(Path.Combine(directory,name)).Length,AppUpdatePackage.HashFile(Path.Combine(directory,name)))).ToList();
        File.WriteAllText(Path.Combine(directory,AppUpdatePackage.ManifestName),JsonSerializer.Serialize(new AppPackageManifest(1,version,files)));
        using var output=new MemoryStream();
        using(var zip=new ZipArchive(output,ZipArchiveMode.Create,true))
            foreach(string file in Directory.EnumerateFiles(directory,"*",SearchOption.AllDirectories))
            {
                using var entry=zip.CreateEntry(Path.GetRelativePath(directory,file).Replace('\\','/')).Open();
                using var input=File.OpenRead(file);input.CopyTo(entry);
            }
        return output.ToArray();
    }
    private static HttpResponseMessage Reply(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
    private static async Task RefusesAsync(Func<Task> action,string message)
    {
        bool refused=false;try{await action().WaitAsync(TimeSpan.FromSeconds(5));}catch(Exception error) when(error is not TimeoutException){refused=true;}
        Require(refused,message);
    }
    private sealed class Handler(Func<HttpRequestMessage,HttpResponseMessage> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)=>Task.FromResult(respond(request));
    }
    private sealed class StallHandler(bool headers):HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)
        {
            if(headers)await Task.Delay(Timeout.InfiniteTimeSpan,cancel);
            return new(HttpStatusCode.OK){Content=new StreamContent(new StallStream())};
        }
    }
    private sealed class StallStream:Stream
    {
        public override bool CanRead=>true;public override bool CanSeek=>false;public override bool CanWrite=>false;
        public override long Length=>throw new NotSupportedException();public override long Position{get=>0;set=>throw new NotSupportedException();}
        public override int Read(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer,CancellationToken cancel=default){await Task.Delay(Timeout.InfiniteTimeSpan,cancel);return 0;}
        public override void Flush()=>throw new NotSupportedException();public override long Seek(long offset,SeekOrigin origin)=>throw new NotSupportedException();
        public override void SetLength(long value)=>throw new NotSupportedException();public override void Write(byte[] buffer,int offset,int count)=>throw new NotSupportedException();
    }
}
