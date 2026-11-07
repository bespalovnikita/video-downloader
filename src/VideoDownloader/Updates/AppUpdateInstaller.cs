using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace VideoDownloader.Native;

public sealed record PreparedAppUpdate(string Directory, string StageDirectory, string BackupDirectory,
    string TargetDirectory, string Version) : IDisposable
{
    public void Dispose() => AppUpdateInstaller.Cleanup(this);
}

public static class AppUpdateInstaller
{
    private const string Prefix = "video-downloader-update-";
    private const string OwnerFile = "update-owner.json";
    private const string SnapshotFile = "previous-package.json";
    private const string NextSnapshotFile = "next-package.json";
    private const string RetainedRuntimeFile = "retained-yt-dlp.exe";
    private const string PointerFile = ".video-downloader-update.json";
    private const int MaxOwnerBytes = 4 * 1024 * 1024;
    private const long MaxArchiveBytes = 512L * 1024 * 1024;
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;
    private const int MaxEntries = 4096;
    private static readonly StringComparer Paths = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions MetadataJson = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    private sealed class Owner
    {
        public int Schema { get; set; } = 2;
        public string Id { get; set; } = "";
        public string TargetDirectory { get; set; } = "";
        public string Version { get; set; } = "";
        public string OriginalManifestSha256 { get; set; } = "";
        public string NextManifestSha256 { get; set; } = "";
        public string PreparedYtDlpSha256 { get; set; } = "";
        public AppPackageFile? RetainedYtDlp { get; set; }
        public bool BackupReady { get; set; }
        public List<BackupFile> OldFiles { get; set; } = new();
        public List<string> NewFiles { get; set; } = new();
        public List<string> TouchedFiles { get; set; } = new();
        public List<string> PendingTemps { get; set; } = new();
        public string State { get; set; } = "Prepared";
    }

    private sealed class BackupFile
    {
        public string Path { get; set; } = "";
        public bool Exists { get; set; }
        public long Size { get; set; }
        public string Sha256 { get; set; } = "";
    }

    private sealed class Pointer
    {
        public int Schema { get; set; } = 1;
        public string Id { get; set; } = "";
        public string UpdateDirectory { get; set; } = "";
        public string TargetDirectory { get; set; } = "";
        public string OriginalManifestSha256 { get; set; } = "";
    }

