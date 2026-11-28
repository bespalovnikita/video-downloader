using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VideoDownloader.Native;

internal interface IRemoteDownloadClient : IDisposable
{
    bool IsAvailable => true;
    void PreserveAssignmentsOnStop() { }
    int MaxConcurrent => 4;
    Task PrepareAsync(CancellationToken token);
    bool Supports(DownloadItem row, DownloadOptions options);
    bool OwnsAssignment(DownloadItem row);
    bool OwnsAssignment(DownloadItem row,string jobId) => false;
    Task<RemoteDownloadArtifact> DownloadAsync(DownloadItem row, DownloadOptions options, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token);
    Task AcknowledgeAsync(RemoteDownloadArtifact artifact, string publishedPath, CancellationToken token);
}

internal enum RemoteDownloadStage { Waiting, WaitingForSpace, Downloading, Receiving }

internal sealed record RemoteDownloadProgress(RemoteDownloadStage Stage, double Percent, long BytesReceived, long TotalBytes,
    double BytesPerSecond = 0, string? WaitingReason = null);
internal sealed record RemoteDownloadArtifact(string JobId, string MediaPath, string Title, long Size);
internal sealed class RemoteDownloadUnsupportedException() : IOException("Сервер передал коллекцию в локальную очередь.");

internal sealed record RemoteDownloadRequest(string Url, string Quality, string Container, string VideoCodec,
    bool EmbedMetadata, bool EmbedChapters)
{
    [JsonIgnore]
    internal bool IsValid => Url is { Length: > 0 and <= 4096 } &&
        !Url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\') &&
        Uri.TryCreate(Url, UriKind.Absolute, out var source) &&
        source.Scheme is "https" or "http" && source.UserInfo.Length == 0 && source.Host.Length > 0 &&
        Quality is "Best" or "2160" or "1440" or "1080" or "720" &&
        Container is "Auto" or "MP4" or "MKV" or "WebM" &&
        VideoCodec is "Auto" or "H264" or "VP9" or "AV1";

    internal static RemoteDownloadRequest? For(DownloadItem row, DownloadOptions options)
    {
        if (row.Selection is not null || row.ResumeState is not null || options.AudioOnly ||
            options.AudioTrack != "Default" || options.Cookies.Length > 0 || options.WriteSubs || options.AutoSubs ||
            options.EmbedSubs || options.EmbedThumbnail || options.Sponsor)
            return null;

        string container = options.Container.Equals("webm", StringComparison.OrdinalIgnoreCase) ? "WebM" : options.Container;
        var request = new RemoteDownloadRequest(row.Url, options.Quality, container, options.Codec,
            options.EmbedMetadata, options.EmbedChapters);
        if (!request.IsValid) return null;
        var address = new Uri(row.Url);
        if (address.Query.TrimStart('?').Split('&').Any(part => part.Split('=', 2)[0] == "list") ||
            address.AbsolutePath.Split('/').Any(segment =>
                segment.Equals("playlist", StringComparison.OrdinalIgnoreCase) ||
                segment.Equals("playlists", StringComparison.OrdinalIgnoreCase)))
            return null;
        return request;
    }
}

internal sealed class RemoteDownloadClient : IRemoteDownloadClient
{
    private static readonly JsonSerializerOptions WireJson = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(30);
    private const double TransferSampleSeconds = 0.2;
    private const int MaxResponseBytes = 32 * 1024;
    private readonly HttpClient _http;
    private readonly RemoteDownloadJournal _journal;
    private readonly Action<string> _log;
    private readonly TimeProvider _clock;
    private volatile bool _preserveAssignmentsOnStop;
    public void PreserveAssignmentsOnStop()=>_preserveAssignmentsOnStop=true;
    public bool IsAvailable { get; private set; }
    public int MaxConcurrent { get; private set; } = 4;

    internal RemoteDownloadClient(RemoteDownloadConnection connection, AppPersistence persistence, Action<string> log,
        HttpMessageHandler? handler = null, TimeProvider? clock = null)
    {
        _log = log;
        _journal = new RemoteDownloadJournal(persistence, connection, log);
        _http = CreateHttpClient(connection, handler);
        _clock = clock ?? TimeProvider.System;
    }

