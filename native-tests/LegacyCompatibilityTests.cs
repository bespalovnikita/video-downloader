using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using VideoDownloader.Native;

/// <summary>
/// Reads a fixed v1 corpus through the production readers. No PowerShell runtime
/// or current writer generates the input schema during these tests.
/// </summary>
internal static class LegacyCompatibilityTests
{
    private const string ExactUrl = "https://legacy.example/Video?token=Exact";
    private const string LowerPathUrl = "https://legacy.example/video?token=Exact";
    private const string LowerQueryUrl = "https://legacy.example/Video?token=exact";

    internal static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-legacy-fixtures-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            RestoreSessionAndEvents(Path.Combine(root, "session"));
            RestoreSubscriptions(Path.Combine(root, "subscriptions"));
            RestoreHistory(Path.Combine(root, "history"));
            RestorePreview(Path.Combine(root, "preview"));
            RefuseCorruptStores(Path.Combine(root, "corrupt"));
            Console.WriteLine("PASS: fixed legacy v1 session, Result replay, cleanup, subscriptions, history and preview fixtures");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void RestoreSessionAndEvents(string root)
    {
        var data = new AppPersistence(Path.Combine(root, "profile"));
        string original = Path.Combine(root, "original-output");
        string selected = Path.Combine(root, "new-output");
        string run = Path.Combine(root, "legacy-run");
        string savedStage = Path.Combine(run, "download-1-11111111111111111111111111111111");
        string eventStage = Path.Combine(run, "download-2-22222222222222222222222222222222");
        string finishedStage = Path.Combine(run, "download-3-33333333333333333333333333333333");
        string savedMedia = Path.Combine(original, "saved.mp4");
        string eventMedia = Path.Combine(original, "event.mp4");
        string finishedMedia = Path.Combine(original, "finished.mp4");
        string finishedSidecar = Path.Combine(original, "finished.en.srt");
        string unownedMedia = Path.Combine(original, "unowned.mp4");
        string events = Path.Combine(run, "events.jsonl");
        var tokens = new Dictionary<string, string>
        {
            ["SELECTED_OUTPUT"] = selected, ["ORIGINAL_OUTPUT"] = original,
            ["COOKIES_FILE"] = Path.Combine(root, "cookies.txt"),
            ["RUN_DIR"] = run, ["CANCEL_DIR"] = Path.Combine(run, "cancel"),
            ["EVENTS_FILE"] = events, ["SAVED_STAGE"] = savedStage,
            ["EVENT_STAGE"] = eventStage, ["FINISHED_STAGE"] = finishedStage,
            ["SAVED_MEDIA"] = savedMedia, ["EVENT_MEDIA"] = eventMedia,
            ["FINISHED_MEDIA"] = finishedMedia, ["FINISHED_SIDECAR"] = finishedSidecar,
            ["UNOWNED_MEDIA"] = unownedMedia
        };
        Materialize("session-v1.json", data.SessionPath, tokens);
        Materialize("events-v1.jsonl", events, tokens);
        foreach (string directory in new[] { original, selected, savedStage, eventStage, finishedStage })
            Directory.CreateDirectory(directory);
        foreach (string file in new[] { savedMedia, eventMedia, finishedMedia, finishedSidecar, unownedMedia })
            File.WriteAllText(file, "KEEP " + Path.GetFileName(file));
        File.WriteAllText(Path.Combine(savedStage, "partial.part"), "PARTIAL");
        File.WriteAllText(Path.Combine(eventStage, "partial.part"), "PARTIAL");
        string unrelated = Path.Combine(selected, "unrelated.mp4");
        File.WriteAllText(unrelated, "UNRELATED");
        byte[] checkpointBytes = File.ReadAllBytes(data.SessionPath);
        byte[] eventBytes = File.ReadAllBytes(events);

        var restored = data.Restore() ?? throw new Exception("Legacy session fixture was not restored");
        Require(!restored.LegacyEngineWasActive && restored.Queue.Count == 6, "A stopped legacy engine must restore every fixture row");
        var options = restored.Settings;
        Require(options.Profile == "Music MP3" && options.AudioOnly && !options.Archive && options.ShowVideoTitles &&
            options.WatchClipboard && options.Output == selected && options.Threads == "3" && options.Fragments == "2" &&
            options.RateLimit == "2M" && options.Cookies == tokens["COOKIES_FILE"] && options.WriteSubs && options.EmbedThumbnail &&
            options.Sponsor && !options.EmbedChapters,
            "Legacy user settings, canonical profile id and current output choice must survive reading");
        var saved = Row(restored, "https://legacy.example/cleanup-saved?token=Exact");
        var fromEvent = Row(restored, "https://legacy.example/cleanup-event");
        foreach (var pair in new[] { (Row: saved, Media: savedMedia, Stage: savedStage), (Row: fromEvent, Media: eventMedia, Stage: eventStage) })
            Require(pair.Row.PendingRemoval && !pair.Row.CanResume && pair.Row.AttemptFinished &&
                pair.Row.CleanupError.StartsWith("Locked file", StringComparison.Ordinal) && pair.Row.OutputRoot == original &&
                pair.Row.RunDir == run && pair.Row.StageDir == pair.Stage &&
                pair.Row.OwnedPaths.SequenceEqual([pair.Media]) && pair.Row.CleanupPaths.SequenceEqual([pair.Media]),
                "Saved and Result-event cleanup must preserve the original roots and complete ownership manifest");
        Require(saved.Title == "Название из прежней очереди" && saved.TransferredBytes == 321 &&
            saved.SubscriptionId == "legacy-source-a" && saved.SubscriptionName == "123",
            "Legacy row metadata and subscription folder snapshots must survive recovery");
        var deferred = Row(restored, "https://legacy.example/next-start");
        Require(deferred.Status == "Queued" && deferred.DeferredToNextStart && deferred.Path == "" &&
            deferred.OwnedPaths.Count == 0 && deferred.OutputRoot == selected && deferred.CanResume,
            "A stale Result must not complete a re-added deferred attempt; old rows without a root use saved settings");
        var completed = Row(restored, ExactUrl);
        Require(completed.Status == "Completed" && completed.State == "Done" && completed.Progress == 100 &&
            completed.Path == finishedMedia && completed.OwnedPaths.SequenceEqual([finishedMedia, finishedSidecar]) &&
            completed.OutputRoot == original && completed.RunDir == run && completed.StageDir == finishedStage,
            "A complete legacy Result must recover media, sidecars and publication boundaries");
        var lowerCase = Row(restored, LowerPathUrl);
        Require(lowerCase.Status == "Interrupted" && lowerCase.Path == "" && lowerCase.OwnedPaths.Count == 0,
            "Path/query case variants, progress=100, malformed Result fields and a truncated event tail cannot manufacture success");
        Require(checkpointBytes.SequenceEqual(File.ReadAllBytes(data.SessionPath)) && eventBytes.SequenceEqual(File.ReadAllBytes(events)),
            "Reading/replaying a legacy checkpoint must not rewrite its source files");

        data.Save(restored.Queue, options);
        var roundtrip = data.Restore()!;
        var durableSaved = Row(roundtrip, saved.Url);
        var durableEvent = Row(roundtrip, fromEvent.Url);
        Require(durableSaved.PendingRemoval && durableEvent.PendingRemoval &&
            durableSaved.CleanupPaths.SequenceEqual(saved.CleanupPaths) && durableEvent.CleanupPaths.SequenceEqual(fromEvent.CleanupPaths) &&
            durableSaved.OwnedPaths.SequenceEqual(saved.OwnedPaths) && durableEvent.OwnedPaths.SequenceEqual(fromEvent.OwnedPaths) &&
            durableSaved.OutputRoot == original && durableEvent.OutputRoot == original && durableEvent.StageDir == eventStage &&
            durableSaved.SubscriptionId == saved.SubscriptionId && durableSaved.SubscriptionName == "123" &&
            Row(roundtrip, deferred.Url).DeferredToNextStart && roundtrip.Settings.ShowVideoTitles,
            "Native save/restart must retain the legacy cleanup transaction, deferred attempt, title mode and subscription routing");
        DownloadCleanup.Retry(durableSaved);
        DownloadCleanup.Retry(durableEvent);
        Require(!File.Exists(savedMedia) && !File.Exists(eventMedia) && !Directory.Exists(savedStage) && !Directory.Exists(eventStage) &&
            File.Exists(finishedMedia) && File.Exists(finishedSidecar) && File.ReadAllText(unrelated) == "UNRELATED",
            "Retry must clean only the legacy-owned attempts using original roots, even after the output choice changes");
        var unowned = Row(roundtrip, "https://legacy.example/unowned-result");
        Refuses<IOException>(() => DownloadCleanup.Retry(unowned), "An old Path field is not proof of file ownership");
        Require(File.ReadAllText(unownedMedia) == "KEEP unowned.mp4" && unowned.PendingRemoval && unowned.CleanupPaths.Count == 0,
            "Refusing unowned legacy cleanup must preserve the file and leave removal retryable");
    }

