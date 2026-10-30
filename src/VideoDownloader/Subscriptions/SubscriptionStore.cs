using System.IO;
using System.Text.Json;

namespace VideoDownloader.Native;

/// <summary>Local subscription state. This class never enumerates or downloads media.</summary>
public sealed class SubscriptionStore
{
    private const long MaxFileBytes = 128L * 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };
    private static readonly string[] DefinitionProperties =
    ["Id", "Url", "Title", "ShortName", "FeedUrl", "DownloadExisting", "Initialized", "Paused",
     "LastAttemptUtc", "LastSuccessUtc", "LastError", "LastWarning"];
    private static readonly string[] ItemProperties =
    ["SubscriptionId", "Url", "Title", "State", "DiscoveredAtUtc", "InitialQueuePending", "SuccessAttributed",
     "DuplicateSubscriptionId", "DuplicateNotified"];
    public string Path { get; }

    public SubscriptionStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Path = System.IO.Path.Combine(System.IO.Path.GetFullPath(directory), "subscriptions.json");
    }

    public SubscriptionSnapshot Read() => Locked(ReadCore);

    public SubscriptionDefinition Add(string url, bool downloadExisting, string feedUrl = "", string shortName = "")
    {
        RequireUrl(url);
        RequireOptionalUrl(feedUrl);
        ArgumentNullException.ThrowIfNull(shortName);
        if (shortName.Length > 0) ArgumentException.ThrowIfNullOrWhiteSpace(shortName);
        return Update(snapshot =>
        {
            if (snapshot.Subscriptions.Any(s => s.Url.Equals(url, StringComparison.Ordinal)))
                throw new InvalidOperationException("Этот адрес уже есть в подписках.");
            var source = new SubscriptionDefinition
            {
                Id = Guid.NewGuid().ToString("N"), Url = url, Title = url,
                FeedUrl = feedUrl, DownloadExisting = downloadExisting,
                ShortName = shortName.Length > 0 ? SubscriptionNames.Normalize(shortName) : ""
            };
            snapshot.Subscriptions.Add(source);
            return source;
        });
    }

    public SubscriptionSnapshot ApplyScan(string id, SubscriptionScan scan, IEnumerable<string> successfulUrls,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentNullException.ThrowIfNull(scan);
        ArgumentNullException.ThrowIfNull(scan.Items);
        ArgumentNullException.ThrowIfNull(scan.Title);
        ArgumentNullException.ThrowIfNull(scan.Warning);
        RequireOptionalUrl(scan.FeedUrl);
        if (scan.Items.Count > 10_000) throw new InvalidDataException("Список подписки превышает 10 000 материалов.");
        // Validate the complete result before changing even the attempt timestamp.
        var discoveries = new List<DiscoveredMedia>();
        var discoveryUrls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in scan.Items)
        {
            if (item is null || item.Title is null) throw new InvalidDataException("Повреждённый материал подписки.");
            RequireUrl(item.Url);
            if (discoveryUrls.Add(item.Url)) discoveries.Add(item);
        }
        var successes = MaterializeUrls(successfulUrls);
        return Update(snapshot =>
        {
            var source = Find(snapshot, id);
            if (scan.Scope == SubscriptionScanScope.RecentWindow)
            {
                var knownUrls = snapshot.Items.Where(item => item.SubscriptionId == id)
                    .Select(item => item.Url).ToHashSet(StringComparer.Ordinal);
                if (!source.Initialized || scan.Items.Count > SubscriptionScanner.RecentWindowSize ||
                    !scan.Items.Any(item => knownUrls.Contains(item.Url)))
                    throw new InvalidDataException("Быстрая проверка не пересекается с сохранённым снимком; требуется полный обход.");
            }
            else if (scan.Scope != SubscriptionScanScope.Full)
                throw new InvalidDataException("Неизвестный режим проверки подписки.");
            ReconcileSuccess(snapshot, successes);
            var processedUrls = snapshot.Items.Where(i => i.State == "Processed")
                .Select(i => i.Url).ToHashSet(StringComparer.Ordinal);
            var reconcile = new HashSet<string>(successes, StringComparer.Ordinal);
            bool initial = !source.Initialized;
            var existing = snapshot.Items.Where(i => i.SubscriptionId == id)
                .ToDictionary(i => i.Url, StringComparer.Ordinal);
            var now = DateTimeOffset.UtcNow;
            foreach (var discovery in discoveries)
            {
                if (existing.TryGetValue(discovery.Url, out var known))
                {
                    // Lightweight windows can omit titles. Do not replace a
                    // previously resolved name with an empty/URL placeholder.
                    if ((!string.IsNullOrWhiteSpace(discovery.Title) && discovery.Title != discovery.Url) ||
                        string.IsNullOrWhiteSpace(known.Title) || known.Title == known.Url)
                        known.Title = discovery.Title;
                    continue;
                }
                var item = new SubscriptionItem
                {
                    SubscriptionId = id, Url = discovery.Url, Title = discovery.Title,
                    DiscoveredAtUtc = now,
                    State = initial && !source.DownloadExisting ? "Baseline" : "New",
                    InitialQueuePending = initial && source.DownloadExisting
                };
                snapshot.Items.Add(item);
                // A Processed record is durable evidence from an earlier real
                // success. Baseline, Queued and Skipped records are never proof.
                if (processedUrls.Contains(item.Url)) reconcile.Add(item.Url);
            }
            ReconcileSuccess(snapshot, reconcile);
            ReorderItems(snapshot, id, discoveries, discoveryUrls);
            if (!string.IsNullOrWhiteSpace(scan.Title) && scan.Title != source.Url)
                source.Title = scan.Title;
            else if (string.IsNullOrWhiteSpace(source.Title)) source.Title = source.Url;
            if (source.ShortName.Length == 0 && scan.Scope == SubscriptionScanScope.Full)
            {
                string suggestion = string.IsNullOrWhiteSpace(scan.SuggestedName) ? scan.Title : scan.SuggestedName;
                if (Uri.TryCreate(suggestion, UriKind.Absolute, out var suggestedUrl) && suggestedUrl.Scheme is "http" or "https") suggestion = "";
                source.ShortName = SubscriptionNames.Normalize(suggestion, "Подписка " + source.Id[..Math.Min(8, source.Id.Length)]);
            }
            if (scan.FeedUrl.Length > 0) source.FeedUrl = scan.FeedUrl;
            source.Initialized = true;
            source.LastAttemptUtc = now;
            source.LastSuccessUtc = now;
            source.LastError = "";
            source.LastWarning = scan.Warning;
            return snapshot;
        }, cancellationToken);
    }

    private static void ReorderItems(SubscriptionSnapshot snapshot, string id,
        IReadOnlyList<DiscoveredMedia> discoveries, IReadOnlySet<string> discoveryUrls)
    {
        // The version-1 Items array is itself ordered; no new persisted field or
        // destructive migration is required. A full scan refreshes source order,
        // while a recent window replaces only the prefix. Preserve historical
        // entries absent from either result, together with all their decisions.
        int insertion = snapshot.Items.FindIndex(item => item.SubscriptionId == id);
        if (insertion < 0) return;
        var previous = snapshot.Items.Where(item => item.SubscriptionId == id).ToArray();
        var byUrl = previous.ToDictionary(item => item.Url, StringComparer.Ordinal);
        var ordered = discoveries.Select(item => byUrl[item.Url])
            .Concat(previous.Where(item => !discoveryUrls.Contains(item.Url))).ToArray();
        snapshot.Items.RemoveAll(item => item.SubscriptionId == id);
        snapshot.Items.InsertRange(insertion, ordered);
    }

    public void RecordFailure(string id, string error)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(error);
        Update(snapshot =>
        {
            var source = Find(snapshot, id);
            source.LastAttemptUtc = DateTimeOffset.UtcNow;
            source.LastError = error;
            return 0;
        });
    }

    public void SetPaused(string id, bool paused) => Update(snapshot =>
    {
        Find(snapshot, id).Paused = paused;
        return 0;
    });

    public void SetShortName(string id, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        string normalized = SubscriptionNames.Normalize(name);
        Update(snapshot => { Find(snapshot, id).ShortName = normalized; return 0; });
    }

    public void Remove(string id) => Update(snapshot =>
    {
        var source = Find(snapshot, id);
        snapshot.Items.RemoveAll(i => i.SubscriptionId == id);
        snapshot.Subscriptions.Remove(source);
        // DuplicateSubscriptionId is provenance and can outlive its source.
        // Keeping it also preserves an already created, unacknowledged notice.
        return 0;
    });

    public void MarkQueued(string id, IEnumerable<string> urls) => ChangeSelection(id, urls, "Queued");
    public void Skip(string id, IEnumerable<string> urls) => ChangeSelection(id, urls, "Skipped");

    private void ChangeSelection(string id, IEnumerable<string> urls, string state)
    {
        var selection = MaterializeUrls(urls);
        Update(snapshot =>
        {
            Find(snapshot, id);
            var byUrl = snapshot.Items.Where(i => i.SubscriptionId == id)
                .ToDictionary(i => i.Url, StringComparer.Ordinal);
            var items = selection.Select(url => byUrl.TryGetValue(url, out var item) ? item :
                throw new InvalidOperationException("Материал не найден в подписке.")).ToList();
            foreach (var item in items)
            {
                bool allowed = state == "Queued" ? item.State is "Baseline" or "New" or "Skipped" or "Queued" :
                    item.State is "New" or "Skipped";
                if (!allowed) throw new InvalidOperationException("Нельзя изменить состояние этого материала.");
            }
            foreach (var item in items)
            {
                item.State = state;
                item.InitialQueuePending = false;
            }
            return 0;
        });
    }

    public void MarkSuccessful(string url)
    {
        RequireUrl(url);
        Update(snapshot => { ReconcileSuccess(snapshot, new HashSet<string>([url], StringComparer.Ordinal)); return 0; });
    }

    public SubscriptionSnapshot ReconcileSuccess(IEnumerable<string> successfulUrls)
    {
        var successes = MaterializeUrls(successfulUrls);
        return Update(snapshot =>
        {
            ReconcileSuccess(snapshot, successes);
            return snapshot;
        });
    }

    public void AcknowledgeDuplicates(IEnumerable<SubscriptionItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var keys = items.Select(item =>
        {
            ArgumentNullException.ThrowIfNull(item);
            ArgumentException.ThrowIfNullOrWhiteSpace(item.SubscriptionId);
            RequireUrl(item.Url);
            return (item.SubscriptionId, item.Url, item.DuplicateSubscriptionId);
        }).ToHashSet();
        Update(snapshot =>
        {
            foreach (var item in snapshot.Items)
                if (item.State == "AlreadyProcessed" && keys.Contains((item.SubscriptionId, item.Url, item.DuplicateSubscriptionId)))
                    item.DuplicateNotified = true;
            return 0;
        });
    }

    private static void ReconcileSuccess(SubscriptionSnapshot snapshot, IReadOnlySet<string> successfulUrls)
    {
        if (successfulUrls.Count == 0) return;
        var order = snapshot.Subscriptions.Select((source, index) => (source.Id, index))
            .ToDictionary(pair => pair.Id, pair => pair.index, StringComparer.Ordinal);
        foreach (var matches in snapshot.Items.Where(i => successfulUrls.Contains(i.Url)).GroupBy(i => i.Url, StringComparer.Ordinal))
        {
            var source = matches.Where(i => i.State == "Processed" && i.SuccessAttributed).MinBy(i => order[i.SubscriptionId])
                ?? matches.Where(i => i.State == "Queued").MinBy(i => order[i.SubscriptionId]);
            string? sourceId = source?.SubscriptionId
                ?? matches.Where(i => i.State == "AlreadyProcessed").MinBy(i => order[i.SubscriptionId])?.DuplicateSubscriptionId;
            foreach (var item in matches)
            {
                // Existing Processed rows may originate in global history without
                // source attribution. Do not invent a notice on a repeated pass.
                if (item.State is "AlreadyProcessed" or "Processed") continue;
                item.InitialQueuePending = false;
                if (sourceId is not null && item.SubscriptionId != sourceId)
                {
                    item.State = "AlreadyProcessed";
                    item.SuccessAttributed = false;
                    item.DuplicateSubscriptionId = sourceId;
                    item.DuplicateNotified = false;
                }
                else
                {
                    item.State = "Processed";
                    item.SuccessAttributed = sourceId is not null;
                    item.DuplicateSubscriptionId = "";
                    item.DuplicateNotified = false;
                }
            }
        }
    }

    private static SubscriptionDefinition Find(SubscriptionSnapshot snapshot, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        return snapshot.Subscriptions.FirstOrDefault(s => s.Id.Equals(id, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Подписка не найдена.");
    }

    private static HashSet<string> MaterializeUrls(IEnumerable<string> urls)
    {
        ArgumentNullException.ThrowIfNull(urls);
        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (string url in urls) { RequireUrl(url); result.Add(url); }
        return result;
    }

    private static void RequireUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.Equals(url.Trim(), StringComparison.Ordinal) || url.Any(char.IsControl) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https") || string.IsNullOrEmpty(uri.Host))
            throw new InvalidDataException("Подписки поддерживают только полные HTTP/HTTPS-адреса.");
    }

    private static void RequireOptionalUrl(string? url)
    {
        ArgumentNullException.ThrowIfNull(url);
        if (url.Length > 0) RequireUrl(url);
    }

    private T Update<T>(Func<SubscriptionSnapshot, T> action, CancellationToken cancellationToken = default) => Locked(() =>
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ReadCore();
        T result = action(snapshot);
        Validate(snapshot);
        WriteCore(snapshot, cancellationToken);
        return result;
    }, cancellationToken);

    private static T Locked<T>(Func<T> action, CancellationToken cancellationToken = default)
    {
        using var mutex = new Mutex(false, @"Local\VideoDownloader.Subscriptions");
        bool acquired = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (cancellationToken.CanBeCanceled)
                {
                    var waiting = System.Diagnostics.Stopwatch.StartNew();
                    do
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        acquired = mutex.WaitOne(50);
                    } while (!acquired && waiting.Elapsed < TimeSpan.FromSeconds(15));
                }
                else acquired = mutex.WaitOne(TimeSpan.FromSeconds(15));
            }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new TimeoutException("Не удалось получить блокировку файла подписок.");
            cancellationToken.ThrowIfCancellationRequested();
            return action();
        }
        finally { if (acquired) mutex.ReleaseMutex(); }
    }

    private SubscriptionSnapshot ReadCore()
    {
        FileStream input;
        try { input = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.Read); }
        catch (FileNotFoundException) { return new SubscriptionSnapshot(); }
        catch (DirectoryNotFoundException) { return new SubscriptionSnapshot(); }
        using (input)
        {
            try
            {
                if (input.Length > MaxFileBytes) throw new InvalidDataException("Файл подписок превышает 128 МиБ.");
                using var document = JsonDocument.Parse(input);
                var root = document.RootElement;
                RequireProperties(root, ["Version", "Subscriptions", "Items"]);
                if (root.GetProperty("Version").GetInt32() != 1) throw new InvalidDataException("Неизвестная версия подписок.");
                var sources = root.GetProperty("Subscriptions");
                var items = root.GetProperty("Items");
                if (sources.ValueKind != JsonValueKind.Array || items.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException("Ожидались списки подписок и материалов.");
                foreach (var source in sources.EnumerateArray()) RequireProperties(source, DefinitionProperties, ["ShortName"]);
                foreach (var item in items.EnumerateArray()) RequireProperties(item, ItemProperties);
                var snapshot = root.Deserialize<SubscriptionSnapshot>(Json)
                    ?? throw new InvalidDataException("Пустой файл подписок.");
                Validate(snapshot);
                return snapshot;
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException or InvalidDataException or ArgumentException or FormatException)
            {
                throw new InvalidDataException("Файл подписок повреждён или имеет неизвестный формат. Данные не изменены.", error);
            }
        }
    }

    private static void RequireProperties(JsonElement element, string[] required, string[]? optional = null)
    {
        if (element.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Ожидался JSON-объект.");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !names.Add(property.Name))
                throw new InvalidDataException("Неизвестное или повторяющееся поле подписок.");
        if (required.Any(name => !names.Contains(name) && !(optional?.Contains(name, StringComparer.Ordinal) ?? false)))
            throw new InvalidDataException("Отсутствует обязательное поле подписок.");
    }

    private static void Validate(SubscriptionSnapshot snapshot)
    {
        if (snapshot.Version != 1 || snapshot.Subscriptions is null || snapshot.Items is null)
            throw new InvalidDataException("Неверный формат подписок.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var urls = new HashSet<string>(StringComparer.Ordinal);
        foreach (var source in snapshot.Subscriptions)
        {
            if (source is null || string.IsNullOrWhiteSpace(source.Id) || !ids.Add(source.Id) ||
                source.Title is null || source.ShortName is null ||
                (source.ShortName.Length > 0 && !SubscriptionNames.IsValid(source.ShortName)) ||
                source.LastError is null || source.LastWarning is null)
                throw new InvalidDataException("Повторяющаяся или повреждённая подписка.");
            RequireUrl(source.Url);
            RequireOptionalUrl(source.FeedUrl);
            if (!urls.Add(source.Url) || (!source.Initialized && source.LastSuccessUtc.HasValue))
                throw new InvalidDataException("Несогласованное состояние подписки.");
        }
        var keys = new HashSet<(string, string)>();
        var initialized = snapshot.Subscriptions.Where(s => s.Initialized).Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var item in snapshot.Items)
        {
            if (item is null || !ids.Contains(item.SubscriptionId) || !initialized.Contains(item.SubscriptionId) ||
                !keys.Add((item.SubscriptionId, item.Url)) || item.Title is null || item.DuplicateSubscriptionId is null ||
                item.State is not ("Baseline" or "New" or "Queued" or "Processed" or "AlreadyProcessed" or "Skipped"))
                throw new InvalidDataException("Повторяющийся или повреждённый материал подписки.");
            RequireUrl(item.Url);
            if (item.InitialQueuePending && item.State != "New")
                throw new InvalidDataException("Несогласованное состояние исходной очереди.");
            if (item.SuccessAttributed && item.State != "Processed")
                throw new InvalidDataException("Источник успеха не соответствует состоянию материала.");
            if (item.State == "AlreadyProcessed")
            {
                if (string.IsNullOrWhiteSpace(item.DuplicateSubscriptionId) || item.DuplicateSubscriptionId == item.SubscriptionId)
                    throw new InvalidDataException("Неизвестен источник обработанного материала.");
            }
            else if (item.DuplicateSubscriptionId.Length != 0 || item.DuplicateNotified)
                throw new InvalidDataException("Уведомление не соответствует состоянию материала.");
        }
    }

    private void WriteCore(SubscriptionSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(output, snapshot, Json);
                if (output.Length > MaxFileBytes) throw new InvalidDataException("Файл подписок превышает 128 МиБ.");
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
