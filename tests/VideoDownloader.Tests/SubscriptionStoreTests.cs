using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using VideoDownloader.Native;

internal static class SubscriptionStoreTests
{
    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-subscription-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            InitialSnapshotsAndFailures(Path.Combine(root, "initial"));
            ExplicitBaselineQueue(Path.Combine(root, "baseline-queue"));
            SupplementalWindows(Path.Combine(root, "windows"));
            NamesAndLegacyMigration(Path.Combine(root, "names"));
            SuccessfulProcessingAndNotices(Path.Combine(root, "notices"));
            CrashRecovery(Path.Combine(root, "recovery"));
            LargeCompleteScan(Path.Combine(root, "large"));
            StrictValidation(Path.Combine(root, "validation"));
            ConcurrentWriters(Path.Combine(root, "concurrent"));
            CancellationBeforeCommit(Path.Combine(root, "cancel"));
            Console.WriteLine("Subscription store tests passed.");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Assert(bool value, string reason)
    {
        if (!value) throw new Exception(reason);
    }

    private static void Refuses(Action operation, string reason)
    {
        bool refused = false;
        try { operation(); } catch (Exception) { refused = true; }
        Assert(refused, reason);
    }

    private static SubscriptionScan Scan(params string[] urls) =>
        new("Collection title", urls.Select(url => new DiscoveredMedia(url, "Title " + url)).ToList(), "", "");

    private static SubscriptionItem Item(SubscriptionSnapshot snapshot, string id, string url) =>
        snapshot.Items.Single(item => item.SubscriptionId == id && item.Url == url);

    private static void ExplicitBaselineQueue(string directory)
    {
        var store = new SubscriptionStore(directory);
        var source = store.Add("https://baseline.example/videos", false);
        const string first = "https://media.example/baseline-first", second = "https://media.example/baseline-second";
        store.ApplyScan(source.Id, Scan(first, second), []);
        Assert(store.Read().Items.All(item => item.State == "Baseline" && !item.InitialQueuePending),
            "The visible initial snapshot must remain unqueued until explicit selection");
        store.MarkQueued(source.Id, [first]);
        var selected = new SubscriptionStore(directory).Read();
        Assert(Item(selected, source.Id, first).State == "Queued" && !Item(selected, source.Id, first).SuccessAttributed &&
            Item(selected, source.Id, second).State == "Baseline" && !Item(selected, source.Id, second).InitialQueuePending,
            "Explicit baseline queueing must affect only the selected row without claiming a successful download");
        store.ApplyScan(source.Id, Scan(first, second), []);
        Assert(Item(store.Read(), source.Id, first).State == "Queued" && Item(store.Read(), source.Id, second).State == "Baseline",
            "A repeated scan must preserve the explicit baseline decision");
        var legacy = JsonNode.Parse(File.ReadAllText(store.Path))!;
        legacy["Subscriptions"]![0]!.AsObject().Remove("ShortName");
        File.WriteAllText(store.Path, legacy.ToJsonString());
        var window = store.ApplyScan(source.Id, Scan(first) with { Scope = SubscriptionScanScope.RecentWindow, SuggestedName = "Partial suggestion" }, []);
        Assert(window.Subscriptions.Single().ShortName == "", "A legacy folder name may be inferred only by a complete scan");
        var full = store.ApplyScan(source.Id, Scan(first, second) with { SuggestedName = "Complete suggestion" }, []);
        Assert(full.Subscriptions.Single().ShortName == "Complete suggestion", "A full scan must still migrate the missing legacy folder name");
    }

