using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using VideoDownloader.Native;

internal static class RemoteDownloadClientTests
{
    private const string Capabilities = "/vd/api/remote/capabilities";
    private const string Jobs = "/vd/api/remote/jobs/";
    private const string Source = "https://example.test/Video?part=A";
    private const string Tag = "\"fixture-result\"";
    private static readonly byte[] Media = Enumerable.Range(0, 128).Select(index => (byte)index).ToArray();
    private static readonly RemoteDownloadConnection Connection =
        new(new Uri("https://remote.example.test/vd/"), "fixture-user", "fixture-password");

    internal static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "vd-remote-client-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await CheckConnectionProbeAsync(deadline.Token);
            await CheckAvailabilityAsync(new AppPersistence(Path.Combine(root, "old-server")), deadline.Token);
            await TransferAndReplayReceiptAsync(new AppPersistence(Path.Combine(root, "transfer")), deadline.Token);
            await RefuseChangedTransferAsync(new AppPersistence(Path.Combine(root, "invalid-range")), deadline.Token);
            await ReuseLostSubmissionAsync(new AppPersistence(Path.Combine(root, "restart")), deadline.Token);
            Console.WriteLine("PASS: authenticated server connection, independent local settings, transfer speed, verified ranges, durable receipts and assignment recovery");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task CheckConnectionProbeAsync(CancellationToken token)
    {
        int requests = 0;
        var status = await RemoteDownloadClient.CheckConnectionAsync(Connection, token, new Handler((request, _) =>
        {
            requests++;
            string basic = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password"));
            Require(request.Method == HttpMethod.Get && request.RequestUri!.Scheme == "https" &&
                PathOf(request) == Capabilities && request.Content is null && request.Headers.Authorization?.Scheme == "Basic" &&
                request.Headers.Authorization.Parameter == basic,
                "The probe must use one authenticated GET at the configured HTTPS base address, without a request body.");
            return Task.FromResult(Json(new { version = 1, maxConcurrent = 3, freeDiskBytes = 6L * 1024 * 1024 * 1024 }));
        }));
        Require(requests == 1 && status is { MaxConcurrent: 3, FreeDiskBytes: 6L * 1024 * 1024 * 1024 },
            "The explicit connection probe must return the server slot limit and real free disk space.");

        var compatible = await RemoteDownloadClient.CheckConnectionAsync(Connection, token,
            new Handler((_, _) => Task.FromResult(Json(new { version = 1, maxConcurrent = 2 }))));
        Require(compatible is { MaxConcurrent: 2, FreeDiskBytes: null },
            "A version 1 server without disk telemetry must remain compatible.");

        async Task ExpectFailure(Func<HttpResponseMessage> respond, string expected)
        {
            try
            {
                await RemoteDownloadClient.CheckConnectionAsync(Connection, token, new Handler((_, _) => Task.FromResult(respond())));
            }
            catch (Exception error) when (RemoteDownloadClient.IsRemoteFailure(error))
            {
                string description = RemoteDownloadClient.DescribeConnectionFailure(error);
                Require(description.Contains(expected, StringComparison.OrdinalIgnoreCase) &&
                    !description.Contains("fixture-password", StringComparison.Ordinal),
                    "Probe diagnostics must identify the failure without including credentials.");
                return;
            }
            throw new InvalidOperationException("The explicit connection probe accepted an incompatible or inaccessible server.");
        }

        await ExpectFailure(() => new HttpResponseMessage(HttpStatusCode.Unauthorized),ErrorMessages.Text("логин или пароль","username or password"));
        await ExpectFailure(() => throw new HttpRequestException(HttpRequestError.SecureConnectionError,"fixture-password"),
            ErrorMessages.Text("сертификат","certificate"));
        await ExpectFailure(() => throw new HttpRequestException(HttpRequestError.NameResolutionError,"fixture-password"),"DNS");
        await ExpectFailure(() => new HttpResponseMessage(HttpStatusCode.NotFound),ErrorMessages.Text("совместимого сервера","compatible server"));
        await ExpectFailure(() => Json(new { version = 2, maxConcurrent = 4 }),ErrorMessages.Text("совместимого сервера","compatible server"));
    }

