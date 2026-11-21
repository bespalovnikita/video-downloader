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
        if (id.StartsWith("paired-", StringComparison.Ordinal)) return RunHttpsFixture(args, id, output);
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

    private static bool RunHttpsFixture(string[] args, string id, string output)
    {
        string format = args[Array.IndexOf(args, "-f") + 1];
        File.WriteAllText(output.Replace("%(ext)s", "mp4.part"), "temporary media");
        if (!args.Contains("--downloader-args"))
        {
            if (id == "paired-4")
            {
                Console.Error.WriteLine("ERROR: Requested format is not available.");
                Environment.ExitCode = 1;
                return true;
            }
            Require(format == "best[protocol=https]", "The paired baseline must select only direct HTTPS flows.");
            Console.WriteLine($"__VD_SPEED_HTTPS__|\"{id}\"");
            Console.WriteLine($"__VD_SPEED__|{id}|{id}|100");
            return true;
        }
        Require(id != "paired-4" && format == $"best[protocol=https][format_id={id}]",
            "aria2c must use the selected native format and skip incompatible samples.");
        string options = args[Array.IndexOf(args, "--downloader-args") + 1];
        Require(options.Contains("-x4 -s4") && options.Contains("--human-readable=false") && options.Contains("--summary-interval=1"),
            "Use four connections and flush exact byte counts for partial transfers.");
        if (id == "paired-1")
        {
            Console.Write("\r[#123abc 200B/400B(50%) CN:4 DL:100B]\r");
            Console.Out.Flush();
            Thread.Sleep(Timeout.Infinite);
        }
        else if (id == "paired-2")
            Console.WriteLine("Download Results:");
        Console.WriteLine($"__VD_SPEED__|{id}|{id}|300");
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

            string[] httpsUrls = Enumerable.Range(1, 4).Select(index => Prefix + "paired-" + index).ToArray();
            var pair = await SpeedDebugBenchmark.RunAsync(httpsUrls, options, null, CancellationToken.None,
                Environment.ProcessPath!, TimeSpan.FromSeconds(12), stageParent, aria2: Environment.ProcessPath!);
            Require(pair.Count == 2 && pair[0] is { Kind: SpeedDebugKind.HttpsNative, EligibleUrls: 3, MeasuredUrls: 3, FailedUrls: 0, Bytes: 300 },
                "Native comparison must keep the compatible subset.");
            Require(pair[1] is { Kind: SpeedDebugKind.HttpsAria2, EligibleUrls: 3, MeasuredUrls: 2, FailedUrls: 1, Bytes: 500 },
                "aria2 must count partial and finished transfers without crediting native fallback.");
            Require(!Directory.EnumerateFileSystemEntries(stageParent).Any(), "Paired stages must also be cleaned up.");
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
