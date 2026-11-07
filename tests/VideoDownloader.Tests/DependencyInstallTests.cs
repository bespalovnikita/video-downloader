using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Security.AccessControl;
using System.Security.Principal;
using VideoDownloader.Native;

internal static class DependencyInstallTests
{
    internal static async Task RunAsync()
    {
        if(OperatingSystem.IsWindows())DaclComparison();
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
            await PairRollbackAsync(root,server);
            Require(!Directory.GetDirectories(Path.GetTempPath(),"vd-ffmpeg-*").Except(before).Any(),
                "Successful, cancelled and timed-out installs must remove their scratch folders");
            Console.WriteLine("PASS: dependency package header/body deadlines, cancellation, retry and cleanup");
        }
        finally {Directory.Delete(root,true);}
    }

    private static async Task PairRollbackAsync(string root,PackageServer server)
    {
        foreach(string kind in new[]{"directory","fault","cancel","missing","readonly","sharing","acl"})
        {
            if ((kind is "sharing" or "acl") && !OperatingSystem.IsWindows()) continue;
            string target=Path.Combine(root,kind);Directory.CreateDirectory(target);
            string ffmpeg=Path.Combine(target,"ffmpeg.exe"),probe=Path.Combine(target,"ffprobe.exe");
            if(kind!="missing")File.WriteAllText(ffmpeg,"previous ffmpeg");
            if(kind=="directory")
            {
                Directory.CreateDirectory(probe);
                File.WriteAllText(Path.Combine(probe,"foreign.txt"),"foreign sentinel");
            }
            else File.WriteAllText(probe,"previous ffprobe");
            File.WriteAllText(Path.Combine(target,"foreign.txt"),"unrelated sentinel");
            FileStream? held=null;
            string? originalDacl=null;
            string? firstAcl=null;
            using var cancel=new CancellationTokenSource();
            int replaced=0;
            try
            {
                if(kind=="sharing")held=new FileStream(probe,FileMode.Open,FileAccess.Read,FileShare.Read);
                if(kind=="readonly")File.SetAttributes(probe,File.GetAttributes(probe)|FileAttributes.ReadOnly);
                if(kind=="acl")
                {
                    originalDacl=new FileInfo(probe).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                    var denied=new FileInfo(probe).GetAccessControl();
                    denied.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.WriteData,AccessControlType.Deny));
                    new FileInfo(probe).SetAccessControl(denied);
                    var distinct=new FileInfo(ffmpeg).GetAccessControl();
                    distinct.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                        FileSystemRights.ExecuteFile,AccessControlType.Deny));
                    new FileInfo(ffmpeg).SetAccessControl(distinct);
                    firstAcl=new FileInfo(ffmpeg).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                }
                bool failed=false;
                try
                {
                    await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"ok"),target,TimeSpan.FromSeconds(5),cancel.Token,count=>
                    {
                        replaced=count;
                        Require(File.ReadAllText(ffmpeg)=="ffmpeg fixture","The rollback case must first publish the real ZIP's ffmpeg bytes");
                        if(kind is "fault" or "missing")throw new IOException("Fixture fails after first replacement");
                        if(kind=="cancel")cancel.Cancel();
                    });
                }
                catch(Exception error) when(error is IOException or UnauthorizedAccessException or OperationCanceledException)
                {
                    failed=true;
                    if(kind=="acl")Require(error is UnauthorizedAccessException && error.HResult==unchecked((int)0x80070005),
                        $"Write-denied component must fail with Windows access denied, actual {error.GetType().Name}/0x{error.HResult:x8}");
                }
                Require(failed,"The component pair fixture must fail: "+kind);
                Require(kind=="missing"?!File.Exists(ffmpeg):File.ReadAllText(ffmpeg)=="previous ffmpeg",
                    "Failure must restore the prior ffmpeg existence and bytes: "+kind);
                Require(kind=="directory"?File.ReadAllText(Path.Combine(probe,"foreign.txt"))=="foreign sentinel":
                    File.ReadAllText(probe)=="previous ffprobe","Second destination must retain its original content: "+kind);
                Require(File.ReadAllText(Path.Combine(target,"foreign.txt"))=="unrelated sentinel","Unrelated files must remain untouched");
                if(firstAcl is not null)Require(SameDacl(firstAcl,new FileInfo(ffmpeg).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)),
                    "Rollback must retain an existing component's explicit Windows DACL");
                if(kind is "sharing" or "acl" or "fault" or "missing" or "cancel")
                    Require(replaced==1,"The real second-target refusal or cancellation must follow the first replacement: "+kind);
                Require(!Directory.EnumerateDirectories(target,".vd-ffmpeg-*").Any(),"Successful rollback must clean only its transaction");
            }
            finally
            {
                held?.Dispose();
                if(originalDacl is not null)
                {
                    // Persist writes only modified sections. A FileSecurity
                    // returned by GetAccessControl has no dirty DACL flag, so
                    // simply passing that snapshot to SetAccessControl is a no-op.
                    var restored=new FileSecurity();
                    restored.SetSecurityDescriptorSddlForm(originalDacl,AccessControlSections.Access);
                    new FileInfo(probe).SetAccessControl(restored);
                    string actual=new FileInfo(probe).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access);
                    Require(SameDacl(originalDacl,actual),$"ACL fixture must restore its original DACL before retry; expected={originalDacl}; actual={actual}");
                }
                if(kind=="readonly")File.SetAttributes(probe,File.GetAttributes(probe)&~FileAttributes.ReadOnly);
            }
            if(kind is "sharing" or "acl")
            {
                await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"ok"),target,TimeSpan.FromSeconds(5),CancellationToken.None);
                Require(File.ReadAllText(ffmpeg)=="ffmpeg fixture" && File.ReadAllText(probe)=="ffprobe fixture",
                    "A retry after releasing the Windows lock/ACL denial must install the full pair");
                if(firstAcl is not null)Require(SameDacl(firstAcl,new FileInfo(ffmpeg).GetAccessControl().GetSecurityDescriptorSddlForm(AccessControlSections.Access)),
                    "A successful retry must also preserve the existing component's explicit Windows DACL");
            }
        }
        string collision=Path.Combine(root,"rollback-collision");Directory.CreateDirectory(collision);
        File.WriteAllText(Path.Combine(collision,"ffmpeg.exe"),"previous ffmpeg");
        File.WriteAllText(Path.Combine(collision,"ffprobe.exe"),"previous ffprobe");
        bool recoveryRequired=false;
        try
        {
            await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"ok"),collision,TimeSpan.FromSeconds(5),CancellationToken.None,count=>
            {
                File.WriteAllText(Path.Combine(collision,"ffmpeg.exe"),"foreign changed bytes");
                throw new IOException("Fixture failure after foreign replacement");
            });
        }
        catch(AggregateException){recoveryRequired=true;}
        string recovery=Directory.GetDirectories(collision,".vd-ffmpeg-*").Single();
        Require(recoveryRequired && File.ReadAllText(Path.Combine(collision,"ffmpeg.exe"))=="foreign changed bytes" &&
            File.ReadAllText(Path.Combine(collision,"ffprobe.exe"))=="previous ffprobe" &&
            File.ReadAllText(Path.Combine(recovery,"ffmpeg.exe.backup"))=="previous ffmpeg",
            "Incomplete rollback must preserve actual old backups and unknown changed destination bytes");
        Console.WriteLine("PASS: real loopback ZIP component pair rollback, missing files, cancellation, foreign directory and read-only preservation"+
            (OperatingSystem.IsWindows()?", Windows sharing lock and ACL denial":"; Windows sharing/ACL cases require Windows"));
    }

    private static bool SameDacl(string expected,string actual)
    {
        static byte[] Bytes(string sddl)
        {
            var descriptor=new RawSecurityDescriptor(sddl);
            // SetNamedSecurityInfo can compute this bookkeeping flag during
            // restoration. Preserve every ACE byte and all other control bits,
            // including protection and the auto-inherit-required flag.
            descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
            byte[] bytes=new byte[descriptor.BinaryLength];descriptor.GetBinaryForm(bytes,0);return bytes;
        }
        return Bytes(expected).SequenceEqual(Bytes(actual));
    }

    private static void DaclComparison()
    {
        const string acl="(A;ID;FA;;;SY)(A;ID;FR;;;BA)";
        Require(SameDacl("D:"+acl,"D:AI"+acl),"Computed AutoInherited alone must not change DACL equality");
        foreach(string changed in new[]{"D:P"+acl,"D:AR"+acl,"D:(D;;WD;;;BA)"+acl,
            "D:"+acl.Replace("FA","FR",StringComparison.Ordinal),"D:"+acl.Replace("BA","BU",StringComparison.Ordinal),
            "D:"+acl.Replace("A;ID;","A;;",StringComparison.Ordinal)})
            Require(!SameDacl("D:"+acl,changed),"DACL equality must preserve protection, inheritance controls, deny ACEs, masks and SIDs: "+changed);
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