    private static void SupplementalWindows(string directory)
    {
        var store = new SubscriptionStore(directory);
        var source = store.Add("https://windows.example/videos", false);
        string[] old = Enumerable.Range(0, 100).Select(index => "https://media.example/window/" + index).ToArray();
        SubscriptionScan Window(params string[] urls) => Scan(urls) with { Scope = SubscriptionScanScope.RecentWindow };
        byte[] uninitialized = File.ReadAllBytes(store.Path);
        Refuses(() => store.ApplyScan(source.Id, Window(old[0]), []), "A window cannot establish the initial baseline");
        Assert(uninitialized.SequenceEqual(File.ReadAllBytes(store.Path)), "Refusing a window must leave an uninitialized source untouched");
        store.ApplyScan(source.Id, Scan(old), []);
        store.MarkQueued(source.Id, [old[0], old[1]]);
        store.MarkSuccessful(old[0]);
        const string skipped = "https://media.example/window/skipped";
        store.ApplyScan(source.Id, Scan(old.Concat([skipped]).ToArray()), []);
        store.Skip(source.Id, [skipped]);
        var before = store.Read();
        var oldStates = before.Items.ToDictionary(item => item.Url, item => item.State, StringComparer.Ordinal);
        var missingTitles = Window(old[0], old[1]) with { Items = [new(old[0], ""), new(old[1], old[1])] };
        var retainedTitles = store.ApplyScan(source.Id, missingTitles, []);
        Assert(retainedTitles.Items.Where(item => item.Url == old[0] || item.Url == old[1]).All(item => item.Title == "Title " + item.Url),
            "A lightweight window without useful titles must preserve previously resolved video names");
        var noUpdates = store.ApplyScan(source.Id, Window(old.Take(19).Append(skipped).ToArray()), []);
        Assert(noUpdates.Items.Count == 101 && noUpdates.Items.All(item => oldStates[item.Url] == item.State) &&
            noUpdates.Subscriptions.Single().LastSuccessUtc >= before.Subscriptions.Single().LastSuccessUtc,
            "Twenty known URLs must keep all 101 saved rows, their queued/skipped/processed states and refresh the successful check time");
        const string firstNew = "https://media.example/window/new1", secondNew = "https://media.example/window/new2";
        var withNews = store.ApplyScan(source.Id, Window(new[] { firstNew, secondNew }.Concat(old.Take(18)).ToArray()), []);
        Assert(withNews.Items.Count == 103 && Item(withNews, source.Id, firstNew).State == "New" &&
            Item(withNews, source.Id, secondNew).State == "New" && !Item(withNews, source.Id, firstNew).InitialQueuePending &&
            oldStates.All(pair => Item(withNews, source.Id, pair.Key).State == pair.Value),
            "A recent window must add only unseen URLs while preserving all previous decisions without autoqueueing");
        byte[] durable = File.ReadAllBytes(store.Path);
        Refuses(() => store.ApplyScan(source.Id, Window("https://media.example/window/no-anchor"), []),
            "A detached window without a currently persisted anchor must be refused atomically");
        Refuses(() => store.ApplyScan(source.Id, Window(old.Take(21).ToArray()), []), "A supplemental window must obey its separate 20-item limit");
        Refuses(() => store.ApplyScan(source.Id, Scan(old) with { Scope = (SubscriptionScanScope)99 }, []), "Unknown discovery scopes must be refused");
        Assert(durable.SequenceEqual(File.ReadAllBytes(store.Path)), "Rejected supplemental results may not mutate state or timestamps");
        store.RecordFailure(source.Id, "Fallback failed");
        var failed = store.Read();
        Assert(failed.Items.Count == withNews.Items.Count && failed.Subscriptions.Single().LastSuccessUtc == withNews.Subscriptions.Single().LastSuccessUtc,
            "Failed full fallback must preserve every saved item and the last successful update time");
    }

