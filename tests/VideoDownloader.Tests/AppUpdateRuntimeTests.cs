using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static partial class AppUpdateInstallerTests
{
    private sealed record RuntimeRelease(string Version,string Sha256);

    private static async Task RuntimePolicyAsync()
    {
        // Setup uses pinned public fixtures, separately from the assertions and
        // controlled production HTTP transport below. No latest-release lookup.
        var releases=JsonSerializer.Deserialize<RuntimeRelease[]>(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory,
            "fixtures","component-updates","yt-dlp-releases.json")))!;
        Require(releases.Length==2,"Two pinned official runtime fixtures are required.");
        string cache=Environment.GetEnvironmentVariable("VD_TEST_YTDLP_FIXTURES")??Path.Combine(Path.GetTempPath(),"VideoDownloaderTestRuntimes");
        foreach(var release in releases)await PrepareOfficialRuntimeAsync(cache,release);
        string oldRuntime=Path.Combine(cache,releases[0].Version,"yt-dlp.exe"),newRuntime=Path.Combine(cache,releases[1].Version,"yt-dlp.exe");
        if(OperatingSystem.IsWindows())Require(YtDlpUpdatePolicy.ReleaseVersion(oldRuntime)==releases[0].Version &&
            YtDlpUpdatePolicy.ReleaseVersion(newRuntime)==releases[1].Version,"Real PE resource versions must match the pinned releases.");
        foreach(var release in releases)
            if(OperatingSystem.IsWindows())await AssertOfficialExecutableVersionAsync(Path.Combine(cache,release.Version,"yt-dlp.exe"),release.Version);
        byte[] officialChecksums=await File.ReadAllBytesAsync(Path.Combine(cache,releases[1].Version,"SHA2-256SUMS"));

        foreach(bool rollback in new[]{false,true})
        {
            using var fixture=new Fixture(oldRuntime,oldRuntime);
            File.Copy(newRuntime,Path.Combine(fixture.Current,"yt-dlp.exe"),true);
            var before=Snapshot(fixture.Current);
            using var prepared=await PrepareRetainedRuntimeAsync(fixture,officialChecksums);
            RequireSame(before,Snapshot(fixture.Current),"Runtime provenance preparation must not modify the running package.");
            var originalStaged=AppUpdatePackage.ReadManifest(Path.Combine(prepared.StageDirectory,AppUpdatePackage.ManifestName));
            AppUpdatePackage.ValidateDirectory(prepared.StageDirectory,originalStaged);
            Require(AppUpdatePackage.HashFile(Path.Combine(prepared.StageDirectory,"yt-dlp.exe"))==releases[0].Sha256,
                "Original verified bundle bytes and manifest must remain unchanged.");
            using var reopened=AppUpdateInstaller.OpenPrepared(prepared.Directory);
            if(rollback)
            {
                Failure(()=>AppUpdateInstaller.Apply(reopened,count=>{if(count==2)throw new IOException("Fixture runtime rollback");}));
                RequireSame(before,Snapshot(fixture.Current),"Rollback must recover actual independent runtime bytes and the old ownership manifest.");
            }
            else
            {
                AppUpdateInstaller.Apply(reopened);
                var installed=AppUpdatePackage.ReadManifest(Path.Combine(fixture.Current,AppUpdatePackage.ManifestName));
                AppUpdatePackage.ValidateDirectory(fixture.Current,installed);
                Require(installed.Version=="2.0.2" && installed.Files.Single(file=>file.Path=="yt-dlp.exe").Sha256==releases[1].Sha256 &&
                    AppUpdatePackage.HashFile(Path.Combine(fixture.Current,"yt-dlp.exe"))==releases[1].Sha256,
                    "New app/effective manifest must retain the exact verified newer official runtime.");
                using(var zip=ZipFile.Open(fixture.Archive,ZipArchiveMode.Update))
                {
                    var entry=zip.GetEntry(AppUpdatePackage.ManifestName)!;
                    AppPackageManifest next;
                    using(var input=entry.Open())next=JsonSerializer.Deserialize<AppPackageManifest>(input,AppUpdatePackage.Json)!;
                    entry.Delete();
                    WriteEntry(zip,AppUpdatePackage.ManifestName,JsonSerializer.Serialize(next with {Version="2.0.3"},AppUpdatePackage.Json));
                }
                using var followup=AppUpdateInstaller.Prepare(fixture.Archive,fixture.Current,fixture.Scratch,"2.0.3",CancellationToken.None);
                string runtime=Path.Combine(fixture.Current,"yt-dlp.exe");
                AppUpdateInstaller.RetainVerifiedYtDlp(followup,new AppPackageFile("yt-dlp.exe",new FileInfo(runtime).Length,releases[1].Sha256));
                AppUpdateInstaller.Apply(followup);
                AppUpdatePackage.ValidateDirectory(fixture.Current,AppUpdatePackage.ReadManifest(Path.Combine(fixture.Current,AppUpdatePackage.ManifestName)));
                Require(AppUpdatePackage.HashFile(runtime)==releases[1].Sha256,"A subsequent application update must accept an effective installed manifest.");
            }
        }

        foreach(string kind in new[]{"hash","duplicate","oversized","network","redirect","timeout","cancel","tampered","revision","same-date-revision"})
        {
            if(!OperatingSystem.IsWindows())continue; // FileVersionInfo reads native PE resources on Windows.
            using var fixture=new Fixture(oldRuntime,oldRuntime);
            string installed=Path.Combine(fixture.Current,"yt-dlp.exe");
            File.Copy(kind=="same-date-revision"?oldRuntime:newRuntime,installed,true);
            if(kind=="tampered")File.AppendAllText(installed,"opaque canary; never a trusted newer runtime");
            if(kind is "revision" or "same-date-revision")ChangePeRevision(installed);
            var before=Snapshot(fixture.Current);
            using var cancel=new CancellationTokenSource();
            if(kind=="cancel")cancel.CancelAfter(TimeSpan.FromMilliseconds(200));
            using var client=RuntimeClient(fixture,officialChecksums,kind);
            Exception? failure=null;
            try {using var prepared=await client.PrepareAsync(RuntimeAppRelease,fixture.Current,cancel.Token);}
            catch(Exception error) when(error is IOException or HttpRequestException or OperationCanceledException or TimeoutException){failure=error;}
            Require(failure is not null,"Unverified apparently newer runtime must refuse app preparation: "+kind);
            if(kind is not ("cancel" or "timeout"))Require(failure!.Message.Contains("yt-dlp",StringComparison.Ordinal),"Refusal must explain runtime verification: "+kind);
            RequireSame(before,Snapshot(fixture.Current),"Provenance failure must keep all installed bytes: "+kind);
            Require(AppUpdateInstaller.FindPending(fixture.Current) is null,"Rejected preparation must not leave a recovery pointer.");
        }

        // A change after a verified preparation requires a new preparation;
        // the helper may not overwrite or blindly trust the changed runtime.
        foreach(string kind in new[]{"live","staged","retained","manifest"})
        {
            using var fixture=new Fixture(oldRuntime,oldRuntime);
            File.Copy(newRuntime,Path.Combine(fixture.Current,"yt-dlp.exe"),true);
            using var prepared=await PrepareRetainedRuntimeAsync(fixture,officialChecksums);
            string path=kind switch
            {
                "live"=>Path.Combine(fixture.Current,"yt-dlp.exe"),
                "staged"=>Path.Combine(prepared.StageDirectory,"yt-dlp.exe"),
                "retained"=>Path.Combine(prepared.Directory,"retained-yt-dlp.exe"),
                _=>Path.Combine(prepared.Directory,"next-package.json")
            };
            File.AppendAllText(path,"late opaque canary");
            var before=Snapshot(fixture.Current);
            Failure(()=>AppUpdateInstaller.Apply(prepared));
            RequireSame(before,Snapshot(fixture.Current),"Changed runtime/source/manifest after preparation must refuse before mutation: "+kind);
        }
        using(var fixture=new Fixture(oldRuntime,newRuntime))
        using(var client=RuntimeClient(fixture,officialChecksums,"no-runtime-request"))
        using(var prepared=await client.PrepareAsync(RuntimeAppRelease,fixture.Current,CancellationToken.None))
        {
            AppUpdateInstaller.Apply(prepared);
            Require(AppUpdatePackage.HashFile(Path.Combine(fixture.Current,"yt-dlp.exe"))==releases[1].Sha256,
                "A newer bundled runtime must install normally without an extra provenance request.");
        }
        await RetainedRuntimeCrashRecoveryAsync(oldRuntime,newRuntime,officialChecksums);
        Console.WriteLine(OperatingSystem.IsWindows()?
            "PASS: pinned official Windows yt-dlp versions and --version, fixed-release provenance, effective manifest, runtime revalidation, rollback and killed-helper recovery":
            "PASS: official-hash runtime pin/effective manifest, revalidation, rollback and killed-helper recovery; PE version/provenance transport and executable assertions require Windows");
    }

    private static AppUpdateRelease RuntimeAppRelease=>new("2.0.2",
        new Uri("https://github.com/bespalovnikita/video-downloader/releases/download/v2.0.2/VideoDownloader-Windows-x64.zip"),
        new Uri("https://github.com/bespalovnikita/video-downloader/releases/download/v2.0.2/SHA256SUMS.txt"));

    private static async Task<PreparedAppUpdate> PrepareRetainedRuntimeAsync(Fixture fixture,byte[] checksums)
    {
        if(OperatingSystem.IsWindows())
        {
            using var client=RuntimeClient(fixture,checksums);
            return await client.PrepareAsync(RuntimeAppRelease,fixture.Current,CancellationToken.None);
        }
        // Portable linked-source runs cover transaction mechanics with known
        // official hashes. They do not claim to execute the Windows PE policy.
        var prepared=fixture.Prepare();
        string runtime=Path.Combine(fixture.Current,"yt-dlp.exe");
        AppUpdateInstaller.RetainVerifiedYtDlp(prepared,new AppPackageFile("yt-dlp.exe",new FileInfo(runtime).Length,AppUpdatePackage.HashFile(runtime)));
        return prepared;
    }

    private static AppUpdateClient RuntimeClient(Fixture fixture,byte[] officialChecksums,string kind="ok")
    {
        byte[] zip=File.ReadAllBytes(fixture.Archive);
        string checksum=Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zip))+"  VideoDownloader-Windows-x64.zip\n";
        return new AppUpdateClient(new RuntimeHandler(async(request,cancel)=>
        {
            if(request.RequestUri==RuntimeAppRelease.ChecksumsUri)return RuntimeReply(Encoding.UTF8.GetBytes(checksum));
            if(request.RequestUri==RuntimeAppRelease.ArchiveUri)return RuntimeReply(zip);
            Require(kind!="no-runtime-request","Known older runtime must not request a release checksum.");
            Require(request.RequestUri!.AbsoluteUri=="https://github.com/yt-dlp/yt-dlp/releases/download/2025.08.22/SHA2-256SUMS",
                "Runtime provenance must request only its exact official release checksum.");
            if(kind=="network")throw new HttpRequestException("Fixture network refusal");
            if(kind=="redirect")return new HttpResponseMessage(HttpStatusCode.Redirect){Headers={Location=new Uri("https://example.invalid/checksum")}};
            if(kind is "timeout" or "cancel")await Task.Delay(Timeout.InfiniteTimeSpan,cancel);
            if(kind=="hash")return RuntimeReply(Encoding.UTF8.GetBytes(new string('0',64)+"  yt-dlp.exe\n"));
            if(kind=="duplicate")return RuntimeReply(officialChecksums.Concat(officialChecksums).ToArray());
            if(kind=="oversized")return RuntimeReply(new byte[YtDlpUpdatePolicy.MaxChecksumBytes+1]);
            return RuntimeReply(officialChecksums);
        }),downloadDeadline:kind=="timeout"?TimeSpan.FromSeconds(1):TimeSpan.FromMinutes(1));
    }

    private static HttpResponseMessage RuntimeReply(byte[] bytes)=>new(HttpStatusCode.OK){Content=new ByteArrayContent(bytes)};
    private sealed class RuntimeHandler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> respond):HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancel)=>respond(request,cancel);
    }

    private static async Task PrepareOfficialRuntimeAsync(string cache,RuntimeRelease release)
    {
        string directory=Path.Combine(cache,release.Version);Directory.CreateDirectory(directory);
        string binary=Path.Combine(directory,"yt-dlp.exe"),checksums=Path.Combine(directory,"SHA2-256SUMS");
        if(File.Exists(binary) && AppUpdatePackage.HashFile(binary)==release.Sha256 && File.Exists(checksums))return;
        Console.WriteLine("SETUP: fetching pinned official Windows runtime fixture "+release.Version);
        using var http=new HttpClient{Timeout=TimeSpan.FromMinutes(2)};
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        try
        {
            string prefix="https://github.com/yt-dlp/yt-dlp/releases/download/"+release.Version+"/";
            byte[] sums=await http.GetByteArrayAsync(prefix+"SHA2-256SUMS",deadline.Token);
            Require(sums.Length<=YtDlpUpdatePolicy.MaxChecksumBytes && Encoding.UTF8.GetString(sums).Split('\n')
                .Any(line=>line.TrimEnd('\r')==release.Sha256+"  yt-dlp.exe"),"Pinned fixture SHA must match the official release checksum.");
            await using(var input=await http.GetStreamAsync(prefix+"yt-dlp.exe",deadline.Token))
            await using(var output=new FileStream(binary,FileMode.Create,FileAccess.Write,FileShare.None))
            {
                byte[] buffer=new byte[81920];int count;long total=0;
                while((count=await input.ReadAsync(buffer,deadline.Token))!=0)
                {
                    total+=count;if(total>64L*1024*1024)throw new IOException("Official runtime fixture exceeds 64 MiB.");
                    await output.WriteAsync(buffer.AsMemory(0,count),deadline.Token);
                }
            }
            Require(AppUpdatePackage.HashFile(binary)==release.Sha256,"Official runtime fixture hash mismatch.");
            await File.WriteAllBytesAsync(checksums,sums);
        }
        catch(Exception error){throw new IOException("SETUP failed for pinned official runtime fixture "+release.Version+"; prefill VD_TEST_YTDLP_FIXTURES for offline tests.",error);}
    }

    private static async Task AssertOfficialExecutableVersionAsync(string path,string version)
    {
        using var process=Process.Start(new ProcessStartInfo(path,"--version")
        {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true})!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(process.ExitCode==0 && (await stdout).Trim()==version,"Hash-verified official Windows runtime must report its actual release version.");
        }
        finally
        {
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}
            await stdout;await stderr;
        }
    }

    private static void ChangePeRevision(string path)
    {
        byte[] bytes=File.ReadAllBytes(path);
        int offset=bytes.AsSpan().IndexOf(new byte[]{0xbd,0x04,0xef,0xfe,0x00,0x00,0x01,0x00});
        Require(offset>=0,"Official PE must contain fixed version information.");
        bytes[offset+12]=1;bytes[offset+13]=0;
        File.WriteAllBytes(path,bytes);
    }

    private static void RemoveRuntimePolicyMetadata(string directory)
    {
        string metadata=Path.Combine(directory,"update-owner.json");
        var owner=JsonNode.Parse(File.ReadAllText(metadata))!.AsObject();
        owner.Remove("PreparedYtDlpSha256");owner.Remove("RetainedYtDlp");
        File.WriteAllText(metadata,owner.ToJsonString());
    }

    private static async Task RetainedRuntimeCrashRecoveryAsync(string oldRuntime,string newRuntime,byte[] checksums)
    {
        using var fixture=new Fixture(oldRuntime,oldRuntime);
        File.Copy(newRuntime,Path.Combine(fixture.Current,"yt-dlp.exe"),true);
        var before=Snapshot(fixture.Current);
        using var prepared=await PrepareRetainedRuntimeAsync(fixture,checksums);
        string host=Environment.ProcessPath!;
        var start=new ProcessStartInfo(host){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        if(Path.GetFileNameWithoutExtension(host).Equals("dotnet",StringComparison.OrdinalIgnoreCase))start.ArgumentList.Add(typeof(AppUpdateInstallerTests).Assembly.Location);
        foreach(string argument in new[]{"--vd-update-fixture",prepared.Directory,"2"})start.ArgumentList.Add(argument);
        using var process=Process.Start(start)!;
        var stdout=process.StandardOutput.ReadToEndAsync();var stderr=process.StandardError.ReadToEndAsync();
        try
        {
            string signal=Path.Combine(prepared.Directory,"fixture-signal");
            var deadline=DateTime.UtcNow.AddSeconds(20);
            while(!File.Exists(signal) && !process.HasExited && DateTime.UtcNow<deadline)await Task.Delay(20);
            Require(File.Exists(signal),"Retained-runtime helper must reach a real durable mutation checkpoint.");
            process.Kill(true);await process.WaitForExitAsync();
            // Recovery needs the actual-byte backup and effective manifest,
            // even when the forward-install source was lost after a crash.
            File.Delete(Path.Combine(prepared.Directory,"retained-yt-dlp.exe"));
            AppUpdateInstaller.Recover(AppUpdateInstaller.FindPending(fixture.Current)!);
            RequireSame(before,Snapshot(fixture.Current),"Killed-helper recovery must restore the independent runtime and original manifest without the retained source.");
        }
        finally
        {
            if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync();}
            await stdout;await stderr;
        }
    }
}
