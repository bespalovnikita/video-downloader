using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using System.Xml;

namespace VideoDownloader.UiTests;

internal sealed partial class UiScenario
{
    private static void TestDependencyControls(string testRoot)
    {
        static void Require(bool condition,string message)
        {if(!condition)throw new InvalidOperationException("Dependency controls WPF test: "+message);}
        static void PumpUntil(Func<bool> condition,string message)
        {
            if(condition())return;
            var frame=new DispatcherFrame();
            var deadline=DateTime.UtcNow.AddSeconds(5);
            var timer=new DispatcherTimer{Interval=TimeSpan.FromMilliseconds(15)};
            timer.Tick+=(_,_)=>{if(condition() || DateTime.UtcNow>=deadline)frame.Continue=false;};
            timer.Start();
            try{System.Windows.Threading.Dispatcher.PushFrame(frame);}finally{timer.Stop();}
            Require(condition(),message);
        }
        static T Control<T>(Window window,string name) where T:class=>(T)(window.FindName(name)
            ??throw new InvalidOperationException("Missing real dependency control: "+name));
        static Window LoadWindow()
        {
            using var input=File.OpenRead(Path.Combine(AppContext.BaseDirectory,"ui","MainWindow.xaml"));
            using var reader=XmlReader.Create(input);
            return (Window)XamlReader.Load(reader);
        }
        string root=Path.Combine(testRoot,"dependency-controls");Directory.CreateDirectory(root);
        var server=new DependencyUiPackageServer();
        Window window=LoadWindow();
        var storage=new AppPersistence(Path.Combine(root,"profile"));
        var subject=new NativeWindow(window,storage,"missing-fixture-yt-dlp.exe",automaticUpdates:false);
        try
        {
            window.Show();window.UpdateLayout();
            var cancel=Control<Button>(window,"CancelDependencyButton");
            var bar=Control<ProgressBar>(window,"DependencyProgress");
            var detail=Control<TextBlock>(window,"DependencyProgressText");
            PumpUntil(()=>Control<TextBlock>(window,"ReadinessText").Text.Contains("yt-dlp",StringComparison.Ordinal),
                "First-run readiness must identify the actually missing packaged tool without launching any process.");
            Require(Control<System.Windows.Controls.Border>(window,"ReadinessCard").IsVisible &&
                    cancel.Visibility==Visibility.Collapsed && bar.Visibility==Visibility.Collapsed,
                "The first-run card is visible before input and does not fabricate an active operation.");
            var row=new DownloadItem{Url="https://dependency-ui.invalid/untouched"};
            subject.SessionState.Queue.Add(row);
            Control<DataGrid>(window,"QueueGrid").SelectedItem=row;
            subject.UpdateState();
            Require(Control<Button>(window,"MediaSelectionButton").IsEnabled,
                "The idle selected row must be editable before testing component-work exclusion.");

            string target=Path.Combine(root,"components");Directory.CreateDirectory(target);
            File.WriteAllText(Path.Combine(target,"ffmpeg.exe"),"old ffmpeg");
            File.WriteAllText(Path.Combine(target,"ffprobe.exe"),"old ffprobe");
            Task Install(string endpoint)=>subject.DependencyOperation("Preparing component fixture…",async(progress,token)=>
            {
                await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+endpoint),target,TimeSpan.FromSeconds(15),token,
                    progress:progress).ConfigureAwait(false);
                return "Component fixture installed";
            });
            var operation=Install("known");
            PumpUntil(()=>server.Requests>=1 && cancel.IsVisible && bar.IsVisible && !bar.IsIndeterminate,
                "A real Content-Length transfer must show a measured progress bar and Cancel in the main card.");
            Require(cancel.IsEnabled && !Control<Button>(window,"StartButton").IsEnabled &&
                    !Control<Button>(window,"InstallFfmpegButton").IsEnabled,
                "Component work must prevent concurrent download admission and duplicate replacement.");
            var edit=Control<Button>(window,"MediaSelectionButton");
            Require(!edit.IsEnabled,"An in-flight component replacement must disable frame/format editing.");
            edit.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Require(subject.MediaSelectionEditor is null,
                "A directly raised disabled editor action cannot bypass the component-operation guard.");
            cancel.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            PumpUntil(()=>operation.IsCompleted,"The actual Cancel button must cancel and join the HTTP body read.");
            operation.GetAwaiter().GetResult();
            Require(cancel.Visibility==Visibility.Collapsed && bar.Visibility==Visibility.Collapsed &&
                    detail.Text.Contains(ErrorMessages.Text("отменена","cancelled"),StringComparison.OrdinalIgnoreCase),
                "Cancellation must leave an explicit final result with no lingering spinner.");
            Require(File.ReadAllText(Path.Combine(target,"ffmpeg.exe"))=="old ffmpeg" &&
                    File.ReadAllText(Path.Combine(target,"ffprobe.exe"))=="old ffprobe",
                "Cancelling actual package transfer must preserve both existing tools.");

