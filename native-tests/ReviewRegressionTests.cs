using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

// These fixtures run in the test executable itself, never against public sites.
// The existing integration suite still exercises the bundled real yt-dlp.exe.
internal static class ReviewRegressionTests
{
    private const string FixtureBase = "https://native-regression.invalid/";

    internal static bool TryRunFixture(string[] args)
    {
        string? url = args.LastOrDefault();
        if (url?.StartsWith(FixtureBase, StringComparison.Ordinal) != true) return false;
        int outputIndex = Array.IndexOf(args, "-o");
        if (outputIndex < 0 || outputIndex + 1 >= args.Length) throw new Exception("Missing fixture output path");
        string stage = Path.GetDirectoryName(args[outputIndex + 1])!;
        Directory.CreateDirectory(stage);
        if (url.Contains("error-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(url.EndsWith("error-transient", StringComparison.Ordinal)
                ? "ERROR: Unable to download webpage: HTTP Error 503: Service unavailable"
                : url.EndsWith("error-tls", StringComparison.Ordinal)
                    ? "ERROR: TLS handshake failed"
                    : "ERROR: HTTP Error 403: Forbidden; connection reset for video503");
            Environment.ExitCode = 1;
            return true;
        }
        if (url.EndsWith("no-media", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(stage, "metadata.info.json"), "{}");
            return true; // yt-dlp may skip all media and still exit zero.
        }
        string media = Path.Combine(stage, "fixture.mp4");
        File.WriteAllBytes(media, new byte[120]);
        if (url.EndsWith("unreported-media", StringComparison.Ordinal)) return true;
        if (url.EndsWith("missing-media", StringComparison.Ordinal))
        {
            Console.WriteLine("__VD_FILE__:" + Path.Combine(stage, "not-created.mp4"));
            return true;
        }
        if (url.EndsWith("external-media", StringComparison.Ordinal))
        {
            int cookieIndex = Array.IndexOf(args, "--cookies");
            Console.WriteLine("__VD_FILE__:" + args[cookieIndex + 1]);
            return true;
        }
        Console.WriteLine("__VD_TITLE__:Fixture");
        foreach (var (stream, bytes) in new[] {
            ("video", 100), ("video", 100), ("audio", 50),
            ("video", 20), ("video", 70), ("audio", 50) })
            Console.WriteLine($"__VD_PROGRESS__|50%|1KiB/s|1|{bytes}|120|{stream}");
        Console.WriteLine("__VD_FILE__:" + media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-review-regressions-" + Guid.NewGuid().ToString("N"));
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
            await Check("legacy history gains a date and keeps the latest success", directory =>
            {
                var data = new AppPersistence(directory);
                const string url = "https://example.com/legacy-history";
                File.WriteAllLines(data.HistoryPath, new[] {
                    JsonSerializer.Serialize(new { Version = 1, Url = url }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url, CompletedAtUtc = "2026-10-18T10:00:00Z" }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url, CompletedAtUtc = "2026-10-17T10:00:00Z" }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url }) });
                Require(data.ReadHistory()[url] == DateTimeOffset.Parse("2026-10-18T10:00:00Z"),
                    "A later timestamp must replace an unknown legacy date and survive older/undated records");
                return Task.CompletedTask;
            });

            await Check("malformed legacy terminal events leave unfinished rows recoverable", directory =>
            {
                var data = new AppPersistence(directory);
                string events = Path.Combine(directory, "events.jsonl");
                var urls = Enumerable.Range(0, 10).Select(i => $"https://example.com/event-{i}").ToArray();
                File.WriteAllLines(events, new[] {
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[0], Success = "false" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[1] }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[2], Success = false, Cancelled = "true" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[3], Success = true, Path = 42 }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[4], Success = true, OwnedPaths = "not-an-array" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[5], Success = true, Cancelled = true }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[6], Success = true, OwnedPaths = new object[] { "file.mp4", 42 } }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[7], Success = false }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[8], Success = false, Cancelled = true }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[9], Success = true, Path = "finished.mp4" }) });
                WriteLegacySession(data, urls, directory, new { Pid = int.MaxValue, StartedUtc = DateTime.UtcNow, EventsPath = events });
                var restored = data.Restore()!;
                Require(restored.Queue.Take(7).All(row => row.Status == "Interrupted" && row.State == "Interrupted" && row.OwnedPaths.Count == 0),
                    "Missing or incorrectly typed terminal flags must not manufacture failed/cancelled results");
                Require(restored.Queue[7].Status == "Failed" && restored.Queue[8].State == "Cancelled" &&
                    restored.Queue[9].Status == "Completed", "Valid failure, cancellation and success events must still reconcile");
                return Task.CompletedTask;
            });

            await Check("live legacy worker blocks replay even while its event file is locked", directory =>
            {
                var data = new AppPersistence(directory);
                string events = Path.Combine(directory, "active-events.jsonl");
                using var oldWorker = Process.Start(new ProcessStartInfo("pwsh") {
                    UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
                })!;
                try
                {
                    var engine = new { Pid = oldWorker.Id, StartedUtc = oldWorker.StartTime.ToUniversalTime().ToString("O"), EventsPath = events };
                    Require(AppPersistence.LegacyEngineIsAlive(JsonSerializer.SerializeToElement(engine)), "Legacy worker fixture must be alive");
                    WriteLegacySession(data, new[] { "https://example.com/active-worker" }, directory, engine);
                    string before = File.ReadAllText(data.SessionPath);
                    using var writer = new FileStream(events, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    var restored = data.Restore()!;
                    Require(restored.LegacyEngineWasActive && AppPersistence.LegacyEngineIsAlive(restored.Engine),
                        "Active engine identity must reach the GUI migration guard");
                    oldWorker.Kill(true);
                    oldWorker.WaitForExit();
                    Require(restored.LegacyEngineWasActive && !AppPersistence.LegacyEngineIsAlive(restored.Engine),
                        "The migration block must remain latched if the worker exits before the GUI checks the snapshot");
                    Require(File.ReadAllText(data.SessionPath) == before, "Blocked recovery must leave the saved checkpoint unchanged");
                }
                finally { if (!oldWorker.HasExited) oldWorker.Kill(true); oldWorker.WaitForExit(); }
                return Task.CompletedTask;
            });

            await Check("zero exit without validated final media never publishes files or history", async directory =>
            {
                string external = Path.Combine(directory, "unrelated-user-file.mp4");
                File.WriteAllText(external, "PRESERVE");
                foreach (string scenario in new[] { "no-media", "unreported-media", "missing-media", "external-media" })
                {
                    var data = new AppPersistence(Path.Combine(directory, scenario, "profile"));
                    var options = Options(Path.Combine(directory, scenario));
                    options.Cookies = external; // Passed unchanged to the offline fixture as the outside sentinel path.
                    var row = new DownloadItem { Url = FixtureBase + scenario };
                    await using (var engine = new DownloadCoordinator(TestExecutable(), data, options, _ => { }, _ => { }))
                    {
                        engine.Add(row);
                        await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    Require(row.Status == "Failed", $"Unvalidated media must not be marked Completed: {scenario}");
                    Require(row.Path == "" && row.OwnedPaths.Count == 0 && !data.ReadHistory().ContainsKey(row.Url),
                        "A skipped/invalid download must not claim ownership or enter successful history");
                    Require(!Directory.GetFiles(options.Output, "*.json", SearchOption.AllDirectories).Any() &&
                        !Directory.GetFiles(options.Output, "*.mp4", SearchOption.AllDirectories).Any(),
                        "Unvalidated media and metadata must not remain published");
                    Require(File.ReadAllText(external) == "PRESERVE", "Invalid media reports must never touch an outside file");
                }
            });

            await Check("transfer totals include counter resets without double-counting repeated samples", async directory =>
            {
                var data = new AppPersistence(Path.Combine(directory, "profile"));
                var row = new DownloadItem { Url = FixtureBase + "progress-reset" };
                await using (var engine = new DownloadCoordinator(TestExecutable(), data, Options(directory), _ => { }, _ => { }))
                {
                    engine.Add(row);
                    await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                Require(row.Status == "Completed" && File.Exists(row.Path), "Progress fixture must complete a real subprocess and publication");
                Require(row.TransferredBytes == 220, "100 video + 50 audio + 70 retried video bytes must total 220");
            });

            await Check("incomplete cached preview is rejected before it reaches WPF", directory =>
            {
                var data = new AppPersistence(directory);
                const string url = "https://example.com/incomplete-cache";
                using var previews = new PreviewService("unused", data, () => "", _ => { }, _ => { });
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                string file = Path.Combine(data.PreviewDir, hash + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { CacheVersion = 1, Url = url, SavedAtUtc = DateTime.UtcNow, Title = "Missing required string fields" }));
                Require(previews.Get(url) == null, "Partial cache records must not return null strings to WPF rendering");
                Require(!File.Exists(file), "Malformed cache must be evicted so a future request can refetch it");
                return Task.CompletedTask;
            });

            await Check("preview cache TTL uses UTC across timestamp offsets", directory =>
            {
                var data = new AppPersistence(directory);
                using var previews = new PreviewService("unused", data, () => "", _ => { }, _ => { });
                foreach (int hours in new[] { 71, 73 })
                {
                    string url = "https://example.com/offset-cache-" + hours;
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                    string file = Path.Combine(data.PreviewDir, hash + ".json");
                    File.WriteAllText(file, JsonSerializer.Serialize(new {
                        CacheVersion = 1, Url = url,
                        SavedAtUtc = DateTimeOffset.UtcNow.AddHours(-hours).ToOffset(TimeSpan.FromHours(6)).ToString("O"),
                        Title = "Cached", Uploader = "", PlaylistTitle = "", Extractor = "", Codecs = "",
                        DynamicRange = "", ThumbnailBase64 = "", Duration = 0 }));
                    var cached = previews.Get(url);
                    Require((cached != null) == (hours < 72), "Cache age must use elapsed UTC time, not local wall time");
                    if (cached != null)
                        Require(cached.SavedAtUtc.Kind == DateTimeKind.Utc &&
                            Math.Abs((DateTime.UtcNow - cached.SavedAtUtc).TotalHours - hours) < .01,
                            "Cached timestamps must be normalized to UTC before memory reuse");
                    else Require(!File.Exists(file), "Expired offset cache must be evicted");
                }
                return Task.CompletedTask;
            });

            await Check("permanent HTTP errors do not retry while transient HTTP/TLS errors do", async directory =>
            {
                foreach (var (name, expectedRetry) in new[] { ("permanent", false), ("transient", true), ("tls", true) })
                {
                    var data = new AppPersistence(Path.Combine(directory, name, "profile"));
                    var row = new DownloadItem { Url = FixtureBase + "error-" + name };
                    bool retried = false;
                    DownloadCoordinator? coordinator = null;
                    await using (coordinator = new DownloadCoordinator(TestExecutable(), data, Options(Path.Combine(directory, name)), _ => { }, message =>
                    {
                        if (message.StartsWith("Transient error on ", StringComparison.Ordinal))
                        {
                            retried = true;
                            coordinator!.Stop(); // Observe the decision without waiting through retry backoff.
                        }
                    }))
                    {
                        coordinator.Add(row);
                        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    Require(retried == expectedRetry, $"Incorrect retry classification for {name}");
                    Require(row.Status == (expectedRetry ? "Interrupted" : "Failed"), "Retry cancellation/permanent failure must retain its distinct result");
                }
            });

            if (failures.Count > 0) throw new AggregateException("Native review regression failures", failures);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WriteLegacySession(AppPersistence data, string[] urls, string output, object engine) =>
        AppPersistence.AtomicWrite(data.SessionPath, JsonSerializer.Serialize(new {
            Version = 1,
            Queue = urls.Select(url => new { Url = url, Status = "Running" }),
            Settings = new { Output = output }, Engine = engine }));

    private static DownloadOptions Options(string directory) => new() {
        Output = Path.Combine(directory, "downloads"), Threads = "1", EmbedMetadata = false, EmbedChapters = false };

    private static string TestExecutable()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            typeof(ReviewRegressionTests).Assembly.GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(path), "Test apphost is required for offline subprocess fixtures");
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
