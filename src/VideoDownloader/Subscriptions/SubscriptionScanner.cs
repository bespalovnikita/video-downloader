using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace VideoDownloader.Native;

public sealed record SubscriptionScannerOptions
{
    public TimeSpan ScanTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public TimeSpan ChannelScanTimeout { get; init; } = TimeSpan.FromMinutes(15);
    public TimeSpan RecentTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan ChannelRecentTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public int MaxChannelPages { get; init; } = 500;
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public int MaxItems { get; init; } = 10_000;
    public int MaxMetadataBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxFeedBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>Manual collection discovery: full baseline, or a verified newest window with a full fallback.</summary>
public sealed class SubscriptionScanner : IDisposable
{
    public const int RecentWindowSize = 20;
    private readonly string _ytDlpPath;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SubscriptionScannerOptions _limits;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex IncompleteDiagnostic = new(
        @"\bERROR:|\bincomplete\b|\btruncat\w*|\bpartial (?:playlist|collection)|" +
        @"\b(?:unable|failed) to (?:download|extract|retrieve).*(?:page|playlist|entries)|\bgiving up after",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public SubscriptionScanner(string ytDlpPath, HttpClient? httpClient = null, SubscriptionScannerOptions? options = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ytDlpPath);
        _limits = options ?? new();
        if (_limits.ScanTimeout <= TimeSpan.Zero || _limits.ScanTimeout > TimeSpan.FromSeconds(180) ||
            _limits.ChannelScanTimeout<=TimeSpan.Zero || _limits.ChannelScanTimeout>TimeSpan.FromMinutes(15) ||
            _limits.RecentTimeout <= TimeSpan.Zero || _limits.RecentTimeout > TimeSpan.FromSeconds(30) ||
            _limits.ChannelRecentTimeout <= TimeSpan.Zero || _limits.ChannelRecentTimeout > TimeSpan.FromSeconds(45) ||
            _limits.MaxChannelPages is <1 or >500 ||
            _limits.FeedTimeout <= TimeSpan.Zero || _limits.FeedTimeout > TimeSpan.FromSeconds(12) ||
            _limits.MaxItems is < 1 or > 10_000 || _limits.MaxMetadataBytes is < 1 or > 32 * 1024 * 1024 ||
            _limits.MaxFeedBytes is < 1 or > 2 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Лимиты проверки нельзя превышать.");
        _ytDlpPath = ytDlpPath;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SubscriptionScan> ScanAsync(SubscriptionDefinition source, string cookiePath, CancellationToken token,
        IProgress<SubscriptionScanProgress>? progress = null, IReadOnlySet<string>? knownUrls = null, bool forceFull = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ValidUrl(source.Url)) throw new ArgumentException("Источник должен быть HTTP(S) URL.", nameof(source));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool channel=PornHubChannel.ListingUrl(source.Url)!=null;
        deadline.CancelAfter(channel?_limits.ChannelScanTimeout:_limits.ScanTimeout);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            SubscriptionScan? scan = null;
            string metadata = "";
            if (!forceFull && _limits.MaxItems >= RecentWindowSize && source.Initialized && knownUrls is { Count: > 0 } &&
                (channel || IsNewestYouTubeTab(source.Url)))
            {
                ReportProgress(progress, new("RecentWindow", MetadataBytes: 0));
                using var probeDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                probeDeadline.CancelAfter(channel ? _limits.ChannelRecentTimeout : _limits.RecentTimeout);
                try
                {
                    metadata = await EnumerateAsync(source.Url, cookiePath, probeDeadline.Token, progress, recent: true).ConfigureAwait(false);
                    var window = channel ? PornHubChannel.ParseRecentScan(metadata, source.Url, RecentWindowSize, _limits.MaxChannelPages)
                        : ParseRecentYouTubeMetadata(metadata, source.Url);
                    // No overlap means the update may exceed the window. A full
                    // scan must bridge the gap rather than losing older news.
                    if (window.Items.Any(item => knownUrls.Contains(item.Url))) scan = window;
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    // A slow newest-page probe must leave time for the normal
                    // complete fallback and release its own child process first.
                }
                catch (Exception error) when (error is IOException or InvalidDataException or JsonException)
                {
                    deadline.Token.ThrowIfCancellationRequested();
                    Debug.WriteLine("Recent subscription window unavailable; falling back to a complete scan: " + error.Message);
                }
                if (scan is null) ReportProgress(progress, new("FullFallback"));
            }
            if (scan is null)
            {
                ReportProgress(progress, new("Enumerating", MetadataBytes: 0));
                metadata = await EnumerateAsync(source.Url, cookiePath, deadline.Token, progress).ConfigureAwait(false);
            }
            deadline.Token.ThrowIfCancellationRequested();
            long metadataBytes = Encoding.UTF8.GetByteCount(metadata);
            ReportProgress(progress, new("Parsing", MetadataBytes: metadataBytes));
            scan ??= channel?PornHubChannel.ParseScan(metadata,source.Url,_limits.MaxItems,_limits.MaxChannelPages)
                :ParseMetadata(metadata, source.Url, _limits.MaxItems);
            deadline.Token.ThrowIfCancellationRequested();
            ReportProgress(progress, new("Parsing", scan.Items.Count, metadataBytes));
            SubscriptionScan Complete(SubscriptionScan result)
            {
                deadline.Token.ThrowIfCancellationRequested();
                ReportProgress(progress, new("Ready", result.Items.Count, metadataBytes));
                deadline.Token.ThrowIfCancellationRequested();
                return result;
            }
            string feedUrl = string.IsNullOrWhiteSpace(source.FeedUrl) ? scan.FeedUrl : source.FeedUrl;
            if (feedUrl.Length == 0) return Complete(scan);
            string confirmedBy = scan.Scope == SubscriptionScanScope.Full ? "полным обходом" : "окном новых публикаций";
            string usedDiscovery = scan.Scope == SubscriptionScanScope.Full ? "полный обход" : "проверенное окно новых публикаций";
            if (!ValidUrl(feedUrl)) return Complete(scan with { FeedUrl = feedUrl, Warning = $"URL ленты некорректен; использован {usedDiscovery} yt-dlp." });
            using var feedDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            feedDeadline.CancelAfter(_limits.FeedTimeout);
            deadline.Token.ThrowIfCancellationRequested();
            ReportProgress(progress, new("Feed", scan.Items.Count, metadataBytes));
            try
            {
                using var response = await _http.GetAsync(feedUrl, HttpCompletionOption.ResponseHeadersRead, feedDeadline.Token).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > _limits.MaxFeedBytes)
                    throw new InvalidDataException("Размер ленты превышает 2 МиБ.");
                using var stream = await response.Content.ReadAsStreamAsync(feedDeadline.Token).ConfigureAwait(false);
                byte[] bytes = await ReadBoundedAsync(stream, _limits.MaxFeedBytes, feedDeadline.Token).ConfigureAwait(false);
                var feed = ParseFeed(StrictUtf8.GetString(bytes), _limits.MaxItems);
                deadline.Token.ThrowIfCancellationRequested();
                var titles = feed.ToDictionary(item => item.Url, item => item.Title, StringComparer.Ordinal);
                var confirmed = scan.Items.Select(item =>
                    (item.Title.Length == 0 || item.Title == item.Url) && titles.TryGetValue(item.Url, out string? title) && title.Length > 0
                        ? item with { Title = title } : item).ToArray();
                var urls = new HashSet<string>(scan.Items.Select(item => item.Url), StringComparer.Ordinal);
                int unconfirmed = feed.Count(item => !urls.Contains(item.Url));
                deadline.Token.ThrowIfCancellationRequested();
                return Complete(scan with {
                    Items = confirmed, FeedUrl = feedUrl,
                    Warning = unconfirmed == 0 ? "" : $"Элементов ленты, не подтверждённых {confirmedBy}: {unconfirmed}."
                });
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
            {
                return Complete(scan with { FeedUrl = feedUrl, Warning = $"Лента не ответила вовремя; использован {usedDiscovery} yt-dlp." });
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or XmlException or DecoderFallbackException)
            {
                deadline.Token.ThrowIfCancellationRequested();
                return Complete(scan with { FeedUrl = feedUrl, Warning = $"Лента недоступна; использован {usedDiscovery} yt-dlp. " + error.Message });
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Полная проверка источника не завершилась за отведённое время.");
        }
    }

    private static void ReportProgress(IProgress<SubscriptionScanProgress>? progress, SubscriptionScanProgress value)
    {
        // Display observers are a side channel; they cannot change whether a
        // complete scan succeeds or cause the metadata pipes to stop draining.
        try { progress?.Report(value); }
        catch(Exception error) { Debug.WriteLine("Subscription progress observer failed: " + error.Message); }
    }

    private async Task<string> EnumerateAsync(string url, string cookiePath, CancellationToken token,
        IProgress<SubscriptionScanProgress>? progress, bool recent = false)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(_ytDlpPath) {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        bool channel=PornHubChannel.ListingUrl(url)!=null;
        foreach (string argument in new[] {
            "--ignore-config", "--dump-single-json", "--skip-download",
            "--abort-on-error", "--no-color", "--no-progress", "--encoding", "utf-8", "--impersonate", "chrome"
        }) start.ArgumentList.Add(argument);
        if(channel)
        {
            PornHubChannel.RequireCollector();
            foreach(string argument in new[]{"--no-plugin-dirs","--plugin-dirs",PornHubChannel.PluginDirectory,
                "--use-extractors","videodownloader:pornhub:channel","--flat-playlist","--socket-timeout","20",
                "--extractor-args",$"VideoDownloaderPornHubChannel:max_items={_limits.MaxItems};max_pages={_limits.MaxChannelPages}"+
                    (recent ? $";recent_window={RecentWindowSize}" : "")})
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.ArgumentList.Add(recent ? "--flat-playlist" : "--no-flat-playlist");start.ArgumentList.Add("--playlist-end");
            start.ArgumentList.Add((recent ? RecentWindowSize : _limits.MaxItems+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
        if (!string.IsNullOrWhiteSpace(cookiePath))
        {
            if (!File.Exists(cookiePath)) throw new FileNotFoundException("Файл cookies не найден.", cookiePath);
            start.ArgumentList.Add("--cookies"); start.ArgumentList.Add(cookiePath);
        }
        start.ArgumentList.Add("--"); start.ArgumentList.Add(url);
        using var child=ManagedChildProcess.Start(start);
        var process=child.Process;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var kill = stop.Token.Register(() => {
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            catch (System.ComponentModel.Win32Exception) { }
        });
        async Task<byte[]> ReadOutput(Stream stream, int maximum)
        {
            var reporting = Stopwatch.StartNew();
            void Received(long bytes)
            {
                if (reporting.Elapsed < TimeSpan.FromMilliseconds(250)) return;
                reporting.Restart();
                ReportProgress(progress, new(recent ? "RecentWindow" : "Enumerating", MetadataBytes: bytes));
            }
            try { return await ReadBoundedAsync(stream, maximum, stop.Token, progress is null ? null : Received).ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }
        var stdout = ReadOutput(child.StandardOutput.BaseStream, _limits.MaxMetadataBytes);
        async Task<(string Text, bool Incomplete)> ReadErrors()
        {
            try { return await ReadDiagnosticsAsync(child.StandardError.BaseStream, stop.Token).ConfigureAwait(false); }
            catch { stop.Cancel(); throw; }
        }
        var stderr = ReadErrors();
        try
        {
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(stop.Token)).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            string diagnostic = stderr.Result.Text;
            if (process.ExitCode != 0 || stderr.Result.Incomplete)
                throw new IOException("Полный обход yt-dlp не завершён: " +
                    (diagnostic.Trim() is { Length: > 0 } text ? text[..Math.Min(text.Length, 4096)] : $"код {process.ExitCode}"));
            return StrictUtf8.GetString(stdout.Result);
        }
        finally
        {
            // Do not leave the child (or a pipe-reading task) behind on timeout,
            // cancellation, or a bounded-output failure.
            try { if (!process.HasExited) process.Kill(true); }
            catch (InvalidOperationException) { }
            finally
            {
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr).ConfigureAwait(false); } catch { }
            }
        }
    }

    private static async Task<(string Text, bool Incomplete)> ReadDiagnosticsAsync(Stream stream, CancellationToken token)
    {
        byte[] buffer = new byte[16 * 1024];
        using var retained = new MemoryStream();
        bool incomplete = false;
        string tail = "";
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (retained.Length < 4096) retained.Write(buffer, 0, Math.Min(count, 4096 - (int)retained.Length));
            string text = tail + Encoding.UTF8.GetString(buffer, 0, count);
            incomplete |= IncompleteDiagnostic.IsMatch(text);
            tail = text.Length <= 256 ? text : text[^256..];
        }
        return (Encoding.UTF8.GetString(retained.ToArray()), incomplete);
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token,
        Action<long>? received = null)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Превышен допустимый размер ответа.");
            output.Write(buffer, 0, count);
            received?.Invoke(output.Length);
        }
        return output.ToArray();
    }

