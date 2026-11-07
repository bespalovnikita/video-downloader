using System.IO.Compression;

namespace VideoDownloader.Native;

// An explicit Install FFmpeg owns only these two destination names. In
// particular, PATH tools and unrelated files in the application are untouched.
internal static class FfmpegInstallTransaction
{
    private static readonly string[] Names = { "ffmpeg.exe", "ffprobe.exe" };
    private sealed record Original(string Name, bool Exists, long Size, string Hash);

    internal static void Install(string archivePath, string installDirectory, CancellationToken cancel,
        Action<int>? afterReplacement = null)
    {
        string target = Path.GetFullPath(installDirectory);
        AppUpdatePackage.AssertNoReparse(target);
        if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The component directory is missing.");
        string transaction = Path.Combine(target, ".vd-ffmpeg-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(transaction);
        bool preserveBackup = false;
        var originals = new List<Original>();
        var changed = new List<string>();
        var expected = new Dictionary<string, (long Size, string Hash)>();
        try
        {
            AppUpdatePackage.AssertNoReparse(transaction);
            ExtractPair(archivePath, transaction, cancel);
            foreach (string name in Names)
            {
                string staged = Path.Combine(transaction, name);
                expected.Add(name, (new FileInfo(staged).Length, AppUpdatePackage.HashFile(staged)));
                string path = Path.Combine(target, name);
                AppUpdatePackage.AssertNoReparse(path);
                if (Directory.Exists(path)) throw new IOException("A component destination is a directory: " + name);
                bool exists = File.Exists(path);
                if (exists && (File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                    throw new IOException("A component destination is read-only: " + name);
                var original = new Original(name, exists, exists ? new FileInfo(path).Length : 0,
                    exists ? AppUpdatePackage.HashFile(path) : "");
                if (exists)
                {
                    Copy(path, Path.Combine(transaction, name + ".backup"));
                    Verify(Path.Combine(transaction, name + ".backup"), original.Size, original.Hash);
                }
                originals.Add(original);
            }
            // Finish both backups, then check for a change before replacing
            // either member of the pair. Cancellation never interrupts rollback.
            foreach (var original in originals) VerifyOriginal(target, original);
            cancel.ThrowIfCancellationRequested();
            try
            {
                foreach (var original in originals)
                {
                    cancel.ThrowIfCancellationRequested();
                    VerifyOriginal(target, original);
                    string staged = Path.Combine(transaction, original.Name);
                    string destination = Path.Combine(target, original.Name);
                    AppUpdatePackage.AssertNoReparse(destination);
                    if (original.Exists)
                    {
                        // Retain the previous overwrite operation's write/share
                        // admission: permission to rename alone is insufficient.
                        using var writable = new FileStream(destination, FileMode.Open, FileAccess.ReadWrite,
                            FileShare.ReadWrite | FileShare.Delete);
                    }
                    // ReplaceFile preserves an existing Windows destination's
                    // DACL instead of inheriting broader staging permissions.
                    if (original.Exists) File.Replace(staged, destination, null);
                    else File.Move(staged, destination, false);
                    changed.Add(original.Name);
                    Verify(destination, expected[original.Name].Size, expected[original.Name].Hash);
                    afterReplacement?.Invoke(changed.Count);
                }
            }
            catch (Exception failure)
            {
                try
                {
                    foreach (string name in changed.AsEnumerable().Reverse())
                    {
                        var original = originals.Single(item => item.Name == name);
                        string destination = Path.Combine(target, name);
                        AppUpdatePackage.AssertNoReparse(destination);
                        // Never remove or replace bytes introduced by someone
                        // else after our publication.
                        Verify(destination, expected[name].Size, expected[name].Hash);
                        if (original.Exists)
                        {
                            string backup = Path.Combine(transaction, name + ".backup");
                            Verify(backup, original.Size, original.Hash);
                            File.Replace(backup, destination, null);
                            Verify(destination, original.Size, original.Hash);
                        }
                        else File.Delete(destination);
                    }
                }
                catch (Exception restoration)
                {
                    preserveBackup = true;
                    throw new AggregateException(ErrorMessages.Text(
                        "Установка компонентов не завершена. Откат неполон; копии прежних файлов сохранены в папке: ",
                        "Component installation failed; recovery copies were preserved at ") + transaction,
                        failure, restoration);
                }
                throw;
            }
        }
        finally
        {
            if (!preserveBackup) Cleanup(transaction);
        }
    }

    private static void ExtractPair(string archivePath, string transaction, CancellationToken cancel)
    {
        using var zip = ZipFile.OpenRead(archivePath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
        {
            string path = entry.FullName.Replace('\\', '/');
            string name = path[(path.LastIndexOf('/') + 1)..];
            if (!Names.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if (path.StartsWith('/') || path.Split('/').Any(part => part is "" or "." or ".." || part.Contains(':')) ||
                (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 || (type != 0 && type != 0x8000) ||
                entry.Length is <= 0 or > 512L * 1024 * 1024 || !entries.TryAdd(name, entry))
                throw new InvalidDataException("Invalid or ambiguous FFmpeg package binaries.");
        }
        if (entries.Count != 2 || Path.GetDirectoryName(entries[Names[0]].FullName) != Path.GetDirectoryName(entries[Names[1]].FullName))
            throw new InvalidDataException("No matching FFmpeg/ffprobe pair in package.");
        byte[] buffer = new byte[81920];
        foreach (string name in Names)
        {
            var entry = entries[name];
            string path = Path.Combine(transaction, name);
            AppUpdatePackage.AssertNoReparse(path);
            using (var input = entry.Open())
            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                long written = 0;
                int count;
                while ((count = input.Read(buffer)) != 0)
                {
                    cancel.ThrowIfCancellationRequested();
                    if (count > entry.Length - written) throw new InvalidDataException("Expanded component exceeds its declared size.");
                    output.Write(buffer, 0, count);
                    written += count;
                }
                if (written != entry.Length) throw new InvalidDataException("Incomplete FFmpeg component.");
                output.Flush(true);
            }
        }
    }

    private static void VerifyOriginal(string target, Original original)
    {
        string path = Path.Combine(target, original.Name);
        AppUpdatePackage.AssertNoReparse(path);
        if (original.Exists) Verify(path, original.Size, original.Hash);
        else if (File.Exists(path) || Directory.Exists(path)) throw new IOException("A component destination appeared during installation.");
    }

    private static void Verify(string path, long size, string hash)
    {
        AppUpdatePackage.AssertNoReparse(path);
        if (new FileInfo(path).Length != size || AppUpdatePackage.HashFile(path) != hash)
            throw new IOException("A component file changed during installation.");
    }

    private static void Copy(string source, string destination)
    {
        AppUpdatePackage.AssertNoReparse(source);
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void Cleanup(string transaction)
    {
        try
        {
            AppUpdatePackage.AssertNoReparse(transaction);
            // Only exact files created here; no recursive traversal/deletion.
            foreach (string name in Names)
            foreach (string suffix in new[] { "", ".backup" })
            {
                string path = Path.Combine(transaction, name + suffix);
                AppUpdatePackage.AssertNoReparse(path);
                File.Delete(path);
            }
            Directory.Delete(transaction, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
