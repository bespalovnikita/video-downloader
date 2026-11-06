using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class AppUpdateBootstrapTests
{
    internal static bool TryRunFixture(string[] args)
    {
        if(args.FirstOrDefault()=="--app-update-smoke-owner")
        {
            File.WriteAllText(args[1],Environment.ProcessId.ToString());
            Thread.Sleep(Timeout.Infinite);return true;
        }
        if(args.FirstOrDefault()!="--published-app-update-smoke")return false;
        if(args.Length!=2)throw new ArgumentException("A published Windows package directory is required.");
        RunPublishedAsync(Path.GetFullPath(args[1])).GetAwaiter().GetResult();return true;
    }
    private static async Task RunPublishedAsync(string published)
    {
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("The published application update smoke requires Windows.");
        var manifest=AppUpdatePackage.ReadManifest(Path.Combine(published,AppUpdatePackage.ManifestName));
        AppUpdatePackage.ValidateDirectory(published,manifest);
        string root=Path.Combine(Path.GetTempPath(),"vd-published-update-"+Guid.NewGuid().ToString("N"));
        string installed=Path.Combine(root,"installed"),profile=Path.Combine(root,"profile");
        string scratchRoot=Path.Combine(Path.GetTempPath(),"VideoDownloaderUpdates");
        PreparedAppUpdate? prepared=null;Process? owner=null,helper=null,restarted=null;
        long restartedAt=0;
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(100));
        try
        {
            Directory.CreateDirectory(installed);
            foreach(string path in Directory.EnumerateFiles(published,"*",SearchOption.AllDirectories))
            {
                string destination=Path.Combine(installed,Path.GetRelativePath(published,path));
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.Copy(path,destination);
            }
            // The installed fixture intentionally records an older ownership
            // version; staged bytes are the actual published, versioned EXE.
            var version=AppUpdatePackage.ParseVersion(manifest.Version);
            string oldVersion=version.Major>0?$"{version.Major-1}.0.0":"0.0.0";
            File.WriteAllText(Path.Combine(installed,AppUpdatePackage.ManifestName),
                JsonSerializer.Serialize(manifest with{Version=oldVersion},AppUpdatePackage.Json));
            var persistence=new AppPersistence(profile);
            persistence.Save([],new DownloadOptions{Quality="720",Output=Path.Combine(root,"media")});
            persistence.AppendSuccess("https://example.invalid/preserved-success");
            string history=File.ReadAllText(persistence.HistoryPath);
            string cookies=Path.Combine(installed,"cookies.txt"),external=Path.Combine(installed,"ffmpeg.exe");
            File.WriteAllText(cookies,"user cookie sentinel");File.WriteAllText(external,"external tool sentinel");
            string archive=Path.Combine(root,"package.zip");ZipFile.CreateFromDirectory(published,archive);
            prepared=AppUpdateInstaller.Prepare(archive,installed,scratchRoot,manifest.Version,deadline.Token);
            string ready=Path.Combine(root,"owner-ready");
            var start=new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"VideoDownloader.Tests.exe"))
                {UseShellExecute=false,CreateNoWindow=true};
            start.ArgumentList.Add("--app-update-smoke-owner");start.ArgumentList.Add(ready);
            owner=Process.Start(start)??throw new IOException("Cannot start the isolated update owner.");
            await Until(()=>File.Exists(ready),deadline.Token,"The update owner did not start.");
            string mutexName=@"Local\"+VideoDownloader.Native.Program.ProfileInstanceName(profile);
            // An unsuccessful secondary startup can retain a named-mutex
            // handle without owning it. The restarted app must acquire the
            // free mutex instead of confusing object existence with ownership.
            using var lingeringSecondaryHandle=new Mutex(false,mutexName);
            helper=AppUpdateBootstrap.StartHelper(prepared,mutexName,
                owner,Path.Combine(installed,"VideoDownloader.exe"),profile);
            await Task.Delay(400,deadline.Token);
            Require(!helper.HasExited,"The copied single-file updater must start without neighbouring native DLLs and wait for its owner.");
            Require(AppUpdatePackage.ReadManifest(Path.Combine(installed,AppUpdatePackage.ManifestName)).Version==oldVersion,
                "The live owner's package must remain untouched.");
            owner.Kill(true);await owner.WaitForExitAsync(deadline.Token);
            await helper.WaitForExitAsync(deadline.Token);
            Require(helper.ExitCode==0,"The production single-file updater failed to apply/restart.");
            await Until(()=>
            {
                foreach(var candidate in Process.GetProcessesByName("VideoDownloader"))
                {
                    bool selected=false;
                    try
                    {
                        if(!candidate.HasExited && string.Equals(candidate.MainModule?.FileName,
                            Path.Combine(installed,"VideoDownloader.exe"),StringComparison.OrdinalIgnoreCase) && WpfWindow(candidate.Id)!=IntPtr.Zero)
                        {restarted=candidate;restartedAt=candidate.StartTime.ToUniversalTime().Ticks;selected=true;return true;}
                    }
                    catch(Exception error) when(error is InvalidOperationException or System.ComponentModel.Win32Exception){}
                    finally {if(!selected)candidate.Dispose();}
                }
                return false;
            },deadline.Token,"The updated package did not restart its real WPF window.");
            Require(restarted!=null && WpfWindow(restarted.Id)!=IntPtr.Zero,"A WinForms error dialog must not count as a restarted application.");
            Require(AppUpdatePackage.ReadManifest(Path.Combine(installed,AppUpdatePackage.ManifestName)).Version==manifest.Version,
                "The complete new package manifest must be installed.");
            foreach(var file in manifest.Files)
            {
                string path=AppUpdatePackage.FilePath(installed,file.Path);
                Require(new FileInfo(path).Length==file.Size && AppUpdatePackage.HashFile(path)==file.Sha256,
                    "Installed package bytes must match the verified manifest: "+file.Path);
            }
            var restored=persistence.Restore();
            Require(restored?.Queue.Count==0 && restored.Settings.Quality=="720" && File.ReadAllText(persistence.HistoryPath)==history,
                "Restart must preserve the isolated profile's settings, queue and successful history.");
            Require(File.ReadAllText(cookies)=="user cookie sentinel" && File.ReadAllText(external)=="external tool sentinel",
                "The updater must preserve external tools and user files beside the package.");
            Console.WriteLine("PASS: published single-file helper starts alone, waits for owner exit, applies and restarts the real WPF application with preserved settings");
        }
        finally
        {
            // Every process below was created by this fixture. For the real GUI
            // also recheck its unique installed path and StartTime before kill.
            if(restarted is not null)
            {
                try
                {
                    if(!restarted.HasExited && restarted.StartTime.ToUniversalTime().Ticks==restartedAt &&
                        string.Equals(restarted.MainModule?.FileName,Path.Combine(installed,"VideoDownloader.exe"),StringComparison.OrdinalIgnoreCase))
                    {restarted.Kill(true);await restarted.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}
                }
                finally {restarted.Dispose();}
            }
            foreach(var process in new[]{owner,helper})
                if(process is not null)
                {
                    try {if(!process.HasExited){process.Kill(true);await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));}}
                    finally {process.Dispose();}
                }
            prepared?.Dispose();
            if(Directory.Exists(root))Directory.Delete(root,true);
        }
    }
    private static async Task Until(Func<bool> condition,CancellationToken cancel,string message)
    {
        while(!condition())
        {
            if(cancel.IsCancellationRequested)throw new TimeoutException(message);
            await Task.Delay(50,cancel);
        }
    }
    private static void Require(bool condition,string message){if(!condition)throw new InvalidOperationException(message);}
    private static IntPtr WpfWindow(int processId)
    {
        IntPtr result=IntPtr.Zero;
        EnumWindows((window,_)=>
        {
            GetWindowThreadProcessId(window,out uint owner);
            if(owner!=processId)return true;
            var name=new StringBuilder(256);GetClassName(window,name,name.Capacity);
            var title=new StringBuilder(128);GetWindowText(window,title,title.Capacity);
            if(name.ToString().StartsWith("HwndWrapper[",StringComparison.Ordinal) && title.ToString()=="Video Downloader")
            {result=window;return false;}
            return true;
        },IntPtr.Zero);
        return result;
    }
    private delegate bool EnumWindowProc(IntPtr window,IntPtr parameter);
    [DllImport("user32.dll")]private static extern bool EnumWindows(EnumWindowProc callback,IntPtr parameter);
    [DllImport("user32.dll")]private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetClassName(IntPtr window,StringBuilder name,int max);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)]private static extern int GetWindowText(IntPtr window,StringBuilder text,int max);
}
