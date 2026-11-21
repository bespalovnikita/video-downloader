using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal sealed record SpeedDebugUpdate(int ProfileIndex, int ProfileCount, string Profile, TimeSpan Elapsed);
internal enum SpeedDebugKind { Regular, HttpsNative, HttpsAria2 }

internal sealed record SpeedDebugResult(string Profile, int Parallel, int Fragments, long Bytes, TimeSpan Duration,
    int MeasuredUrls, int FailedUrls, string? Error, bool Chunked, SpeedDebugKind Kind = SpeedDebugKind.Regular,
    int EligibleUrls = SpeedDebugBenchmark.SampleCount)
{
    internal double BytesPerSecond => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds : 0;
}

internal static class SpeedDebugBenchmark
{
    internal const int SampleCount = 4;
    internal static readonly TimeSpan Budget = TimeSpan.FromMinutes(1);
    private const string ProgressMarker = "__VD_SPEED__|";
    private const string FormatMarker = "__VD_SPEED_HTTPS__|";
    private const int MaxPipeChars = 256 * 1024;
    private static readonly Regex AriaProgress = new(@"^\[#[a-f0-9]+ (?<bytes>[0-9]+)B/(?:[0-9]+B|0)(?:\(|\s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    internal static bool Aria2Available => File.Exists(DependencyCenter.Executable("aria2c.exe"));

    private sealed record Profile(string Name, int Parallel, int Fragments, bool Chunked = false,
        SpeedDebugKind Kind = SpeedDebugKind.Regular);
    private sealed record Sample(long Bytes, string? Error, string? HttpsFormat = null);

    internal static SpeedDebugResult? SelectDefaults(IReadOnlyList<SpeedDebugResult> results, bool compareAria2)
    {
        if (!compareAria2)
            return results.Where(row => row.Kind == SpeedDebugKind.Regular && row.FailedUrls == 0 &&
                    row.MeasuredUrls == SampleCount && row.BytesPerSecond > 0)
                .MaxBy(row => row.BytesPerSecond);

        var native = results.FirstOrDefault(row => row.Kind == SpeedDebugKind.HttpsNative);
        var aria2 = results.FirstOrDefault(row => row.Kind == SpeedDebugKind.HttpsAria2);
        if (native is not { EligibleUrls: > 0, FailedUrls: 0, BytesPerSecond: > 0 } ||
            aria2 is not { FailedUrls: 0, BytesPerSecond: > 0 } ||
            native.EligibleUrls != aria2.EligibleUrls || native.MeasuredUrls != native.EligibleUrls ||
            aria2.MeasuredUrls != aria2.EligibleUrls) return null;
        return aria2.BytesPerSecond > native.BytesPerSecond ? aria2 : native;
    }

    internal static Task<IReadOnlyList<SpeedDebugResult>> RunAsync(IReadOnlyList<string> urls, DownloadOptions currentOptions,
        IProgress<SpeedDebugUpdate>? progress, CancellationToken ct, string? executable = null, bool compareAria2 = false) =>
        RunAsync(urls, currentOptions, progress, ct, executable, Budget, Path.GetTempPath(),
            compareAria2 ? DependencyCenter.Executable("aria2c.exe") : null);

    internal static async Task<IReadOnlyList<SpeedDebugResult>> RunAsync(IReadOnlyList<string> urls, DownloadOptions currentOptions,
        IProgress<SpeedDebugUpdate>? progress, CancellationToken ct, string? executable, TimeSpan budget, string workParent,
        string? aria2 = null)
    {
        if (urls.Count != SampleCount || urls.Distinct(StringComparer.Ordinal).Count() != SampleCount ||
            urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Provide four distinct HTTP(S) video URLs.", nameof(urls));

        ct.ThrowIfCancellationRequested();
        string ytDlp = executable ?? DependencyCenter.Executable("yt-dlp.exe");
        if (!File.Exists(ytDlp)) throw new FileNotFoundException("Не найден yt-dlp.exe.", ytDlp);
        if (aria2 is not null && !File.Exists(aria2))
            throw new FileNotFoundException(ErrorMessages.Text("Не найден aria2c.exe.", "aria2c.exe was not found."));
        if (aria2 is not null) aria2 = Path.GetFullPath(aria2);
        var options = currentOptions.Snapshot();
        string? format = null;
        if (aria2 is null)
        {
            string quality = new YtDlpArguments(options).QualityLevels().First();
            format = AudioTrackSelection.FormatSelector(options, quality);
            if (options.AudioOnly && format is null) format = "bestaudio/best";
        }

        int parallel = Math.Min(options.Parallel, SampleCount);
        int fragments = options.FragmentCount;
        int alternateFragments = fragments == 8 ? 2 : 8;
        int alternateParallel = parallel == SampleCount ? 2 : SampleCount;
        Profile[] profiles = aria2 is not null
        ?
        [
            new(ErrorMessages.Text("Прямой HTTPS · yt-dlp", "Direct HTTPS · yt-dlp"), SampleCount, 1, options.UseHttpChunks, SpeedDebugKind.HttpsNative),
            new(ErrorMessages.Text("Прямой HTTPS · aria2c", "Direct HTTPS · aria2c"), SampleCount, 1, Kind: SpeedDebugKind.HttpsAria2)
        ]
        :
        [
            new(ErrorMessages.Text("Текущие параметры", "Current settings"), parallel, fragments, options.UseHttpChunks),
            new(ErrorMessages.Text("Другое число фрагментов", "Alternate fragment count"), parallel, alternateFragments, options.UseHttpChunks),
            new(ErrorMessages.Text("Другая параллельность", "Alternate URL concurrency"), alternateParallel, 8, options.UseHttpChunks),
            new(options.UseHttpChunks?ErrorMessages.Text("HTTP без блоков", "HTTP without chunking"):"HTTP --http-chunk-size 4M",
                SampleCount, 8, Chunked: !options.UseHttpChunks)
        ];

        var results = new List<SpeedDebugResult>();
        var httpsFormats = new Dictionary<string, string>(StringComparer.Ordinal);
        // Reuse the staging ownership checks without touching the user's output or queue.
        string stage = Path.Combine(workParent, Guid.NewGuid().ToString("N"));
        OwnedFiles.NoReparseAncestors(Path.Combine(stage, "probe"));
        Directory.CreateDirectory(stage);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var clock = Stopwatch.StartNew();
        deadline.CancelAfter(budget);
        var slice = budget / profiles.Length;
        try
        {
            for (int index = 0; index < profiles.Length && !deadline.IsCancellationRequested; index++)
            {
                var profile = profiles[index];
                progress?.Report(new(index + 1, profiles.Length, profile.Name, clock.Elapsed));
                var candidates = profile.Kind == SpeedDebugKind.HttpsAria2 ? urls.Where(httpsFormats.ContainsKey) : urls;
                var waves = candidates.Chunk(profile.Parallel).ToArray();
                var samples = new List<Sample>();
                TimeSpan remaining = budget - clock.Elapsed;
                TimeSpan window = remaining < slice ? remaining : slice;
                if (window <= TimeSpan.Zero) break;
                var measurement = Stopwatch.StartNew();
                using var profileDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                profileDeadline.CancelAfter(window);

                for (int wave = 0; wave < waves.Length && !profileDeadline.IsCancellationRequested; wave++)
                {
                    var left = window - measurement.Elapsed;
                    if (left <= TimeSpan.Zero) break;
                    // Give each wave a share of the remaining profile window so
                    // slow first URLs cannot use all of a lower-concurrency profile.
                    using var waveDeadline = CancellationTokenSource.CreateLinkedTokenSource(profileDeadline.Token);
                    waveDeadline.CancelAfter(left / (waves.Length - wave));
                    var batch = await Task.WhenAll(waves[wave].Select(async url =>
                    {
                        string? selector = profile.Kind switch
                        {
                            SpeedDebugKind.HttpsNative => "best[protocol=https]",
                            SpeedDebugKind.HttpsAria2 => $"best[protocol=https][format_id={httpsFormats[url]}]",
                            _ => format
                        };
                        var sample = await MeasureAsync(ytDlp, options, selector, profile, url, stage, waveDeadline.Token, aria2)
                            .ConfigureAwait(false);
                        return (url, sample);
                    })).ConfigureAwait(false);
                    foreach (var (url, sample) in batch)
                    {
                        samples.Add(sample);
                        if (profile.Kind == SpeedDebugKind.HttpsNative && sample.HttpsFormat is { } id)
                            httpsFormats.Add(url, id);
                    }
                }

                TimeSpan duration = measurement.Elapsed;
                if (profile.Kind != SpeedDebugKind.Regular && duration > window) duration = window;
                ct.ThrowIfCancellationRequested();
                int measured = samples.Count(sample => sample.Bytes > 0);
                int failed = samples.Count(sample => sample.Error is not null);
                results.Add(new(profile.Name, profile.Parallel, profile.Fragments, samples.Sum(sample => sample.Bytes),
                    duration, measured, failed, samples.FirstOrDefault(sample => sample.Error is not null)?.Error, profile.Chunked,
                    profile.Kind, profile.Kind == SpeedDebugKind.Regular ? urls.Count : httpsFormats.Count));
            }
            ct.ThrowIfCancellationRequested();
            return aria2 is null ? results.OrderByDescending(result => result.BytesPerSecond).ToArray() : results;
        }
        finally
        {
            OwnedFiles.DeleteStage(stage, workParent);
        }
    }

    private static async Task<Sample> MeasureAsync(string ytDlp, DownloadOptions options, string? format, Profile profile,
        string url, string stage, CancellationToken token, string? aria2)
    {
        string target = Path.Combine(stage, Guid.NewGuid().ToString("N"));
        try
        {
            if (token.IsCancellationRequested) return new(0, null);
            OwnedFiles.NoReparseAncestors(Path.Combine(target, "probe"));
            Directory.CreateDirectory(target);
            var start = new ProcessStartInfo(ytDlp)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = AppContext.BaseDirectory
            };
            if (profile.Kind != SpeedDebugKind.Regular)
            {
                // The external downloader can create a temporary cookie jar.
                start.Environment["TEMP"] = target;
                start.Environment["TMP"] = target;
                start.Environment["TMPDIR"] = target;
            }
            foreach (string arg in BuildArguments(options, format, profile, url, target, aria2)) start.ArgumentList.Add(arg);
            if (token.IsCancellationRequested) return new(0, null);

            using var child = ManagedChildProcess.Start(start);
            using var cancel = token.Register(child.Terminate);
            var stdout = ReadBoundedAsync(child.StandardOutput, child.Terminate);
            var stderr = ReadBoundedAsync(child.StandardError, child.Terminate);
            await Task.WhenAll(stdout, stderr, child.Process.WaitForExitAsync()).ConfigureAwait(false);

            long bytes = MediaBytes(stdout.Result);
            string? formatId = profile.Kind == SpeedDebugKind.HttpsNative ? HttpsFormat(stdout.Result) : null;
            string? error = null;
            if (stdout.Result.Length >= MaxPipeChars || stderr.Result.Length >= MaxPipeChars)
                error = ErrorMessages.Text("Превышен лимит диагностического вывода.", "Diagnostic output limit exceeded.");
            else
            {
                string? detail = (stderr.Result + "\n" + stdout.Result).Split('\n')
                    .FirstOrDefault(line => line.StartsWith("ERROR:", StringComparison.OrdinalIgnoreCase));
                if (detail != null) error = JournalSanitizer.Redact(detail.Trim());
                else if (!token.IsCancellationRequested && child.Process.ExitCode != 0)
                    error = $"yt-dlp exit {child.Process.ExitCode}";
            }
            if (profile.Kind == SpeedDebugKind.HttpsNative)
            {
                if (error is not null && YtDlpOutputParser.FormatUnavailable(error)) return new(0, null);
                if (bytes > 0 && formatId is null)
                    return new(0, ErrorMessages.Text("Не удалось зафиксировать формат HTTPS.", "Could not pin the HTTPS format."));
            }
            if (profile.Kind == SpeedDebugKind.HttpsAria2)
            {
                long partial = AriaBytes(stdout.Result);
                string? ariaError = stdout.Result.Split(['\r', '\n'])
                    .FirstOrDefault(line => line.Contains("[ERROR]", StringComparison.Ordinal));
                if (ariaError is not null && (error is null || error.StartsWith("ERROR: aria2c exited", StringComparison.Ordinal)))
                    error = JournalSanitizer.Redact(ariaError.Trim());
                // yt-dlp can fall back to its own downloader when aria2 fails
                // the executable check or the format requires impersonation.
                bool ariaRan = partial > 0 || stdout.Result.Contains("Download Results:", StringComparison.Ordinal);
                if (!ariaRan && bytes > 0)
                    error ??= ErrorMessages.Text("yt-dlp не подтвердил запуск aria2c.", "yt-dlp did not confirm aria2c ran.");
                bytes = ariaRan ? Math.Max(bytes, partial) : 0;
            }
            return new(bytes, error, formatId);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return new(0, JournalSanitizer.Redact(error.Message));
        }
        finally
        {
            OwnedFiles.DeleteStage(target, stage);
        }
    }

    private static IEnumerable<string> BuildArguments(DownloadOptions options, string? format, Profile profile,
        string url, string target, string? aria2)
    {
        var args = new List<string>
        {
            "--ignore-config", "--no-cache-dir", "--encoding", "utf-8", "--no-color", "--newline", "--progress",
            "--progress-delta", "0.2", "--no-playlist", "--playlist-items", "1", "--impersonate", "chrome",
            "--no-continue", "--retries", "0", "--fragment-retries", "0", "--abort-on-unavailable-fragments", "--fixup", "never",
            "-N", profile.Fragments.ToString(CultureInfo.InvariantCulture),
            "--progress-template", "download:" + ProgressMarker + "%(info.id)s|%(info.format_id)s|%(progress.downloaded_bytes)s",
            "-o", Path.Combine(target, "video.%(ext)s")
        };
        if (profile.Chunked) args.AddRange(["--http-chunk-size", "4M"]);
        if (format is not null) args.AddRange(["-f", format]);
        if (profile.Kind == SpeedDebugKind.Regular && AudioTrackSelection.Multiple(options)) args.Add("--audio-multistreams");
        if (profile.Kind == SpeedDebugKind.HttpsNative)
            args.AddRange(["--no-simulate", "--print", "before_dl:" + FormatMarker + "%(format_id)j", "--downloader", "http:native"]);
        if (profile.Kind == SpeedDebugKind.HttpsAria2)
            // aria2 flushes periodic summaries when output is redirected.
            args.AddRange(["--downloader", "http:" + aria2, "--downloader-args",
                "aria2c:" + YtDlpArguments.Aria2DownloadOptions +
                " --max-tries=1 --human-readable=false --enable-color=false --show-console-readout=false --summary-interval=1 --download-result=default"]);
        string ffmpeg = DependencyCenter.Executable("ffmpeg.exe");
        if (File.Exists(ffmpeg)) args.AddRange(["--ffmpeg-location", ffmpeg]);
        if (File.Exists(options.Cookies))
        {
            // yt-dlp can update its cookie jar; each probe gets an isolated copy.
            string cookieCopy = Path.Combine(target, "cookies.txt");
            File.Copy(options.Cookies, cookieCopy);
            File.SetAttributes(cookieCopy, FileAttributes.Normal);
            args.AddRange(["--cookies", cookieCopy]);
        }
        args.Add("--");
        args.Add(url);
        return args;
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, Action terminate)
    {
        var output = new StringBuilder();
        char[] buffer = new char[4096];
        try
        {
            while (output.Length < MaxPipeChars)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxPipeChars - output.Length)))
                    .ConfigureAwait(false);
                if (count == 0) return output.ToString();
                output.Append(buffer, 0, count);
            }
        }
        catch (IOException)
        {
            terminate();
            throw;
        }
        terminate();
        return output.ToString();
    }

    internal static long MediaBytes(string output)
    {
        var components = new Dictionary<(string Id, string Format), long>();
        foreach (string line in output.Split('\n'))
        {
            if (!line.StartsWith(ProgressMarker, StringComparison.Ordinal)) continue;
            var fields = line[ProgressMarker.Length..].Split('|');
            if (fields.Length != 3 || !long.TryParse(fields[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out long bytes)
                || bytes <= 0 || bytes > (1L << 40)) continue;
            var key = (fields[0], fields[1]);
            components[key] = Math.Max(components.GetValueOrDefault(key), bytes);
        }
        return components.Values.Sum();
    }

    private static string? HttpsFormat(string output)
    {
        string? line = output.Split('\n').FirstOrDefault(line => line.StartsWith(FormatMarker, StringComparison.Ordinal));
        if (line is null) return null;
        try
        {
            string? id = JsonSerializer.Deserialize<string>(line[FormatMarker.Length..]);
            return id is not null && Regex.IsMatch(id, @"\A[A-Za-z0-9_.-]{1,128}\z", RegexOptions.CultureInvariant) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    internal static long AriaBytes(string output)
    {
        long highest = 0;
        foreach (string line in output.Split(['\r', '\n']))
        {
            var match = AriaProgress.Match(line);
            if (match.Success && long.TryParse(match.Groups["bytes"].ValueSpan, NumberStyles.None, CultureInfo.InvariantCulture,
                out long bytes) && bytes <= (1L << 40))
                highest = Math.Max(highest, bytes);
        }
        return highest;
    }
}