    private static void RestoreSubscriptions(string root)
    {
        var store = new SubscriptionStore(root);
        Materialize("subscriptions-v1.json", store.Path);
        byte[] before = File.ReadAllBytes(store.Path);
        var initial = store.Read();
        Require(initial.Subscriptions.Count == 2 && initial.Items.Count == 7 &&
            initial.Subscriptions.Single(source => source.Id == "legacy-source-a").ShortName == "" &&
            initial.Subscriptions.Single(source => source.Id == "legacy-source-b").ShortName == "Архив",
            "The original v1 schema without ShortName and later v1 folder snapshots must both read");
        Require(initial.Subscriptions[0].Paused && !initial.Subscriptions[0].DownloadExisting &&
            initial.Subscriptions[0].LastSuccessUtc == DateTimeOffset.Parse("2026-10-17T10:00:00Z") &&
            initial.Subscriptions[0].LastError == "Previous scan failed" && initial.Subscriptions[0].LastWarning == "RSS unavailable",
            "Pause, baseline choice, previous scan timestamps and diagnostics must remain intact");
        Require(before.SequenceEqual(File.ReadAllBytes(store.Path)), "A legacy subscription read must not silently migrate its file");
        store.SetShortName("legacy-source-a", "123");
        store.ReconcileSuccess([ExactUrl]);
        var reopened = new SubscriptionStore(root).Read();
        var noticeA = Item(reopened, "legacy-source-a", ExactUrl);
        var noticeB = Item(reopened, "legacy-source-b", ExactUrl);
        Require(noticeA.DuplicateNotified && !noticeB.DuplicateNotified &&
            noticeA.DuplicateSubscriptionId == "removed-source" && noticeB.DuplicateSubscriptionId == "removed-source",
            "Reconciliation after native save must preserve per-source notice acknowledgment and removed-source provenance");
        Require(Item(reopened, "legacy-source-a", LowerPathUrl).State == "New" &&
            Item(reopened, "legacy-source-a", LowerQueryUrl).State == "Skipped" &&
            Item(reopened, "legacy-source-a", "https://legacy.example/baseline").State == "Baseline" &&
            Item(reopened, "legacy-source-b", "https://legacy.example/initial-backlog").InitialQueuePending,
            "Exact URL identity, skipped rows, baseline and pending initial import must survive rewriting");
        store.AcknowledgeDuplicates([noticeB]);
        store.MarkSuccessful("https://legacy.example/global-only");
        var final = new SubscriptionStore(root).Read();
        var global = Item(final, "legacy-source-b", "https://legacy.example/global-only");
        Require(final.Subscriptions[0].ShortName == "123" && final.Subscriptions[1].ShortName == "Архив" &&
            Item(final, "legacy-source-b", ExactUrl).DuplicateNotified &&
            global.State == "Processed" && !global.SuccessAttributed && global.DuplicateSubscriptionId == "",
            "Editing a legacy store must retain folders, acknowledged notices and history-only success without inventing a source");
    }

