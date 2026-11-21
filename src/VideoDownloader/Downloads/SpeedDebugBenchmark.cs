using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace VideoDownloader.Native;

internal sealed record SpeedDebugUpdate(int ProfileIndex, int ProfileCount, string Profile, TimeSpan Elapsed);
internal sealed record SpeedDebugResult(string Profile, int Parallel, int Fragments, long Bytes, TimeSpan Duration,
    int MeasuredUrls, int FailedUrls, string? Error, bool Chunked)
{
    internal double BytesPerSecond => Duration.TotalSeconds > 0 ? Bytes / Duration.TotalSeconds : 0;
}

internal static class SpeedDebugBenchmark
{
    internal const int SampleCount = 4;
    internal static readonly TimeSpan Budget = TimeSpan.FromMinutes(1);
    private const string ProgressMarker = "__VD_SPEED__|";
    private const int MaxPipeChars = 256 * 1024;

    private sealed record Profile(string Name, int Parallel, int Fragments, bool Chunked = false);
    private sealed record Sample(long Bytes, string? Error);

    internal static Task<IReadOnlyList<SpeedDebugResult>> RunAsync(IReadOnlyList<string> urls, DownloadOptions currentOptions,
        IProgress<SpeedDebugUpdate>? progress, CancellationToken ct, string? executable = null) =>
        RunAsync(urls, currentOptions, progress, ct, executable, Budget, Path.GetTempPath());

    internal static async Task<IReadOnlyList<SpeedDebugResult>> RunAsync(IReadOnlyList<string> urls, DownloadOptions currentOptions,
        IProgress<SpeedDebugUpdate>? progress, CancellationToken ct, string? executable, TimeSpan budget, string workParent)
    {
        if (urls.Count != SampleCount || urls.Distinct(StringComparer.Ordinal).Count() != SampleCount ||
            urls.Any(url => !Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")))
            throw new ArgumentException("Provide four distinct HTTP(S) video URLs.", nameof(urls));

        ct.ThrowIfCancellationRequested();
        string ytDlp = executable ?? DependencyCenter.Executable("yt-dlp.exe");
        if (!File.Exists(ytDlp)) throw new FileNotFoundException("Не найден yt-dlp.exe.", ytDlp);
        var options = currentOptions.Snapshot();
        string quality = new YtDlpArguments(options).QualityLevels().First();
        string? format = AudioTrackSelection.FormatSelector(options, quality);
        if (options.AudioOnly && format is null) format = "bestaudio/best";

        int parallel = Math.Min(options.Parallel, SampleCount);
        int fragments = options.FragmentCount;
        int alternateFragments = fragments == 8 ? 2 : 8;
        int alternateParallel = parallel == SampleCount ? 2 : SampleCount;
        Profile[] profiles =
        [
            new(ErrorMessages.Text("Текущие параметры", "Current settings"), parallel, fragments),
            new(ErrorMessages.Text("Другое число фрагментов", "Alternate fragment count"), parallel, alternateFragments),
            new(ErrorMessages.Text("Другая параллельность", "Alternate URL concurrency"), alternateParallel, 8),
            new("HTTP --http-chunk-size 4M", SampleCount, 8, Chunked: true)
        ];

        var results = new List<SpeedDebugResult>();
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
                var waves = urls.Chunk(profile.Parallel).ToArray();
                var samples = new List<Sample>();
                var measurement = Stopwatch.StartNew();
                using var profileDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                profileDeadline.CancelAfter(slice);

                for (int wave = 0; wave < waves.Length && !profileDeadline.IsCancellationRequested; wave++)
                {
                    var left = slice - measurement.Elapsed;
                    if (left <= TimeSpan.Zero) break;
                    // Give each wave a share of the remaining profile window so
                    // slow first URLs cannot use all of a lower-concurrency profile.
                    using var waveDeadline = CancellationTokenSource.CreateLinkedTokenSource(profileDeadline.Token);
                    waveDeadline.CancelAfter(left / (waves.Length - wave));
                    var batch = waves[wave].Select(url => MeasureAsync(ytDlp, options, format, profile, url, stage, waveDeadline.Token));
                    samples.AddRange(await Task.WhenAll(batch).ConfigureAwait(false));
                }

                TimeSpan duration = measurement.Elapsed;
                ct.ThrowIfCancellationRequested();
                int measured = samples.Count(sample => sample.Bytes > 0);
                int failed = samples.Count(sample => sample.Error is not null);
                results.Add(new(profile.Name, profile.Parallel, profile.Fragments, samples.Sum(sample => sample.Bytes),
                    duration, measured, failed, samples.FirstOrDefault(sample => sample.Error is not null)?.Error, profile.Chunked));
            }
            ct.ThrowIfCancellationRequested();
            return results.OrderByDescending(result => result.BytesPerSecond).ToArray();
        }
        finally
        {
            OwnedFiles.DeleteStage(stage, workParent);
        }
    }

    private static async Task<Sample> MeasureAsync(string ytDlp, DownloadOptions options, string? format, Profile profile,
        string url, string stage, CancellationToken token)
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
            foreach (string arg in BuildArguments(options, format, profile, url, target)) start.ArgumentList.Add(arg);
            if (token.IsCancellationRequested) return new(0, null);

            using var child = ManagedChildProcess.Start(start);
            using var cancel = token.Register(child.Terminate);
            var stdout = ReadBoundedAsync(child.StandardOutput, child.Terminate);
            var stderr = ReadBoundedAsync(child.StandardError, child.Terminate);
            await Task.WhenAll(stdout, stderr, child.Process.WaitForExitAsync()).ConfigureAwait(false);

            long bytes = MediaBytes(stdout.Result);
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
            return new(bytes, error);
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
        string url, string target)
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
        if (AudioTrackSelection.Multiple(options)) args.Add("--audio-multistreams");
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
}
