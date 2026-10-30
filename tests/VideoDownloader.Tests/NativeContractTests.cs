using System.Diagnostics;
using System.IO;
using System.Text.Json;
using VideoDownloader.Native;

// Observe the actual managed child boundary, rather than calling private argument
// builders or searching source text. Media conversion itself is covered by the
// bundled yt-dlp integration suite; this fixture records the request it receives.
internal static class NativeContractTests
{
    private const string Prefix = "https://native-contract.invalid/";
    private const string LogVariable = "VD_NATIVE_CONTRACT_LOG";

    internal static bool TryRunFixture(string[] args)
    {
        string? url = args.LastOrDefault();
        if (url?.StartsWith(Prefix, StringComparison.Ordinal) != true) return false;
        string directory = Environment.GetEnvironmentVariable(LogVariable)
            ?? throw new InvalidOperationException("Contract fixture requires its isolated log directory.");
        string name = url[Prefix.Length..];
        Require(name.Length > 0 && name.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'), "Unexpected fixture URL");
        File.AppendAllText(Path.Combine(directory, name + ".jsonl"), JsonSerializer.Serialize(args) + "\n");
        string? selector = OptionalValue(args, "-f");
        if (name == "quality-retry" && (selector?.Contains("height<=2160", StringComparison.Ordinal) == true ||
            selector?.Contains("height<=1440", StringComparison.Ordinal) == true))
        {
            Console.Error.WriteLine("ERROR: Requested format is not available");
            Environment.ExitCode = 1;
            return true;
        }
        string stage = Path.GetDirectoryName(Value(args, "-o"))!;
        Directory.CreateDirectory(stage);
        string extension = args.Contains("-x", StringComparer.Ordinal) ? "mp3" : OptionalValue(args, "--remux-video") ?? "mp4";
        string media = Path.Combine(stage, "contract." + extension);
        File.WriteAllText(media, "OFFLINE CONTRACT MEDIA");
        Console.WriteLine("__VD_TITLE__:Contract fixture");
        Console.WriteLine("__VD_FILE__:" + media);
        return true;
    }

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-native-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? previousLog = Environment.GetEnvironmentVariable(LogVariable);
        Environment.SetEnvironmentVariable(LogVariable, root);
        try
        {
            await ArgumentContracts(root);
            if (OperatingSystem.IsWindows()) JunctionBoundaries(root);
            Console.WriteLine("PASS: native yt-dlp argument contracts and output/staging reparse boundaries");
        }
        finally
        {
            Environment.SetEnvironmentVariable(LogVariable, previousLog);
            Directory.Delete(root, true);
        }
    }