    private static void NamesAndLegacyMigration(string directory)
    {
        foreach (var (raw, expected) in new[] {
            (" . Мой: канал / Test . ", "Мой_ канал _ Test"), ("CON", "_CON"), ("nul.txt", "_nul.txt"),
            ("LPT³", "_LPT³"), ("con .txt", "_con .txt"), ("Conductor", "Conductor"), ("..", "Подписка"),
            ("Имя   канала", "Имя канала"), ("bad\u0001name", "bad_name"), ("left\ud800right", "left_right") })
        {
            string name = SubscriptionNames.Normalize(raw);
            Assert(name == expected && SubscriptionNames.IsValid(name), "Safe folder normalization: " + expected);
        }
        string unicodeBoundary = new('x', 79);
        Assert(SubscriptionNames.Normalize(unicodeBoundary + "😀") == unicodeBoundary,
            "Length cap must not leave a split UTF16 surrogate pair");
        foreach (string invalid in new[] { "", ".", "..", "../escape", @"..\escape", @"C:\escape", "a/b", "a\\b", "CON", "name.", " name", "a  b", new string('x', 81) })
        {
            Assert(!SubscriptionNames.IsValid(invalid), "Unsafe folder must fail validation: " + invalid);
            if (invalid.Length > 0) Refuses(() => SubscriptionNames.ResolveOutputDirectory(directory, invalid), "Output resolution must reject unsafe leaves");
        }
        Assert(SubscriptionNames.ResolveOutputDirectory(directory, "123") == Path.Combine(Path.GetFullPath(directory), "123") &&
            SubscriptionNames.ResolveOutputDirectory(directory, "") == Path.GetFullPath(directory),
            "Subscription folders are exactly one child of the selected output; ordinary rows retain the base");

        var store = new SubscriptionStore(directory);
        var source = store.Add("https://names.example/playlist", false);
        var first = store.ApplyScan(source.Id, new("Полное имя плейлиста", [], "", "", "Канал: 123"), []);
        Assert(first.Subscriptions.Single().ShortName == "Канал_ 123", "The first successful scan must prefer the channel's suggested name");
        store.ApplyScan(source.Id, new("Новое имя плейлиста", [], "", "", "Новое имя канала"), []);
        Assert(store.Read().Subscriptions.Single().ShortName == "Канал_ 123", "Later scans must never move an assigned folder silently");
        store.SetShortName(source.Id, "123");
        store.ApplyScan(source.Id, new("Плейлист", [], "", "", "Другой канал"), []);
        Assert(new SubscriptionStore(directory).Read().Subscriptions.Single().ShortName == "123", "An explicit short name must survive scans and restarts");
        var custom = store.Add("https://names.example/custom", true, shortName: "Моя: папка");
        Assert(custom.ShortName == "Моя_ папка", "An explicit name before discovery must use the same leaf rules");
        byte[] before = File.ReadAllBytes(store.Path);
        Refuses(() => store.SetShortName(source.Id, " \t"), "A blank manual name must not replace a real folder");
        Refuses(() => store.Add("https://names.example/rejected", false, shortName: "   "), "A blank manual initial name must fail");
        Assert(before.SequenceEqual(File.ReadAllBytes(store.Path)), "Rejected renames must preserve durable bytes");

        var legacy = JsonNode.Parse(before)!.AsObject();
        foreach (var row in legacy["Subscriptions"]!.AsArray()) row!.AsObject().Remove("ShortName");
        byte[] old = Encoding.UTF8.GetBytes(legacy.ToJsonString());
        File.WriteAllBytes(store.Path, old);
        Assert(store.Read().Subscriptions.All(row => row.ShortName == "") && old.SequenceEqual(File.ReadAllBytes(store.Path)),
            "Released v1 without ShortName must read without overwriting its file");
        var migrated = store.ApplyScan(source.Id, new("Плейлист", [], "", "", "Канал после обновления"), []);
        Assert(migrated.Subscriptions.Single(row => row.Id == source.Id).ShortName == "Канал после обновления" &&
            new SubscriptionStore(directory).Read().Subscriptions.Count == 2,
            "The next successful scan may safely populate a legacy subscription's folder");
    }