    private static void RestoreHistory(string root)
    {
        var data = new AppPersistence(root);
        Materialize("history-v1.jsonl", data.HistoryPath);
        // Older appenders also left a valid last JSON record without a newline.
        File.WriteAllText(data.HistoryPath, File.ReadAllText(data.HistoryPath).TrimEnd('\r', '\n'), new UTF8Encoding(false));
        byte[] before = File.ReadAllBytes(data.HistoryPath);
        var history = data.ReadHistory();
        Require(history.Count == 4 && history[ExactUrl] == DateTimeOffset.Parse("2026-10-18T10:00:00Z") &&
            history[LowerPathUrl] == DateTimeOffset.Parse("2026-10-18T10:00:00Z") &&
            history[LowerQueryUrl] == DateTimeOffset.Parse("2026-10-18T12:00:00Z") &&
            history["https://legacy.example/no-timestamp"] == null,
            "Legacy history accepts missing timestamps, keeps the newest duplicate date and distinguishes path/query case");
        Require(before.SequenceEqual(File.ReadAllBytes(data.HistoryPath)), "Reading legacy history must preserve its bytes");
        data.AppendSuccess("https://legacy.example/native-after-upgrade");
        byte[] appended = File.ReadAllBytes(data.HistoryPath);
        Require(appended.Take(before.Length).SequenceEqual(before) && data.ReadHistory().Count == 5 &&
            File.ReadAllLines(data.HistoryPath).Length == 7,
            "A native append must separate an unterminated legacy record without changing old bytes");
    }