    public static PreparedAppUpdate Prepare(string archivePath, string currentDirectory, string scratchRoot,
        string expectedVersion, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentDirectory));
        EnsurePlainPath(target);
        if (!System.IO.Directory.Exists(target)) throw new DirectoryNotFoundException("The application directory is missing.");
        string originalManifest = Child(target, AppUpdatePackage.ManifestName);
        // A package ownership manifest is required. Actual old bytes can differ:
        // yt-dlp is independently updated by the application.
        _ = ReadPackageManifest(originalManifest);
        string root = Path.GetFullPath(scratchRoot);
        EnsurePlainPath(root);
        System.IO.Directory.CreateDirectory(root);
        EnsurePlainPath(root);
        string id = Guid.NewGuid().ToString("N");
        string directory = Path.Combine(root, Prefix + id);
        if (Within(directory, target) || Within(target, directory))
            throw new IOException("Update staging must be separate from the application directory.");
        if (System.IO.Directory.Exists(directory) || File.Exists(directory)) throw new IOException("Update staging already exists.");
        System.IO.Directory.CreateDirectory(directory);
        var prepared = new PreparedAppUpdate(directory, Path.Combine(directory, "stage"), Path.Combine(directory, "backup"), target, expectedVersion);
        try
        {
            EnsurePlainPath(directory);
            System.IO.Directory.CreateDirectory(prepared.StageDirectory);
            System.IO.Directory.CreateDirectory(prepared.BackupDirectory);
            string snapshot = Path.Combine(directory, SnapshotFile);
            File.Copy(originalManifest, snapshot, false);
            var owner = new Owner
            {
                Id = id, TargetDirectory = target, Version = expectedVersion,
                OriginalManifestSha256 = AppUpdatePackage.HashFile(snapshot),
                PreparedYtDlpSha256 = File.Exists(Child(target, "yt-dlp.exe"))
                    ? AppUpdatePackage.HashFile(Child(target, "yt-dlp.exe")) : ""
            };
            WriteOwner(directory, owner);
            Extract(archivePath, prepared.StageDirectory, cancellationToken);
            var manifest = ReadPackageManifest(Path.Combine(prepared.StageDirectory, AppUpdatePackage.ManifestName));
            if (!string.Equals(manifest.Version, expectedVersion, StringComparison.Ordinal))
                throw new InvalidDataException("The update package has a different version.");
            cancellationToken.ThrowIfCancellationRequested();
            AppUpdatePackage.ValidateDirectory(prepared.StageDirectory, manifest);
            cancellationToken.ThrowIfCancellationRequested();
            File.Copy(Path.Combine(prepared.StageDirectory, AppUpdatePackage.ManifestName), Path.Combine(directory, NextSnapshotFile), false);
            owner.NextManifestSha256 = AppUpdatePackage.HashFile(Path.Combine(directory, NextSnapshotFile));
            owner.NewFiles = manifest.Files.Select(file => file.Path).ToList();
            WriteOwner(directory, owner);
            Preflight(prepared, owner, manifest);
            return prepared;
        }
        catch
        {
            // The unique directory was created by this invocation. Never follow
            // a reparse point even when cleaning a failed extraction.
            SafeDeleteTree(directory);
            throw;
        }
    }

    // The policy verifies the installed bytes against a fixed official release
    // checksum before calling this. The ZIP/stage remains unchanged; only the
    // effective ownership manifest describes the verified retained runtime.
    internal static void RetainVerifiedYtDlp(PreparedAppUpdate update, AppPackageFile verified)
    {
        var owner = ReadOwner(update.Directory);
        ValidatePrepared(update, owner);
        RequirePreparedState(owner, update.Directory);
        if (verified.Path != "yt-dlp.exe" || owner.RetainedYtDlp is not null ||
            verified.Sha256 != owner.PreparedYtDlpSha256)
            throw new InvalidDataException("The verified yt-dlp no longer matches the prepared update.");
        string installed = Child(update.TargetDirectory, verified.Path);
        VerifyFile(installed, verified.Size, verified.Sha256);
        var bundled = ReadPackageManifest(Child(update.StageDirectory, AppUpdatePackage.ManifestName));
        AppUpdatePackage.ValidateDirectory(update.StageDirectory, bundled);
        CopyDurably(installed, Child(update.Directory, RetainedRuntimeFile));
        VerifyFile(Child(update.Directory, RetainedRuntimeFile), verified.Size, verified.Sha256);
        owner.RetainedYtDlp = verified;
        var effective = EffectiveManifest(bundled, owner);
        string next = Child(update.Directory, NextSnapshotFile);
        using (var output = new FileStream(next, FileMode.Truncate, FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(output, effective, AppUpdatePackage.Json);
            output.Flush(true);
        }
        owner.NextManifestSha256 = AppUpdatePackage.HashFile(next);
        WriteOwner(update.Directory, owner);
    }

    private static AppPackageManifest EffectiveManifest(AppPackageManifest bundled, Owner owner)
    {
        if (owner.RetainedYtDlp is not { } retained) return bundled;
        if (retained.Path != "yt-dlp.exe" || retained.Sha256 != owner.PreparedYtDlpSha256)
            throw new InvalidDataException("Invalid retained yt-dlp metadata.");
        var effective = bundled with { Files = bundled.Files.Select(file => Paths.Equals(file.Path, retained.Path) ? retained : file).ToList() };
        AppUpdatePackage.ValidateManifest(effective);
        return effective;
    }

    public static PreparedAppUpdate OpenPrepared(string directory)
    {
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        var owner = ReadOwner(full);
        var prepared = new PreparedAppUpdate(full, Path.Combine(full, "stage"), Path.Combine(full, "backup"),
            owner.TargetDirectory, owner.Version);
        ValidatePrepared(prepared, owner);
        RequirePreparedState(owner, full);
        var manifest = ReadPackageManifest(Path.Combine(prepared.StageDirectory, AppUpdatePackage.ManifestName));
        if (!string.Equals(manifest.Version, owner.Version, StringComparison.Ordinal))
            throw new InvalidDataException("The staged update version changed.");
        AppUpdatePackage.ValidateDirectory(prepared.StageDirectory, manifest);
        ValidateNextSnapshot(prepared, owner, manifest);
        Preflight(prepared, owner, manifest);
        return prepared;
    }

    public static void Apply(PreparedAppUpdate update, Action<int>? faultAfterFile = null)
    {
        var owner = ReadOwner(update.Directory);
        ValidatePrepared(update, owner);
        RequirePreparedState(owner, update.Directory);
        // A second helper must never interleave backup and installation. The
        // lock file lives inside the owned staging directory and is retained
        // on a crashed helper; its metadata state then preserves recovery.
        string lockPath = Child(update.Directory, "apply.lock");
        using var applyLock = AcquireApplyLock(lockPath);
        owner = ReadOwner(update.Directory);
        ValidatePrepared(update, owner);
        RequirePreparedState(owner, update.Directory);
        var manifest = ReadPackageManifest(Path.Combine(update.StageDirectory, AppUpdatePackage.ManifestName));
        if (!string.Equals(manifest.Version, update.Version, StringComparison.Ordinal)) throw new InvalidDataException("The staged update version changed.");
        AppUpdatePackage.ValidateDirectory(update.StageDirectory, manifest);
        var previous = Preflight(update, owner, manifest);
        string pointerPath = Child(update.TargetDirectory, PointerFile);
        if (File.Exists(pointerPath) || System.IO.Directory.Exists(pointerPath)) throw new IOException("Another application update is pending recovery.");
        ValidateNextSnapshot(update, owner, manifest);
        var effective = EffectiveManifest(manifest, owner);
        // Complete and verify the backup before recording any mutation intent.
        // Originals, especially the launchable EXE, are never moved away.
        foreach (string relative in previous.Files.Select(file => file.Path).Append(AppUpdatePackage.ManifestName))
        {
            string source = Child(update.TargetDirectory, relative);
            var record = new BackupFile { Path = relative, Exists = File.Exists(source) };
            if (record.Exists)
            {
                record.Size = new FileInfo(source).Length;
                record.Sha256 = AppUpdatePackage.HashFile(source);
                string backup = Child(update.BackupDirectory, relative);
                CreateParent(backup);
                CopyDurably(source, backup);
                VerifyFile(backup, record.Size, record.Sha256);
            }
            owner.OldFiles.Add(record);
        }
        foreach (var record in owner.OldFiles)
        {
            string current = Child(update.TargetDirectory, record.Path);
            if (record.Exists) VerifyFile(current, record.Size, record.Sha256);
            else if (File.Exists(current)) throw new IOException("An application file appeared during backup.");
        }
        owner.BackupReady = true;
        int changed = 0;
        owner.State = "Applying";
        WriteOwner(update.Directory, owner);
        try { CreatePointer(update, owner); }
        catch
        {
            owner.State = "Prepared";
            WriteOwner(update.Directory, owner);
            throw;
        }
        try
        {
            foreach (var file in effective.Files.Where(file => !Paths.Equals(file.Path, "VideoDownloader.exe")))
            {
                string source = owner.RetainedYtDlp is not null && Paths.Equals(file.Path, "yt-dlp.exe")
                    ? Child(update.Directory, RetainedRuntimeFile) : Child(update.StageDirectory, file.Path);
                Install(update, owner, file.Path, source);
                VerifyFile(Child(update.TargetDirectory, file.Path), file.Size, file.Sha256);
                faultAfterFile?.Invoke(++changed);
            }
            foreach (var record in owner.OldFiles.Where(record => !Paths.Equals(record.Path, AppUpdatePackage.ManifestName) &&
                !manifest.Files.Any(file => Paths.Equals(file.Path, record.Path))))
            {
                JournalTouch(update, owner, record.Path);
                string path = Child(update.TargetDirectory, record.Path);
                if (File.Exists(path)) File.Delete(path);
                faultAfterFile?.Invoke(++changed);
            }
            var executable = manifest.Files.Single(file => Paths.Equals(file.Path, "VideoDownloader.exe"));
            Install(update, owner, executable.Path, Child(update.StageDirectory, executable.Path));
            VerifyFile(Child(update.TargetDirectory, executable.Path), executable.Size, executable.Sha256);
            faultAfterFile?.Invoke(++changed);
            Install(update, owner, AppUpdatePackage.ManifestName, Child(update.Directory, NextSnapshotFile));
            VerifyFile(Child(update.TargetDirectory, AppUpdatePackage.ManifestName),
                new FileInfo(Child(update.Directory, NextSnapshotFile)).Length, owner.NextManifestSha256);
            faultAfterFile?.Invoke(++changed);
            owner.State = "Applied";
            WriteOwner(update.Directory, owner);
            CleanTemps(update, owner);
            DeletePointer(update, owner);
        }
        catch (Exception failure)
        {
            try { RecoverCore(update, owner); }
            catch (Exception restoration)
            {
                owner.State = "RollbackFailed";
                try { WriteOwner(update.Directory, owner); } catch { }
                throw new AggregateException("The update failed and recovery files were preserved at " + update.Directory, failure, restoration);
            }
            throw;
        }
    }

    public static PreparedAppUpdate? FindPending(string targetDirectory)
    {
        string target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
        string pointerPath = Child(target, PointerFile);
        if (!File.Exists(pointerPath)) return null;
        var pointer = ReadPointer(pointerPath);
        if (!Paths.Equals(pointer.TargetDirectory, target)) throw new InvalidDataException("The update recovery target is invalid.");
        var owner = ReadOwner(pointer.UpdateDirectory);
        var update = new PreparedAppUpdate(pointer.UpdateDirectory, Path.Combine(pointer.UpdateDirectory, "stage"),
            Path.Combine(pointer.UpdateDirectory, "backup"), target, owner.Version);
        ValidatePrepared(update, owner, requireDirectories: false);
        ValidatePointer(update, owner, pointer);
        ValidateRecoveryMetadata(update, owner);
        return update;
    }

    public static void Recover(PreparedAppUpdate update)
    {
        var owner = ReadOwner(update.Directory);
        ValidatePrepared(update, owner, requireDirectories: false);
        using var applyLock = AcquireApplyLock(Child(update.Directory, "apply.lock"));
        owner = ReadOwner(update.Directory);
        try { RecoverCore(update, owner); }
        catch (Exception error)
        {
            owner.State = "RollbackFailed";
            try { WriteOwner(update.Directory, owner); } catch { }
            throw new AggregateException("Application recovery is incomplete; backup files were preserved at " + update.Directory, error);
        }
    }

    public static void CleanupCompleted(string scratchRoot, Func<string, bool> isActiveHelper)
    {
        ArgumentNullException.ThrowIfNull(isActiveHelper);
        try
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(scratchRoot));
            EnsurePlainPath(root);
            if (!System.IO.Directory.Exists(root)) return;
            foreach (string directory in System.IO.Directory.EnumerateDirectories(root, Prefix + "*", SearchOption.TopDirectoryOnly).Take(32))
            {
                try
                {
                    EnsurePlainPath(directory);
                    var owner = ReadOwner(directory);
                    if (owner.State is not ("Applied" or "RolledBack")) continue;
                    var update = new PreparedAppUpdate(directory, Path.Combine(directory, "stage"), Path.Combine(directory, "backup"),
                        owner.TargetDirectory, owner.Version);
                    ValidatePrepared(update, owner, requireDirectories: false);
                    if (isActiveHelper(directory) || File.Exists(Child(update.TargetDirectory, PointerFile))) continue;
                    Cleanup(update);
                }
                catch (Exception) { /* An uncertain lease, owner or path is never deleted. */ }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException) { }
    }

    private static void RecoverCore(PreparedAppUpdate update, Owner owner)
    {
        ValidatePrepared(update, owner, requireDirectories: false);
        ValidatePointer(update, owner, ReadPointer(Child(update.TargetDirectory, PointerFile)));
        var next = ValidateRecoveryMetadata(update, owner);
        // Validate every backup before changing the target. Repeating recovery
        // after another process crash always uses these same complete copies.
        foreach (var record in owner.OldFiles.Where(record => record.Exists))
            VerifyFile(Child(update.BackupDirectory, record.Path), record.Size, record.Sha256);
        owner.State = "Applying";
        WriteOwner(update.Directory, owner);
        foreach (string relative in owner.TouchedFiles)
        {
            var old = owner.OldFiles.SingleOrDefault(record => Paths.Equals(record.Path, relative));
            if (old?.Exists == true) continue;
            string path = Child(update.TargetDirectory, relative);
            if (!File.Exists(path)) continue;
            var expected = next.Files.SingleOrDefault(file => Paths.Equals(file.Path, relative));
            if (expected is null) throw new IOException("Recovery found an unexpected installed file.");
            VerifyFile(path, expected.Size, expected.Sha256);
            File.Delete(path);
        }
        foreach (var record in owner.OldFiles.Where(record => record.Exists &&
            !Paths.Equals(record.Path, "VideoDownloader.exe") && !Paths.Equals(record.Path, AppUpdatePackage.ManifestName)))
            Restore(update, owner, record);
        foreach (string last in new[] { "VideoDownloader.exe", AppUpdatePackage.ManifestName })
        {
            var record = owner.OldFiles.Single(file => Paths.Equals(file.Path, last));
            if (!record.Exists) throw new InvalidDataException("The original application entry point is missing from recovery.");
            Restore(update, owner, record);
        }
        CleanTemps(update, owner);
        owner.State = "RolledBack";
        WriteOwner(update.Directory, owner);
        DeletePointer(update, owner);
    }

    private static void Restore(PreparedAppUpdate update, Owner owner, BackupFile record)
    {
        AtomicCopy(update, owner, record.Path, Child(update.BackupDirectory, record.Path), true);
        VerifyFile(Child(update.TargetDirectory, record.Path), record.Size, record.Sha256);
    }

    private static void Install(PreparedAppUpdate update, Owner owner, string relative, string source)
    {
        JournalTouch(update, owner, relative);
        bool overwrite = owner.OldFiles.Any(file => Paths.Equals(file.Path, relative) && file.Exists);
        AtomicCopy(update, owner, relative, source, overwrite);
    }

    private static void JournalTouch(PreparedAppUpdate update, Owner owner, string relative)
    {
        if (!owner.TouchedFiles.Contains(relative, Paths)) owner.TouchedFiles.Add(relative);
        WriteOwner(update.Directory, owner);
    }

    private static void AtomicCopy(PreparedAppUpdate update, Owner owner, string relative, string source, bool overwrite)
    {
        string destination = Child(update.TargetDirectory, relative);
        CreateParent(destination);
        string temporary = Path.Combine(Path.GetDirectoryName(destination)!, ".vd-update-" + owner.Id + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        string temporaryRelative = Path.GetRelativePath(update.TargetDirectory, temporary).Replace('\\', '/');
        owner.PendingTemps.Add(temporaryRelative);
        WriteOwner(update.Directory, owner);
        EnsurePlainPath(source);
        CopyDurably(source, temporary);
        EnsurePlainPath(destination);
        File.Move(temporary, destination, overwrite);
    }

    private static void CopyDurably(string source, string destination)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        input.CopyTo(output);
        output.Flush(true);
    }

    private static void VerifyFile(string path, long size, string hash)
    {
        EnsurePlainPath(path);
        if (new FileInfo(path).Length != size || AppUpdatePackage.HashFile(path) != hash)
            throw new InvalidDataException("Application recovery checksum mismatch: " + Path.GetFileName(path));
    }

    private static AppPackageManifest ReadPackageManifest(string path)
    {
        var manifest = AppUpdatePackage.ReadManifest(path);
        return manifest with { Files = manifest.Files.Select(file => file with
        { Path = AppUpdatePackage.ValidateRelativePath(file.Path) }).ToList() };
    }

    private static void ValidateNextSnapshot(PreparedAppUpdate update, Owner owner, AppPackageManifest manifest, bool verifyRetainedSource = true)
    {
        string path = Child(update.Directory, NextSnapshotFile);
        if (AppUpdatePackage.HashFile(path) != owner.NextManifestSha256) throw new InvalidDataException("The new package ownership snapshot changed.");
        var saved = ReadPackageManifest(path);
        var effective = EffectiveManifest(manifest, owner);
        if (verifyRetainedSource && owner.RetainedYtDlp is { } retained)
            VerifyFile(Child(update.Directory, RetainedRuntimeFile), retained.Size, retained.Sha256);
        if (saved.Schema != effective.Schema || saved.Version != effective.Version || !saved.Files.SequenceEqual(effective.Files) ||
            !owner.NewFiles.SequenceEqual(manifest.Files.Select(file => file.Path)))
            throw new InvalidDataException("The new package ownership snapshot is inconsistent.");
    }

    private static AppPackageManifest ValidateRecoveryMetadata(PreparedAppUpdate update, Owner owner)
    {
        if (!owner.BackupReady || owner.State is not ("Applying" or "RollbackFailed" or "Applied" or "RolledBack"))
            throw new InvalidDataException("The update has no complete durable recovery backup.");
        string oldPath = Child(update.Directory, SnapshotFile);
        if (AppUpdatePackage.HashFile(oldPath) != owner.OriginalManifestSha256) throw new InvalidDataException("The original ownership snapshot changed.");
        var original = ReadPackageManifest(oldPath);
        var next = ReadPackageManifest(Child(update.Directory, NextSnapshotFile));
        ValidateNextSnapshot(update, owner, next, verifyRetainedSource: false);
        var expected = new HashSet<string>(original.Files.Select(file => file.Path).Append(AppUpdatePackage.ManifestName), Paths);
        foreach (var record in owner.OldFiles)
        {
            if (record is null || record.Path is null || record.Sha256 is null)
                throw new InvalidDataException("The original file recovery record is empty.");
            string path = AppUpdatePackage.ValidateRelativePath(record.Path);
            if (!expected.Remove(path) || record.Size < 0 ||
                (record.Exists && (record.Sha256.Length != 64 || !record.Sha256.All(Uri.IsHexDigit))) ||
                (!record.Exists && (record.Size != 0 || record.Sha256.Length != 0)))
                throw new InvalidDataException("The original file recovery records are invalid.");
        }
        if (expected.Count != 0) throw new InvalidDataException("Original file recovery records are incomplete.");
        var planned = new HashSet<string>(original.Files.Select(file => file.Path).Concat(owner.NewFiles).Append(AppUpdatePackage.ManifestName), Paths);
        var touched = new HashSet<string>(Paths);
        foreach (string relative in owner.TouchedFiles)
            if (!planned.Contains(AppUpdatePackage.ValidateRelativePath(relative)) || !touched.Add(relative))
                throw new InvalidDataException("The update mutation journal is invalid.");
        foreach (string temporary in owner.PendingTemps) ValidateOwnedTemp(owner, temporary);
        var originalManifest = owner.OldFiles.Single(file => Paths.Equals(file.Path, AppUpdatePackage.ManifestName));
        if (!originalManifest.Exists || originalManifest.Sha256 != owner.OriginalManifestSha256)
            throw new InvalidDataException("The original manifest recovery hash is invalid.");
        return next;
    }

    private static void CreatePointer(PreparedAppUpdate update, Owner owner)
    {
        string path = Child(update.TargetDirectory, PointerFile);
        string temporary = Path.Combine(update.TargetDirectory, ".vd-update-" + owner.Id + "-" + Guid.NewGuid().ToString("N") + ".tmp");
        owner.PendingTemps.Add(Path.GetFileName(temporary));
        WriteOwner(update.Directory, owner);
        bool created = false;
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                created = true;
                JsonSerializer.Serialize(stream, new Pointer
                {
                    Id = owner.Id, UpdateDirectory = update.Directory, TargetDirectory = update.TargetDirectory,
                    OriginalManifestSha256 = owner.OriginalManifestSha256
                }, MetadataJson);
                stream.Flush(true);
            }
            // Atomically publish complete metadata with CreateNew admission:
            // an existing/foreign pointer is never opened or overwritten.
            EnsurePlainPath(path);
            File.Move(temporary, path, false);
        }
        catch
        {
            if (created) { try { EnsurePlainPath(temporary); File.Delete(temporary); } catch { } }
            throw;
        }
    }

    private static Pointer ReadPointer(string path)
    {
        EnsurePlainPath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 16 * 1024) throw new InvalidDataException("The update recovery pointer is too large.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        return JsonSerializer.Deserialize<Pointer>(bytes, MetadataJson) ?? throw new InvalidDataException("Empty application recovery pointer.");
    }

    private static void ValidatePointer(PreparedAppUpdate update, Owner owner, Pointer pointer)
    {
        if (pointer.Schema != 1 || pointer.Id != owner.Id ||
            !Paths.Equals(pointer.UpdateDirectory, update.Directory) || !Paths.Equals(pointer.TargetDirectory, update.TargetDirectory) ||
            pointer.OriginalManifestSha256 != owner.OriginalManifestSha256)
            throw new InvalidDataException("The pending recovery pointer is not owned by this update.");
    }

    private static void DeletePointer(PreparedAppUpdate update, Owner owner)
    {
        string path = Child(update.TargetDirectory, PointerFile);
        ValidatePointer(update, owner, ReadPointer(path));
        File.Delete(path);
    }

    private static void ValidateOwnedTemp(Owner owner, string relative)
    {
        string path = ValidatePath(relative);
        string name = path[(path.LastIndexOf('/') + 1)..];
        if (!Regex.IsMatch(name, @"^\.vd-update-" + Regex.Escape(owner.Id) + @"-[0-9a-f]{32}\.tmp$"))
            throw new InvalidDataException("An update temporary file is not owned by this transaction.");
        string parent = path.Contains('/') ? path[..path.LastIndexOf('/')] : "";
        var packagePaths = owner.OldFiles.Select(file => file.Path).Concat(owner.NewFiles).Append(AppUpdatePackage.ManifestName);
        if (!packagePaths.Any(file => Paths.Equals(file.Contains('/') ? file[..file.LastIndexOf('/')] : "", parent)))
            throw new InvalidDataException("An update temporary file is outside package directories.");
    }

    private static void CleanTemps(PreparedAppUpdate update, Owner owner)
    {
        foreach (string relative in owner.PendingTemps)
        {
            ValidateOwnedTemp(owner, relative);
            string path = Child(update.TargetDirectory, relative);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static AppPackageManifest Preflight(PreparedAppUpdate update, Owner owner, AppPackageManifest manifest)
    {
        ValidatePrepared(update, owner);
        string snapshot = Child(update.Directory, SnapshotFile);
        string current = Child(update.TargetDirectory, AppUpdatePackage.ManifestName);
        if (AppUpdatePackage.HashFile(snapshot) != owner.OriginalManifestSha256 ||
            AppUpdatePackage.HashFile(current) != owner.OriginalManifestSha256)
            throw new IOException("The installed package ownership changed after the update was prepared.");
        var previous = ReadPackageManifest(snapshot);
        if (AppUpdatePackage.ParseVersion(manifest.Version) <= AppUpdatePackage.ParseVersion(previous.Version))
            throw new InvalidDataException("The application update must be newer than the installed package.");
        var owned = new HashSet<string>(previous.Files.Select(file => file.Path), Paths);
        foreach (string relative in previous.Files.Select(file => file.Path).Append(AppUpdatePackage.ManifestName))
        {
            string path = Child(update.TargetDirectory, relative);
            if (System.IO.Directory.Exists(path)) throw new IOException("A package file was replaced by a directory: " + relative);
        }
        foreach (var file in manifest.Files)
        {
            string path = Child(update.TargetDirectory, file.Path);
            if (System.IO.Directory.Exists(path) || (File.Exists(path) && !owned.Contains(file.Path)))
                throw new IOException("The update would overwrite a file outside the installed package: " + file.Path);
        }
        if (System.IO.Directory.EnumerateFileSystemEntries(update.BackupDirectory).Any())
            throw new IOException("The recovery directory is not empty; its files will be preserved.");
        string runtime = Child(update.TargetDirectory, "yt-dlp.exe");
        string actualRuntime = File.Exists(runtime) ? AppUpdatePackage.HashFile(runtime) : "";
        if (actualRuntime != owner.PreparedYtDlpSha256)
            throw new IOException("yt-dlp changed after update preparation. Prepare the application update again.");
        return previous;
    }

    private static void Extract(string archivePath, string stage, CancellationToken cancellationToken)
    {
        EnsurePlainPath(Path.GetFullPath(archivePath));
        if (new FileInfo(archivePath).Length > MaxArchiveBytes) throw new InvalidDataException("The update archive is too large.");
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > MaxEntries) throw new InvalidDataException("The update archive has too many entries.");
        var names = new HashSet<string>(Paths);
        var files = new HashSet<string>(Paths);
        var directories = new HashSet<string>(Paths);
        var entries = new List<(ZipArchiveEntry Entry, string Relative, bool Directory)>();
        long declared = 0;
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool directory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');
            string relative = ValidatePath(directory ? entry.FullName[..^1] : entry.FullName);
            if (!directory) relative = AppUpdatePackage.ValidateRelativePath(relative);
            int type = (entry.ExternalAttributes >> 16) & 0xf000;
            if ((entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0 ||
                (type != 0 && type != (directory ? 0x4000 : 0x8000)))
                throw new InvalidDataException("Links and special files are not allowed in an update archive.");
            if (!names.Add(relative)) throw new InvalidDataException("The update archive contains duplicate paths.");
            if (directory)
            {
                if (entry.Length != 0) throw new InvalidDataException("An update directory contains file data.");
                directories.Add(relative);
            }
            else
            {
                files.Add(relative);
                if (entry.Length < 0 || entry.Length > MaxExpandedBytes - declared) throw new InvalidDataException("The expanded update is too large.");
                declared += entry.Length;
            }
            for (int slash = relative.IndexOf('/'); slash >= 0; slash = relative.IndexOf('/', slash + 1))
                directories.Add(relative[..slash]);
            entries.Add((entry, relative, directory));
        }
        if (files.Overlaps(directories)) throw new InvalidDataException("A path is both a file and a directory in the update archive.");
        byte[] buffer = new byte[64 * 1024];
        long expanded = 0;
        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string destination = Child(stage, item.Relative);
            if (item.Directory) { System.IO.Directory.CreateDirectory(destination); EnsurePlainPath(destination); continue; }
            CreateParent(destination);
            using var input = item.Entry.Open();
            using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long written = 0;
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (read > item.Entry.Length - written || read > MaxExpandedBytes - expanded)
                    throw new InvalidDataException("The expanded update exceeded its declared size.");
                output.Write(buffer, 0, read);
                written += read;
                expanded += read;
            }
            if (written != item.Entry.Length) throw new InvalidDataException("The update archive is truncated.");
        }
    }

    private static Owner ReadOwner(string directory)
    {
        EnsurePlainPath(directory);
        string path = Child(directory, OwnerFile);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxOwnerBytes) throw new InvalidDataException("The prepared update metadata is too large.");
        byte[] bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        var owner = JsonSerializer.Deserialize<Owner>(bytes, MetadataJson)
            ?? throw new InvalidDataException("The prepared update metadata is empty.");
        if (owner.Schema != 2 || !Guid.TryParseExact(owner.Id, "N", out _) ||
            !Paths.Equals(Path.GetFileName(Path.TrimEndingDirectorySeparator(directory)), Prefix + owner.Id) ||
            string.IsNullOrEmpty(owner.TargetDirectory) || string.IsNullOrEmpty(owner.Version) ||
            string.IsNullOrEmpty(owner.OriginalManifestSha256) || owner.OriginalManifestSha256.Length != 64 || !owner.OriginalManifestSha256.All(Uri.IsHexDigit) ||
            string.IsNullOrEmpty(owner.NextManifestSha256) || owner.NextManifestSha256.Length != 64 || !owner.NextManifestSha256.All(Uri.IsHexDigit) ||
            owner.PreparedYtDlpSha256 is null || (owner.PreparedYtDlpSha256.Length != 0 &&
                (owner.PreparedYtDlpSha256.Length != 64 || !owner.PreparedYtDlpSha256.All(Uri.IsHexDigit))) ||
            owner.OldFiles is null || owner.NewFiles is null || owner.TouchedFiles is null || owner.PendingTemps is null ||
            owner.OldFiles.Count > MaxEntries || owner.NewFiles.Count > MaxEntries || owner.TouchedFiles.Count > 2 * MaxEntries || owner.PendingTemps.Count > 8 * MaxEntries ||
            owner.State is not ("Prepared" or "Applying" or "Applied" or "RolledBack" or "RollbackFailed"))
            throw new InvalidDataException("The prepared update metadata is invalid.");
        return owner;
    }

    private static void ValidatePrepared(PreparedAppUpdate update, Owner owner, bool requireDirectories = true)
    {
        string directory = Path.GetFullPath(update.Directory);
        if (!Path.IsPathFullyQualified(update.Directory) ||
            !Paths.Equals(update.StageDirectory, Path.Combine(directory, "stage")) ||
            !Paths.Equals(update.BackupDirectory, Path.Combine(directory, "backup")) ||
            !Paths.Equals(update.TargetDirectory, owner.TargetDirectory) || update.Version != owner.Version ||
            !Path.IsPathFullyQualified(owner.TargetDirectory) ||
            Within(owner.TargetDirectory, directory) || Within(directory, owner.TargetDirectory))
            throw new InvalidDataException("The prepared update paths are invalid.");
        EnsurePlainPath(directory);
        EnsurePlainPath(update.StageDirectory);
        EnsurePlainPath(update.BackupDirectory);
        EnsurePlainPath(update.TargetDirectory);
        if (requireDirectories && (!System.IO.Directory.Exists(update.StageDirectory) || !System.IO.Directory.Exists(update.BackupDirectory) ||
            !System.IO.Directory.Exists(update.TargetDirectory))) throw new IOException("A prepared update directory is missing.");
    }

    private static void WriteOwner(string directory, Owner owner)
    {
        string path = Child(directory, OwnerFile);
        string temporary = Path.Combine(directory, "owner-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(owner, MetadataJson);
                if (bytes.Length > MaxOwnerBytes) throw new InvalidDataException("The update journal is too large.");
                stream.Write(bytes);
                stream.Flush(true);
            }
            EnsurePlainPath(path);
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void RequirePreparedState(Owner owner, string directory)
    {
        if (owner.State is "Applying" or "RollbackFailed")
            throw new AggregateException("An incomplete update requires recovery; its files were preserved at " + directory,
                new IOException("The installed application may be incomplete and must not be restarted automatically."));
        if (owner.State != "Prepared")
            throw new IOException("The update was already attempted; its recovery files are preserved at " + directory);
    }

    private static FileStream AcquireApplyLock(string path)
    {
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException error)
        {
            throw new AggregateException("Another update helper may be active; the application must not be restarted automatically.", error);
        }
    }

    internal static void Cleanup(PreparedAppUpdate update)
    {
        try
        {
            if (!System.IO.Directory.Exists(update.Directory)) return;
            var owner = ReadOwner(update.Directory);
            ValidatePrepared(update, owner, requireDirectories: false);
            if (owner.State is "Applying" or "RollbackFailed") return;
            if (File.Exists(Child(update.TargetDirectory, PointerFile))) return;
            SafeDeleteTree(update.Directory);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { }
    }

    private static void SafeDeleteTree(string directory)
    {
        try
        {
            EnsurePlainPath(directory);
            string owner = Path.Combine(directory, OwnerFile);
            foreach (string path in System.IO.Directory.EnumerateFileSystemEntries(directory).Where(path => !Paths.Equals(path, owner)))
            {
                try
                {
                    EnsurePlainPath(path);
                    if (System.IO.Directory.Exists(path)) SafeDeleteTree(path);
                    else File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
            // Keep the ownership marker while a running helper EXE or any
            // unsafe/locked child prevents complete cleanup. A later Dispose
            // can safely retry without needing the already removed stage.
            if (System.IO.Directory.EnumerateFileSystemEntries(directory).Any(path => !Paths.Equals(path, owner))) return;
            if (File.Exists(owner)) { EnsurePlainPath(owner); File.Delete(owner); }
            System.IO.Directory.Delete(directory, false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private static string Child(string directory, string relative)
    {
        string normalized = ValidatePath(relative);
        string path = Path.GetFullPath(Path.Combine(directory, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!Within(path, directory)) throw new InvalidDataException("An update path escaped its owned directory.");
        EnsurePlainPath(path);
        return path;
    }

    private static string ValidatePath(string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Length > 240 || relative.StartsWith('/') || relative.StartsWith('\\'))
            throw new InvalidDataException("Invalid update path.");
        string path = relative.Replace('\\', '/');
        foreach (string part in path.Split('/'))
            if (part.Length == 0 || part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') ||
                part.Any(character => character < 32 || "<>:\"|?*".Contains(character)) ||
                Regex.IsMatch(part, @"^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)", RegexOptions.IgnoreCase))
                throw new InvalidDataException("Unsafe update path.");
        return path;
    }

    private static bool Within(string path, string directory)
    {
        string fullPath = Path.GetFullPath(path);
        string fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        string prefix = Path.EndsInDirectorySeparator(fullDirectory) ? fullDirectory : fullDirectory + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Paths.Equals(fullPath, fullDirectory);
    }

    private static void CreateParent(string file)
    {
        string parent = Path.GetDirectoryName(file)!;
        EnsurePlainPath(parent);
        System.IO.Directory.CreateDirectory(parent);
        EnsurePlainPath(parent);
        EnsurePlainPath(file);
    }

    private static void EnsurePlainPath(string path)
    {
        string? current = Path.GetFullPath(path);
        bool leaf = true;
        while (current != null)
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if ((attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Update paths cannot contain a reparse point or symbolic link.");
                if (!leaf && (attributes & FileAttributes.Directory) == 0) throw new IOException("An update parent path is not a directory.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            leaf = false;
            current = Path.GetDirectoryName(current);
        }
    }
}