    private static void InitialSnapshotsAndFailures(string directory)
    {
        var store = new SubscriptionStore(directory);
        Assert(store.Read().Subscriptions.Count == 0 && store.Read().Items.Count == 0,
            "Missing subscriptions file must read as empty");
        Assert(!Directory.Exists(directory), "Reading an absent store must not create files or directories");
        string old = "https://media.example/Old?token=one";
        string newer = "https://media.example/New?token=two";
        var source = store.Add("http://any-platform.example/collection?category=clips", false, "https://feeds.example/atom");
        Assert(!source.Initialized && !source.DownloadExisting && source.FeedUrl.EndsWith("/atom"),
            "Arbitrary HTTP collections and optional feeds must be stored before scanning");
        source.Url = "https://mutated.example/";
        Assert(store.Read().Subscriptions[0].Url.StartsWith("http://any-platform.example/", StringComparison.Ordinal),
            "Returned subscription definitions must be detached from durable state");

        var baseline = store.ApplyScan(source.Id, Scan(old), []);
        Assert(Item(baseline, source.Id, old).State == "Baseline" && !Item(baseline, source.Id, old).InitialQueuePending,
            "Default first scan must take a baseline without queueing or claiming success");
        var completedScan = baseline.Subscriptions.Single();
        Assert(completedScan.Initialized && completedScan.LastSuccessUtc.HasValue && completedScan.LastError == "",
            "Only a complete successful scan initializes the source");
        var later = store.ApplyScan(source.Id, Scan(old, newer, newer), []);
        Assert(later.Items.Count == 2 && Item(later, source.Id, old).State == "Baseline" &&
            Item(later, source.Id, newer).State == "New" && !Item(later, source.Id, newer).InitialQueuePending,
            "Later unseen exact URLs must enter the inbox once; baseline must remain baseline");
        var withDisappeared = store.ApplyScan(source.Id, Scan(newer), []);
        Assert(withDisappeared.Items.Count == 2, "A disappearing entry must not be forgotten and rediscovered as new later");

        byte[] beforeInvalidScan = File.ReadAllBytes(store.Path);
        Refuses(() => store.ApplyScan(source.Id, Scan("file:///not-a-collection-item"), []), "Invalid scan entries must be refused");
        Refuses(() => store.ApplyScan(source.Id, Scan(newer), ["not-a-success-url"]), "Invalid success history must be refused");
        Refuses(() => store.ApplyScan(source.Id, new("Too large", Enumerable.Range(0, 10001)
            .Select(i => new DiscoveredMedia("https://media.example/" + i, "item")).ToList(), "", ""), []),
            "An incomplete capped scan may not initialize or update a subscription");
        Assert(beforeInvalidScan.SequenceEqual(File.ReadAllBytes(store.Path)),
            "A refused scan must not change items, baseline or the last successful scan timestamp");

        DateTimeOffset? successTime = store.Read().Subscriptions[0].LastSuccessUtc;
        store.RecordFailure(source.Id, "Enumeration interrupted");
        var failed = store.Read();
        Assert(failed.Subscriptions[0].LastError == "Enumeration interrupted" && failed.Subscriptions[0].LastSuccessUtc == successTime &&
            failed.Items.Count == 2 && failed.Subscriptions[0].Initialized,
            "A scan failure must preserve the previous complete snapshot and success timestamp");
        store.SetPaused(source.Id, true);
        Assert(new SubscriptionStore(directory).Read().Subscriptions[0].Paused, "Pause must survive a new store instance");
        store.ApplyScan(source.Id, new("Renamed", Scan(old, newer).Items, "https://new-feed.example/rss", "RSS unavailable"), []);
        var retried = store.Read().Subscriptions[0];
        Assert(retried.Title == "Renamed" && retried.LastError == "" && retried.LastWarning == "RSS unavailable" &&
            retried.FeedUrl == "https://new-feed.example/rss", "A successful retry must persist metadata and optional-feed warning");

        var backlog = store.Add("https://other-platform.example/playlist", true);
        store.RecordFailure(backlog.Id, "First scan failed");
        Assert(!store.Read().Subscriptions.Single(s => s.Id == backlog.Id).Initialized,
            "A failed first scan must leave the subscription retryable and uninitialized");
        store.ApplyScan(backlog.Id, Scan(old, newer), []);
        var pending = new SubscriptionStore(directory).Read();
        Assert(pending.Items.Where(i => i.SubscriptionId == backlog.Id).All(i => i.State == "New" && i.InitialQueuePending),
            "Download-existing first scan must keep its explicit queue intent until the GUI confirms queueing");
        store.MarkQueued(backlog.Id, [old]);
        store.Skip(backlog.Id, [newer]);
        store.MarkQueued(backlog.Id, [old]);
        var selected = store.Read();
        Assert(Item(selected, backlog.Id, old).State == "Queued" && !Item(selected, backlog.Id, old).InitialQueuePending &&
            Item(selected, backlog.Id, newer).State == "Skipped" && !Item(selected, backlog.Id, newer).InitialQueuePending,
            "Only selected queue/skip decisions may clear the initial queue intent");
        byte[] beforeSelection = File.ReadAllBytes(store.Path);
        Refuses(() => store.Skip(backlog.Id, [old]), "Skipping may not silently remove an already queued material");
        Refuses(() => store.MarkQueued(backlog.Id, [newer, "https://missing.example/item"]), "Unknown selected items must fail atomically");
        Assert(beforeSelection.SequenceEqual(File.ReadAllBytes(store.Path)), "Rejected selections may not partly update an earlier selected row");
        store.MarkQueued(backlog.Id, [newer]);
        Assert(Item(store.Read(), backlog.Id, newer).State == "Queued", "Explicit queueing may restore a previously skipped material");
        Refuses(() => store.Add("https://other-platform.example/playlist", false), "Exact duplicate collection URLs must be rejected");
        Assert(!File.ReadAllText(store.Path).Contains("StateLabel", StringComparison.Ordinal), "Display-only labels must not leak into the shared JSON schema");
        Assert(!File.ReadAllBytes(store.Path).Take(3).SequenceEqual(new byte[] { 0xef, 0xbb, 0xbf }), "Store JSON must be UTF-8 without a BOM");

        // Subscription removal owns only these records, never downloader data.
        foreach (string name in new[] { "session.json", "successful-downloads.jsonl", "downloaded.mp4" })
            File.WriteAllText(Path.Combine(directory, name), "KEEP " + name);
        store.Remove(source.Id);
        Assert(store.Read().Items.All(i => i.SubscriptionId != source.Id) && store.Read().Subscriptions.Count == 1,
            "Removal must delete only the selected subscription and its items");
        foreach (string name in new[] { "session.json", "successful-downloads.jsonl", "downloaded.mp4" })
            Assert(File.ReadAllText(Path.Combine(directory, name)) == "KEEP " + name, "Removing a subscription must preserve media, session and history");
    }

