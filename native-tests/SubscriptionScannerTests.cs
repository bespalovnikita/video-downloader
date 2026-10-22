using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

// The test executable impersonates yt-dlp; HTTP is provided by an in-memory handler.
internal static class SubscriptionScannerTests
{
    private const string Prefix = "https://subscription-scanner.invalid/";
    private const string MediaA = "https://media.example.org/Video?source=A";
    private const string MediaB = "https://media.example.org/video?source=A";

    internal static bool TryRunFixture(string[] args)
    {
        if (args.Length == 2 && args[0] == "--subscription-scanner-sleeper")
        {
            File.WriteAllText(args[1], Environment.ProcessId.ToString());
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return true;
        }
        if (args.LastOrDefault()?.StartsWith(Prefix, StringComparison.Ordinal) != true) return false;
        foreach (string required in new[] { "--ignore-config", "--no-flat-playlist", "--dump-single-json", "--skip-download", "--abort-on-error" })
            Require(args.Contains(required, StringComparer.Ordinal), "Missing bounded collection argument: " + required);
        int end = Array.IndexOf(args, "--playlist-end");
        Require(end >= 0 && args[end + 1] == "10001", "Full enumeration must request the truncation sentinel item");
        int cookie = Array.IndexOf(args, "--cookies");
        Require(cookie >= 0, "Fixture state must be forwarded through --cookies");
        string statePath = args[cookie + 1];
        using var config = JsonDocument.Parse(File.ReadAllText(statePath));
        string state = Path.GetDirectoryName(statePath)!;
        File.AppendAllText(Path.Combine(state, "calls.jsonl"), JsonSerializer.Serialize(args) + "\n");
        File.WriteAllText(Path.Combine(state, "parent.pid"), Environment.ProcessId.ToString());
        string mode = config.RootElement.GetProperty("Mode").GetString()!;
        if (mode == "hang")
        {
            var child = new ProcessStartInfo(TestExecutable()) { UseShellExecute = false };
            child.ArgumentList.Add("--subscription-scanner-sleeper");
            child.ArgumentList.Add(Path.Combine(state, "child.pid"));
            using var process = Process.Start(child)!;
            Thread.Sleep(TimeSpan.FromSeconds(30));
            return true;
        }
        if (mode == "nonzero")
        {
            Console.WriteLine(config.RootElement.GetProperty("Response").GetString());
            Console.Error.WriteLine("ERROR: collection enumeration is incomplete");
            Environment.ExitCode = 1;
            return true;
        }
        if (mode == "stdout-limit")
        {
            Console.Write("{\"_type\":\"playlist\",\"title\":\"");
            string block = new('x', 64 * 1024);
            for (int n = 0; n < 513; n++) Console.Write(block);
            Console.Write("\",\"entries\":[]}");
            return true;
        }
        if (mode is "stderr-flood" or "stderr-incomplete")
        {
            string block = new('e', 4096);
            for (int n = 0; n < 1024; n++) Console.Error.WriteLine(block);
            if (mode == "stderr-incomplete") Console.Error.WriteLine("WARNING: incomplete playlist enumeration");
        }
        Console.WriteLine(config.RootElement.GetProperty("Response").GetString());
        return true;
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-subscription-scanner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<Exception>();
        async Task Check(string name, Func<string, Task> test)
        {
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); Console.WriteLine("PASS: " + name); }
            catch (Exception error) { failures.Add(new Exception(name, error)); Console.WriteLine($"FAIL: {name}: {error.Message}"); }
        }
        try
        {
            await Check("subscription scan defaults enforce the documented resource limits", _ =>
            {
                var options = new SubscriptionScannerOptions();
                Require(options.ScanTimeout == TimeSpan.FromSeconds(180) && options.FeedTimeout == TimeSpan.FromSeconds(12),
                    "Scan/feed deadlines must default to 180/12 seconds");
                Require(options.MaxItems == 10000 && options.MaxMetadataBytes == 32 * 1024 * 1024 && options.MaxFeedBytes == 2 * 1024 * 1024,
                    "Item, metadata and feed caps must match the subscription contract");
                foreach (var raised in new[] {
                    options with { ScanTimeout = TimeSpan.FromSeconds(181) }, options with { FeedTimeout = TimeSpan.FromSeconds(13) },
                    options with { MaxItems = 10001 }, options with { MaxMetadataBytes = options.MaxMetadataBytes + 1 },
                    options with { MaxFeedBytes = options.MaxFeedBytes + 1 } })
                    Refuses(() => { using var scanner = new SubscriptionScanner(TestExecutable(), options: raised); },
                        "Test configuration must not raise a documented hard resource limit");
                return Task.CompletedTask;
            });

            await Check("generic collections are fully scanned again despite an unchanged feed", async directory =>
            {
                string config = Fixture(directory, Collection(Entry(MediaA, ""), Entry(MediaB, "Lowercase"), Entry(MediaA, "")));
                int requests = 0;
                using var http = Http((_, _) => {
                    Interlocked.Increment(ref requests);
                    return Task.FromResult(Xml(Rss((MediaA, "Feed title"), ("https://media.example.org/feed-only", "Unconfirmed"))));
                });
                using var scanner = new SubscriptionScanner(TestExecutable(), http);
                var source = Source("generic");
                source.FeedUrl = "https://feeds.example.org/unchanged.xml";
                var first = await scanner.ScanAsync(source, config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                Require(first.Items.Count == 2 && first.Items.Any(item => item.Url == MediaA && item.Title == "Feed title") && first.Items.Any(item => item.Url == MediaB),
                    "Generic HTTP collections must deduplicate exact URLs while retaining case-distinct URLs and feed title enrichment");
                Require(first.Items.All(item => item.Url != "https://media.example.org/feed-only") && first.Warning.Length > 0,
                    "An optional feed cannot introduce unconfirmed collection members");
                string late = "https://media.example.org/late?token=unchanged";
                Fixture(directory, Collection(Entry(MediaA, ""), Entry(MediaB, "Lowercase"), Entry(late, "Late")));
                var second = await scanner.ScanAsync(source, config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                Require(second.Items.Any(item => item.Url == late) && File.ReadAllLines(Path.Combine(directory, "calls.jsonl")).Length == 2 && requests == 2,
                    "An unchanged feed must never suppress the next full yt-dlp enumeration");
                Require(!source.Initialized && source.LastSuccessUtc == null,
                    "Scanner must return detached discovery data without advancing persisted subscription state");
            });

            await Check("RSS and Atom parsers merge exact item links without platform restrictions", _ =>
            {
                var rss = SubscriptionScanner.ParseFeed(Rss((MediaA, "First"), (MediaA, "Duplicate"), (MediaB, "Case distinct")));
                Require(rss.Count == 2 && rss.Any(item => item.Url == MediaB), "RSS deduplication must compare exact URLs ordinally");
                string atom = $"<feed xmlns=\"http://www.w3.org/2005/Atom\"><entry><title>Atom title</title><link rel=\"alternate\" href=\"{Escape(MediaA)}\"/></entry><entry><title>Other</title><link href=\"{Escape(MediaB)}\"/></entry></feed>";
                var parsed = SubscriptionScanner.ParseFeed(atom);
                Require(parsed.Count == 2 && parsed.Any(item => item.Url == MediaA && item.Title == "Atom title"), "Atom namespaces and alternate links must be supported");
                string channel = "UC" + new string('a', 22);
                string metadata = JsonSerializer.Serialize(new { channel_id = channel, entries = new[] { Entry(MediaA, "Media") } });
                var youtube = SubscriptionScanner.ParseMetadata(metadata, "https://www.youtube.com/@offline");
                Require(youtube.FeedUrl == "https://www.youtube.com/feeds/videos.xml?channel_id=" + channel,
                    "YouTube collection metadata can discover its optional Atom feed");
                Require(SubscriptionScanner.ParseMetadata(metadata, Prefix + "generic").Items.Count == 1,
                    "Automatic YouTube feed support must not restrict generic yt-dlp collections");
                const string videoId = "AbCdEf012_-";
                string flatYoutube = JsonSerializer.Serialize(new { entries = new[] { new { id = videoId, url = videoId, ie_key = "Youtube" } } });
                Require(SubscriptionScanner.ParseMetadata(flatYoutube, "https://www.youtube.com/@offline").Items[0].Url == "https://www.youtube.com/watch?v=" + videoId,
                    "A YouTube media entry can resolve its extractor-specific bare video ID");
                return Task.CompletedTask;
            });

            await Check("incomplete and malformed full metadata cannot claim a successful scan", _ =>
            {
                string entry = JsonSerializer.Serialize(Entry(MediaA, "Valid"));
                string[] invalid = {
                    "{", "null", "{}", "{\"entries\":null}", "{\"entries\":{}}", "{\"entries\":[null]}",
                    "{\"entries\":[],\"_error\":\"failed\"}", "{\"entries\":[],\"truncated\":true}",
                    "{\"entries\":[{\"_type\":\"playlist\",\"entries\":[],\"_error\":\"page failed\"}]}",
                    "{\"entries\":[{\"id\":\"missing-url\"}]}",
                    "{\"entries\":[{\"url\":\"ftp://media.example.org/asset\"}]}",
                    "{\"entries\":[{\"url\":\"not an absolute URL\"}]}",
                    "{\"entries\":[{\"url\":\"https://media.example.org/private\",\"availability\":\"private\"}]}",
                    "{\"entries\":[{\"_type\":\"playlist\",\"url\":\"https://media.example.org/unresolved\"}]}",
                    "{\"entries\":[{\"_type\":\"url\",\"ie_key\":\"CBCGemPlaylist\",\"url\":\"https://gem.cbc.ca/show/s01\"}]}",
                    "{\"entries\":[{\"_type\":\"url_transparent\",\"url\":\"https://soundcloud.com/artist/sets/album\"}]}",
                    "{\"playlist_count\":2,\"entries\":[" + entry + "]}",
                    "{\"playlist_count\":0,\"entries\":[" + entry + "]}",
                    "{\"requested_entries\":[2],\"entries\":[" + entry + "]}"
                };
                foreach (string json in invalid) Refuses(() => SubscriptionScanner.ParseMetadata(json, Prefix + "invalid"),
                    "Malformed, unavailable or truncated collection metadata must be rejected: " + json);
                var nested = SubscriptionScanner.ParseMetadata(JsonSerializer.Serialize(new { entries = new object[] {
                    new { _type = "playlist", entries = new[] { Entry(MediaA, "Nested") } }, Entry(MediaB, "Other") } }), Prefix + "nested");
                Require(nested.Items.Count == 2, "Inline nested collections must be enumerated for generic extractors");
                return Task.CompletedTask;
            });

            await Check("the 10000 item boundary rejects the sentinel instead of truncating", _ =>
            {
                var entries = Enumerable.Range(0, 10001).Select(index => Entry("https://media.example.org/item/" + index, "Item")).ToArray();
                var full = SubscriptionScanner.ParseMetadata(Collection(entries.Take(10000).ToArray()), Prefix + "limit");
                Require(full.Items.Count == 10000, "Exactly 10000 complete items must remain supported");
                Refuses(() => SubscriptionScanner.ParseMetadata(Collection(entries), Prefix + "limit"), "The 10001st item must fail the full scan");
                return Task.CompletedTask;
            });

            await Check("optional feed errors fall back to complete metadata with a warning", async directory =>
            {
                string config = Fixture(directory, Collection(Entry(MediaA, "Full metadata")));
                foreach (string scenario in new[] { "malformed", "http-error", "body-limit" })
                {
                    var oversizedBody = scenario == "body-limit" ? new CountingStream(Encoding.UTF8.GetBytes("<rss><channel>" + new string(' ', 3 * 1024 * 1024) + "</channel></rss>")) : null;
                    using var http = Http((_, _) => Task.FromResult(scenario switch {
                        "http-error" => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable),
                        "body-limit" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new FixtureContent(oversizedBody!) },
                        _ => Xml("<rss><broken>") }));
                    var source = Source("feed-fallback"); source.FeedUrl = "https://feeds.example.org/optional.xml";
                    using var scanner = new SubscriptionScanner(TestExecutable(), http);
                    var scan = await scanner.ScanAsync(source, config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                    Require(scan.Items.Count == 1 && scan.Items[0].Url == MediaA && scan.Warning.Length > 0,
                        "Optional feed failure must preserve successful full enumeration and report its warning: " + scenario);
                    if (oversizedBody != null) Require(oversizedBody.BytesRead < oversizedBody.TotalBytes,
                        "The feed byte cap must stop a chunked response early, before consuming its entire oversized body");
                }
            });

            await Check("feed deadline includes the response body and falls back without blocking", async directory =>
            {
                string config = Fixture(directory, Collection(Entry(MediaA, "Full metadata")));
                var stream = new WaitingStream();
                using var http = Http((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new FixtureContent(stream) }));
                var source = Source("slow-feed"); source.FeedUrl = "https://feeds.example.org/slow-body";
                var options = new SubscriptionScannerOptions { FeedTimeout = TimeSpan.FromMilliseconds(150) };
                using var scanner = new SubscriptionScanner(TestExecutable(), http, options);
                var watch = Stopwatch.StartNew();
                var scan = await scanner.ScanAsync(source, config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
                Require(scan.Items.Count == 1 && scan.Warning.Length > 0 && watch.Elapsed < TimeSpan.FromSeconds(3),
                    "The feed body must obey its own deadline without discarding full metadata");
                Require(stream.CancellationObserved, "Cancellation must reach the actual response-body read");
            });

            await Check("both process pipes are drained and a nonzero exit rejects valid partial JSON", async directory =>
            {
                string config = Fixture(directory, Collection(Entry(MediaA, "Media")), "stderr-flood");
                using var scanner = new SubscriptionScanner(TestExecutable());
                var scan = await scanner.ScanAsync(Source("stderr"), config, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(15));
                Require(scan.Items.Count == 1, "A full stderr pipe must not deadlock successful metadata enumeration");
                Fixture(directory, Collection(Entry(MediaA, "Partial")), "nonzero");
                await RefusesAsync(() => scanner.ScanAsync(Source("failed"), config, CancellationToken.None),
                    "Valid JSON from a failed yt-dlp process is still an incomplete scan");
                Fixture(directory, Collection(Entry(MediaA, "Partial")), "stderr-incomplete");
                await RefusesAsync(() => scanner.ScanAsync(Source("partial-warning"), config, CancellationToken.None),
                    "An incomplete warning after a large diagnostic stream must reject a zero-exit partial collection");
            });

            await Check("metadata stdout is capped at 32 MiB", async directory =>
            {
                string config = Fixture(directory, "", "stdout-limit");
                using var scanner = new SubscriptionScanner(TestExecutable());
                await RefusesAsync(() => scanner.ScanAsync(Source("stdout-limit"), config, CancellationToken.None),
                    "Metadata larger than 32 MiB must fail instead of accepting a truncated response");
                Require(!IsAlive(ReadPid(directory, "parent.pid")), "Output-cap failure must leave no live metadata process");
            });

            foreach (bool cancel in new[] { true, false })
            {
                await Check(cancel ? "scan cancellation kills its native process tree" : "scan timeout kills its native process tree", async directory =>
                {
                    string config = Fixture(directory, "", "hang");
                    using var cancellation = new CancellationTokenSource();
                    var options = new SubscriptionScannerOptions { ScanTimeout = cancel ? TimeSpan.FromSeconds(15) : TimeSpan.FromSeconds(2) };
                    using var scanner = new SubscriptionScanner(TestExecutable(), null, options);
                    Task<SubscriptionScan> pending = scanner.ScanAsync(Source("hang"), config, cancellation.Token);
                    try
                    {
                        await WaitUntil(() => File.Exists(Path.Combine(directory, "child.pid")), pending, TimeSpan.FromSeconds(10));
                        var watch = Stopwatch.StartNew();
                        if (cancel) cancellation.Cancel();
                        await RefusesAsync(async () => await pending, "Cancelled/timed-out scans must not return a successful result");
                        Require(watch.Elapsed < TimeSpan.FromSeconds(4), "Cancellation/timeout must release the scanner promptly");
                        await WaitUntil(() => !IsAlive(ReadPid(directory, "parent.pid")) && !IsAlive(ReadPid(directory, "child.pid")), null, TimeSpan.FromSeconds(2));
                    }
                    finally { KillFixture(directory, "parent.pid"); KillFixture(directory, "child.pid"); }
                });
            }

            if (failures.Count > 0) throw new AggregateException("Subscription scanner regression failures", failures);
        }
        finally { Directory.Delete(root, true); }
    }

    private static object Entry(string url, string title) => new { _type = "video", id = url, webpage_url = url, title };
    private static string Collection(params object[] entries) => JsonSerializer.Serialize(new { _type = "playlist", title = "Generic collection", playlist_count = entries.Length, entries });
    private static SubscriptionDefinition Source(string name) => new() { Id = Guid.NewGuid().ToString("N"), Url = Prefix + name };
    private static string Fixture(string directory, string response, string mode = "normal")
    {
        string path = Path.Combine(directory, "fixture.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new { Mode = mode, Response = response }));
        return path;
    }
    private static string Rss(params (string Url, string Title)[] entries) => "<rss version=\"2.0\"><channel>" +
        string.Concat(entries.Select(entry => $"<item><title>{Escape(entry.Title)}</title><link>{Escape(entry.Url)}</link></item>")) + "</channel></rss>";
    private static string Escape(string value) => System.Security.SecurityElement.Escape(value)!;
    private static HttpResponseMessage Xml(string body) => new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "application/xml") };
    private static HttpClient Http(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> handler) => new(new Handler(handler));
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }
    private sealed class FixtureContent : HttpContent
    {
        private readonly Stream stream;
        internal FixtureContent(byte[] bytes) : this(new MemoryStream(bytes)) { }
        internal FixtureContent(Stream stream) { this.stream = stream; }
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream destination, TransportContext? context) => stream.CopyToAsync(destination);
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult(stream);
        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken token) => Task.FromResult(stream);
        protected override void Dispose(bool disposing) { if (disposing) stream.Dispose(); base.Dispose(disposing); }
    }
    private sealed class WaitingStream : Stream
    {
        internal bool CancellationObserved { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(30), token); return 0; }
            catch (OperationCanceledException) { CancellationObserved = true; throw; }
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    private sealed class CountingStream(byte[] bytes) : MemoryStream(bytes)
    {
        internal long BytesRead { get; private set; }
        internal int TotalBytes { get; } = bytes.Length;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            int count = base.Read(buffer.Span);
            BytesRead += count;
            return ValueTask.FromResult(count);
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
    }
    private static async Task RefusesAsync(Func<Task> action, string message)
    {
        Task pending;
        try { pending = action(); } catch { return; }
        if (await Task.WhenAny(pending, Task.Delay(TimeSpan.FromSeconds(15))) != pending)
            throw new TimeoutException("Native fixture did not terminate within the test deadline");
        try { await pending; } catch { return; }
        throw new InvalidOperationException(message);
    }
    private static void Refuses(Action action, string message)
    {
        bool threw = false; try { action(); } catch { threw = true; } Require(threw, message);
    }
    private static async Task WaitUntil(Func<bool> condition, Task? pending, TimeSpan timeout)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (pending?.IsCompleted == true) { await pending; throw new InvalidOperationException("Fixture finished before its child process began"); }
            if (watch.Elapsed > timeout) throw new TimeoutException("Timed out waiting for native fixture process state");
            await Task.Delay(20);
        }
    }
    private static int ReadPid(string directory, string name) => int.Parse(File.ReadAllText(Path.Combine(directory, name)));
    private static bool IsAlive(int pid)
    {
        try
        {
            if (OperatingSystem.IsLinux())
            {
                string status = File.ReadAllText($"/proc/{pid}/stat");
                if (status[status.LastIndexOf(") ", StringComparison.Ordinal) + 2] == 'Z') return false;
            }
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException) { return false; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private static void KillFixture(string directory, string name)
    {
        try
        {
            int pid = ReadPid(directory, name);
            if (!IsAlive(pid)) return; // Linux PID 1 can retain an already-killed orphan as a zombie.
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) { process.Kill(true); process.WaitForExit(2000); }
        }
        catch (ArgumentException) { } catch (IOException) { }
    }
    private static string TestExecutable()
    {
        string path = Path.Combine(AppContext.BaseDirectory, typeof(SubscriptionScannerTests).Assembly.GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(path), "A test apphost is required for offline scanner child processes");
        return path;
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
}
