using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

internal static class YtDlpUpdatePolicy
{
    internal const int MaxChecksumBytes = 64 * 1024;

    // PE metadata is only a version candidate. It never authorizes retention:
    // the entire file must match SHA2-256SUMS from that exact official release.
    internal static string? ReleaseVersion(string path)
    {
        AppUpdatePackage.AssertNoReparse(path);
        if (!File.Exists(path)) return null;
        var info = FileVersionInfo.GetVersionInfo(path);
        if (info.FileMajorPart < 2000 || info.FileMajorPart > 9999) return null;
        try { return new DateOnly(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart).ToString("yyyy.MM.dd", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    internal static async Task PreserveNewerAsync(PreparedAppUpdate prepared,
        Func<Uri, long, CancellationToken, Task<byte[]>> readChecksums, CancellationToken cancel)
    {
        string installed = Path.Combine(prepared.TargetDirectory, "yt-dlp.exe");
        string bundled = Path.Combine(prepared.StageDirectory, "yt-dlp.exe");
        string? currentVersion = ReleaseVersion(installed);
        if (currentVersion is null) return;
        string? bundledVersion = ReleaseVersion(bundled);
        if (bundledVersion is null)
            throw new InvalidDataException("Не удалось определить версию yt-dlp в пакете. Обновление приложения не установлено.");
        int comparison = string.CompareOrdinal(currentVersion, bundledVersion);
        int currentRevision = FileVersionInfo.GetVersionInfo(installed).FilePrivatePart;
        int bundledRevision = FileVersionInfo.GetVersionInfo(bundled).FilePrivatePart;
        if (comparison < 0 || (comparison == 0 && currentRevision <= bundledRevision)) return;
        try
        {
            if (comparison == 0 || currentRevision != 0)
                throw new InvalidDataException("The installed yt-dlp is not a stable release version.");
            // Pin the identity before HTTP. RetainVerifiedYtDlp checks it again
            // against both the live file and the preparation snapshot.
            var runtime = new AppPackageFile("yt-dlp.exe", new FileInfo(installed).Length, AppUpdatePackage.HashFile(installed));
            var uri = new Uri($"https://github.com/yt-dlp/yt-dlp/releases/download/{currentVersion}/SHA2-256SUMS");
            byte[] bytes = await readChecksums(uri, MaxChecksumBytes, cancel).ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            if (bytes.Length > MaxChecksumBytes) throw new InvalidDataException("yt-dlp checksum response exceeds its limit.");
            string text = new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF');
            var matches = Regex.Matches(text, @"(?m)^([0-9a-fA-F]{64})[ \t]+\*?yt-dlp\.exe\r?$");
            if (matches.Count != 1 || !runtime.Sha256.Equals(matches[0].Groups[1].Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The installed yt-dlp does not match the official release checksum.");
            cancel.ThrowIfCancellationRequested();
            await Task.Run(() => AppUpdateInstaller.RetainVerifiedYtDlp(prepared, runtime), cancel).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or InvalidDataException or HttpRequestException or DecoderFallbackException or UnauthorizedAccessException)
        {
            throw new IOException("Установленный yt-dlp выглядит новее версии в пакете, но его официальную контрольную сумму не удалось подтвердить. " +
                "Обновление приложения остановлено; компоненты сохранены. Проверьте подключение и повторите обновление.", error);
        }
    }
}