    private static void SuccessfulProcessingAndNotices(string directory)
    {
        var store = new SubscriptionStore(directory);
        string shared = "https://media.example/Video?id=42&token=one";
        string differentCase = "https://media.example/video?id=42&token=one";
        string differentQuery = "https://media.example/Video?id=42&token=two";
        var first = store.Add("https://one.example/collection", true);
        var second = store.Add("https://two.example/collection", false);
        var third = store.Add("https://three.example/collection", false);
        store.ApplyScan(first.Id, Scan(shared), []);
        store.Skip(first.Id, [shared]);
        store.ApplyScan(second.Id, Scan(), []);
        store.ApplyScan(second.Id, Scan(shared, differentCase, differentQuery), []);
        Assert(Item(store.Read(), second.Id, shared).State == "New", "A skipped matching URL is not evidence of successful processing");
        store.ApplyScan(third.Id, Scan(shared), []);
        Assert(Item(store.Read(), third.Id, shared).State == "Baseline", "A matching baseline is not successful processing or a duplicate notice");
        Assert(store.Read().Items.All(i => !i.DuplicateNotified && i.DuplicateSubscriptionId == ""),
            "Discovery, baseline and skipping must not produce success notices");
        store.MarkQueued(first.Id, [shared]);
        store.MarkQueued(second.Id, [shared]);
        Assert(store.Read().Items.All(i => i.State != "AlreadyProcessed"), "Queueing alone must not create a cross-subscription notice");
        store.MarkSuccessful(shared);
        var success = store.Read();
        Assert(Item(success, first.Id, shared).State == "Processed" && Item(success, first.Id, shared).SuccessAttributed,
            "The first queued subscription must deterministically own the real success");
        foreach (var source in new[] { second, third })
        {
            var item = Item(success, source.Id, shared);
            Assert(item.State == "AlreadyProcessed" && item.DuplicateSubscriptionId == first.Id && !item.DuplicateNotified && !item.InitialQueuePending,
                "A genuine success must persist an unacknowledged notice for every other exact-URL subscription item");
        }
        Assert(Item(success, second.Id, differentCase).State == "New" && Item(success, second.Id, differentQuery).State == "New",
            "Case or query variants must not match by normalized URL or video/extractor ID");
        store.AcknowledgeDuplicates([Item(success, second.Id, shared)]);
        store.MarkSuccessful(shared);
        store.ApplyScan(third.Id, Scan(shared), [shared]);
        var restarted = new SubscriptionStore(directory).Read();
        Assert(Item(restarted, second.Id, shared).DuplicateNotified && !Item(restarted, third.Id, shared).DuplicateNotified,
            "Acknowledgment must be durable and repeated scans/success callbacks may neither replay nor acknowledge another source's notice");
        store.Remove(first.Id);
        var removed = store.Read();
        Assert(Item(removed, second.Id, shared).DuplicateSubscriptionId == first.Id && Item(removed, second.Id, shared).DuplicateNotified &&
            !Item(removed, third.Id, shared).DuplicateNotified,
            "Removing the success source must preserve notice provenance and acknowledgment as a tombstone");
        store.AcknowledgeDuplicates([Item(removed, third.Id, shared)]);
        Assert(Item(store.Read(), third.Id, shared).DuplicateNotified, "A pending notice remains acknowledgeable after source removal");

        var later = store.Add("https://later.example/collection", false);
        string historyOnly = "https://media.example/from-global-history";
        var global = store.ApplyScan(later.Id, Scan(historyOnly), [historyOnly]);
        var globalItem = Item(global, later.Id, historyOnly);
        Assert(globalItem.State == "Processed" && !globalItem.SuccessAttributed && globalItem.DuplicateSubscriptionId == "" && !globalItem.DuplicateNotified,
            "Global real success without a known subscription source must not fabricate a cross-source notice");
        var exactUrl = store.Add("https://LATER.example/collection", false);
        Assert(exactUrl.Id != later.Id, "Subscription identities and source matching use exact ordinal URLs");
        var afterRestart = new SubscriptionStore(directory);
        afterRestart.ApplyScan(exactUrl.Id, Scan(historyOnly), []);
        afterRestart.MarkSuccessful(historyOnly);
        Assert(afterRestart.Read().Items.Where(i => i.Url == historyOnly).All(i =>
                i.State == "Processed" && !i.SuccessAttributed && i.DuplicateSubscriptionId == "" && !i.DuplicateNotified),
            "Persisted global-only success must remain unattributed when another subscription discovers it later");

        var afterDeletion = store.Add("https://after-deletion.example/collection", false);
        var inherited = store.ApplyScan(afterDeletion.Id, Scan(shared), [shared]);
        Assert(Item(inherited, afterDeletion.Id, shared).State == "AlreadyProcessed" &&
            Item(inherited, afterDeletion.Id, shared).DuplicateSubscriptionId == first.Id,
            "Existing real-success provenance must remain usable after its origin subscription is deleted");
    }

