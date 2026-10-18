using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Xml;

namespace VideoDownloader.Native;

public static class Program
{
    private static readonly string PipeName="VideoDownloader.Native."+Environment.UserName.Replace(' ','_');
    [STAThread]
    public static void Main(string[] args)
    {
        if(args.Contains("--cli",StringComparer.OrdinalIgnoreCase))
        {
            NativeCli.RunAsync(args).GetAwaiter().GetResult();
            return;
        }
        using var singleton=new Mutex(true,@"Local\VideoDownloader.Native.Gui",out bool isFirst);
        if(!isFirst)
        {
            try
            {
                using var client=new NamedPipeClientStream(".",PipeName,PipeDirection.Out);
                client.Connect(1200);
                using var writer=new StreamWriter(client,new UTF8Encoding(false)) { AutoFlush=true };
                writer.WriteLine(JsonSerializer.Serialize(args));
            }
            catch(Exception e)
            {
                System.Windows.Forms.MessageBox.Show("An instance is already running but cannot be contacted: "+e.Message,
                    "Video Downloader",System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            return;
        }
        var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException+=(sender,eventArgs)=>
        {
            System.Windows.MessageBox.Show(eventArgs.Exception.ToString(),"Video Downloader error",MessageBoxButton.OK,MessageBoxImage.Error);
            eventArgs.Handled=true;
        };
        NativeWindow? ui=null;
        CancellationTokenSource? pipeStop=null;
        try
        {
            var file=Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml");
            using var input=File.OpenRead(file);
            using var reader=XmlReader.Create(input);
            var window=(Window)XamlReader.Load(reader);
            ui=new NativeWindow(window);
            app.MainWindow=window;
            pipeStop=new CancellationTokenSource();
            _=ListenForSecondaryInstancesAsync(ui,pipeStop.Token);
            ui.ReceiveArguments(args);
            app.Run(window);
        }
        catch(Exception e)
        {
            System.Windows.Forms.MessageBox.Show(e.ToString(),"Video Downloader startup error",
                System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Error);
        }
        finally
        {
            pipeStop?.Cancel();
            ui?.Dispose();
            try { singleton.ReleaseMutex(); }catch(ApplicationException){}
        }
    }
    private static async Task ListenForSecondaryInstancesAsync(NativeWindow ui,CancellationToken cancel)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var pipe=new NamedPipeServerStream(PipeName,PipeDirection.In,1,
                    PipeTransmissionMode.Byte,PipeOptions.Asynchronous|PipeOptions.CurrentUserOnly);
                await pipe.WaitForConnectionAsync(cancel).ConfigureAwait(false);
                using var reader=new StreamReader(pipe,Encoding.UTF8,leaveOpen:true);
                var raw=await reader.ReadLineAsync(cancel).ConfigureAwait(false);
                var args=JsonSerializer.Deserialize<string[]>(raw??"[]")??[];
                _=ui.Dispatcher.BeginInvoke(()=>ui.ReceiveArguments(args));
            }
            catch(OperationCanceledException) {break;}
            catch(Exception e) { System.Diagnostics.Debug.WriteLine("Native IPC error: "+e.Message); }
        }
    }
}

internal static class NativeCli
{
    public static async Task RunAsync(string[] args)
    {
        static string Option(string[] a,string name,string fallback="")
        {
            int i=Array.FindIndex(a,s=>s.Equals(name,StringComparison.OrdinalIgnoreCase));
            return i>=0&&i+1<a.Length ? a[i+1] : fallback;
        }
        try
        {
            string input=Option(args,"--input");
            if (string.IsNullOrWhiteSpace(input) || !File.Exists(input)) throw new ArgumentException("Usage: VideoDownloader.exe --cli --input list.txt [--out folder] [--threads 4]");
            var options=new DownloadOptions
            {
                Output=Option(args,"--out",Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),"Downloads")),
                Threads=Option(args,"--threads","4"),
                Quality=Option(args,"--quality","Best"),
                Codec=Option(args,"--codec","Auto"),
                Container=Option(args,"--container","Auto"),
                RateLimit=Option(args,"--rate","Unlimited"),
                Cookies=Option(args,"--cookies","")
            };
            var store=new AppPersistence();
            using var logger=new StreamWriter(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!,"native-cli.log"),append:false);
            var rows=new List<DownloadItem>();
            await using var coordinator=new DownloadCoordinator(DependencyCenter.Executable("yt-dlp.exe"),store,options,
                row=>{},line=>{lock(logger){logger.WriteLine(line);logger.Flush();}});
            foreach(var line in File.ReadAllLines(input))
            {
                string url=line.Trim();
                if(!AppPersistence.ValidUrl(url)||rows.Any(x=>x.Url==url))continue;
                var row=new DownloadItem{Url=url,Order=rows.Count+1};
                rows.Add(row);coordinator.Add(row);
            }
            await coordinator.RunAsync().ConfigureAwait(false);
            logger.WriteLine($"Finished: OK={rows.Count(x=>x.Status=="Completed")}, errors={rows.Count(x=>x.Status=="Failed")}");
            logger.Flush();
            Environment.ExitCode=rows.Any(x=>x.Status=="Failed")?1:0;
        }
        catch(Exception e){System.Diagnostics.Debug.WriteLine(e);Environment.ExitCode=1;}
    }
}