            operation=Install("unknown");
            PumpUntil(()=>server.Requests>=2 && cancel.IsVisible && bar.IsIndeterminate,
                "A transfer without Content-Length must remain indeterminate instead of displaying a fictional total.");
            server.ReleaseUnknown();
            PumpUntil(()=>operation.IsCompleted,"A released real HTTP ZIP must complete its component transaction.");
            operation.GetAwaiter().GetResult();
            Require(File.ReadAllText(Path.Combine(target,"ffmpeg.exe"))=="new ffmpeg" &&
                    File.ReadAllText(Path.Combine(target,"ffprobe.exe"))=="new ffprobe" &&
                    bar.Visibility==Visibility.Collapsed,
                "Successful completion must publish the real pair and stop progress feedback.");
            PumpUntil(()=>Control<TextBlock>(window,"ReadinessText").Text.Contains("yt-dlp",StringComparison.Ordinal),
                "Readiness after an operation must still report an actually missing tool.");
            Require(detail.Text.Contains(ErrorMessages.Text("Операция с компонентами завершена","Component operation completed"),StringComparison.Ordinal) &&
                    !detail.Text.Contains(ErrorMessages.Text("Компоненты готовы","Components are ready"),StringComparison.Ordinal),
                "Completing one component operation must not claim all download tools are ready.");
            Require(subject.SessionState.Queue.Count==1 && ReferenceEquals(subject.SessionState.Queue[0],row) &&
                    subject.SessionState.Coordinator is null && !File.Exists(storage.HistoryPath),
                "Readiness and installation never start a download, replace the queue or create successful history.");

            operation=Install("exit");
            PumpUntil(()=>server.Requests>=3 && cancel.IsVisible,"Exit fixture did not reach a live HTTP transfer.");
            bool closed=false;window.Closed+=(_,_)=>closed=true;
            subject.Lifecycle.RequestExit();
            PumpUntil(()=>closed && operation.IsCompleted,"Actual window exit must cancel and join component work before closing.");
            Require(File.ReadAllText(Path.Combine(target,"ffmpeg.exe"))=="new ffmpeg" &&
                    !Directory.GetDirectories(target,".vd-ffmpeg-*").Any(),
                "Window exit must preserve installed tools and finish its own transaction cleanup.");

            subject.Dispose();window.Close();
            window=LoadWindow();
            subject=new NativeWindow(window,new AppPersistence(Path.Combine(root,"dispose-profile")),"missing-fixture-yt-dlp.exe",automaticUpdates:false);
            window.Show();
            var disposing=subject.DependencyOperation("Dispose fixture…",async(progress,token)=>
            {
                await DependencyCenter.InstallFfmpegAsync(new Uri(server.BaseUrl+"dispose"),target,
                    TimeSpan.FromSeconds(15),token,progress:progress).ConfigureAwait(false);
                return "unreachable complete transfer";
            });
            PumpUntil(()=>server.Requests>=4,"Dispose fixture must have an actual in-flight body to cancel.");
            subject.Dispose();
            PumpUntil(()=>disposing.IsCompleted,"Disposing the owner must also observe the cancelled service completion.");
            Require(File.ReadAllText(Path.Combine(target,"ffprobe.exe"))=="new ffprobe",
                "Dispose must join service cancellation without losing a previous component.");
            Console.WriteLine("PASS: real readiness card, HTTP transfer progress/cancel, unknown length, manual Start boundary and joined exit/Dispose");
        }
        finally
        {
            subject.Dispose();window.Close();
            var stopped=server.StopAsync();
            PumpUntil(()=>stopped.IsCompleted,"Dependency fixture server cleanup did not finish.");
            stopped.GetAwaiter().GetResult();
        }
    }

    private sealed class DependencyUiPackageServer
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly TaskCompletionSource _unknownReleased=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<Task> _requests=[];
        private readonly Task _accept;
        private readonly byte[] _zip;
        private int _entered;
        internal int Requests=>Volatile.Read(ref _entered);
        internal string BaseUrl {get;}
        internal DependencyUiPackageServer()
        {
            using var bytes=new MemoryStream();
            using(var archive=new ZipArchive(bytes,ZipArchiveMode.Create,true))
            foreach(string name in new[]{"ffmpeg","ffprobe"})
            {
                using var writer=new StreamWriter(archive.CreateEntry("fixture/bin/"+name+".exe").Open());
                writer.Write("new "+name);
            }
            _zip=bytes.ToArray();
            _listener.Start();BaseUrl="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/";
            _accept=AcceptAsync();
        }
        internal void ReleaseUnknown()=>_unknownReleased.TrySetResult();
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
                bool unknown=request.Contains("/unknown ",StringComparison.Ordinal);
                byte[] header=Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\n"+
                    (unknown?"":$"Content-Length: {_zip.Length}\r\n")+"Connection: close\r\n\r\n");
                await stream.WriteAsync(header,_stop.Token);
                int prefix=_zip.Length/2;
                await stream.WriteAsync(_zip.AsMemory(0,prefix),_stop.Token);
                Interlocked.Increment(ref _entered);
                if(unknown)
                {
                    await _unknownReleased.Task.WaitAsync(_stop.Token);
                    await stream.WriteAsync(_zip.AsMemory(prefix),_stop.Token);
                }
                else await Task.Delay(Timeout.InfiniteTimeSpan,_stop.Token);
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException){}
        }
        internal async Task StopAsync()
        {
            _stop.Cancel();_listener.Stop();
            await _accept;
            await Task.WhenAll(_requests);
            _stop.Dispose();
        }
    }
}