    private static void CrashRecovery(string directory)
    {
        var store = new SubscriptionStore(directory);
        string url = "https://media.example/queued-before-crash";
        var first = store.Add("https://source.example/archive", true);
        var second = store.Add("https://source.example/another", true);
        store.ApplyScan(first.Id, Scan(url), []);
        store.ApplyScan(second.Id, Scan(url), []);
        store.MarkQueued(first.Id, [url]);
        var before = store.Read();
        var recovered = new SubscriptionStore(directory).ReconcileSuccess([url]);
        Assert(Item(recovered, first.Id, url).State == "Processed" && Item(recovered, second.Id, url).State == "AlreadyProcessed" &&
            !Item(recovered, second.Id, url).InitialQueuePending,
            "Global success must recover a missed UI success callback without another scan or download");
        Assert(recovered.Subscriptions.Zip(before.Subscriptions).All(pair =>
            pair.First.LastAttemptUtc == pair.Second.LastAttemptUtc && pair.First.LastSuccessUtc == pair.Second.LastSuccessUtc &&
            pair.First.Initialized == pair.Second.Initialized), "Offline reconciliation must not pretend that a collection was scanned");
        recovered.Items.Clear();
        Assert(store.Read().Items.Count == 2, "Returned scan/reconciliation snapshots must be detached");

        var unseenSource = store.Add("https://source.example/not-scanned", false);
        store.ReconcileSuccess(["https://media.example/not-discovered"]);
        Assert(!store.Read().Subscriptions.Single(s => s.Id == unseenSource.Id).Initialized,
            "A history success must not initialize an unscanned subscription");

        string historyOnly = "https://media.example/unknown-success-source";
        store.ApplyScan(first.Id, Scan(historyOnly), []);
        store.ApplyScan(second.Id, Scan(historyOnly), []);
        store.ReconcileSuccess([historyOnly]);
        store.ReconcileSuccess([historyOnly]);
        var twiceRecovered = store.Read().Items.Where(i => i.Url == historyOnly).ToList();
        Assert(twiceRecovered.Count == 2 && twiceRecovered.All(i => i.State == "Processed" && i.DuplicateSubscriptionId == ""),
            "Repeated reconciliation of an unattributed global success must never fabricate a new cross-source notice");
    }

