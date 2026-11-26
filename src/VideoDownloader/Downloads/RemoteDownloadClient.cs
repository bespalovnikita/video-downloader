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
    int MaxConcurrent => 4;
    Task PrepareAsync(CancellationToken token);
    bool Supports(DownloadItem row, DownloadOptions options);
    bool OwnsAssignment(DownloadItem row);
    Task<RemoteDownloadArtifact> DownloadAsync(DownloadItem row, DownloadOptions options, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token);
    Task AcknowledgeAsync(RemoteDownloadArtifact artifact, string publishedPath, CancellationToken token);
}

internal enum RemoteDownloadStage { Waiting, Downloading, Receiving }

internal sealed record RemoteDownloadProgress(RemoteDownloadStage Stage, double Percent, long BytesReceived, long TotalBytes);
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
            options.EmbedSubs || options.EmbedThumbnail || options.Sponsor || options.UseHttpChunks ||
            options.PreferAria2c || options.RateLimit != "Unlimited" || options.Fragments != "Auto" ||
            options.FilenameTemplate != "%(title)s [%(id)s].%(ext)s")
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
    private const int MaxResponseBytes = 32 * 1024;
    private readonly HttpClient _http;
    private readonly RemoteDownloadJournal _journal;
    private readonly Action<string> _log;
    public bool IsAvailable { get; private set; }
    public int MaxConcurrent { get; private set; } = 4;

    internal RemoteDownloadClient(RemoteDownloadConnection connection, AppPersistence persistence, Action<string> log,
        HttpMessageHandler? handler = null)
    {
        _log = log;
        _journal = new RemoteDownloadJournal(persistence, connection, log);
        _http = CreateHttpClient(connection, handler);
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

    internal static async Task<int> CheckConnectionAsync(RemoteDownloadConnection connection, CancellationToken token,
        HttpMessageHandler? handler = null)
    {
        using var client = CreateHttpClient(connection, handler);
        return (await ReadCapabilitiesAsync(client, token).ConfigureAwait(false)).MaxConcurrent;
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

    public async Task<RemoteDownloadArtifact> DownloadAsync(DownloadItem row, DownloadOptions options, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token)
    {
        var assignment = _journal.Find(row.Url);
        if (assignment is { ReceivedLocally: true })
        {
            await SendReceiptAsync(assignment, token).ConfigureAwait(false);
            assignment = null;
        }

        var request = assignment?.Request ?? RemoteDownloadRequest.For(row, options)
            ?? throw new InvalidOperationException("Настройки задания не поддерживаются серверной очередью.");
        await waitForResume(token).ConfigureAwait(false);
        bool reopening = assignment is not null;
        assignment ??= Register(request);
        try
        {
            var status = await PutAsync(assignment, token).ConfigureAwait(false);
            if (reopening && !status.Unsupported && !status.Expired &&
                status.State is ("cancelled" or "interrupted" or "failed"))
            {
                _journal.Remove(assignment);
                assignment = Register(request);
                status = await PutAsync(assignment, token).ConfigureAwait(false);
            }
            while (status.State is "queued" or "downloading")
            {
                var phase = status.State == "queued" ? RemoteDownloadStage.Waiting : RemoteDownloadStage.Downloading;
                progress(new RemoteDownloadProgress(phase, status.Progress ?? 0, 0, status.Size ?? 0));
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
                await waitForResume(token).ConfigureAwait(false);
                status = await RepeatAsync(ct => ReadStatusAsync(HttpMethod.Get, assignment, null, ct), token).ConfigureAwait(false);
            }
            if (status.State != "completed")
            {
                _journal.Remove(assignment);
                if (status.Expired) throw ExpiredResult();
                if (status.State == "failed" && status.Unsupported) throw new RemoteDownloadUnsupportedException();
                string error = status.Error is { Length: > 0 } detail ? detail[..Math.Min(detail.Length, 500)] : "Сервер не подготовил файл.";
                throw new IOException(error);
            }
            string path = await ReceiveAsync(assignment, status, stage, waitForResume, progress, token).ConfigureAwait(false);
            return new RemoteDownloadArtifact(assignment.JobId, path, status.Title ?? Path.GetFileNameWithoutExtension(path), status.Size!.Value);
        }
        catch (HttpRequestException error) when (error.StatusCode == HttpStatusCode.Gone)
        {
            _journal.Remove(assignment);
            throw ExpiredResult();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
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
        OwnedFiles.RegularOrAbsent(publishedPath);
        using (var published = new FileStream(publishedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
        {
            if (published.Length != artifact.Size) throw new IOException("Размер опубликованного файла изменился.");
            published.Flush(flushToDisk: true);
        }
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

    private Task<RemoteJobStatus> PutAsync(RemoteDownloadAssignment assignment, CancellationToken token) =>
        RepeatAsync(ct => ReadStatusAsync(HttpMethod.Put, assignment, assignment.Request, ct), token);

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

    private static async Task<T?> ReadJsonAsync<T>(HttpClient client, HttpRequestMessage request, CancellationToken token)
    {
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        RequireSuccess(response);
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
            throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
        using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[4096];
        for (int count; (count = await stream.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
        {
            if (bytes.Length + count > MaxResponseBytes)
                throw new InvalidDataException("Ответ сервера превышает допустимый размер.");
            bytes.Write(buffer, 0, count);
        }
        return JsonSerializer.Deserialize<T>(bytes.GetBuffer().AsSpan(0, (int)bytes.Length), WireJson);
    }

    private async Task SendEmptyAsync(HttpMethod method, string path, CancellationToken token)
    {
        using var deadline = Deadline(token, RequestTimeout);
        using var request = new HttpRequestMessage(method, path);
        try
        {
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            RequireSuccess(response);
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw new InvalidDataException("Сервер не подтвердил операцию.");
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Сервер очереди не ответил вовремя."); }
    }

    private async Task<string> ReceiveAsync(RemoteDownloadAssignment assignment, RemoteJobStatus status, string stage,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token)
    {
        long size = status.Size!.Value;
        string result = Path.Combine(stage, SafeFileName(status.Filename!));
        if (!HistoryFileAccess.IsSupportedMediaPath(result) ||
            assignment.Request.Container != "Auto" &&
                !Path.GetExtension(result).Equals("." + assignment.Request.Container, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Сервер вернул неподдерживаемое расширение файла.");
        string partial = Path.Combine(stage, "remote-" + assignment.JobId + ".part");
        OwnedFiles.RegularOrAbsent(partial);
        OwnedFiles.RegularOrAbsent(result);
        EntityTagHeaderValue? etag = null;
        using (var file = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    await waitForResume(token).ConfigureAwait(false);
                    if (etag is null && file.Position > 0) { file.SetLength(0); file.Position = 0; }
                    long offset = file.Position;
                    using var request = new HttpRequestMessage(HttpMethod.Get, JobPath(assignment) + "/file");
                    if (offset > 0)
                    {
                        request.Headers.Range = new RangeHeaderValue(offset, size - 1);
                        request.Headers.IfRange = new RangeConditionHeaderValue(etag!);
                    }
                    using var response = await GetFileResponseAsync(request, token).ConfigureAwait(false);
                    if (response.StatusCode != (offset == 0 ? HttpStatusCode.OK : HttpStatusCode.PartialContent) ||
                        response.Content.Headers.ContentLength != size - offset ||
                        response.Content.Headers.ContentEncoding.Count != 0 ||
                        offset > 0 && (!Equals(response.Headers.ETag, etag) ||
                            response.Content.Headers.ContentRange is not { Unit: "bytes" } range ||
                            range.From != offset || range.To != size - 1 || range.Length != size))
                        throw new InvalidDataException("Размер или диапазон серверного файла изменился.");
                    etag = response.Headers.ETag is { IsWeak: false } strong ? strong : null;
                    using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
                    await CopyFileAsync(input, file, size, waitForResume, progress, token).ConfigureAwait(false);
                    file.Flush(flushToDisk: true);
                    break;
                }
                catch (Exception error) when (!token.IsCancellationRequested && attempt < 3 && IsTransient(error))
                { await Task.Delay(300 << attempt, token).ConfigureAwait(false); }
            }
        }
        token.ThrowIfCancellationRequested();
        File.Move(partial, result);
        return result;
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

    private static async Task CopyFileAsync(Stream input, FileStream output, long size,
        Func<CancellationToken, Task> waitForResume, Action<RemoteDownloadProgress> progress, CancellationToken token)
    {
        byte[] buffer = new byte[128 * 1024];
        long lastUpdate = 0;
        while (output.Position < size)
        {
            await waitForResume(token).ConfigureAwait(false);
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
            long now = Environment.TickCount64;
            if (lastUpdate == 0 || now - lastUpdate >= 200 || output.Position == size)
            {
                progress(new RemoteDownloadProgress(RemoteDownloadStage.Receiving, output.Position * 100d / size, output.Position, size));
                lastUpdate = now;
            }
        }
    }

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
        error is HttpRequestException http && (http.StatusCode is null or HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests ||
            (int?)http.StatusCode >= 500);

    private static async Task<T> RepeatAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        for (int attempt = 0; ; attempt++)
        {
            try { return await action(token).ConfigureAwait(false); }
            catch (Exception error) when (!token.IsCancellationRequested && attempt < 2 && IsTransient(error))
            { await Task.Delay(300 << attempt, token).ConfigureAwait(false); }
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

    private sealed record RemoteCapabilities(int Version, int MaxConcurrent);
    private sealed record RemoteJobStatus(string State, int? Progress, string? Filename, long? Size, string? Error, string? Title,
        bool Unsupported, bool Expired);
}
