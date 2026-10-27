using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class GuiInstanceTests
{
    private const string FixtureSwitch="--native-gui-instance";

    internal static bool TryRunFixture(string[] args)
    {
        if(args.FirstOrDefault()!=FixtureSwitch)return false;
        if(!OperatingSystem.IsWindows())throw new PlatformNotSupportedException("Native GUI instances require Windows.");
        if(args.Length<3)throw new ArgumentException("An isolated profile and instance name are required.");
        Exception? failure=null;
        // The test executable's top-level Main is MTA. Use the same STA and
        // production startup path as the application without changing the host.
        var thread=new Thread(()=>
        {
            try { VideoDownloader.Native.Program.RunGui(args.Skip(3).ToArray(),new AppPersistence(args[1]),args[2]); }
            catch(Exception error) {failure=error;}
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();thread.Join();
        if(failure!=null)ExceptionDispatchInfo.Capture(failure).Throw();
        Console.WriteLine("GUI_RETURNED:"+Environment.ProcessId);
        return true;
    }

    internal static async Task RunAsync()
    {
        if(!OperatingSystem.IsWindows())return;
        string root=Path.Combine(Path.GetTempPath(),"vd-gui-instances-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var processes=new List<Process>();
        // One deadline bounds the complete scenario. Reserve five more seconds
        // for killing only our own processes and releasing their file handles.
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            string instance="VideoDownloader.Native.GuiTest."+Guid.NewGuid().ToString("N");
            var data=new AppPersistence(Path.Combine(root,"profile"));
            string prefix="https://gui-instance.invalid/"+Guid.NewGuid().ToString("N")+"/";
            string[] urls=[prefix+"initial?Token=AbC",prefix+"Video?Token=A%2FB&name=Upper+Case#part",
                prefix+"from-file?Token=XyZ",prefix+"video?Token=A%2FB&name=Upper+Case#part"];
            var cacheBytes=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            foreach(string url in urls)
            {
                string file=Path.Combine(data.PreviewDir,Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant()+".json");
                AppPersistence.AtomicWrite(file,JsonSerializer.Serialize(new {
                    CacheVersion=1,PreviewFetchVersion=2,Url=url,Title="Cached "+url,SavedAtUtc=DateTime.UtcNow,
                    Uploader="",PlaylistTitle="",Duration=0,Extractor="",MaxHeight=0,MaxFps=0,
                    Codecs="",DynamicRange="",ThumbnailBase64=""
                },AppPersistence.Json));
                cacheBytes.Add(file,File.ReadAllBytes(file));
            }

            Process Start(params string[] arguments)
            {
                var start=new ProcessStartInfo(TestExecutable()) {
                    UseShellExecute=false,CreateNoWindow=true,WorkingDirectory=AppContext.BaseDirectory,
                    RedirectStandardOutput=true,RedirectStandardError=true,
                    StandardOutputEncoding=Encoding.UTF8,StandardErrorEncoding=Encoding.UTF8
                };
                start.ArgumentList.Add(FixtureSwitch);
                start.ArgumentList.Add(Path.GetDirectoryName(data.SessionPath)!);
                start.ArgumentList.Add(instance);
                foreach(string argument in arguments)start.ArgumentList.Add(argument);
                // A GUI is not a download worker and must never be put inside
                // ManagedChildProcess's worker job. This process belongs to the test.
                var process=Process.Start(start)??throw new InvalidOperationException("Could not start GUI fixture.");
                processes.Add(process);
                return process;
            }
            async Task Until(Func<bool> condition,string message)
            {
                while(!condition())
                {
                    if(deadline.IsCancellationRequested)throw new TimeoutException("GUI singleton test: "+message);
                    await Task.Delay(25,deadline.Token);
                }
            }
            SessionSnapshot? Snapshot()
            {
                try
                {
                    return File.Exists(data.SessionPath)
                        ? JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(data.SessionPath,Encoding.UTF8),AppPersistence.Json)
                        : null;
                }
                catch(IOException) {return null;}
                catch(JsonException) {return null;}
            }
            bool CheckpointMatches(IEnumerable<string> expected) => Snapshot() is {} snapshot &&
                snapshot.Queue.Select(row=>row.Url).SequenceEqual(expected,StringComparer.Ordinal);

            var first=Start(urls[0]);
            IntPtr window=IntPtr.Zero;
            await Until(()=>
            {
                Require(!first.HasExited,"The first GUI must stay alive during startup.");
                window=FindWindow(first.Id);
                return window!=IntPtr.Zero && IsWindowVisible(window) && CheckpointMatches(urls.Take(1));
            },"The first process must open its real WPF window and checkpoint the initial URL.");
            Require(WindowProcess(window)==first.Id,"The observed WPF window must belong to the first test process.");

            string list=Path.Combine(root,"ссылки [second instance].txt");
            File.WriteAllText(list,urls[2]+Environment.NewLine+urls[0]+Environment.NewLine,new UTF8Encoding(false));
            static string Protocol(string url)=>"videodownloader://"+Uri.EscapeDataString(url);
            async Task Secondary(params string[] arguments)
            {
                var second=Start(arguments);
                var stdout=second.StandardOutput.ReadToEndAsync(deadline.Token);
                var stderr=second.StandardError.ReadToEndAsync(deadline.Token);
                bool secondWindow=false;
                await Until(()=>
                {
                    if(FindWindow(second.Id)!=IntPtr.Zero)secondWindow=true;
                    return second.HasExited;
                },"A second GUI instance must forward arguments and exit instead of opening another window.");
                await Task.WhenAll(stdout,stderr,second.WaitForExitAsync(deadline.Token));
                Require(!secondWindow && second.ExitCode==0 && stdout.Result.Trim()=="GUI_RETURNED:"+second.Id && stderr.Result.Length==0,
                    "A real second startup must finish via the singleton path without a second WPF window or error.");
                Require(!first.HasExited && WindowProcess(window)==first.Id,"Secondary delivery must leave the original GUI alive.");
            }

            await Secondary(Protocol(urls[1]),list,urls[3],Protocol(urls[1]));
            await Until(()=>CheckpointMatches(urls),"The original GUI must checkpoint the complete exact argument array once in input order.");
            var imported=Snapshot()!;
            Require(imported.Queue.All(row=>row.Title=="Cached "+row.Url && row.Status=="Queued" && row.Path.Length==0 && row.OwnedPaths.Count==0),
                "Protocol, TXT and case-distinct plain URLs must retain cached titles and remain queued without downloading.");

            Require(PostMessageW(window,0x0112,(IntPtr)0xF020,IntPtr.Zero),"The test must send the normal minimize command to the actual window.");
            await Until(()=>!IsWindowVisible(window),"Minimizing the first WPF window must hide it in the tray.");
            await Secondary();
            await Until(()=>IsWindowVisible(window) && !IsIconic(window),"An empty second startup must restore the same hidden WPF window.");
            Require(FindWindow(first.Id)==window && CheckpointMatches(urls),"Reactivation must preserve the original window and its exact queue.");
            Require(!File.Exists(data.HistoryPath) && cacheBytes.All(pair=>File.ReadAllBytes(pair.Key).SequenceEqual(pair.Value)),
                "GUI-only ingress must neither record downloads nor replace its pre-seeded offline metadata cache.");
            Console.WriteLine("PASS: real native GUI singleton, second-process protocol/TXT/exact URLs, minimize-to-tray and empty-launch restore");
        }
        finally
        {
            using var cleanup=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            foreach(var process in processes)
            {
                try
                {
                    if(!process.HasExited)process.Kill(true);
                    await process.WaitForExitAsync(cleanup.Token);
                }
                catch(InvalidOperationException) { }
                finally {process.Dispose();}
            }
            Directory.Delete(root,true);
        }
    }

    private static string TestExecutable()
    {
        string path=Path.Combine(AppContext.BaseDirectory,typeof(GuiInstanceTests).Assembly.GetName().Name+".exe");
        Require(File.Exists(path),"The Windows native test apphost is required for real GUI instances.");
        return path;
    }
    private static IntPtr FindWindow(int pid)
    {
        IntPtr found=IntPtr.Zero;
        EnumWindows((handle,_)=>
        {
            if(WindowProcess(handle)!=pid)return true;
            var caption=new StringBuilder(256);GetWindowTextW(handle,caption,caption.Capacity);
            if(caption.ToString()!="Video Downloader")return true;
            found=handle;return false;
        },IntPtr.Zero);
        return found;
    }
    private static int WindowProcess(IntPtr handle)
    {
        GetWindowThreadProcessId(handle,out uint pid);
        return (int)pid;
    }
    private static void Require(bool condition,string message)
    {
        if(!condition)throw new InvalidOperationException("GUI singleton test failed: "+message);
    }
    private delegate bool EnumWindowsCallback(IntPtr window,IntPtr state);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback,IntPtr state);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window,out uint processId);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] private static extern int GetWindowTextW(IntPtr window,StringBuilder text,int count);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool PostMessageW(IntPtr window,uint message,IntPtr wParam,IntPtr lParam);
}