    private static void StrictValidation(string directory)
    {
        var store = new SubscriptionStore(directory);
        var source = store.Add("https://valid.example/collection", false);
        store.ApplyScan(source.Id, Scan("https://valid.example/material"), []);
        string valid = File.ReadAllText(store.Path);
        var malformed = new List<string> { "", "{", "null", valid.Replace("\"Version\":1", "\"Version\":2", StringComparison.Ordinal),
            valid.Replace("\"Version\":1", "\"Version\":1,\"Version\":1", StringComparison.Ordinal) };
        void Variant(Action<JsonObject> mutate)
        {
            var snapshot = JsonNode.Parse(valid)!.AsObject();
            mutate(snapshot);
            malformed.Add(snapshot.ToJsonString());
        }
        Variant(root => root["Unknown"] = true);
        Variant(root => root.Remove("Version"));
        Variant(root => root["Items"] = null);
        Variant(root => root["Subscriptions"]![0]!["Unknown"] = false);
        Variant(root => root["Subscriptions"]![0]!["Initialized"] = "false");
        Variant(root => root["Subscriptions"]![0]!["LastAttemptUtc"] = "not-a-date");
        Variant(root => root["Subscriptions"]![0]!["Title"] = null);
        Variant(root => root["Subscriptions"]![0]!["ShortName"] = null);
        Variant(root => root["Subscriptions"]![0]!["ShortName"] = "../escape");
        Variant(root => root["Subscriptions"]![0]!["ShortName"] = "CON");
        Variant(root => root["Subscriptions"]![0]!["Url"] = "file:///outside");
        Variant(root => root["Subscriptions"]!.AsArray().Add(root["Subscriptions"]![0]!.DeepClone()));
        Variant(root => root["Items"]!.AsArray().Add(root["Items"]![0]!.DeepClone()));
        Variant(root => root["Items"]![0]!["SubscriptionId"] = "missing-source");
        Variant(root => root["Items"]![0]!["State"] = "Completed");
        Variant(root => root["Items"]![0]!["DiscoveredAtUtc"] = null);
        Variant(root => root["Items"]![0]!["InitialQueuePending"] = true);
        Variant(root => root["Items"]![0]!["SuccessAttributed"] = true);
        Variant(root => root["Items"]![0]!["SuccessAttributed"] = "false");
        Variant(root => root["Items"]![0]!.AsObject().Remove("SuccessAttributed"));
        Variant(root => root["Items"]![0]!["DuplicateNotified"] = true);
        Variant(root => root["Items"]![0]!["State"] = "AlreadyProcessed");
        Variant(root => root["Items"]![0]!.AsObject().Remove("Url"));
        foreach (string damaged in malformed)
        {
            byte[] original = Encoding.UTF8.GetBytes(damaged);
            File.WriteAllBytes(store.Path, original);
            Refuses(() => store.Read(), "Malformed or inconsistent v1 subscription files must fail closed");
            Refuses(() => store.Add("https://never-written.example/", false), "Mutations must not replace a damaged store with empty state");
            Assert(original.SequenceEqual(File.ReadAllBytes(store.Path)), "Rejected reads/mutations must leave corrupt bytes unchanged");
        }
        File.WriteAllText(store.Path, valid, new UTF8Encoding(false));
        Assert(store.Read().Items.Count == 1, "Restoring the original valid file must restore access without a hidden cached snapshot");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Rejected operations must not leave temporary stores behind");
    }

