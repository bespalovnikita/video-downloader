#requires -Version 7.0
# The pipe never executes received text; the GUI validates arguments as URLs/files.
if (-not ('VideoDownloader.Ipc' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
namespace VideoDownloader {
 public sealed class Ipc : IDisposable {
  readonly CancellationTokenSource stop = new CancellationTokenSource();
  readonly ConcurrentQueue<string> messages = new ConcurrentQueue<string>();
  readonly Task worker;
  const int MaxMessage = 65536;
  public Ipc(string name) { worker = Task.Run(() => Run(name)); }
  static async Task ReadExact(Stream stream, byte[] data, CancellationToken token) {
   int offset=0;
   while(offset<data.Length) {
    int n=await stream.ReadAsync(data,offset,data.Length-offset,token);
    if(n==0) throw new EndOfStreamException();
    offset+=n;
   }
  }
  async Task Run(string name) {
   while(!stop.IsCancellationRequested) {
    try {
     using(var pipe=new NamedPipeServerStream(name,PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly)) {
      await pipe.WaitForConnectionAsync(stop.Token);
      using(var timeout=CancellationTokenSource.CreateLinkedTokenSource(stop.Token)) {
       timeout.CancelAfter(5000);
       var header=new byte[4]; await ReadExact(pipe,header,timeout.Token);
       int length=BitConverter.ToInt32(header,0);
       if(length<1 || length>MaxMessage) continue;
       var data=new byte[length]; await ReadExact(pipe,data,timeout.Token);
       messages.Enqueue(Encoding.UTF8.GetString(data));
       await pipe.WriteAsync(new byte[]{1},0,1,timeout.Token);
      }
     }
    } catch(OperationCanceledException) { } catch(IOException) { }
   }
  }
  public string Take() { string value; return messages.TryDequeue(out value) ? value : null; }
  public static async Task Send(string name,string message) {
   var data=Encoding.UTF8.GetBytes(message);
   if(data.Length<1 || data.Length>MaxMessage) throw new ArgumentException("Too many startup arguments.");
   using(var timeout=new CancellationTokenSource(5000))
   using(var pipe=new NamedPipeClientStream(".",name,PipeDirection.InOut,PipeOptions.Asynchronous)) {
    await pipe.ConnectAsync(timeout.Token);
    var header=BitConverter.GetBytes(data.Length);
    await pipe.WriteAsync(header,0,header.Length,timeout.Token);
    await pipe.WriteAsync(data,0,data.Length,timeout.Token);
    var ack=new byte[1]; await ReadExact(pipe,ack,timeout.Token);
    if(ack[0]!=1) throw new IOException("Startup arguments were not accepted.");
   }
  }
  public void Dispose() { stop.Cancel(); try { worker.GetAwaiter().GetResult(); } finally { stop.Dispose(); } }
 }
}
'@
}

function Get-VdIpcName {
    return 'VideoDownloader.' + [Security.Principal.WindowsIdentity]::GetCurrent().User.Value + '.' + [Diagnostics.Process]::GetCurrentProcess().SessionId
}

Export-ModuleMember -Function Get-VdIpcName
