using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

public sealed record AppUpdateRelease(string Version,Uri ArchiveUri,Uri ChecksumsUri,long ArchiveSize=0);
public interface IAppUpdateService:IDisposable
{
    Task<AppUpdateRelease?> CheckAsync(Version currentVersion,CancellationToken cancel);
    Task<PreparedAppUpdate> PrepareAsync(AppUpdateRelease release,string currentDirectory,CancellationToken cancel);
}

public sealed class AppUpdateClient:IAppUpdateService
{
    internal const string Repository="bespalovnikita/video-downloader";
    internal const string ArchiveName="VideoDownloader-Windows-x64.zip";
    internal const long MaxArchiveBytes=512L*1024*1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _checkDeadline,_downloadDeadline;
    public AppUpdateClient(HttpMessageHandler? handler=null,TimeSpan? checkDeadline=null,TimeSpan? downloadDeadline=null)
    {
        _http=new HttpClient(handler??new HttpClientHandler{AllowAutoRedirect=false}){Timeout=Timeout.InfiniteTimeSpan};
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("VideoDownloader-AppUpdater/1.0");
        _checkDeadline=checkDeadline??TimeSpan.FromSeconds(20);
        _downloadDeadline=downloadDeadline??TimeSpan.FromMinutes(6);
    }
    public async Task<AppUpdateRelease?> CheckAsync(Version currentVersion,CancellationToken cancel)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(_checkDeadline);
        byte[] json=await ReadBytesAsync(new Uri("https://api.github.com/repos/"+Repository+"/releases/latest"),1024*1024,deadline.Token).ConfigureAwait(false);
        using var document=JsonDocument.Parse(json,new JsonDocumentOptions{MaxDepth=32});
        var release=document.RootElement;
        string tag=release.GetProperty("tag_name").GetString()??"";
        if(release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() || !tag.StartsWith('v'))
            throw new InvalidDataException("GitHub did not return a stable release.");
        string version=tag[1..];var parsed=AppUpdatePackage.ParseVersion(version);
        if(parsed<=new Version(currentVersion.Major,currentVersion.Minor,Math.Max(0,currentVersion.Build)))return null;
        Uri? archive=null,checksums=null;long size=0;
        foreach(var asset in release.GetProperty("assets").EnumerateArray())
        {
            string name=asset.GetProperty("name").GetString()??"";
            if(name!=ArchiveName && name!="SHA256SUMS.txt")continue;
            var uri=new Uri(asset.GetProperty("browser_download_url").GetString()??"");
            ValidateAssetUri(uri,version,name);
            if(name==ArchiveName)
            {
                if(archive!=null)throw new InvalidDataException("Duplicate Windows release archive.");
                archive=uri;size=asset.GetProperty("size").GetInt64();
                if(size<=0 || size>MaxArchiveBytes)throw new InvalidDataException("Windows release archive is too large.");
            }
            else {if(checksums!=null)throw new InvalidDataException("Duplicate release checksums.");checksums=uri;}
        }
        return archive!=null && checksums!=null?new(version,archive,checksums,size):null;
    }
    internal static void ValidateAssetUri(Uri uri,string version,string name)
    {
        if(uri.AbsoluteUri!=$"https://github.com/{Repository}/releases/download/v{version}/{name}")
            throw new InvalidDataException("Release asset belongs to an unexpected repository or version.");
    }
    public async Task<PreparedAppUpdate> PrepareAsync(AppUpdateRelease release,string currentDirectory,CancellationToken cancel)
    {
        AppUpdatePackage.ParseVersion(release.Version);
        ValidateAssetUri(release.ArchiveUri,release.Version,ArchiveName);ValidateAssetUri(release.ChecksumsUri,release.Version,"SHA256SUMS.txt");
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(cancel);deadline.CancelAfter(_downloadDeadline);
        string root=Path.Combine(Path.GetTempPath(),"VideoDownloaderUpdates");AppUpdatePackage.AssertNoReparse(root);Directory.CreateDirectory(root);
        string download=Path.Combine(root,"download-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(download);
        try
        {
            string checksums=Encoding.UTF8.GetString(await ReadBytesAsync(release.ChecksumsUri,16*1024,deadline.Token).ConfigureAwait(false)).TrimStart('\uFEFF');
            var matches=Regex.Matches(checksums,@"(?m)^([0-9a-fA-F]{64}) [ *]VideoDownloader-Windows-x64\.zip\r?$");
            if(matches.Count!=1)throw new InvalidDataException("The Windows archive checksum is missing or ambiguous.");
            string archive=Path.Combine(download,"package.zip");
            await DownloadAsync(release.ArchiveUri,archive,MaxArchiveBytes,deadline.Token).ConfigureAwait(false);
            if(release.ArchiveSize>0 && new FileInfo(archive).Length!=release.ArchiveSize)throw new InvalidDataException("Incomplete Windows archive.");
            using(var input=File.OpenRead(archive))
                if(!Convert.ToHexString(await SHA256.HashDataAsync(input,deadline.Token).ConfigureAwait(false)).Equals(matches[0].Groups[1].Value,StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The downloaded application checksum does not match.");
            return await Task.Run(()=>AppUpdateInstaller.Prepare(archive,currentDirectory,root,release.Version,deadline.Token),deadline.Token).ConfigureAwait(false);
        }
        finally {if(Directory.Exists(download))Directory.Delete(download,true);}
    }
    private async Task<byte[]> ReadBytesAsync(Uri uri,long max,CancellationToken cancel)
    {
        using var output=new MemoryStream();await TransferAsync(uri,output,max,cancel).ConfigureAwait(false);return output.ToArray();
    }
    private async Task DownloadAsync(Uri uri,string file,long max,CancellationToken cancel)
    {
        await using var output=new FileStream(file,FileMode.CreateNew,FileAccess.Write,FileShare.None,81920,true);
        await TransferAsync(uri,output,max,cancel).ConfigureAwait(false);
    }
    private async Task TransferAsync(Uri uri,Stream output,long max,CancellationToken cancel)
    {
        for(int redirect=0;;redirect++)
        {
            using var response=await _http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,cancel).ConfigureAwait(false);
            if((int)response.StatusCode is >=300 and <=399)
            {
                if(redirect>=5 || response.Headers.Location is null)throw new HttpRequestException("Invalid release redirect.");
                uri=new Uri(uri,response.Headers.Location);
                if(uri.Scheme!="https" || !uri.IsDefaultPort || uri.UserInfo.Length!=0 ||
                    uri.Host is not ("release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com"))
                    throw new HttpRequestException("Release download redirected outside GitHub's asset hosts.");
                continue;
            }
            response.EnsureSuccessStatusCode();
            if(response.Content.Headers.ContentLength is long length && length>max)throw new InvalidDataException("Release response exceeds its size limit.");
            await using var input=await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
            byte[] buffer=new byte[81920];long count=0;int read;
            while((read=await input.ReadAsync(buffer,cancel).ConfigureAwait(false))!=0)
            {
                count+=read;if(count>max)throw new InvalidDataException("Release response exceeds its size limit.");
                await output.WriteAsync(buffer.AsMemory(0,read),cancel).ConfigureAwait(false);
            }
            return;
        }
    }
    public void Dispose()=>_http.Dispose();
}