    private static void LargeCompleteScan(string directory)
    {
        var store = new SubscriptionStore(directory);
        var source = store.Add("https://large.example/collection", true);
        var urls = Enumerable.Range(0, 10_000).Select(index => "https://large.example/item-" + index).ToArray();
        var history = urls.Concat(Enumerable.Range(0, 40_000).Select(index => "https://other.example/history-" + index)).ToArray();
        var complete = store.ApplyScan(source.Id, Scan(urls), history);
        Assert(complete.Items.Count == 10_000 && complete.Items.All(i => i.State == "Processed" && !i.InitialQueuePending),
            "A full collection at the supported cap must reconcile only actual successes from a much larger global history");
        var repeated = store.ReconcileSuccess(history);
        Assert(repeated.Items.Count == 10_000 && repeated.Items.All(i => i.State == "Processed" && i.DuplicateSubscriptionId == ""),
            "Large unrelated history must neither add subscription items nor fabricate duplicate notices");
    }

    private static void CancellationBeforeCommit(string directory)
    {
        var store = new SubscriptionStore(directory);
        var source = store.Add("https://cancel.example/collection", false);
        byte[] before = File.ReadAllBytes(store.Path);
        using var cancellation = new CancellationTokenSource();
        using var entered = new ManualResetEventSlim();
        using var mutex = new Mutex(false, @"Local\VideoDownloader.Subscriptions");
        mutex.WaitOne();
        Task<bool>? operation = null;
        bool stopped;
        try
        {
            operation = Task.Run(() =>
            {
                entered.Set();
                try { store.ApplyScan(source.Id, Scan("https://cancel.example/video"), [], cancellation.Token); }
                catch (OperationCanceledException) { return true; }
                return false;
            });
            Assert(entered.Wait(TimeSpan.FromSeconds(3)), "The commit worker must start");
            cancellation.Cancel();
            stopped = operation.Wait(TimeSpan.FromSeconds(3));
        }
        finally { mutex.ReleaseMutex(); }
        Assert(stopped && operation!.GetAwaiter().GetResult(), "Cancellation while awaiting the store lock must abort before committing");
        Assert(before.SequenceEqual(File.ReadAllBytes(store.Path)), "Cancelled late scan must leave baseline, items and success timestamp unchanged");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Cancelled commit must not retain a temporary snapshot");
    }

    private static void ConcurrentWriters(string directory)
    {
        Task.WaitAll(Enumerable.Range(0, 12).Select(index => Task.Run(() =>
        {
            var store = new SubscriptionStore(directory);
            var source = store.Add("https://parallel.example/source-" + index, false);
            store.ApplyScan(source.Id, Scan("https://parallel.example/item-" + index), []);
            store.SetPaused(source.Id, true);
        })).ToArray());
        var snapshot = new SubscriptionStore(directory).Read();
        Assert(snapshot.Subscriptions.Count == 12 && snapshot.Items.Count == 12 && snapshot.Subscriptions.All(s => s.Initialized && s.Paused),
            "Separate store instances must serialize full read-modify-write transactions without losing another writer's updates");
        Assert(!Directory.EnumerateFiles(directory, "*.tmp").Any(), "Atomic writes must clean up all temporary files");
    }
}
