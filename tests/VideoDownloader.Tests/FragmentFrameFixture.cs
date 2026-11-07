using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using VideoDownloader.Native;

// Shared only by native tests and the real WPF roundtrip; never included in the app.
internal static class FragmentFrameFixture
{
    internal static async Task<string> Source(string directory,string rate,int count,int keyframe)
    {
        string raw=Path.Combine(directory,"barcode.rgb"),source=Path.Combine(directory,"source.mp4");
        await using(var stream=File.Create(raw))
        {
            byte[] pixels=new byte[160*90*3];
            for(int frame=0;frame<count;frame++)
            {
                Array.Fill(pixels,(byte)48);
                for(int bit=0;bit<10;bit++)
                    for(int y=10;y<35;y++)
                        for(int x=10+bit*12;x<10+(bit+1)*12;x++)
                            for(int c=0;c<3;c++)pixels[(y*160+x)*3+c]=(byte)((frame&(1<<bit))!=0?240:16);
                await stream.WriteAsync(pixels);
            }
        }
        string ffmpeg=DependencyCenter.Executable(OperatingSystem.IsWindows()?"ffmpeg.exe":"ffmpeg");
        await Tool(ffmpeg,["-v","error","-nostdin","-f","rawvideo","-pixel_format","rgb24","-video_size","160x90",
            "-framerate",rate,"-i",raw,"-c:v","libx264","-preset","fast","-crf","18","-g",keyframe.ToString(CultureInfo.InvariantCulture),
            "-keyint_min",keyframe.ToString(CultureInfo.InvariantCulture),"-sc_threshold","0","-bf","3","-pix_fmt","yuv420p","-movflags","+faststart",source]);
        File.Delete(raw);
        return source;
    }
    internal static async Task<int[]> Decode(string ffmpeg,string path)
    {
        byte[] pixels=await FragmentFrameFixture.Tool(ffmpeg,["-v","error","-nostdin","-i",path,"-map","0:v:0","-vf",
            "crop=120:2:10:20,scale=10:1:flags=neighbor,format=gray","-fps_mode","passthrough","-f","rawvideo","pipe:1"]);
        Require(pixels.Length>0 && pixels.Length%10==0,"Decoded barcode must contain complete frames");
        return Enumerable.Range(0,pixels.Length/10).Select(index=>Enumerable.Range(0,10)
            .Where(bit=>pixels[index*10+bit]>128).Sum(bit=>1<<bit)).ToArray();
    }
    internal static async Task<byte[]> Tool(string executable,IEnumerable<string> arguments)
    {
        var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(string argument in arguments)start.ArgumentList.Add(argument);
        using var child=ManagedChildProcess.Start(start);
        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var result=await BoundedProcessOutput.ReadAsync(child,2*1024*1024,128*1024,deadline.Token);
        Require(child.Process.ExitCode==0,Path.GetFileName(executable)+" failed: "+Encoding.UTF8.GetString(result.StandardError));
        return result.StandardOutput;
    }
    private static void Require(bool condition,string message)
    {if(!condition)throw new InvalidOperationException("Frame media fixture: "+message);}
    internal sealed class Origin:IAsyncDisposable
    {
        private readonly TcpListener _listener=new(IPAddress.Loopback,0);
        private readonly CancellationTokenSource _stop=new();
        private readonly Task _accept;
        private readonly ConcurrentBag<Task> _responses=new();
        private readonly byte[] _media;
        internal string Url {get;}
        internal Origin(byte[] media)
        {
            _media=media;_listener.Start();
            Url="http://127.0.0.1:"+((IPEndPoint)_listener.LocalEndpoint).Port+"/source.mp4";
            _accept=Accept();
        }
        private async Task Accept()
        {
            try
            {
                while(!_stop.IsCancellationRequested)
                {
                    var client=await _listener.AcceptTcpClientAsync(_stop.Token);
                    _responses.Add(Respond(client));
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(SocketException) when(_stop.IsCancellationRequested){}
        }
        private async Task Respond(TcpClient client)
        {
            try
            {
                using(client)
                {
                    var stream=client.GetStream();
                    using var reader=new StreamReader(stream,Encoding.ASCII,false,1024,leaveOpen:true);
                    string request=await reader.ReadLineAsync(_stop.Token)??"";
                    string? range=null;
                    int length=request.Length;
                    while(true)
                    {
                        string? line=await reader.ReadLineAsync(_stop.Token);
                        if(string.IsNullOrEmpty(line))break;
                        length+=line.Length;
                        if(length>16*1024)throw new IOException("Fixture request headers exceed their bound");
                        if(line.StartsWith("Range:",StringComparison.OrdinalIgnoreCase))range=line[6..].Trim();
                    }
                    bool head=request.StartsWith("HEAD ",StringComparison.Ordinal);
                    int first=0,last=_media.Length-1;
                    bool partial=false;
                    if(range?.StartsWith("bytes=",StringComparison.Ordinal)==true)
                    {
                        string[] parts=range[6..].Split('-');
                        if(parts.Length!=2 || !int.TryParse(parts[0],out first) || first<0 || first>=_media.Length)
                            throw new IOException("Unexpected fixture byte range");
                        if(parts[1].Length>0 && !int.TryParse(parts[1],out last))throw new IOException("Unexpected fixture byte range end");
                        last=Math.Min(last,_media.Length-1);partial=true;
                    }
                    int count=last-first+1;
                    string headers="HTTP/1.1 "+(partial?"206 Partial Content":"200 OK")+"\r\nContent-Type: video/mp4\r\nAccept-Ranges: bytes\r\nContent-Length: "+count+
                        (partial?"\r\nContent-Range: bytes "+first+"-"+last+"/"+_media.Length:"")+"\r\nConnection: close\r\n\r\n";
                    await stream.WriteAsync(Encoding.ASCII.GetBytes(headers),_stop.Token);
                    if(!head)await stream.WriteAsync(_media.AsMemory(first,count),_stop.Token);
                }
            }
            catch(OperationCanceledException) when(_stop.IsCancellationRequested){}
            catch(IOException) { /* A probe may close its connection after reading the headers. */ }
            catch(SocketException) { }
        }
        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();_listener.Stop();
            await _accept;
            await Task.WhenAll(_responses);
            _stop.Dispose();
        }
    }

}