    private static async Task ArgumentContracts(string root)
    {
        async Task<string[][]> Run(string name, DownloadOptions options)
        {
            options.Output = Path.Combine(root, name, "output");
            options.Threads = "1";
            var data = new AppPersistence(Path.Combine(root, name, "profile"));
            var row = new DownloadItem { Url = Prefix + name };
            await using (var engine = new DownloadCoordinator(TestExecutable(), data, options, _ => { }, _ => { }))
            {
                Require(engine.Add(row), "Contract fixture row must be accepted: " + name);
                await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
            }
            Require(row.Status == "Completed" && File.Exists(row.Path) && data.ReadHistory().ContainsKey(row.Url),
                "Argument fixture must complete actual worker, publication and history: " + name + " / " + row.RawError);
            return File.ReadAllLines(Path.Combine(root, name + ".jsonl"))
                .Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToArray();
        }

        var codecCases = new[] { ("Auto", ""), ("H264", "avc1"), ("VP9", "vp9"), ("AV1", "av01") };
        await Task.WhenAll(codecCases.Select(async item =>
        {
            var calls = await Run("codec-" + item.Item1, new DownloadOptions {
                Quality = "1080", Codec = item.Item1, EmbedMetadata = false, EmbedChapters = false });
            Require(calls.Length == 1, "Available quality must not trigger extra requests");
            string[] args = calls[0];
            CommonArguments(args);
            string generic = "bv*[height<=1080]+ba/b[height<=1080]";
            string expected = item.Item2.Length == 0 ? generic :
                "bv*[height<=1080][vcodec^=" + item.Item2 + "]+ba/b[height<=1080][vcodec^=" + item.Item2 + "]/" + generic;
            Require(Value(args, "-f") == expected,
                "Codec preference must retain generic fallbacks under the same quality bound: " + item.Item1);
            Require(!args.Contains("--enable-file-urls", StringComparer.Ordinal), "HTTP downloads must not enable local file URLs");
        }));

        var qualityCalls = await Run("quality-retry", new DownloadOptions {
            Quality = "2160", Codec = "Auto", EmbedMetadata = false, EmbedChapters = false });
        Require(qualityCalls.Select(args => Value(args, "-f")).SequenceEqual(new[] {
            "bv*[height<=2160]+ba/b[height<=2160]", "bv*[height<=1440]+ba/b[height<=1440]",
            "bv*[height<=1080]+ba/b[height<=1080]" }),
            "Unavailable format must step down quality, stop on success and retain the selected upper bound");

        string cookies = Path.Combine(root, "cookies with spaces.txt");
        File.WriteAllText(cookies, "# Netscape HTTP Cookie File\n");
        const string template = "nested folder/%(title)s [%(id)s].%(ext)s";
        var enabled = (await Run("enabled-options", new DownloadOptions {
            FilenameTemplate = template, Cookies = cookies, RateLimit = "5M", Fragments = "7", Sponsor = true,
            WriteSubs = true, AutoSubs = true, EmbedSubs = true, SubtitleLangs = "ru.*,en.*",
            EmbedThumbnail = true, EmbedMetadata = true, EmbedChapters = true, Container = "MKV" }))[0];
        CommonArguments(enabled);
        Require(Value(enabled, "--cookies") == cookies && Value(enabled, "--limit-rate") == "5M" &&
            Value(enabled, "-N") == "7" && Value(enabled, "--sponsorblock-remove") == "sponsor",
            "Cookies, rate, fragment concurrency and SponsorBlock values must reach the child unchanged");
        Require(Value(enabled, "-o").EndsWith(Path.DirectorySeparatorChar + template, StringComparison.Ordinal),
            "Nested filename template, spaces and yt-dlp substitutions must survive the process argument boundary");
        foreach (string flag in new[] { "--write-subs", "--write-auto-subs", "--embed-subs", "--write-thumbnail",
            "--embed-thumbnail", "--embed-metadata", "--embed-chapters" })
            Require(enabled.Count(arg => arg == flag) == 1, "Requested optional flag must occur exactly once: " + flag);
        Require(Value(enabled, "--sub-langs") == "ru.*,en.*" && Value(enabled, "--merge-output-format") == "mkv" &&
            Value(enabled, "--remux-video") == "mkv", "Subtitle language and matching merge/remux containers must reach yt-dlp");

        var automaticSubs = (await Run("automatic-subs", new DownloadOptions {
            AutoSubs = true, SubtitleLangs = "ja.*,en", EmbedMetadata = false, EmbedChapters = false }))[0];
        Require(automaticSubs.Contains("--write-auto-subs", StringComparer.Ordinal) &&
            !automaticSubs.Contains("--write-subs", StringComparer.Ordinal) &&
            Value(automaticSubs, "--sub-langs") == "ja.*,en", "Automatic-only subtitles still need their selected language filter");

        var embeddedSubs = (await Run("embedded-subs", new DownloadOptions {
            EmbedSubs = true, WriteSubs = false, EmbedMetadata = false, EmbedChapters = false }))[0];
        Require(embeddedSubs.Contains("--write-subs", StringComparer.Ordinal) &&
            embeddedSubs.Contains("--embed-subs", StringComparer.Ordinal), "Embedding subtitles must also request subtitle download");

        var disabled = (await Run("disabled-options", new DownloadOptions {
            Cookies = Path.Combine(root, "missing-cookies.txt"), RateLimit = "Unlimited", Fragments = "Auto",
            EmbedMetadata = false, EmbedChapters = false }))[0];
        foreach (string flag in new[] { "--cookies", "--limit-rate", "--sponsorblock-remove", "--write-subs", "--write-auto-subs",
            "--sub-langs", "--embed-subs", "--write-thumbnail", "--embed-thumbnail", "--embed-metadata", "--embed-chapters",
            "-x", "--audio-format", "--merge-output-format", "--remux-video" })
            Require(!disabled.Contains(flag, StringComparer.Ordinal), "Disabled option must stay absent: " + flag);
        Require(Value(disabled, "-N") == "1" && Value(disabled, "-f") == "bv*+ba/b",
            "Automatic fragments follow the worker count; Best adds no arbitrary height limit");

        var music = new DownloadOptions { Container = "MKV" };
        ProfileCatalog.Apply(music, "Music MP3");
        var audio = (await Run("music-profile", music))[0];
        Require(audio.Contains("-x", StringComparer.Ordinal) && Value(audio, "--audio-format") == "mp3" &&
            audio.Contains("--embed-thumbnail", StringComparer.Ordinal) && audio.Contains("--embed-metadata", StringComparer.Ordinal),
            "Music profile must extract MP3 and preserve its artwork/metadata options");
        Require(!audio.Contains("-f", StringComparer.Ordinal) && !audio.Contains("--merge-output-format", StringComparer.Ordinal) &&
            !audio.Contains("--remux-video", StringComparer.Ordinal), "Audio extraction must not receive video format/container arguments");
    }