    internal static bool IsNewestYouTubeTab(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var source) || source.Scheme is not ("http" or "https") ||
            source.UserInfo.Length != 0 || !source.IsDefaultPort || source.Query.Length != 0 || source.Fragment.Length != 0 ||
            !(source.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) ||
              source.Host.Equals("www.youtube.com", StringComparison.OrdinalIgnoreCase) ||
              source.Host.Equals("m.youtube.com", StringComparison.OrdinalIgnoreCase))) return false;
        // Channel roots can combine videos, shorts and live tabs. Playlists can
        // be arbitrarily reordered, so neither has a safe newest-first prefix.
        return Regex.IsMatch(source.AbsolutePath,
            @"^/(?:@[^/]+|(?:channel|c|user)/[^/]+)/(?:videos|shorts|streams)/?$", RegexOptions.CultureInvariant);
    }

    internal static SubscriptionScan ParseRecentYouTubeMetadata(string json, string sourceUrl)
    {
        if (!IsNewestYouTubeTab(sourceUrl)) throw new InvalidDataException("Источник не гарантирует порядок новых публикаций.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || String(root, "extractor_key") != "YoutubeTab" ||
            String(root, "webpage_url") != sourceUrl ||
            !root.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array ||
            entries.GetArrayLength() > RecentWindowSize)
            throw new InvalidDataException("Не подтверждено окно публикаций YouTube.");
        foreach (string flag in new[] { "_error", "incomplete", "_incomplete", "truncated" })
            if (root.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                throw new InvalidDataException("Проверка окна YouTube содержит ошибку.");
        foreach (string name in new[] { "playlist_count", "n_entries" })
            if (root.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null &&
                (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out long count) || count < entries.GetArrayLength()))
                throw new InvalidDataException("Некорректное количество публикаций YouTube.");
        if (root.TryGetProperty("requested_entries", out var requested) && requested.ValueKind != JsonValueKind.Null)
        {
            if (requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() != entries.GetArrayLength())
                throw new InvalidDataException("Проверка окна YouTube пропустила публикации.");
            int expected = 1;
            foreach (var index in requested.EnumerateArray())
                if (index.ValueKind != JsonValueKind.Number || !index.TryGetInt32(out int number) || number != expected++)
                    throw new InvalidDataException("Проверка окна YouTube нарушила порядок публикаций.");
        }
        var found = new List<DiscoveredMedia>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || entry.TryGetProperty("entries", out _) ||
                String(entry, "_type") is not ("url" or "video") ||
                !(String(entry, "ie_key") == "Youtube" || String(entry, "extractor_key") == "Youtube"))
                throw new InvalidDataException("Окно YouTube содержит нераскрытую коллекцию или неизвестный материал.");
            foreach (string flag in new[] { "_error", "incomplete", "_incomplete", "truncated" })
                if (entry.TryGetProperty(flag, out var value) && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.False))
                    throw new InvalidDataException("Окно YouTube содержит ошибку материала.");
            string id = String(entry, "id"), availability = String(entry, "availability");
            if (!Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant) ||
                (availability.Length != 0 && availability is not ("public" or "unlisted")))
                throw new InvalidDataException("Окно YouTube содержит недоступный или неизвестный материал.");
            string canonical = "https://www.youtube.com/watch?v=" + id;
            string url = String(entry, "webpage_url");
            if (url.Length == 0) url = String(entry, "url");
            if (url == id || url.Length == 0) url = canonical;
            // Match the full YoutubeIE discovery URL after validating this
            // adapter's specific flat routes and ID. User-entered and already
            // persisted URLs retain the application's exact-URL semantics.
            if (!(url == canonical || url == "https://www.youtube.com/shorts/" + id || url == "https://www.youtube.com/live/" + id) ||
                !known.Add(canonical))
                throw new InvalidDataException("Окно YouTube содержит повторный или посторонний URL.");
            string title = String(entry, "title");
            found.Add(new(canonical, title.Length == 0 ? canonical : title));
        }
        string caption = String(root, "title");
        string suggested = new[] { String(root, "channel"), String(root, "uploader"), caption }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        string feed = "";
        string channel = String(root, "channel_id");
        if (Regex.IsMatch(channel, @"^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant))
            feed = "https://www.youtube.com/feeds/videos.xml?channel_id=" + channel;
        return new(caption.Length > 0 ? caption : sourceUrl, found, feed, "", suggested) { Scope = SubscriptionScanScope.RecentWindow };
    }

    public static SubscriptionScan ParseMetadata(string json, string sourceUrl, int maxItems = 10_000)
    {
        if (maxItems is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxItems));
        if (!ValidUrl(sourceUrl)) throw new InvalidDataException("Некорректный URL источника.");
        using var document = JsonDocument.Parse(json);
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("entries", out _))
            throw new InvalidDataException("Источник не содержит полностью перечисленную коллекцию.");
        var found = new List<DiscoveredMedia>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        int enumerated = 0;
        void ValidateCompleteness(JsonElement value)
        {
            if (value.TryGetProperty("_error", out var error) && error.ValueKind is not JsonValueKind.Null and not JsonValueKind.False)
                throw new InvalidDataException("Обход содержит ошибку материала или коллекции.");
            foreach (string name in new[] { "incomplete", "_incomplete", "truncated" })
                if (value.TryGetProperty(name, out var flag) && flag.ValueKind is not JsonValueKind.False and not JsonValueKind.Null)
                    throw new InvalidDataException("Источник сообщает о неполном обходе.");
        }
        void Collection(JsonElement collection)
        {
            if (collection.ValueKind != JsonValueKind.Object || !collection.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Коллекция не перечислена полностью.");
            ValidateCompleteness(collection);
            if (entries.GetArrayLength() > maxItems) throw new InvalidDataException("В источнике больше допустимого числа материалов.");
            foreach (string name in new[] { "playlist_count", "n_entries" })
                if (collection.TryGetProperty(name, out var count) && count.ValueKind != JsonValueKind.Null &&
                    (!count.TryGetInt64(out long total) || total != entries.GetArrayLength()))
                    throw new InvalidDataException("Количество материалов указывает на неполный обход.");
            if (collection.TryGetProperty("requested_entries", out var requested) && requested.ValueKind != JsonValueKind.Null)
            {
                if (requested.ValueKind != JsonValueKind.Array || requested.GetArrayLength() != entries.GetArrayLength())
                    throw new InvalidDataException("Обход вернул только часть коллекции.");
                int index = 1;
                foreach (var value in requested.EnumerateArray())
                    if (!value.TryGetInt32(out int number) || number != index++) throw new InvalidDataException("Обход содержит пропущенные материалы.");
            }
            foreach (var entry in entries.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Обход содержит недоступный материал.");
                ValidateCompleteness(entry);
                string kind = String(entry, "_type");
                if (entry.TryGetProperty("entries", out _) || kind is "playlist" or "multi_video" or "compat_list") { Collection(entry); continue; }
                if (kind is "url" or "url_transparent")
                    throw new InvalidDataException("Вложенный источник не раскрыт до материалов; полный обход не подтверждён.");
                if (++enumerated > maxItems) throw new InvalidDataException("В источнике больше допустимого числа материалов.");
                string availability = String(entry, "availability");
                if (availability.Length > 0 && availability is not "public" and not "unlisted")
                    throw new InvalidDataException("Коллекция содержит недоступный или требующий авторизации материал.");
                string url = String(entry, "webpage_url");
                if (!ValidUrl(url)) url = String(entry, "url");
                if (!ValidUrl(url) && (String(entry, "ie_key") == "Youtube" || String(entry, "extractor_key") == "Youtube"))
                {
                    string id = String(entry, "id");
                    if (Regex.IsMatch(id, @"^[A-Za-z0-9_-]{11}$", RegexOptions.CultureInvariant)) url = "https://www.youtube.com/watch?v=" + id;
                }
                if (!ValidUrl(url)) throw new InvalidDataException("Материал не содержит корректного HTTP(S) URL.");
                string title = String(entry, "title");
                if (known.Add(url)) found.Add(new(url, title.Length == 0 ? url : title));
            }
        }
        Collection(root);
        string title = String(root, "title");
        string feed = "";
        if (Uri.TryCreate(sourceUrl, UriKind.Absolute, out var source) &&
            (source.Host.Equals("youtube.com", StringComparison.OrdinalIgnoreCase) || source.Host.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)))
        {
            string channel = String(root, "channel_id");
            if (!Regex.IsMatch(channel, @"^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant)) channel = String(root, "uploader_id");
            if (Regex.IsMatch(channel, @"^UC[A-Za-z0-9_-]{22}$", RegexOptions.CultureInvariant))
                feed = "https://www.youtube.com/feeds/videos.xml?channel_id=" + channel;
        }
        string suggestedName = new[] { String(root, "channel"), String(root, "uploader"), title }
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? "";
        return new(title.Length == 0 ? sourceUrl : title, found, feed, "", suggestedName);
    }

    public static IReadOnlyList<DiscoveredMedia> ParseFeed(string xml, int maxItems = 10_000)
    {
        if (maxItems is < 1 or > 10_000) throw new ArgumentOutOfRangeException(nameof(maxItems));
        using var text = new StringReader(xml);
        using var reader = XmlReader.Create(text, new XmlReaderSettings {
            DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 2 * 1024 * 1024
        });
        var document = XDocument.Load(reader);
        var root = document.Root ?? throw new InvalidDataException("Лента пуста.");
        IEnumerable<XElement> entries = root.Name.LocalName switch {
            "feed" => root.Elements().Where(element => element.Name.LocalName == "entry"),
            "rss" => root.Elements().Where(element => element.Name.LocalName == "channel").SelectMany(channel => channel.Elements().Where(element => element.Name.LocalName == "item")),
            "RDF" => root.Elements().Where(element => element.Name.LocalName == "item"),
            _ => throw new InvalidDataException("Ответ не является RSS или Atom лентой.")
        };
        var found = new List<DiscoveredMedia>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        int count = 0;
        foreach (var entry in entries)
        {
            if (++count > maxItems) throw new InvalidDataException("В ленте больше допустимого числа материалов.");
            string url = "";
            foreach (var link in entry.Elements().Where(element => element.Name.LocalName == "link"))
            {
                string relation = (string?)link.Attribute("rel") ?? "alternate";
                if (relation != "alternate") continue;
                string candidate = ((string?)link.Attribute("href") ?? link.Value).Trim();
                if (ValidUrl(candidate)) { url = candidate; break; }
            }
            if (url.Length == 0)
            {
                string candidate = (string?)entry.Elements().FirstOrDefault(element => element.Name.LocalName == "enclosure")?.Attribute("url") ?? "";
                if (ValidUrl(candidate)) url = candidate;
            }
            if (!ValidUrl(url)) throw new InvalidDataException("Материал ленты не содержит корректного HTTP(S) URL.");
            string title = entry.Elements().FirstOrDefault(element => element.Name.LocalName == "title")?.Value.Trim() ?? "";
            if (seen.Add(url)) found.Add(new(url, title));
        }
        return found;
    }

    private static string String(JsonElement value, string name) =>
        value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() ?? "" : "";
    private static bool ValidUrl(string value) => value.Length > 0 && value == value.Trim() && !value.Any(char.IsControl) &&
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0;
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
