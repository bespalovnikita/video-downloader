using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Markup;
using System.Xml;
using System.Security.Cryptography;

namespace VideoDownloader.Native;

public static class Program
{
    private static readonly string PipeName="VideoDownloader.Native."+Environment.UserName.Replace(' ','_');
    [STAThread]
    public static void Main(string[] args)
    {
        string? directory=Environment.GetEnvironmentVariable("VD_DATA_DIRECTORY");
        if(string.IsNullOrWhiteSpace(directory))RunGui(args);
        else
        {
            directory=Path.GetFullPath(directory);
            string identity=Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory.TrimEnd(Path.DirectorySeparatorChar).ToUpperInvariant())));
            RunGui(args,new AppPersistence(directory),"VideoDownloader.Native.Profile."+identity);
        }
    }

    // Tests exercise the same singleton and GUI startup with isolated data/names.
    internal static void RunGui(string[] args,AppPersistence? persistence=null,string? instanceName=null)
    {
        string pipeName=instanceName??PipeName;
        using var singleton=new Mutex(true,instanceName==null?@"Local\VideoDownloader.Native.Gui":@"Local\"+instanceName,out bool isFirst);
        if(!isFirst)
        {
            try
            {
                ForwardArguments(args,pipeName);
            }
            catch(Exception e)
            {
                System.Windows.Forms.MessageBox.Show(ErrorMessages.Text("Приложение уже запущено, но не отвечает. ",
                    "An instance is already running but cannot be contacted: ")+ExceptionDetails(e),
                    "Video Downloader",System.Windows.Forms.MessageBoxButtons.OK,
                    System.Windows.Forms.MessageBoxIcon.Warning);
            }
            return;
        }
        var app=new Application { ShutdownMode=ShutdownMode.OnMainWindowClose };
        app.DispatcherUnhandledException+=(sender,eventArgs)=>
        {
            System.Windows.MessageBox.Show(ExceptionDetails(eventArgs.Exception),ErrorMessages.Text("Ошибка Video Downloader","Video Downloader error"),MessageBoxButton.OK,MessageBoxImage.Error);
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
            ui=new NativeWindow(window,persistence);
            app.MainWindow=window;
            pipeStop=new CancellationTokenSource();
            _=ListenForSecondaryInstancesAsync(ui,pipeStop.Token,pipeName);
            ui.ReceiveArguments(args);
            app.Run(window);
        }
        catch(Exception e)
        {
            System.Windows.Forms.MessageBox.Show(ExceptionDetails(e),ErrorMessages.Text("Ошибка запуска Video Downloader","Video Downloader startup error"),
                System.Windows.Forms.MessageBoxButtons.OK,System.Windows.Forms.MessageBoxIcon.Error);
        }
        finally
        {
            pipeStop?.Cancel();
            ui?.Dispose();
            try { singleton.ReleaseMutex(); }catch(ApplicationException){}
        }
    }
    private static string ExceptionDetails(Exception error) => ErrorMessages.IsRussian
        ? ErrorMessages.ForException(error)+"\n\nИсходная диагностика:\n"+error
        : error.ToString();
    internal static void ForwardArguments(string[] args,string? pipeName=null)
    {
        using var client=new NamedPipeClientStream(".",pipeName??PipeName,PipeDirection.Out);
        client.Connect(1200);
        using var writer=new StreamWriter(client,new UTF8Encoding(false)) { AutoFlush=true };
        writer.WriteLine(JsonSerializer.Serialize(args));
    }

    internal static async Task ListenForSecondaryInstancesAsync(NativeWindow ui,CancellationToken cancel,string? pipeName=null)
    {
        while (!cancel.IsCancellationRequested)
        {
            try
            {
                await using var pipe=new NamedPipeServerStream(pipeName??PipeName,PipeDirection.In,1,
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