    private static void RestorePreview(string root)
    {
        var data = new AppPersistence(root);
        string CacheFile(string url) => Path.Combine(data.PreviewDir,
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant() + ".json");
        string path = CacheFile(ExactUrl);
        Materialize("preview-v1.json", path, new Dictionary<string, string>
        { ["CACHE_SAVED_AT_UTC"] = DateTime.UtcNow.AddHours(-1).ToString("O") });
        byte[] before = File.ReadAllBytes(path);
        using (var previews = new PreviewService("unused-legacy-test-tool", data, () => "", _ => throw new Exception("A cache-only read must not fetch metadata"), _ => { }))
        {
            var cached = previews.Get(ExactUrl);
            Require(cached?.Title == "Название из прежнего кеша" && cached.Uploader == "Название канала" &&
                cached.PlaylistTitle == "Старый плейлист" && cached.Duration == 123.5 && cached.MaxHeight == 1080 &&
                cached.MaxFps == 60 && cached.Codecs == "H264, VP9" && cached.DynamicRange == "HDR" && cached.ThumbnailBase64 == "",
                "The legacy cache schema without PreviewFetchVersion must preserve all text metadata despite an image failure");
            Require(previews.Get(LowerPathUrl) == null && previews.Get(LowerQueryUrl) == null,
                "Legacy memory cache identities must use exact path/query casing");
            Require(before.SequenceEqual(File.ReadAllBytes(path)), "Reading a valid legacy cache record must not rewrite it");
        }
        // A misplaced entry has the right hash filename for the requested URL,
        // but its serialized Url still belongs to another exact URL.
        File.Copy(path, CacheFile(LowerPathUrl));
        using (var restarted = new PreviewService("unused-legacy-test-tool", data, () => "", _ => { }, _ => { }))
        {
            Require(restarted.Get(ExactUrl)?.Title == "Название из прежнего кеша" && restarted.Get(LowerPathUrl) == null &&
                !File.Exists(CacheFile(LowerPathUrl)), "Disk cache identity must match the complete exact URL after restart");
        }
        Materialize("preview-v1.json", path, new Dictionary<string, string>
        { ["CACHE_SAVED_AT_UTC"] = DateTime.UtcNow.AddHours(-73).ToString("O") });
        using var expired = new PreviewService("unused-legacy-test-tool", data, () => "", _ => { }, _ => { });
        Require(expired.Get(ExactUrl) == null && !File.Exists(path), "An expired legacy preview must become a cache miss after 72 hours");
    }

