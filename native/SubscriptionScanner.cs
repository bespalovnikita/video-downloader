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
    public int MaxChannelPages { get; init; } = 500;
    public TimeSpan FeedTimeout { get; init; } = TimeSpan.FromSeconds(12);
    public int MaxItems { get; init; } = 10_000;
    public int MaxMetadataBytes { get; init; } = 32 * 1024 * 1024;
    public int MaxFeedBytes { get; init; } = 2 * 1024 * 1024;
}

/// <summary>A manual, complete collection scan. A feed never replaces enumeration.</summary>
public sealed class SubscriptionScanner : IDisposable
{
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
            _limits.MaxChannelPages is <1 or >500 ||
            _limits.FeedTimeout <= TimeSpan.Zero || _limits.FeedTimeout > TimeSpan.FromSeconds(12) ||
            _limits.MaxItems is < 1 or > 10_000 || _limits.MaxMetadataBytes is < 1 or > 32 * 1024 * 1024 ||
            _limits.MaxFeedBytes is < 1 or > 2 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options), "Лимиты проверки нельзя превышать.");
        _ytDlpPath = ytDlpPath;
        _ownsHttp = httpClient is null;
        _http = httpClient ?? new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
    }

    public async Task<SubscriptionScan> ScanAsync(SubscriptionDefinition source, string cookiePath, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!ValidUrl(source.Url)) throw new ArgumentException("Источник должен быть HTTP(S) URL.", nameof(source));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        bool channel=PornHubChannel.ListingUrl(source.Url)!=null;
        deadline.CancelAfter(channel?_limits.ChannelScanTimeout:_limits.ScanTimeout);
        try
        {
            // Always enumerate first, including when the feed has not changed.
            string metadata = await EnumerateAsync(source.Url, cookiePath, deadline.Token).ConfigureAwait(false);
            deadline.Token.ThrowIfCancellationRequested();
            SubscriptionScan scan = channel?PornHubChannel.ParseScan(metadata,source.Url,_limits.MaxItems,_limits.MaxChannelPages)
                :ParseMetadata(metadata, source.Url, _limits.MaxItems);
            deadline.Token.ThrowIfCancellationRequested();
            string feedUrl = string.IsNullOrWhiteSpace(source.FeedUrl) ? scan.FeedUrl : source.FeedUrl;
            if (feedUrl.Length == 0) return scan;
            if (!ValidUrl(feedUrl)) return scan with { FeedUrl = feedUrl, Warning = "URL ленты некорректен; использован полный обход yt-dlp." };
            using var feedDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            feedDeadline.CancelAfter(_limits.FeedTimeout);
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
                return scan with {
                    Items = confirmed, FeedUrl = feedUrl,
                    Warning = unconfirmed == 0 ? "" : $"Элементов ленты, не подтверждённых полным обходом: {unconfirmed}."
                };
            }
            catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
            {
                return scan with { FeedUrl = feedUrl, Warning = "Лента не ответила вовремя; использован полный обход yt-dlp." };
            }
            catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or XmlException or DecoderFallbackException)
            {
                deadline.Token.ThrowIfCancellationRequested();
                return scan with { FeedUrl = feedUrl, Warning = "Лента недоступна; использован полный обход yt-dlp. " + error.Message };
            }
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Полная проверка источника не завершилась за отведённое время.");
        }
    }

    private async Task<string> EnumerateAsync(string url, string cookiePath, CancellationToken token)
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
                "--extractor-args",$"VideoDownloaderPornHubChannel:max_items={_limits.MaxItems};max_pages={_limits.MaxChannelPages}"})
                start.ArgumentList.Add(argument);
        }
        else
        {
            start.ArgumentList.Add("--no-flat-playlist");start.ArgumentList.Add("--playlist-end");
            start.ArgumentList.Add((_limits.MaxItems+1).ToString(System.Globalization.CultureInfo.InvariantCulture));
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
            try { return await ReadBoundedAsync(stream, maximum, stop.Token).ConfigureAwait(false); }
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

    private static async Task<byte[]> ReadBoundedAsync(Stream stream, int maximum, CancellationToken token)
    {
        using var output = new MemoryStream();
        byte[] buffer = new byte[16 * 1024];
        int count;
        while ((count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("Превышен допустимый размер ответа.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
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