    private static HttpClient CreateHttpClient(RemoteDownloadConnection connection, HttpMessageHandler? handler)
    {
        var client = new HttpClient(handler ?? new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = !connection.Address.IsLoopback
        })
        {
            BaseAddress = connection.Address,
            Timeout = Timeout.InfiniteTimeSpan
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(connection.Username + ":" + connection.Password)));
        client.DefaultRequestHeaders.Add("X-Video-Downloader", "1");
        return client;
    }

    internal static async Task<RemoteCapabilities> CheckConnectionAsync(RemoteDownloadConnection connection, CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        using var client = CreateHttpClient(connection, handler);
        return await ReadCapabilitiesAsync(client, token).ConfigureAwait(false);
    }

    private static async Task<RemoteCapabilities> ReadCapabilitiesAsync(HttpClient client, CancellationToken token)
    {
        using var deadline = Deadline(token, TimeSpan.FromSeconds(5));
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/remote/capabilities");
        try
        {
            var capability = await ReadJsonAsync<RemoteCapabilities>(client, request, deadline.Token).ConfigureAwait(false);
            if (capability is not { Version: 1, MaxConcurrent: >= 1 and <= 4 })
                throw new InvalidDataException("Сервер использует несовместимую версию протокола.");
            return capability;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Сервер очереди не ответил вовремя.");
        }
    }

    internal static async Task<RemoteQueuePage> ListQueueAsync(RemoteDownloadConnection connection, string? after,
        CancellationToken token, bool receivedOnly = false)
    {
        using var client = CreateHttpClient(connection, null);
        using var deadline = Deadline(token, RequestTimeout);
        string path = "api/remote/jobs";
        if (after is not null) path += "?after=" + Uri.EscapeDataString(after);
        if (receivedOnly) path += after is null ? "?received=1" : "&received=1";
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        var page = await ReadJsonAsync<RemoteQueuePage>(client, request, deadline.Token,
            maxResponseBytes: 128 * 1024).ConfigureAwait(false);
        if (page?.Jobs is not { Length: <= 25 } || page.Jobs.Any(job => job is null || !IsQueueId(job.Id) ||
            job.State is not ("queued" or "downloading" or "completed" or "received")) ||
            page.Next is not null && !IsQueueId(page.Next))
            throw new InvalidDataException("Некорректный список серверной очереди.");
        return page;
    }

    private static bool IsQueueId(string? id) =>
        id is { Length: 32 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    internal static async Task RemoveQueueItemAsync(RemoteDownloadConnection connection, RemoteQueueItem item,
        CancellationToken token)
    {
        using var client = CreateHttpClient(connection, null);
        string path = "api/remote/jobs/" + item.Id + (item.State is "completed" or "received" ? "/file" : "");
        await SendEmptyAsync(client, HttpMethod.Delete, path, token).ConfigureAwait(false);
    }

    internal static async Task RaiseQueuePriorityAsync(RemoteDownloadConnection connection, RemoteQueueItem item, CancellationToken token)
    {
        using var client = CreateHttpClient(connection, null);
        await SendEmptyAsync(client, HttpMethod.Post, "api/remote/jobs/" + item.Id + "/priority", token).ConfigureAwait(false);
    }

    internal static async Task<RemoteQueueNotices> ReadNoticesAsync(RemoteDownloadConnection connection, CancellationToken token)
    {
        using var client = CreateHttpClient(connection, null);
        using var deadline = Deadline(token, RequestTimeout);
        using var request = new HttpRequestMessage(HttpMethod.Get, "api/remote/notices");
        var notices = await ReadJsonAsync<RemoteQueueNotices>(client, request, deadline.Token).ConfigureAwait(false);
        if (notices?.Expiring is not { Length: <= 4 } || notices.Expiring.Any(id => !IsQueueId(id)))
            throw new InvalidDataException("Invalid remote queue notices.");
        return notices;
    }

    public async Task PrepareAsync(CancellationToken token)
    {
        var received = await Task.Run(_journal.Load, token).ConfigureAwait(false);
        try
        {
            var capability = await ReadCapabilitiesAsync(_http, token).ConfigureAwait(false);
            MaxConcurrent = capability.MaxConcurrent;
            IsAvailable = true;
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsRemoteFailure(error))
        {
            _log(DescribeConnectionFailure(error) + " " + ErrorMessages.Text("Новые задания остаются в локальной очереди.",
                "New jobs stay in the local queue."));
            return;
        }
        foreach (var assignment in received)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var deadline = Deadline(token, TimeSpan.FromSeconds(5));
                await SendReceiptAsync(assignment, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (!token.IsCancellationRequested && IsRemoteFailure(error))
            {
                _log("Подтверждение серверу пока не доставлено. Приложение повторит попытку при следующем запуске очереди.");
                break;
            }
        }
    }

    public bool Supports(DownloadItem row, DownloadOptions options) => RemoteDownloadRequest.For(row, options) is not null;
    public bool OwnsAssignment(DownloadItem row) => _journal.OwnsAssignment(row.Url);
    public bool OwnsAssignment(DownloadItem row,string jobId) =>
        _journal.FindById(jobId) is { ReceivedLocally: false } assignment && assignment.Request.Url==row.Url;

    public async Task<RemoteDownloadArtifact> DownloadAsync(DownloadItem row, DownloadOptions options, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token)
    {
        var assignment = _journal.Find(row.Url);
        if (assignment is { ReceivedLocally: true })
        {
            await SendReceiptAsync(assignment, token).ConfigureAwait(false);
            assignment = null;
        }

        if (options.ReconnectServerOnly && (assignment is null ||
            options.RemotePickupJobId is { } expectedId && assignment.JobId!=expectedId))
            throw new IOException(ErrorMessages.Text("Исходное серверное задание уже недоступно.",
                "The original server job is no longer available."));
        var request = assignment?.Request ?? RemoteDownloadRequest.For(row, options)
            ?? throw new InvalidOperationException("Настройки задания не поддерживаются серверной очередью.");
        await waitForResume(token).ConfigureAwait(false);
        bool reopening = assignment is not null;
        bool waitForConnection = row.ServerDownloadRequested;
        assignment ??= Register(request);
        try
        {
            var status = options.ReconnectServerOnly
                ? await RepeatAsync(ct => ReadStatusAsync(HttpMethod.Get, assignment, null, ct), token, waitForConnection).ConfigureAwait(false)
                : await PutAsync(assignment, token, waitForConnection).ConfigureAwait(false);
            if (reopening && !options.ReconnectServerOnly && !status.Unsupported && !status.Expired && !status.Removed &&
                status.State is ("cancelled" or "interrupted" or "failed"))
            {
                DiscardPartial(assignment.JobId);
                _journal.Remove(assignment);
                assignment = Register(request);
                status = await PutAsync(assignment, token, waitForConnection).ConfigureAwait(false);
            }
            while (status.State is "queued" or "downloading")
            {
                var phase = status.WaitingForSpace ? RemoteDownloadStage.WaitingForSpace :
                    status.State == "queued" ? RemoteDownloadStage.Waiting : RemoteDownloadStage.Downloading;
                progress(new RemoteDownloadProgress(phase, status.Progress ?? 0, 0, status.Size ?? 0,
                    WaitingReason: status.WaitingReason));
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                await waitForResume(token).ConfigureAwait(false);
                status = await RepeatAsync(ct => ReadStatusAsync(HttpMethod.Get, assignment, null, ct), token, waitForConnection).ConfigureAwait(false);
            }
            if (status.State != "completed")
            {
                DiscardPartial(assignment.JobId);
                _journal.Remove(assignment);
                if (status.Expired) throw ExpiredResult();
                if (status.State == "failed" && status.Unsupported) throw new RemoteDownloadUnsupportedException();
                string error = status.Error is { Length: > 0 } detail ? detail[..Math.Min(detail.Length, 500)] : "Сервер не подготовил файл.";
                throw new IOException(error);
            }
            string path;
            try
            {
                path = await ReceiveAsync(assignment.JobId, assignment.Request.Container, status, stage,
                    waitForResume, progress, token, waitForConnection).ConfigureAwait(false);
            }
            catch (InvalidDataException)
            {
                DiscardPartial(assignment.JobId);
                throw;
            }
            catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.NotFound)
            {
                DiscardPartial(assignment.JobId);
                _journal.Remove(assignment);
                throw new IOException(ErrorMessages.Text("Серверная копия больше недоступна. Запустите скачивание повторно.",
                    "The server copy is no longer available. Start the download again."), error);
            }
            return new RemoteDownloadArtifact(assignment.JobId, path, status.Title ?? Path.GetFileNameWithoutExtension(path), status.Size!.Value);
        }
        catch (HttpRequestException error) when (options.ReconnectServerOnly && error.StatusCode == HttpStatusCode.NotFound)
        {
            _journal.Remove(assignment);
            throw new IOException(ErrorMessages.Text("Исходное серверное задание уже недоступно.",
                "The original server job is no longer available."), error);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Gone)
        {
            DiscardPartial(assignment.JobId);
            _journal.Remove(assignment);
            throw ExpiredResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            if(_preserveAssignmentsOnStop && row.ServerDownloadRequested)throw;
            DiscardPartial(assignment.JobId);
            // A timed-out PUT may have reached the server. Cancellation of an
            // unknown ID leaves a tombstone there, preventing late submission.
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            try { await SendEmptyAsync(HttpMethod.Delete, JobPath(assignment), deadline.Token).ConfigureAwait(false); }
            catch (Exception error) when (IsRemoteFailure(error))
            { _log("Сервер не подтвердил отмену. Задание сохранено для повторного подключения."); }
            throw;
        }
    }

    private static IOException ExpiredResult() => new("The server's copy has expired. Start the download again.");

    public async Task AcknowledgeAsync(RemoteDownloadArtifact artifact, string publishedPath, CancellationToken token)
    {
        var assignment = _journal.FindById(artifact.JobId)
            ?? throw new IOException("Не найдена запись о серверной загрузке.");
        FlushPublished(publishedPath, artifact.Size);
        assignment = assignment with { ReceivedLocally = true };
        _journal.Write(assignment);
        await SendReceiptAsync(assignment, token).ConfigureAwait(false);
    }

    private RemoteDownloadAssignment Register(RemoteDownloadRequest request)
    {
        var assignment = new RemoteDownloadAssignment(Guid.NewGuid().ToString("N"), request);
        _journal.Write(assignment);
        return assignment;
    }

    private async Task SendReceiptAsync(RemoteDownloadAssignment assignment, CancellationToken token)
    {
        await RepeatAsync(async ct =>
        {
            await SendEmptyAsync(HttpMethod.Post, JobPath(assignment) + "/received", ct).ConfigureAwait(false);
            return true;
        }, token).ConfigureAwait(false);
        _journal.Remove(assignment);
    }

    private Task<RemoteJobStatus> PutAsync(RemoteDownloadAssignment assignment, CancellationToken token, bool waitForConnection) =>
        RepeatAsync(ct => ReadStatusAsync(HttpMethod.Put, assignment, assignment.Request, ct), token, waitForConnection);

    private async Task<RemoteJobStatus> ReadStatusAsync(HttpMethod method, RemoteDownloadAssignment assignment,
        RemoteDownloadRequest? payload, CancellationToken token)
    {
        using var deadline = Deadline(token, RequestTimeout);
        using var request = new HttpRequestMessage(method, JobPath(assignment));
        if (payload is not null)
        {
            request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(payload, WireJson));
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        }
        try
        {
            var result = await ReadJsonAsync<RemoteJobStatus>(_http, request, deadline.Token).ConfigureAwait(false);
            if (result is null || result.State is not ("queued" or "downloading" or "completed" or "failed" or "interrupted" or "received" or "cancelled") ||
                (result.Expired && result.State != "failed") ||
                result.Progress is < 0 or > 100 || (result.State == "completed" && (result.Size is null or <= 0 || string.IsNullOrWhiteSpace(result.Filename))))
                throw new InvalidDataException("Сервер вернул неверные данные о задании.");
            return result;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Сервер очереди не ответил вовремя."); }
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpClient client, HttpRequestMessage request, CancellationToken token,
        int maxResponseBytes = MaxResponseBytes)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        RequireSuccess(response);
        if (response.Content.Headers.ContentLength > maxResponseBytes)
            throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        for (int count; (count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
        {
            if (bytes.Length + count > maxResponseBytes)
                throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
            bytes.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), WireJson);
    }

    private Task SendEmptyAsync(HttpMethod method, string path, CancellationToken token) =>
        SendEmptyAsync(_http, method, path, token);

    private static async Task SendEmptyAsync(HttpClient client, HttpMethod method, string path, CancellationToken token)
    {
        using var deadline = Deadline(token, RequestTimeout);
        using var request = new HttpRequestMessage(method, path);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            RequireSuccess(response);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new InvalidDataException("Сервер не подтвердил операцию.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Сервер очереди не ответил вовремя."); }
    }

    private async Task<string> ReceiveAsync(string jobId, string container, RemoteJobStatus status, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token, bool waitForConnection)
    {
        long size = status.Size!.Value;
        string result = Path.Combine(stage, SafeFileName(status.Filename!));
        if (!HistoryFileAccess.IsSupportedMediaPath(result) ||
            container != "Auto" &&
                !Path.GetExtension(result).Equals("." + container, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер вернул неподдерживаемое расширение файла.");
        string partial = _journal.PartialPath(jobId);
        string validatorPath = partial + ".etag";
        OwnedFiles.RegularOrAbsent(partial);
        OwnedFiles.RegularOrAbsent(result);
        OwnedFiles.RegularOrAbsent(validatorPath);
        EntityTagHeaderValue? etag = null;
        var savedValidator = new FileInfo(validatorPath);
        if (savedValidator.Exists && savedValidator.Length <= 1024 &&
            EntityTagHeaderValue.TryParse(File.ReadAllText(validatorPath), out var savedTag) &&
            savedTag is { IsWeak: false } && savedTag.Tag != "*") etag = savedTag;
        using (var file = new FileStream(partial, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            if (etag is null || file.Length > size) file.SetLength(0);
            if (file.Length == size) file.SetLength(size - 1);
            file.Position = file.Length;
            bool restored = file.Position > 0;
            progress(ReceivingProgress(file.Position, size));
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await waitForResume(token).ConfigureAwait(false);
                    if (etag is null && file.Position > 0) { file.SetLength(0); file.Position = 0; }
                    long offset = file.Position;
                    using var request = new HttpRequestMessage(HttpMethod.Get, "api/remote/jobs/" + jobId + "/file");
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, size - 1);
                        request.Headers.IfRange = new RangeConditionHeaderValue(etag!);
                    }
                    using var response = await GetFileResponseAsync(request, token).ConfigureAwait(false);
                    if (offset > 0 && response.StatusCode == HttpStatusCode.OK &&
                        (restored || Equals(response.Headers.ETag, etag)))
                    {
                        file.SetLength(0);
                        file.Position = 0;
                        offset = 0;
                    }
                    restored = false;
                    if (response.StatusCode != (offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) ||
                        response.Content.Headers.ContentLength != size - offset ||
                        response.Content.Headers.ContentEncoding.Count != 0 ||
                        offset > 0 && (!Equals(response.Headers.ETag, etag) ||
                            response.Content.Headers.ContentRange is not { Unit: "bytes" } range ||
                            range.From != offset || range.To != size - 1 || range.Length != size))
                        throw new InvalidDataException("Размер или диапазон серверного файла изменился.");
                    etag = response.Headers.ETag is { IsWeak: false } strong && strong.Tag != "*" ? strong : null;
                    if (offset == 0)
                    {
                        if (etag is not null && etag.ToString().Length <= 512)
                            AppPersistence.AtomicWrite(validatorPath, etag.ToString(), flushToDisk: true);
                        else File.Delete(validatorPath);
                    }
                    using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await CopyFileAsync(input, file, size, waitForResume, progress, token).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    break;
                }
                catch (Exception error) when (!token.IsCancellationRequested && (attempt < 3 || waitForConnection) && IsTransient(error))
                {
                    progress(ReceivingProgress(file.Position, size));
                    await Task.Delay(300 << Math.Min(attempt, 5), token).ConfigureAwait(false);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        File.Move(partial, result);
        File.Delete(validatorPath);
        return result;
    }

    private void DiscardPartial(string jobId)
    {
        try { _journal.RemovePartial(jobId); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { _log("Could not clean up the remote transfer cache (" + error.GetType().Name + ")."); }
    }

    internal async Task<string> PickupAsync(RemoteQueueItem item, string output, Action<RemoteDownloadProgress> progress,
        CancellationToken token)
    {
        if (!IsQueueId(item.Id) || item.State is not ("completed" or "received") ||
            item.Size is null or <= 0 || string.IsNullOrWhiteSpace(item.Name))
            throw new InvalidDataException("Invalid remote result.");
        string stageRoot = Path.Combine(Path.GetTempPath(), "vd-pickup-" + Guid.NewGuid().ToString("N"));
        string stage = Path.Combine(stageRoot, Guid.NewGuid().ToString("N"));
        OwnedFiles.NoReparseAncestors(Path.Combine(output, "check"));
        OwnedFiles.NoReparseAncestors(Path.Combine(stage, "check"));
        Directory.CreateDirectory(stage);
        try
        {
            var status = new RemoteJobStatus(item.State, 100, item.Name, item.Size, null, null, false, false, false, false);
            string media = await ReceiveAsync(item.Id, "Auto", status, stage, _ => Task.CompletedTask,
                progress, token, waitForConnection: false).ConfigureAwait(false);
            await DownloadCoordinator.VerifyRemoteMediaAsync(media, () => false, FragmentMergeService.RunOwnedAsync, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return OwnedFiles.Publish(stage, output, media, renameCollisions: true).Path;
        }
        catch (InvalidDataException)
        {
            DiscardPartial(item.Id);
            throw;
        }
        finally
        {
            try
            {
                OwnedFiles.DeleteStage(stage, stageRoot);
                Directory.Delete(stageRoot);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { _log("Could not clean up server pickup staging (" + error.GetType().Name + ")."); }
        }
    }

    private static void FlushPublished(string path, long expectedSize)
    {
        OwnedFiles.RegularOrAbsent(path);
        using var published = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (published.Length != expectedSize) throw new IOException("Размер опубликованного файла изменился.");
        published.Flush(flushToDisk: true);
    }

    internal async Task ConfirmPickupAsync(string jobId, string path, long expectedSize, CancellationToken token)
    {
        FlushPublished(path, expectedSize);
        string receipt = _journal.PickupReceiptPath(jobId);
        AppPersistence.AtomicWrite(receipt, "received", flushToDisk: true);
        await SendPickupReceiptAsync(jobId, token).ConfigureAwait(false);
    }

    private async Task SendPickupReceiptAsync(string jobId, CancellationToken token)
    {
        await SendEmptyAsync(HttpMethod.Post, "api/remote/jobs/" + jobId + "/received", token).ConfigureAwait(false);
        File.Delete(_journal.PickupReceiptPath(jobId));
    }

    internal async Task ReplayPickupReceiptsAsync(CancellationToken token)
    {
        foreach (string jobId in _journal.PendingPickupReceipts())
        {
            try { await SendPickupReceiptAsync(jobId, token).ConfigureAwait(false); }
            catch (HttpRequestException error) when (error.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict)
            { File.Delete(_journal.PickupReceiptPath(jobId)); }
        }
    }

    private async Task<HttpResponseMessage> GetFileResponseAsync(HttpRequestMessage request, CancellationToken token)
    {
        using var deadline = Deadline(token, RequestTimeout);
        HttpResponseMessage response;
        try { response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Сервер не начал отдавать готовый файл."); }
        try { RequireSuccess(response); return response; }
        catch { response.Dispose(); throw; }
    }

    private async Task CopyFileAsync(Stream input, FileStream output, long size,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        long sampleBytes = 0;
        double sampleSeconds = 0;
        double speed = 0;
        bool reportedBytes = false;
        while (output.Position < size)
        {
            var resume = waitForResume(token);
            if (!resume.IsCompleted && speed > 0)
            {
                speed = 0;
                progress(ReceivingProgress(output.Position, size));
            }
            await resume.ConfigureAwait(false);
            long started = _clock.GetTimestamp();
            using var deadline = Deadline(token, ReadTimeout);
            int count;
            try
            {
                count = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, size - output.Position)),
                    deadline.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested)
            { throw new TimeoutException("Прервалась передача файла с сервера."); }
            if (count == 0) throw new EndOfStreamException("Передача файла завершилась раньше ожидаемого размера.");
            await output.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
            sampleBytes += count;
            sampleSeconds += _clock.GetElapsedTime(started).TotalSeconds;
            bool sampled = sampleSeconds >= TransferSampleSeconds;
            if (sampled)
            {
                speed = sampleBytes / sampleSeconds;
                sampleBytes = 0;
                sampleSeconds = 0;
            }
            if (sampled || !reportedBytes || output.Position == size)
            {
                progress(ReceivingProgress(output.Position, size, speed));
                reportedBytes = true;
            }
        }
    }

    private static RemoteDownloadProgress ReceivingProgress(long received, long size, double bytesPerSecond = 0) =>
        new(RemoteDownloadStage.Receiving, received * 100d / size, received, size, bytesPerSecond);

    private static string SafeFileName(string filename)
    {
        string extension = Path.GetExtension(filename);
        if (extension.Length is < 2 or > 12 || extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidDataException("Сервер вернул неверное имя файла.");
        string stem = filename[..^extension.Length];
        var invalid = Path.GetInvalidFileNameChars();
        stem = new string(stem.Select(c => invalid.Contains(c) || char.IsControl(c) || "<>:\"/\\|?*".Contains(c) ? '_' : c).ToArray());
        stem = stem[..Math.Min(stem.Length, 170)].Trim(' ', '.');
        if (stem.Length == 0) stem = "Видео";
        string device = stem.Split('.', 2)[0].TrimEnd(' ').ToUpperInvariant();
        if (device is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
            device.Length == 4 && (device.StartsWith("COM", StringComparison.Ordinal) || device.StartsWith("LPT", StringComparison.Ordinal)) &&
            "123456789¹²³".Contains(device[3])) stem = "_" + stem;
        return stem + extension.ToLowerInvariant();
    }

    private static void RequireSuccess(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        string error = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Сервер отклонил вход. Проверьте логин и пароль.",
            HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed => "Сервер не поддерживает совместную загрузку или задание больше недоступно.",
            _ => $"Сервер очереди вернул HTTP {(int)response.StatusCode}."
        };
        throw new HttpRequestException(error, null, response.StatusCode);
    }

    internal static bool IsRemoteFailure(Exception error) =>
        error is HttpRequestException or AuthenticationException or IOException or InvalidDataException or
            TimeoutException or OperationCanceledException or JsonException;

    internal static string DescribeConnectionFailure(Exception error)
    {
        if (error is AuthenticationException or HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            or { InnerException: AuthenticationException })
            return ErrorMessages.Text("Не удалось установить HTTPS-соединение. Проверьте сертификат сервера и совпадение его имени с адресом.",
                "Could not establish an HTTPS connection. Check the server certificate and whether its name matches the address.");
        if (error is HttpRequestException { StatusCode: HttpStatusCode.Unauthorized })
            return ErrorMessages.Text("Сервер отклонил логин или пароль.", "The server rejected the username or password.");
        if (error is HttpRequestException { StatusCode: HttpStatusCode.Forbidden })
            return ErrorMessages.Text("Сервер запретил доступ. Проверьте права подключения и настройки HTTPS-прокси.",
                "The server denied access. Check connection permissions and HTTPS proxy settings.");
        if (error is InvalidDataException or JsonException or
            HttpRequestException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed })
            return ErrorMessages.Text("По этому адресу нет совместимого сервера. Проверьте адрес или обновите серверную часть.",
                "No compatible server responded at this address. Check the address or update the server application.");
        if (error is HttpRequestException { HttpRequestError: HttpRequestError.NameResolutionError })
            return ErrorMessages.Text("Не удалось найти сервер. Проверьте адрес и DNS.", "Could not find the server. Check the address and DNS.");
        if (error is HttpRequestException { StatusCode: { } status })
        {
            if ((int)status is >= 300 and < 400)
                return ErrorMessages.Text("Сервер перенаправляет запрос. Введите конечный HTTPS-адрес.",
                    "The server redirected the request. Enter the final HTTPS address.");
            return ErrorMessages.Text($"Сервер вернул HTTP {(int)status}.", $"The server returned HTTP {(int)status}.");
        }
        if (error is TimeoutException or OperationCanceledException)
            return ErrorMessages.Text("Сервер не ответил вовремя. Проверьте адрес и доступность сервера.",
                "The server did not respond in time. Check the address and whether the server is reachable.");
        return ErrorMessages.Text("Не удалось подключиться к серверу. Проверьте адрес и доступность сервера.",
            "Could not connect to the server. Check the address and whether the server is reachable.");
    }

    private static bool IsTransient(Exception error) =>
        error is TimeoutException or EndOfStreamException or HttpIOException ||
        error is HttpRequestException { HttpRequestError: not HttpRequestError.SecureConnectionError } http && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int?)http.StatusCode >= 500);

    private static async Task<T> RepeatAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token, bool waitForConnection = false)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await action(token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && (attempt < 2 || waitForConnection) && IsTransient(error))
            { await Task.Delay(300 << Math.Min(attempt, 5), token).ConfigureAwait(false); }
        }
    }

    private static CancellationTokenSource Deadline(CancellationToken token, TimeSpan timeout)
    {
        var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        return deadline;
    }

    private static string JobPath(RemoteDownloadAssignment assignment) => "api/remote/jobs/" + assignment.JobId;

    public void Dispose() => _http.Dispose();

    internal sealed record RemoteCapabilities(int Version, int MaxConcurrent, long? FreeDiskBytes = null);
    private sealed record RemoteJobStatus(string State, int? Progress, string? Filename, long? Size, string? Error, string? Title,
        bool Unsupported, bool Expired, bool WaitingForSpace, bool Removed, string? WaitingReason = null);
}

internal sealed record RemoteQueuePage(RemoteQueueItem[] Jobs, string? Next, long? FreeDiskBytes, long? UnclaimedBytes, long? UnclaimedLimitBytes);
internal sealed record RemoteQueueItem(string Id, string State, string? Name, string? Source, long? Size, double? Progress, double? ExpiresAt, double? DeleteAfter,
    string? WaitingReason = null, bool Prioritized = false);

internal sealed record RemoteQueueNotices(string[] Expiring, string? WaitingReason);

internal static class RemoteQueueStatus
{
    internal static string Waiting(string? reason) => reason switch
    {
        "disk" => ErrorMessages.Text("Мало места на VPS", "Low VPS disk space"),
        "quota" => ErrorMessages.Text("Ожидание квоты 15 ГБ", "Waiting for the 15 GB quota"),
        "slots" => ErrorMessages.Text("Заняты серверные слоты", "Server slots are busy"),
        _ => ErrorMessages.Text("В очереди на сервере", "Queued on the server")
    };
}
