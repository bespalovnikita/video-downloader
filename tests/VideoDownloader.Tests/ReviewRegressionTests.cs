using System.Diagnostics;
using System.Collections.Concurrent;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

// These fixtures run in the test executable itself, never against public sites.
// The existing integration suite still exercises the bundled real yt-dlp.exe.
internal static class ReviewRegressionTests
{
    private const string FixtureBase = "https://native-regression.invalid/";

    internal static bool TryRunFixture(string[] args)
    {
        string? url = args.LastOrDefault();
        if (url?.StartsWith(FixtureBase, StringComparison.Ordinal) != true) return false;
        if (args.Contains("--dump-single-json", StringComparer.Ordinal))
        {
            if (url != FixtureBase + "preview-metadata")
                throw new InvalidOperationException("Unexpected offline preview URL.");
            Console.WriteLine(JsonSerializer.Serialize(new {
                id = "offline-preview", title = "Native preview works",
                uploader = "Offline fixture", extractor = "fixture",
                duration = 123.0, thumbnail = "",
                formats = new[] { new { vcodec = "avc1.640028", height = 1080, fps = 60.0, dynamic_range = "SDR" } }
            }));
            return true;
        }
        int outputIndex = Array.IndexOf(args, "-o");
        if (outputIndex < 0 || outputIndex + 1 >= args.Length) throw new Exception("Missing fixture output path");
        string stage = Path.GetDirectoryName(args[outputIndex + 1])!;
        Directory.CreateDirectory(stage);
        if (url.Contains("error-", StringComparison.Ordinal))
        {
            Console.Error.WriteLine(url.EndsWith("error-transient", StringComparison.Ordinal)
                ? "ERROR: Unable to download webpage: HTTP Error 503: Service unavailable"
                : url.EndsWith("error-tls", StringComparison.Ordinal)
                    ? "ERROR: TLS handshake failed"
                    : "ERROR: HTTP Error 403: Forbidden; connection reset for video503");
            Environment.ExitCode = 1;
            return true;
        }
        if (url.EndsWith("no-media", StringComparison.Ordinal))
        {
            File.WriteAllText(Path.Combine(stage, "metadata.info.json"), "{}");
            return true; // yt-dlp may skip all media and still exit zero.
        }
        string media = Path.Combine(stage, "fixture.mp4");
        File.WriteAllBytes(media, new byte[120]);
        if(url.EndsWith("fallback-stale-marker",StringComparison.Ordinal))
        {
            int formatIndex=Array.IndexOf(args,"-f");
            if(args[formatIndex+1].Contains("height<=2160",StringComparison.Ordinal))
            {
                Console.WriteLine("__VD_FILE__:"+media);
                Console.Error.WriteLine("ERROR: Requested format is not available");
                Environment.ExitCode=1;
            }
            return true;
        }
        if (url.EndsWith("unreported-media", StringComparison.Ordinal)) return true;
        if (url.EndsWith("missing-media", StringComparison.Ordinal))
        {
            Console.WriteLine("__VD_FILE__:" + Path.Combine(stage, "not-created.mp4"));
            return true;
        }
        if (url.EndsWith("external-media", StringComparison.Ordinal))
        {
            int cookieIndex = Array.IndexOf(args, "--cookies");
            Console.WriteLine("__VD_FILE__:" + args[cookieIndex + 1]);
            return true;
        }
        Console.WriteLine("__VD_TITLE__:Fixture");
        if(url.EndsWith("multi-component",StringComparison.Ordinal) || url.EndsWith("postprocess-error",StringComparison.Ordinal))
        {
            Console.WriteLine("__VD_PLAN__:{\"id\":\"test\",\"format_id\":\"137+140\",\"requested_formats.:.format_id\":[\"137\",\"140\"]}");
            foreach(var (component,percent,video,audio) in new[]{("137",0,"avc1","none"),("137",100,"avc1","none"),("140",0,"none","mp4a"),("140",50,"none","mp4a"),("140",100,"none","mp4a")})
                Console.WriteLine($"__VD_PROGRESS__|{percent}%|1KiB/s|1|{percent}|100|test:{component}|{video}|{audio}");
            if(url.EndsWith("postprocess-error",StringComparison.Ordinal))
            {
                Console.Error.WriteLine("ERROR: Postprocessing: ffmpeg failed: connection reset");
                Environment.ExitCode=1;return true;
            }
            Console.WriteLine("[Merger] Merging formats into fixture.mp4");
            Console.WriteLine("__VD_FILE__:"+media);
            return true;
        }
        foreach (var (stream, bytes, percent) in new[] {
            ("video", 100, 100), ("video", 100, 100), ("audio", 50, 50),
            ("video", 20, 20), ("video", 70, 70), ("audio", 50, 50) })
            Console.WriteLine($"__VD_PROGRESS__|{percent}%|1KiB/s|1|{bytes}|120|{stream}");
        if(url.EndsWith("unknown-percent",StringComparison.Ordinal))
            foreach(string percent in new[]{"N/A","NaN","Infinity"})
                Console.WriteLine($"__VD_PROGRESS__|{percent}|1KiB/s|1|50|120|audio");
        Console.Error.WriteLine("[fixture] Process stderr remains identifiable");
        Console.WriteLine("__VD_FILE__:" + media);
        return true;
    }