    private static async Task CheckAvailabilityAsync(AppPersistence persistence, CancellationToken token)
    {
        using var old = Client(persistence, (request, _) =>
        {
            Require(request.Method == HttpMethod.Get && PathOf(request) == Capabilities, "Availability must read the authenticated capability endpoint.");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        });
        await old.PrepareAsync(token);
        Require(!old.IsAvailable, "An older server without the endpoint must be unavailable for new assignments.");

        using var incompatible = Client(persistence, (_, _) => Task.FromResult(Json(new { version = 2, maxConcurrent = 4 })));
        await incompatible.PrepareAsync(token);
        Require(!incompatible.IsAvailable, "Only the supported version can receive new assignments.");

        using var constrained = Client(persistence, (_, _) => Task.FromResult(Json(new { version = 1, maxConcurrent = 2 })));
        await constrained.PrepareAsync(token);
        Require(constrained.IsAvailable && constrained.MaxConcurrent == 2, "The Windows client must respect the server slot limit.");
    }

    private static async Task TransferAndReplayReceiptAsync(AppPersistence persistence, CancellationToken token)
    {
        var row = new DownloadItem { Url = Source };
        string stage = MakeStage(persistence);
        string output = Path.Combine(Path.GetDirectoryName(persistence.SessionPath)!, "published.mp4");
        string? job = null;
        int fileRequests = 0;
        int receipts = 0;
        bool delivered = false;
        var clock = new TransferClock();
        var options = new DownloadOptions
        {
            Threads = "6", Fragments = "8", UseHttpChunks = true, PreferAria2c = true, RateLimit = "5M",
            FilenameTemplate = "%(id)s.%(ext)s", Quality = "720"
        };
        using (var client = Client(persistence, async (request, ct) =>
        {
            string path = PathOf(request);
            if (path == Capabilities) return Json(new { version = 1, maxConcurrent = 4 });
            if (request.Method == HttpMethod.Put)
            {
                job = path;
                Require(PathOfAssignment(request), "A submitted assignment needs a stable random identifier.");
                string body = await request.Content!.ReadAsStringAsync(ct);
                Require(request.Content.Headers.ContentLength == Encoding.UTF8.GetByteCount(body) &&
                    request.Headers.TransferEncodingChunked != true, "The server requires a bounded JSON body with an explicit length.");
                using var payload = JsonDocument.Parse(body);
                Require(payload.RootElement.GetProperty("url").GetString() == Source &&
                    payload.RootElement.GetProperty("quality").GetString() == options.Quality &&
                    payload.RootElement.EnumerateObject().Select(property => property.Name).ToHashSet().SetEquals(
                        ["url", "quality", "container", "videoCodec", "embedMetadata", "embedChapters"]),
                    "The VPS must receive the exact URL and media requirements without the local downloader settings.");
                Require(!ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(), "Windows must save the assignment before submitting.");
                return Completed();
            }
            if (path == job + "/file")
            {
                Require(!ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(), "An in-flight transfer must not become a receipt.");
                if (++fileRequests == 1)
                {
                    Require(request.Headers.Range is null, "The initial transfer starts from byte zero.");
                    return FileResponse(Media[..37], HttpStatusCode.OK, Media.Length, clock: clock);
                }
                Require(request.Headers.Range?.ToString() == $"bytes=37-{Media.Length - 1}" &&
                    request.Headers.IfRange?.EntityTag?.Tag == Tag, "A retry must bind its exact offset to the first result's strong ETag.");
                return FileResponse(Media[37..], HttpStatusCode.PartialContent, Media.Length - 37, 37, clock: clock);
            }
            Require(request.Method == HttpMethod.Post && path == job + "/received", "An acknowledgement must refer to the received assignment.");
            Require(File.Exists(output) && ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(),
                "The acknowledgement must follow local publication and a stored receipt.");
            receipts++;
            delivered = true;
            throw new HttpRequestException("The response was lost after the server processed the receipt.");
        }, clock))
        {
            await client.PrepareAsync(token);
            Require(client.IsAvailable, "The client must recognise the current protocol.");
            Require(client.Supports(row, options), "Additional local settings must not disqualify a supported single video from the VPS.");
            var progress = new List<RemoteDownloadProgress>();
            var artifact = await client.DownloadAsync(row, options, stage, ct =>
            {
                clock.Advance(5000);
                return Resume(ct);
            }, progress.Add, token);
            Require(fileRequests == 2 && receipts == 0 && File.ReadAllBytes(artifact.MediaPath).SequenceEqual(Media),
                "An interrupted transfer must restore the original bytes without acknowledging staging.");
            Require(progress.Any(value => value is { Stage: RemoteDownloadStage.Receiving, Percent: 100 } &&
                value.BytesReceived == Media.Length && value.TotalBytes == Media.Length), "The final transfer update must report the received bytes.");
            Require(progress.Where(value => value.BytesPerSecond > 0).Select(value => value.BytesPerSecond)
                .SequenceEqual([37 * 4d, (Media.Length - 37) * 4d]),
                "The speed must exclude pause time and count only bytes received after the current request's offset.");
            var firstSample = progress.FindIndex(value => value.BytesPerSecond > 0);
            Require(progress[firstSample + 1] is { Stage: RemoteDownloadStage.Receiving, BytesReceived: 37, BytesPerSecond: 0 },
                "The old rate must clear during the transfer retry.");
            Require(artifact.Title == "Fixture media" && client.OwnsAssignment(row), "The downloaded result remains assigned until publication.");
            await RefusesAsync<IOException>(() => client.AcknowledgeAsync(artifact, output, token),
                "A missing published file must not authorize a receipt.");
            File.WriteAllBytes(output, Media[..37]);
            await RefusesAsync<IOException>(() => client.AcknowledgeAsync(artifact, output, token),
                "An incomplete published file must not authorize a receipt.");
            Require(receipts == 0 && !ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(),
                "Failed local publication must leave the server result available.");
            File.Move(artifact.MediaPath, output, overwrite: true);
            await RefusesAsync<HttpRequestException>(() => client.AcknowledgeAsync(artifact, output, token),
                "All acknowledgement responses must be lost after the server processes the receipt.");
        }

        Require(delivered && receipts == 3 && ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(),
            "Exhausting the bounded acknowledgement retries must leave the verified receipt available for replay.");
        using var reopened = Client(persistence, (request, _) =>
        {
            if (PathOf(request) == Capabilities) return Task.FromResult(Json(new { version = 1, maxConcurrent = 4 }));
            Require(delivered && request.Method == HttpMethod.Post && PathOf(request) == job + "/received",
                "Restart must replay only the original receipt, without a new submission or transfer.");
            receipts++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
        });
        await reopened.PrepareAsync(token);
        Require(receipts == 4 && AssignmentPaths(persistence).Length == 0 && !reopened.OwnsAssignment(row),
            "A repeated acknowledgement must retire the local record on success.");
        Require(File.ReadAllBytes(output).SequenceEqual(Media), "Acknowledging server cleanup must keep the verified Windows file.");
    }

