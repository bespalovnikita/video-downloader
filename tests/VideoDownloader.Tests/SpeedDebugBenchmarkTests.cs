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
        if (id == "saved-default") return RunSavedDefaultFixture(args, output);
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

    private static bool RunSavedDefaultFixture(string[] args, string output)
    {
        string media = output.Replace("%(ext)s", "mp4");
        string partial = media + ".part";
        Require(args.Contains("--http-chunk-size"), "New downloads must use the stored HTTP tuning.");
        if (args.Contains("--downloader"))
        {
            int cookies = Array.IndexOf(args, "--cookies");
            Require(cookies >= 0 && File.ReadAllText(args[cookies + 1]) == Cookie, "aria2 must get its private cookie copy.");
            File.WriteAllText(args[cookies + 1], "modified copy");
            File.WriteAllText(partial, "possibly sparse range data without a control file");
            Console.Error.WriteLine("ERROR: aria2c exited with code 1");
            Environment.ExitCode = 1;
            return true;
        }
        Require(!File.Exists(partial), "Native fallback must never append to aria2's possibly sparse part.");
        File.WriteAllText(media, "fresh native download");
        Console.WriteLine("__VD_FILE__:" + media);
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
            Require(SpeedDebugBenchmark.SelectDefaults(pair, compareAria2: true) is null, "Incomplete comparison must not be applied.");
            VerifyWinnerPolicy();
            await SavedDefaultsReachDownloadsAsync(root, cookies);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void VerifyWinnerPolicy()
    {
        var regular = new SpeedDebugResult("Current", 4, 8, 256, TimeSpan.FromSeconds(2), 4, 0, null, false);
        var fast = regular with { Bytes = 512, Chunked = true };
        var incomplete = regular with { Bytes = 1024, MeasuredUrls = 3 };
        Require(SpeedDebugBenchmark.SelectDefaults([incomplete, regular, fast], false) == fast,
            "Only successful complete results compete by aggregate speed.");
        var native = regular with { Kind = SpeedDebugKind.HttpsNative, EligibleUrls = 2, MeasuredUrls = 2 };
        var aria = native with { Kind = SpeedDebugKind.HttpsAria2 };
        Require(SpeedDebugBenchmark.SelectDefaults([native, aria], true) == native, "Keep native on a tie.");
        var fasterAria = aria with { Bytes = 512 };
        Require(SpeedDebugBenchmark.SelectDefaults([native, fasterAria], true) == fasterAria,
            "Only a faster aria2 result from the matched subset can replace native.");
    }

    private static async Task SavedDefaultsReachDownloadsAsync(string root, string cookies)
    {
        string profile = Path.Combine(root, "saved-profile");
        var storage = new AppPersistence(profile);
        var options = new DownloadOptions
        {
            Output = Path.Combine(root, "published"), Cookies = cookies, FilenameTemplate = "video.%(ext)s",
            Threads = "4", Fragments = "8", UseHttpChunks = true, PreferAria2c = true
        };
        storage.Save([], options);
        var restored = new AppPersistence(profile).Restore()!.Settings;
        Require(restored.Parallel == 4 && restored.FragmentCount == 8 && restored.UseHttpChunks && restored.PreferAria2c,
            "Benchmark tuning persists across restoring the profile.");
        Require(PortableProfileSettings.From(new DownloadOptions()).Apply(restored) is { UseHttpChunks: true, PreferAria2c: true },
            "Portable imports must preserve the destination device's transport preferences.");
        string tools = Directory.CreateDirectory(Path.Combine(root, "tools")).FullName;
        File.WriteAllText(Path.Combine(tools, "aria2c.exe"), "external executable discovery fixture");
        string? path = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Environment.SetEnvironmentVariable("PATH", tools + Path.PathSeparator + path);
            var row = new DownloadItem { Url = Prefix + "saved-default" };
            await using var coordinator = new DownloadCoordinator(Environment.ProcessPath!, storage, restored, _ => { }, _ => { });
            Require(coordinator.Add(row), "Saved defaults fixture must be admitted.");
            await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
            Require(row.Status == "Completed" && File.ReadAllText(row.Path) == "fresh native download" &&
                row.OwnedPaths.Count == 1 && File.ReadAllText(cookies) == Cookie,
                "Fallback must publish only the fresh native result and keep the original cookie file: " + row.RawError);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", path);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
