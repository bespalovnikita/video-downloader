using System.IO;
using VideoDownloader.Native;

internal static class SpeedDebugBenchmarkTests
{
    private const string Prefix = "https://speed-debug.invalid/";
    private const string Cookie = "original fixture cookie";
    private const string Secret = "private-test-token";

    internal static bool TryRunFixture(string[] args)
    {
        if (args.LastOrDefault() is not { } url || !url.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        string id = url[Prefix.Length..];
        string output = args[Array.IndexOf(args, "-o") + 1];
        string cookies = args[Array.IndexOf(args, "--cookies") + 1];
        File.WriteAllText(output.Replace("%(ext)s", "mp4.part"), "temporary media");
        Require(File.ReadAllText(cookies) == Cookie, "Each process must receive its own cookie copy.");
        File.WriteAllText(cookies, "updated");
        Require(args.Contains("--ignore-config") && args.Contains("--no-playlist") && !args.Contains("--limit-rate"),
            "The test must isolate config, bound playlists and omit the configured speed cap.");
        Console.WriteLine($"__VD_SPEED__|{id}|video|100");
        Console.WriteLine($"__VD_SPEED__|{id}|video|200");
        Console.WriteLine($"__VD_SPEED__|{id}|audio|50");
        if (args.Contains("--http-chunk-size"))
        {
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        if (args[Array.IndexOf(args, "-N") + 1] == "2" && id == "1")
        {
            Console.Error.WriteLine($"ERROR: HTTP Error 403: {url}?token={Secret}");
            Environment.ExitCode = 1;
        }
        return true;
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-speed-test-" + Guid.NewGuid().ToString("N"));
        string stageParent = Directory.CreateDirectory(Path.Combine(root, "stages")).FullName;
        string cookies = Path.Combine(root, "cookies.txt");
        File.WriteAllText(cookies, Cookie);
        try
        {
            string[] urls = Enumerable.Range(1, 4).Select(index => Prefix + index).ToArray();
            var options = new DownloadOptions { Threads = "4", Fragments = "2", Cookies = cookies, RateLimit = "1M" };
            var results = await SpeedDebugBenchmark.RunAsync(urls, options, null, CancellationToken.None,
                Environment.ProcessPath!, TimeSpan.FromSeconds(16), stageParent);
            Require(results.Count == 4 && results.All(row => row.MeasuredUrls == 4 && row.Bytes == 1000),
                "All profiles must measure the same samples, count components once and retain bytes at the time limit.");
            var failure = results.Single(row => row.FailedUrls == 1);
            Require(failure.Error is not null && failure.Error.Contains("403") && !failure.Error.Contains(Secret),
                "A child error must be counted and scrubbed.");
            Require(results.Select(row => row.BytesPerSecond).SequenceEqual(results.Select(row => row.BytesPerSecond).OrderDescending()),
                "Results must be sorted by effective speed.");
            Require(!Directory.EnumerateFileSystemEntries(stageParent).Any() && File.ReadAllText(cookies) == Cookie,
                "Neither timed-out child files nor cookie modifications may remain.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