    private static void CommonArguments(string[] args)
    {
        foreach (string flag in new[] { "--ignore-config", "--newline", "--progress", "--no-color", "--no-quiet" })
            Require(args.Contains(flag, StringComparer.Ordinal), "Missing structured worker contract: " + flag);
        Require(Value(args, "--encoding") == "utf-8", "Worker pipe output must request UTF-8");
        Require(Value(args, "--progress-template").StartsWith("download:__VD_PROGRESS__|", StringComparison.Ordinal),
            "Progress must use the machine-readable marker");
        var printValues = args.Select((value, index) => (value, index)).Where(pair => pair.value == "--print")
            .Select(pair => args[pair.index + 1]).ToArray();
        Require(printValues.SequenceEqual(new[] { "before_dl:__VD_TITLE__:%(title)s",
            "before_dl:__VD_PLAN__:%(.{id,format_id,requested_formats.:.format_id,playlist_autonumber,n_entries})j",
            "after_move:__VD_FILE__:%(filepath)s" }),
            "Title, component plan and final media path must remain distinct machine-readable events without dumping media URLs");
    }

    private static void JunctionBoundaries(string root)
    {
        string boundary = Path.Combine(root, "boundaries");
        string outside = Path.Combine(boundary, "outside");
        string output = Path.Combine(boundary, "output");
        string stage = Path.Combine(boundary, Guid.NewGuid().ToString("N"));
        foreach (string path in new[] { outside, output, stage }) Directory.CreateDirectory(path);
        string sentinel = Path.Combine(outside, "protected.mp4");
        string existing = Path.Combine(output, "existing.mp4");
        string media = Path.Combine(stage, "new.mp4");
        File.WriteAllText(sentinel, "OUTSIDE SENTINEL");
        File.WriteAllText(existing, "PREEXISTING SENTINEL");
        File.WriteAllText(media, "STAGED MEDIA");
        string rootLink = Path.Combine(boundary, "root-is-junction");
        string stageLink = Path.Combine(stage, "nested-junction");
        try
        {
            Junction(rootLink, outside);
            Refuses(() => OwnedFiles.DeleteOwned(rootLink, [Path.Combine(rootLink, "protected.mp4")]),
                "Output root itself must not authorize deletion through a junction");
            Refuses(() => OwnedFiles.Publish(stage, rootLink, media), "Output root junction must reject publication");
            Require(!File.Exists(Path.Combine(outside, "new.mp4")), "Root junction must never redirect publication outside output");

            Junction(stageLink, outside);
            Refuses(() => OwnedFiles.Publish(stage, output, media), "Nested staging junction must not import outside files");
            Require(File.Exists(media) && !File.Exists(Path.Combine(output, "new.mp4")) &&
                !File.Exists(Path.Combine(output, "nested-junction", "protected.mp4")),
                "Unsafe staging must be rejected before any media or sidecar moves");
            Refuses(() => OwnedFiles.DeleteStage(stage, boundary), "Known staging folder containing a junction must reject recursive removal");
            Require(File.ReadAllText(sentinel) == "OUTSIDE SENTINEL" && File.ReadAllText(existing) == "PREEXISTING SENTINEL",
                "Every reparse refusal must preserve outside and pre-existing sentinels byte for byte");
        }
        finally
        {
            // Remove reparse points as links before recursive test-root cleanup.
            if (Directory.Exists(stageLink)) Directory.Delete(stageLink);
            if (Directory.Exists(rootLink)) Directory.Delete(rootLink);
        }
    }

    private static void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        string stdout = process.StandardOutput.ReadToEnd();
        string stderr = process.StandardError.ReadToEnd();
        Require(process.WaitForExit(10000) && process.ExitCode == 0 &&
            (File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0,
            "Mandatory NTFS junction fixture creation failed: " + stdout + stderr);
    }

    private static string? OptionalValue(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static string Value(string[] args, string flag) => OptionalValue(args, flag)
        ?? throw new InvalidOperationException("Missing argument value: " + flag);
    private static string TestExecutable()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            typeof(NativeContractTests).Assembly.GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(path), "Native test apphost is required for contract subprocesses");
        return path;
    }
    private static void Refuses(Action action, string message)
    {
        try { action(); }
        catch (IOException) { return; }
        catch (ArgumentException) { return; }
        throw new InvalidOperationException(message);
    }
    private static void Require(bool value, string message)
    {
        if (!value) throw new InvalidOperationException(message);
    }
}
