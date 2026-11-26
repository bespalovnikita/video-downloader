using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VideoDownloader.Native;

// The assignment survives restart before the first request leaves Windows.
// This store contains no credentials and is outside session/portable backup.
internal sealed class RemoteDownloadJournal
{
    private const int MaxRecordBytes = 64 * 1024;
    private readonly string _directory;
    private readonly ConcurrentDictionary<string, RemoteDownloadAssignment> _active = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _unreadable = new(StringComparer.Ordinal);
    private readonly Action<string> _log;

    internal RemoteDownloadJournal(AppPersistence persistence, RemoteDownloadConnection connection, Action<string> log)
    {
        _directory = Path.Combine(Path.GetDirectoryName(persistence.SessionPath)!, "remote-jobs",
            Hash(connection.Address.AbsoluteUri + "\n" + connection.Username));
        _log = log;
    }

    internal RemoteDownloadAssignment[] Load()
    {
        OwnedFiles.NoReparseAncestors(Path.Combine(_directory, "check"));
        if (!Directory.Exists(_directory)) return [];
        foreach (string path in Directory.EnumerateFiles(_directory, "*.json"))
        {
            string key = Path.GetFileNameWithoutExtension(path);
            if (key.Length != 64 || key.Any(c => !Uri.IsHexDigit(c))) continue;
            try
            {
                OwnedFiles.RegularOrAbsent(path);
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                if (stream.Length > MaxRecordBytes) throw new InvalidDataException("Remote assignment record exceeds its limit.");
                var entry = JsonSerializer.Deserialize<RemoteDownloadAssignment>(stream, AppPersistence.Json);
                if (entry is null || !IsJobId(entry.JobId) || entry.Request is null ||
                    !entry.Request.IsValid || key != Hash(entry.Request.Url))
                    throw new InvalidDataException("Invalid remote assignment record.");
                _active[key] = entry;
            }
            catch (Exception error) when (error is IOException or InvalidDataException or UnauthorizedAccessException or JsonException)
            {
                _unreadable[key] = 0;
                _log("Повреждена запись о задании на сервере. Исходная запись сохранена.");
            }
        }
        return _active.Values.Where(entry => entry.ReceivedLocally).ToArray();
    }

    internal bool OwnsAssignment(string url)
    {
        string key = Hash(url);
        return _unreadable.ContainsKey(key) || _active.TryGetValue(key, out var entry) && !entry.ReceivedLocally;
    }

    internal RemoteDownloadAssignment? Find(string url)
    {
        string key = Hash(url);
        if (_unreadable.ContainsKey(key)) throw new IOException("Запись о задании на сервере повреждена.");
        return _active.GetValueOrDefault(key);
    }

    internal RemoteDownloadAssignment? FindById(string jobId) =>
        _active.Values.FirstOrDefault(entry => entry.JobId == jobId);

    internal void Write(RemoteDownloadAssignment assignment)
    {
        string key = Hash(assignment.Request.Url);
        string path = Path.Combine(_directory, key + ".json");
        OwnedFiles.RegularOrAbsent(path);
        AppPersistence.AtomicWrite(path, JsonSerializer.Serialize(assignment, AppPersistence.Json));
        _active[key] = assignment;
    }

    internal void Remove(RemoteDownloadAssignment assignment)
    {
        string key = Hash(assignment.Request.Url);
        if (!_active.TryGetValue(key, out var current) || current.JobId != assignment.JobId) return;
        string path = Path.Combine(_directory, key + ".json");
        OwnedFiles.RegularOrAbsent(path);
        File.Delete(path);
        _active.TryRemove(key, out _);
    }

    private static bool IsJobId(string? value) =>
        Guid.TryParseExact(value, "N", out var guid) && value == guid.ToString("N");

    private static string Hash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

internal sealed record RemoteDownloadAssignment(string JobId, RemoteDownloadRequest Request, bool ReceivedLocally = false);
