# Local HTTP fixtures keep real yt-dlp integration independent of external sites
# and of the user's yt-dlp configuration. The listener only serves flat files.
if (-not ('VideoDownloader.Tests.OfflineMediaServer' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace VideoDownloader.Tests {
    public sealed class OfflineMediaServer : IDisposable {
        readonly string root;
        readonly TcpListener listener;
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly ConcurrentDictionary<TcpClient, byte> clients = new ConcurrentDictionary<TcpClient, byte>();
        readonly ConcurrentBag<Task> requests = new ConcurrentBag<Task>();
        readonly Task acceptLoop;
        public string BaseUrl { get; private set; }

        public OfflineMediaServer(string directory) {
            root = Path.GetFullPath(directory);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            BaseUrl = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/";
            acceptLoop = Task.Run(() => AcceptAsync());
        }

        async Task AcceptAsync() {
            try {
                while (!stop.IsCancellationRequested) {
                    var client = await listener.AcceptTcpClientAsync();
                    clients.TryAdd(client, 0);
                    requests.Add(ServeAsync(client));
                }
            } catch (ObjectDisposedException) { }
              catch (SocketException) { if (!stop.IsCancellationRequested) throw; }
        }

        static Task HeaderAsync(NetworkStream stream, string status, long length, string extra = "") {
            var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + status + "\r\nContent-Type: video/mp4\r\nContent-Length: " +
                length + "\r\nAccept-Ranges: bytes\r\nConnection: close\r\n" + extra + "\r\n");
            return stream.WriteAsync(header, 0, header.Length);
        }

        async Task ServeAsync(TcpClient client) {
            try {
                using (client)
                using (var stream = client.GetStream())
                using (var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true)) {
                    var first = await reader.ReadLineAsync();
                    var parts = (first ?? "").Split(' ');
                    if (parts.Length < 2 || (parts[0] != "GET" && parts[0] != "HEAD")) {
                        await HeaderAsync(stream, "400 Bad Request", 0); return;
                    }
                    string range = null;
                    int headerSize = first.Length;
                    string line;
                    while (!string.IsNullOrEmpty(line = await reader.ReadLineAsync())) {
                        headerSize += line.Length;
                        if (headerSize > 16384) { await HeaderAsync(stream, "431 Request Header Fields Too Large", 0); return; }
                        if (line.StartsWith("Range:", StringComparison.OrdinalIgnoreCase)) range = line.Substring(6).Trim();
                    }
                    var name = Uri.UnescapeDataString(parts[1].Split('?')[0].TrimStart('/'));
                    if (name.Length == 0 || name.Contains("/") || name.Contains("\\") || name.Contains(":")) {
                        await HeaderAsync(stream, "404 Not Found", 0); return;
                    }
                    var path = Path.Combine(root, name);
                    if (!File.Exists(path)) { await HeaderAsync(stream, "404 Not Found", 0); return; }
                    using (var file = File.OpenRead(path)) {
                        long start = 0, end = file.Length - 1;
                        if (range != null) {
                            var match = Regex.Match(range, @"^bytes=(\d+)-(\d*)$");
                            if (!match.Success || !long.TryParse(match.Groups[1].Value, out start) ||
                                (match.Groups[2].Length > 0 && !long.TryParse(match.Groups[2].Value, out end)) ||
                                start < 0 || start >= file.Length || end < start) {
                                await HeaderAsync(stream, "416 Range Not Satisfiable", 0, "Content-Range: bytes */" + file.Length + "\r\n"); return;
                            }
                            end = Math.Min(end, file.Length - 1);
                        }
                        long remaining = Math.Max(0, end - start + 1);
                        await HeaderAsync(stream, range == null ? "200 OK" : "206 Partial Content", remaining,
                            range == null ? "" : "Content-Range: bytes " + start + "-" + end + "/" + file.Length + "\r\n");
                        if (parts[0] == "HEAD") return;
                        file.Position = start;
                        var buffer = new byte[65536];
                        while (remaining > 0 && !stop.IsCancellationRequested) {
                            var count = await file.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, remaining), stop.Token);
                            if (count == 0) break;
                            await stream.WriteAsync(buffer, 0, count, stop.Token);
                            remaining -= count;
                        }
                    }
                }
            } catch (IOException) { }
              catch (SocketException) { }
              catch (ObjectDisposedException) { }
              catch (OperationCanceledException) { }
            finally { byte ignored; clients.TryRemove(client, out ignored); }
        }

        public void Dispose() {
            stop.Cancel();
            listener.Stop();
            acceptLoop.GetAwaiter().GetResult();
            foreach (var client in clients.Keys) client.Dispose();
            try { Task.WhenAll(requests.ToArray()).GetAwaiter().GetResult(); }
            finally { stop.Dispose(); }
        }
    }
}
'@
}

function Start-VdOfflineMediaServer {
    param([Parameter(Mandatory)][string]$Root)
    return [VideoDownloader.Tests.OfflineMediaServer]::new($Root)
}