    internal static async Task RunAsync()
    {
        DownloadProgressPlanTests.Run();
        string root = Path.Combine(Path.GetTempPath(), "vd-review-regressions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var failures = new List<Exception>();
        async Task Check(string name, Func<string, Task> test)
        {
            string directory = Path.Combine(root, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); Console.WriteLine("PASS: " + name); }
            catch (Exception error) { failures.Add(new Exception(name, error)); Console.WriteLine($"FAIL: {name}: {error.Message}"); }
        }
        try
        {
            await Check("legacy history gains a date and keeps the latest success", directory =>
            {
                var data = new AppPersistence(directory);
                const string url = "https://example.com/legacy-history";
                File.WriteAllLines(data.HistoryPath, new[] {
                    JsonSerializer.Serialize(new { Version = 1, Url = url }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url, CompletedAtUtc = "2026-10-18T10:00:00Z" }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url, CompletedAtUtc = "2026-10-17T10:00:00Z" }),
                    JsonSerializer.Serialize(new { Version = 1, Url = url }) });
                Require(data.ReadHistory()[url] == DateTimeOffset.Parse("2026-10-18T10:00:00Z"),
                    "A later timestamp must replace an unknown legacy date and survive older/undated records");
                return Task.CompletedTask;
            });

            await Check("malformed legacy terminal events leave unfinished rows recoverable", directory =>
            {
                var data = new AppPersistence(directory);
                string events = Path.Combine(directory, "events.jsonl");
                var urls = Enumerable.Range(0, 10).Select(i => $"https://example.com/event-{i}").ToArray();
                File.WriteAllLines(events, new[] {
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[0], Success = "false" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[1] }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[2], Success = false, Cancelled = "true" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[3], Success = true, Path = 42 }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[4], Success = true, OwnedPaths = "not-an-array" }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[5], Success = true, Cancelled = true }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[6], Success = true, OwnedPaths = new object[] { "file.mp4", 42 } }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[7], Success = false }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[8], Success = false, Cancelled = true }),
                    JsonSerializer.Serialize(new { Kind = "Result", Url = urls[9], Success = true, Path = "finished.mp4" }) });
                WriteLegacySession(data, urls, directory, new { Pid = int.MaxValue, StartedUtc = DateTime.UtcNow, EventsPath = events });
                var restored = data.Restore()!;
                Require(restored.Queue.Take(7).All(row => row.Status == "Interrupted" && row.State == "Interrupted" && row.OwnedPaths.Count == 0),
                    "Missing or incorrectly typed terminal flags must not manufacture failed/cancelled results");
                Require(restored.Queue[7].Status == "Failed" && restored.Queue[8].State == "Cancelled" &&
                    restored.Queue[9].Status == "Completed", "Valid failure, cancellation and success events must still reconcile");
                return Task.CompletedTask;
            });

            await Check("live legacy worker blocks replay even while its event file is locked", directory =>
            {
                var data = new AppPersistence(directory);
                string events = Path.Combine(directory, "active-events.jsonl");
                using var oldWorker = Process.Start(new ProcessStartInfo("pwsh") {
                    UseShellExecute = false, CreateNoWindow = true,
                    ArgumentList = { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", "Start-Sleep -Seconds 30" }
                })!;
                try
                {
                    var engine = new { Pid = oldWorker.Id, StartedUtc = oldWorker.StartTime.ToUniversalTime().ToString("O"), EventsPath = events };
                    Require(AppPersistence.LegacyEngineIsAlive(JsonSerializer.SerializeToElement(engine)), "Legacy worker fixture must be alive");
                    WriteLegacySession(data, new[] { "https://example.com/active-worker" }, directory, engine);
                    string before = File.ReadAllText(data.SessionPath);
                    using var writer = new FileStream(events, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
                    var restored = data.Restore()!;
                    Require(restored.LegacyEngineWasActive && AppPersistence.LegacyEngineIsAlive(restored.Engine),
                        "Active engine identity must reach the GUI migration guard");
                    oldWorker.Kill(true);
                    oldWorker.WaitForExit();
                    Require(restored.LegacyEngineWasActive && !AppPersistence.LegacyEngineIsAlive(restored.Engine),
                        "The migration block must remain latched if the worker exits before the GUI checks the snapshot");
                    Require(File.ReadAllText(data.SessionPath) == before, "Blocked recovery must leave the saved checkpoint unchanged");
                }
                finally { if (!oldWorker.HasExited) oldWorker.Kill(true); oldWorker.WaitForExit(); }
                return Task.CompletedTask;
            });

            await Check("zero exit without validated final media never publishes files or history", async directory =>
            {
                string external = Path.Combine(directory, "unrelated-user-file.mp4");
                File.WriteAllText(external, "PRESERVE");
                foreach (string scenario in new[] { "no-media", "unreported-media", "missing-media", "external-media" })
                {
                    var data = new AppPersistence(Path.Combine(directory, scenario, "profile"));
                    var options = Options(Path.Combine(directory, scenario));
                    options.Cookies = external; // Passed unchanged to the offline fixture as the outside sentinel path.
                    var row = new DownloadItem { Url = FixtureBase + scenario };
                    await using (var engine = new DownloadCoordinator(TestExecutable(), data, options, _ => { }, _ => { }))
                    {
                        engine.Add(row);
                        await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    Require(row.Status == "Failed", $"Unvalidated media must not be marked Completed: {scenario}");
                    Require(row.Path == "" && row.OwnedPaths.Count == 0 && !data.ReadHistory().ContainsKey(row.Url),
                        "A skipped/invalid download must not claim ownership or enter successful history");
                    Require(!Directory.GetFiles(options.Output, "*.json", SearchOption.AllDirectories).Any() &&
                        !Directory.GetFiles(options.Output, "*.mp4", SearchOption.AllDirectories).Any(),
                        "Unvalidated media and metadata must not remain published");
                    Require(File.ReadAllText(external) == "PRESERVE", "Invalid media reports must never touch an outside file");
                }
            });

            await Check("readable trace labels preserve video order, privacy, Unicode and aggregate bounds", directory =>
            {
                var unknown=new DownloadItem{Order=17,Url="https://user:password@example.org/watch?v=PublicVideo&token=private-token"};
                string label=DownloadTraceIdentity.VideoLabel(unknown);
                Require(label.Contains("video_seq=17 video=",StringComparison.Ordinal) && label.Contains("v=PublicVideo",StringComparison.Ordinal) &&
                    !label.Contains("password",StringComparison.Ordinal) && !label.Contains("private-token",StringComparison.Ordinal),
                    "A video without a known title must retain its queue number and useful URL without credentials");
                unknown.Title=new string('x',158)+"🎬 trailing";
                label=DownloadTraceIdentity.VideoLabel(unknown);
                string decoded=JsonSerializer.Deserialize<string>(label[(label.IndexOf(" video=",StringComparison.Ordinal)+7)..])!;
                Require(decoded.Length<=160 && decoded.EndsWith('…'),"A long display label must be visibly bounded");
                _=new UTF8Encoding(false,true).GetBytes(decoded);

                var rows=Enumerable.Range(1,64).Select(index=>new DownloadItem
                {
                    Order=index,Title="Видео "+index+" "+new string('Ж',500),Url="https://trace.invalid/"+index,
                    Slot="T"+index,Status="Running",Progress=index
                }).ToArray();
                string members=DownloadTraceIdentity.OverallMembers(rows,out int shown);
                Require(shown>0 && shown<32 && members.Length<=DownloadTraceIdentity.OverallMemberTextBudget &&
                    members.Contains("video_seq="+shown+" video=",StringComparison.Ordinal) &&
                    members.Contains("job="+DownloadTraceIdentity.JobId(rows[shown-1].Url),StringComparison.Ordinal) &&
                    !members.Contains("video_seq="+(shown+1)+" video=",StringComparison.Ordinal),
                    "An oversized session must report only complete readable members within its independent byte-free character budget");
                return Task.CompletedTask;
            });

            await Check("transfer totals include counter resets without double-counting repeated samples", async directory =>
            {
                var data = new AppPersistence(Path.Combine(directory, "profile"));
                var row = new DownloadItem { Url = FixtureBase + "progress-reset",Order=17,Title="Ролик с компонентами" };
                var trace=new ConcurrentQueue<JournalRecord>();
                string session;
                await using (var engine = new DownloadCoordinator(TestExecutable(), data, Options(directory), _ => { }, _ => { },trace.Enqueue))
                {
                    session=engine.TraceSessionId;
                    engine.Add(row);
                    await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                Require(row.Status == "Completed" && File.Exists(row.Path), "Progress fixture must complete a real subprocess and publication");
                Require(row.TransferredBytes == 220, "100 video + 50 audio + 70 retried video bytes must total 220");
                var records=trace.ToArray();
                string job=DownloadTraceIdentity.JobId(row.Url);
                Require(records.All(record=>record.Session==session && (record.Job.Length==0 || record.Job==job)),
                    "Session and exact-URL identity must survive every traced download stage");
                var progress=records.Where(record=>record.Level==JournalLevel.Progress).ToArray();
                Require(progress.All(record=>record.Job==job && record.Worker=="T1") && progress.Length>=4,
                    "A short transfer must trace initial and structural changes before the periodic sample interval");
                Require(records.Where(record=>record.Job==job).All(record=>record.Message.Contains("video_seq=17 video=",StringComparison.Ordinal)) &&
                    records.Any(record=>record.Level==JournalLevel.Verbose && record.Category=="yt-dlp" &&
                        record.Message.Contains("stream=stderr",StringComparison.Ordinal) && record.Message.Contains("video=",StringComparison.Ordinal)),
                    "A real child process must identify the video in every output/progress record, including stderr");
                foreach(string reason in new[]{"component_change","counter_reset","percent_regression","component_complete"})
                    Require(progress.Any(record=>record.Message.Contains(reason,StringComparison.Ordinal)),"Missing progress explanation: "+reason);
                Require(progress.Any(record=>record.Message.Contains(" raw_pct=20 ",StringComparison.Ordinal) && record.Message.Contains(" shown_pct=20 ",StringComparison.Ordinal)),
                    "An actual yt-dlp percentage decrease must remain visible rather than being hidden by a monotonic maximum");
                Require(records.Any(record=>record.Level==JournalLevel.Verbose && record.Message.Contains("stream=stderr",StringComparison.Ordinal) && record.Message.Contains("[fixture]",StringComparison.Ordinal)) &&
                    records.Any(record=>record.Message.Contains("event=process_start ",StringComparison.Ordinal) && record.Message.Contains("pid=",StringComparison.Ordinal)) &&
                    records.Any(record=>record.Message.Contains("event=process_exit ",StringComparison.Ordinal)) &&
                    records.Any(record=>record.Message.Contains("event=job_finished ",StringComparison.Ordinal) && record.Message.Contains("status=Completed",StringComparison.Ordinal)),
                    "Process identity, diagnostic stream and the verified terminal result must be traceable");
            });

            await Check("unknown and non-finite percentages retain the last finite display value", async directory=>
            {
                foreach(string value in new[]{"N/A","NaN","Infinity","-Infinity","1e9999"})
                {
                    var parsed=(YtDlpOutputEvent.Progress)YtDlpOutputParser.Parse($"__VD_PROGRESS__|{value}|1KiB/s|1|50|120|audio")!;
                    Require(!parsed.PercentKnown && double.IsFinite(parsed.Percent),"Unknown input must never manufacture a displayable zero or NaN: "+value);
                }
                var data=new AppPersistence(Path.Combine(directory,"profile"));
                var row=new DownloadItem{Url=FixtureBase+"unknown-percent"};
                var trace=new ConcurrentQueue<JournalRecord>();
                var observed=new ConcurrentQueue<double>();
                await using(var engine=new DownloadCoordinator(TestExecutable(),data,Options(directory),
                    item=>observed.Enqueue(item.Progress),_=>{},trace.Enqueue))
                {
                    engine.Add(row);
                    await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                Require(row.Status=="Completed" && observed.All(double.IsFinite),"Non-finite child output must not invalidate the UI or publication");
                // Match the current field, not previous_raw_pct=unknown on a
                // component's first perfectly valid percentage sample.
                var unknown=trace.Where(record=>record.Message.Contains(" raw_pct=unknown ",StringComparison.Ordinal)).ToArray();
                Require(unknown.Length>0 && unknown.All(record=>record.Message.Contains(" shown_pct=50 ",StringComparison.Ordinal)) &&
                    unknown.Any(record=>record.Message.Contains("unknown_percent",StringComparison.Ordinal)),
                    "An unknown upstream percentage must keep the previous 50% and explain why its raw value is missing: "+
                    string.Join(" | ",unknown.Select(record=>record.Message)));
                Require(row.TransferredBytes==220,"Unknown percentages must not disturb byte accounting");
            });

            await Check("a failing trace consumer cannot change a verified download result", async directory=>
            {
                var data=new AppPersistence(Path.Combine(directory,"profile"));
                var row=new DownloadItem{Url=FixtureBase+"trace-consumer-failure"};
                int callbacks=0;
                await using(var engine=new DownloadCoordinator(TestExecutable(),data,Options(directory),_=>{},_=>{},
                    _=>{Interlocked.Increment(ref callbacks);throw new InvalidOperationException("Isolated failing diagnostics consumer");}))
                {
                    engine.Add(row);
                    await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                Require(callbacks>0 && row.Status=="Completed" && File.Exists(row.Path) && data.ReadHistory().ContainsKey(row.Url),
                    "Diagnostics failure must not abort a native subprocess, media publication or successful history");
            });

            await Check("separate video and audio form one continuous download and only publication reaches 100",async directory=>
            {
                var data=new AppPersistence(Path.Combine(directory,"profile"));
                var row=new DownloadItem{Url=FixtureBase+"multi-component"};
                var observed=new ConcurrentQueue<(string Status,string State,double Percent,string Detail)>();
                var trace=new ConcurrentQueue<JournalRecord>();
                await using(var engine=new DownloadCoordinator(TestExecutable(),data,Options(directory),
                    item=>observed.Enqueue((item.Status,item.State,item.Progress,item.ProgressDetail)),_=>{},trace.Enqueue))
                {
                    engine.Add(row);await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                }
                var samples=observed.Where(sample=>sample.State=="Downloading").ToArray();
                Require(samples.Select(sample=>sample.Percent).SequenceEqual(new[]{0d,49.5,49.5,74.25,99}),
                    "The second stream must continue at the first stream's completed fraction rather than restart the file");
                Require(observed.All(sample=>sample.Status=="Completed" || sample.Percent<100) && row.Progress==100 && row.Status=="Completed",
                    "Stream completion and postprocessing must remain below 100 until owned media publication succeeds");
                Require(samples[2].Detail.Contains("2/2",StringComparison.Ordinal) && observed.Any(sample=>sample.State=="Merging") && observed.Any(sample=>sample.State=="Publishing"),
                    "Component transition, merge and publication must be visible as distinct phases");
                Require(trace.Count(record=>record.Message.Contains("event=process_start ",StringComparison.Ordinal))==1 && row.TransferredBytes==200 && data.ReadHistory().ContainsKey(row.Url),
                    "Video and audio must use one process and one successful history entry without double-counting transfer");
            });

            await Check("postprocessing failures never redownload completed streams",async directory=>
            {
                var data=new AppPersistence(Path.Combine(directory,"profile"));
                var row=new DownloadItem{Url=FixtureBase+"postprocess-error"};
                var trace=new ConcurrentQueue<JournalRecord>();
                var options=Options(directory);options.Quality="2160";
                await using(var engine=new DownloadCoordinator(TestExecutable(),data,options,_=>{},_=>{},trace.Enqueue))
                {engine.Add(row);await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));}
                Require(row.Status=="Failed" && row.Progress<100 && !data.ReadHistory().ContainsKey(row.Url) && row.OwnedPaths.Count==0,
                    "A failed postprocessor must not claim a completed output or successful history");
                Require(trace.Count(record=>record.Message.Contains("event=process_start ",StringComparison.Ordinal))==1 &&
                    trace.Any(record=>record.Message.Contains("event=processing_failed ",StringComparison.Ordinal)),
                    "A network-like phrase inside a postprocessor error must not trigger a complete download retry");
            });

            await Check("fallback cannot reuse an earlier failed invocation's final media marker",async directory=>
            {
                var data=new AppPersistence(Path.Combine(directory,"profile"));
                var row=new DownloadItem{Url=FixtureBase+"fallback-stale-marker"};
                var trace=new ConcurrentQueue<JournalRecord>();
                var options=Options(directory);options.Quality="2160";
                await using(var engine=new DownloadCoordinator(TestExecutable(),data,options,_=>{},_=>{},trace.Enqueue))
                {engine.Add(row);await engine.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));}
                Require(row.Status=="Failed" && row.OwnedPaths.Count==0 && !data.ReadHistory().ContainsKey(row.Url) &&
                    trace.Count(record=>record.Message.Contains("event=process_start ",StringComparison.Ordinal))==2,
                    "Only the successful invocation's own after_move marker may authorize publication");
            });

            await Check("background yt-dlp preview fetch, callback and cache work without WPF thread access", async directory =>
            {
                var data = new AppPersistence(directory);
                const string url = FixtureBase + "preview-metadata";
                var ready = new TaskCompletionSource<PreviewData>(TaskCreationOptions.RunContinuationsAsynchronously);
                var logs = new System.Collections.Concurrent.ConcurrentQueue<string>();
                int credentialReads = 0;
                using (var previews = new PreviewService(TestExecutable(), data,
                    () => {
                        if (System.Windows.Application.Current?.Dispatcher.CheckAccess()==true)
                            throw new InvalidOperationException("Preview metadata did not execute on a worker thread.");
                        Interlocked.Increment(ref credentialReads);
                        return "";
                    }, preview => ready.TrySetResult(preview), log => logs.Enqueue(log)))
                {
                    previews.Request(url);
                    var result = await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
                    Require(result.Url == url && result.Title == "Native preview works" &&
                            result.Uploader == "Offline fixture" && result.MaxHeight == 1080 &&
                            result.MaxFps == 60 && result.Codecs.Contains("H264"),
                        "Async yt-dlp metadata must reach the preview panel callback.");
                    Require(Volatile.Read(ref credentialReads) == 1,
                        "Worker must read credential snapshot exactly once for the preview.");
                    Require(previews.Get(url)?.Title == result.Title,
                        "Successful async previews must be cached immediately.");
                    var diagnostics=logs.ToArray();
                    Require(diagnostics.Length==1 && diagnostics[0].StartsWith(
                            "Thumbnail unavailable: stage=candidates candidate=0/0 ",StringComparison.Ordinal) &&
                            diagnostics[0].Contains("video="+url,StringComparison.Ordinal),
                        "Metadata without image candidates must log that reason and its video, without an unrelated background-thread error.");
                }
                using (var newService = new PreviewService("nonexistent", data, () => "",
                    _ => throw new Exception("Already cached preview must not refetch"), _ => { }))
                {
                    Require(newService.Get(url)?.Title == "Native preview works",
                        "Cached metadata must survive a service restart for 72 hours.");
                }
            });

            await Check("incomplete cached preview is rejected before it reaches WPF", directory =>
            {
                var data = new AppPersistence(directory);
                const string url = "https://example.com/incomplete-cache";
                using var previews = new PreviewService("unused", data, () => "", _ => { }, _ => { });
                string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                string file = Path.Combine(data.PreviewDir, hash + ".json");
                File.WriteAllText(file, JsonSerializer.Serialize(new { CacheVersion = 1, Url = url, SavedAtUtc = DateTime.UtcNow, Title = "Missing required string fields" }));
                Require(previews.Get(url) == null, "Partial cache records must not return null strings to WPF rendering");
                Require(!File.Exists(file), "Malformed cache must be evicted so a future request can refetch it");
                return Task.CompletedTask;
            });

            await Check("preview cache TTL uses UTC across timestamp offsets", directory =>
            {
                var data = new AppPersistence(directory);
                using var previews = new PreviewService("unused", data, () => "", _ => { }, _ => { });
                foreach (int hours in new[] { 71, 73 })
                {
                    string url = "https://example.com/offset-cache-" + hours;
                    string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
                    string file = Path.Combine(data.PreviewDir, hash + ".json");
                    File.WriteAllText(file, JsonSerializer.Serialize(new {
                        CacheVersion = 1, Url = url,
                        SavedAtUtc = DateTimeOffset.UtcNow.AddHours(-hours).ToOffset(TimeSpan.FromHours(6)).ToString("O"),
                        Title = "Cached", Uploader = "", PlaylistTitle = "", Extractor = "", Codecs = "",
                        DynamicRange = "", ThumbnailBase64 = "", Duration = 0 }));
                    var cached = previews.Get(url);
                    Require((cached != null) == (hours < 72), "Cache age must use elapsed UTC time, not local wall time");
                    if (cached != null)
                        Require(cached.SavedAtUtc.Kind == DateTimeKind.Utc &&
                            Math.Abs((DateTime.UtcNow - cached.SavedAtUtc).TotalHours - hours) < .01,
                            "Cached timestamps must be normalized to UTC before memory reuse");
                    else Require(!File.Exists(file), "Expired offset cache must be evicted");
                }
                return Task.CompletedTask;
            });

            await Check("permanent HTTP errors do not retry while transient HTTP/TLS errors do", async directory =>
            {
                foreach (var (name, expectedRetry) in new[] { ("permanent", false), ("transient", true), ("tls", true) })
                {
                    var data = new AppPersistence(Path.Combine(directory, name, "profile"));
                    var row = new DownloadItem { Url = FixtureBase + "error-" + name };
                    bool retried = false;
                    bool retryPhase=false;
                    DownloadCoordinator? coordinator = null;
                    await using (coordinator = new DownloadCoordinator(TestExecutable(), data, Options(Path.Combine(directory, name)), item=>
                    {
                        if(item.State=="Retrying" && item.ProgressDetail.Contains("2/4",StringComparison.Ordinal))retryPhase=true;
                    }, message =>
                    {
                        if (message.StartsWith("Transient error on ", StringComparison.Ordinal))
                        {
                            retried = true;
                            coordinator!.Stop(); // Observe the decision without waiting through retry backoff.
                        }
                    }))
                    {
                        coordinator.Add(row);
                        await coordinator.RunAsync().WaitAsync(TimeSpan.FromSeconds(15));
                    }
                    Require(retried == expectedRetry, $"Incorrect retry classification for {name}");
                    Require(retryPhase==expectedRetry,"A real retry must expose its next attempt and delay as a distinct phase");
                    Require(row.Status == (expectedRetry ? "Interrupted" : "Failed"), "Retry cancellation/permanent failure must retain its distinct result");
                }
            });

            if (failures.Count > 0) throw new AggregateException("Native review regression failures", failures);
        }
        finally { Directory.Delete(root, true); }
    }

    private static void WriteLegacySession(AppPersistence data, string[] urls, string output, object engine) =>
        AppPersistence.AtomicWrite(data.SessionPath, JsonSerializer.Serialize(new {
            Version = 1,
            Queue = urls.Select(url => new { Url = url, Status = "Running" }),
            Settings = new { Output = output }, Engine = engine }));

    private static DownloadOptions Options(string directory) => new() {
        Output = Path.Combine(directory, "downloads"), Threads = "1", EmbedMetadata = false, EmbedChapters = false };

    private static string TestExecutable()
    {
        string path = Path.Combine(AppContext.BaseDirectory,
            typeof(ReviewRegressionTests).Assembly.GetName().Name + (OperatingSystem.IsWindows() ? ".exe" : ""));
        Require(File.Exists(path), "Test apphost is required for offline subprocess fixtures");
        return path;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