    private static async Task RefuseChangedTransferAsync(AppPersistence persistence, CancellationToken token)
    {
        int transfers = 0;
        using var client = Client(persistence, (request, _) =>
        {
            string path = PathOf(request);
            if (path == Capabilities) return Task.FromResult(Json(new { version = 1, maxConcurrent = 4 }));
            if (request.Method == HttpMethod.Put) return Task.FromResult(Completed());
            Require(request.Method == HttpMethod.Get && path.EndsWith("/file", StringComparison.Ordinal),
                "Invalid range data must never produce a receipt.");
            if (++transfers == 1) return Task.FromResult(FileResponse(Media[..37], HttpStatusCode.OK, Media.Length));
            Require(request.Headers.IfRange?.EntityTag?.Tag == Tag && request.Headers.Range?.ToString() == $"bytes=37-{Media.Length - 1}",
                "The transfer retry must ask for the original file's missing bytes.");
            return Task.FromResult(FileResponse(Media[37..], HttpStatusCode.PartialContent, Media.Length - 37, 37, "\"changed\""));
        });
        await client.PrepareAsync(token);
        string stage = MakeStage(persistence);
        var row = new DownloadItem { Url = Source };
        await RefusesAsync<InvalidDataException>(() => client.DownloadAsync(row, new DownloadOptions(), stage, Resume, _ => { }, token),
            "A range for a changed entity must not be accepted even when its size and offsets match.");
        Require(transfers == 2 && client.OwnsAssignment(row) && !File.Exists(Path.Combine(stage, "fixture.mp4")) &&
            !ReadAssignment(persistence).GetProperty("ReceivedLocally").GetBoolean(),
            "A changed result must stay unpublished and unacknowledged.");
    }