    private static void RefuseCorruptStores(string root)
    {
        var data = new AppPersistence(root);
        Materialize("corrupt-session.json", data.SessionPath, new Dictionary<string, string> { ["SELECTED_OUTPUT"] = root });
        byte[] session = File.ReadAllBytes(data.SessionPath);
        Refuses<JsonException>(() => data.Restore(), "A truncated legacy session must be rejected");
        Require(session.SequenceEqual(File.ReadAllBytes(data.SessionPath)), "Failed recovery must leave the damaged session byte for byte intact");

        var store = new SubscriptionStore(root);
        Materialize("corrupt-subscriptions.json", store.Path);
        byte[] subscriptions = File.ReadAllBytes(store.Path);
        Refuses<InvalidDataException>(() => store.Read(), "An unknown legacy subscription schema must be rejected");
        Refuses<InvalidDataException>(() => store.Add("https://legacy.example/no-overwrite", false), "A mutator must not replace unreadable subscriptions");
        Require(subscriptions.SequenceEqual(File.ReadAllBytes(store.Path)), "Rejected subscription reads/updates must preserve the original bytes");

        Materialize("corrupt-history.jsonl", data.HistoryPath);
        File.WriteAllText(data.HistoryPath, File.ReadAllText(data.HistoryPath).TrimEnd('\r', '\n'), new UTF8Encoding(false));
        byte[] history = File.ReadAllBytes(data.HistoryPath);
        Refuses<InvalidDataException>(() => data.ReadHistory(), "A truncated legacy history tail must be rejected");
        Refuses<InvalidDataException>(() => data.AppendSuccess("https://legacy.example/no-overwrite"), "A native append must refuse to extend a corrupt legacy tail");
        Require(history.SequenceEqual(File.ReadAllBytes(data.HistoryPath)), "Rejected history reads/appends must preserve the original bytes");
    }

    private static DownloadItem Row(SessionSnapshot snapshot, string url) => snapshot.Queue.Single(row => row.Url.Equals(url, StringComparison.Ordinal));
    private static SubscriptionItem Item(SubscriptionSnapshot snapshot, string id, string url) =>
        snapshot.Items.Single(item => item.SubscriptionId == id && item.Url.Equals(url, StringComparison.Ordinal));

    private static void Materialize(string fixture, string destination, IReadOnlyDictionary<string, string>? tokens = null)
    {
        string source = Path.Combine(AppContext.BaseDirectory, "fixtures", "legacy", fixture);
        if (!File.Exists(source)) throw new FileNotFoundException("Missing committed legacy fixture", source);
        string text = File.ReadAllText(source, Encoding.UTF8);
        // Each token occupies a complete JSON string. Encode the replacement as
        // JSON so Windows slashes, quotes and Unicode never corrupt the fixture.
        text = Regex.Replace(text, "\"__([A-Z_]+)__\"", match =>
            tokens != null && tokens.TryGetValue(match.Groups[1].Value, out string? value)
                ? JsonSerializer.Serialize(value)
                : throw new InvalidDataException("Unbound legacy fixture token: " + match.Value));
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.WriteAllText(destination, text, new UTF8Encoding(false));
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static void Refuses<T>(Action action, string message) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }
}