    private static async Task ReuseLostSubmissionAsync(AppPersistence persistence, CancellationToken token)
    {
        string? job = null;
        int submissions = 0;
        var row = new DownloadItem { Url = Source };
        using (var client = Client(persistence, (request, _) =>
        {
            if (PathOf(request) == Capabilities) return Task.FromResult(Json(new { version = 1, maxConcurrent = 4 }));
            Require(request.Method == HttpMethod.Put && PathOfAssignment(request), "Assignment must use its idempotent PUT.");
            job ??= PathOf(request);
            Require(PathOf(request) == job, "Retrying a lost submission must reuse the first ID.");
            submissions++;
            throw new HttpRequestException("Simulated disconnect during submission.");
        }))
        {
            await client.PrepareAsync(token);
            await RefusesAsync<HttpRequestException>(() => client.DownloadAsync(row, new DownloadOptions { Quality = "720" },
                MakeStage(persistence), Resume, _ => { }, token), "The first client should exhaust its transport retries.");
            Require(submissions == 3 && client.OwnsAssignment(row),
                "An uncertain submission must remain recoverable after bounded retries.");
        }

        using var reopened = Client(persistence, async (request, ct) =>
        {
            string path = PathOf(request);
            if (path == Capabilities) return Json(new { version = 1, maxConcurrent = 4 });
            if (request.Method == HttpMethod.Put)
            {
                Require(path == job, "The reopened client must retain the original assignment ID.");
                using var payload = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Require(payload.RootElement.GetProperty("quality").GetString() == "720", "Recovery must use the captured request settings.");
                return Completed();
            }
            Require(path == job + "/file" && request.Method == HttpMethod.Get, "The recovered assignment must receive its original result.");
            return FileResponse(Media, HttpStatusCode.OK, Media.Length);
        });
        await reopened.PrepareAsync(token);
        Require(reopened.OwnsAssignment(row) && !reopened.OwnsAssignment(new DownloadItem { Url = Source.ToLowerInvariant() }),
            "Ownership after restart must use the exact task URL.");
        var result = await reopened.DownloadAsync(row, new DownloadOptions { Quality = "2160" }, MakeStage(persistence), Resume, _ => { }, token);
        Require(job == Jobs + result.JobId && File.ReadAllBytes(result.MediaPath).SequenceEqual(Media),
            "Recovery must reuse the created assignment and complete its transfer.");
    }

    private static RemoteDownloadClient Client(AppPersistence persistence,
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond, TimeProvider? clock = null) =>
        new(Connection, persistence, _ => { }, new Handler(respond), clock);

    private static Task Resume(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    private static string MakeStage(AppPersistence persistence) =>
        Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(persistence.SessionPath)!, Guid.NewGuid().ToString("N"))).FullName;

    private static string[] AssignmentPaths(AppPersistence persistence) =>
        Directory.GetFiles(Path.Combine(Path.GetDirectoryName(persistence.SessionPath)!, "remote-jobs"), "*.json", SearchOption.AllDirectories);

    private static JsonElement ReadAssignment(AppPersistence persistence)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(AssignmentPaths(persistence).Single()));
        return document.RootElement.Clone();
    }

    private static HttpResponseMessage Completed() => Json(new
    {
        state = "completed", progress = 100, filename = "fixture.mp4", size = Media.Length, title = "Fixture media"
    });

    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json")
    };

    private static HttpResponseMessage FileResponse(byte[] bytes, HttpStatusCode status, long length, long? offset = null,
        string tag = Tag, TransferClock? clock = null)
    {
        var response = new HttpResponseMessage(status)
        {
            Content = clock is null ? new ByteArrayContent(bytes) : new StreamContent(new TimedMediaStream(bytes, clock))
        };
        response.Headers.ETag = new EntityTagHeaderValue(tag);
        response.Content.Headers.ContentLength = length;
        if (offset.HasValue) response.Content.Headers.ContentRange = new ContentRangeHeaderValue(offset.Value, Media.Length - 1, Media.Length);
        return response;
    }

    private static string PathOf(HttpRequestMessage request) => request.RequestUri!.AbsolutePath;

    private static bool PathOfAssignment(HttpRequestMessage request) =>
        PathOf(request).StartsWith(Jobs, StringComparison.Ordinal) && Guid.TryParseExact(PathOf(request)[Jobs.Length..], "N", out _);

    private static async Task RefusesAsync<T>(Func<Task> action, string message) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException(message);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class TransferClock : TimeProvider
    {
        private long _milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => _milliseconds;
        internal void Advance(int milliseconds) => _milliseconds += milliseconds;
    }

    private sealed class TimedMediaStream(byte[] bytes, TransferClock clock) : MemoryStream(bytes)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        {
            clock.Advance(250);
            return base.ReadAsync(buffer, token);
        }
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            string credentials = Convert.ToBase64String(Encoding.UTF8.GetBytes("fixture-user:fixture-password"));
            Require(request.RequestUri is { Scheme: "https", Host: "remote.example.test" } &&
                request.Headers.Authorization is { Scheme: "Basic" } auth && auth.Parameter == credentials &&
                request.Headers.TryGetValues("X-Video-Downloader", out var marker) && marker.Single() == "1",
                "Requests must stay on the configured HTTPS endpoint and carry the required authentication.");
            return respond(request, token);
        }
    }
}
