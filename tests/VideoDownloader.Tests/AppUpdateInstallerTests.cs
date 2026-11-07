using System.IO;
using System.IO.Compression;
using System.Text.Json;
using VideoDownloader.Native;

internal static partial class AppUpdateInstallerTests
{
    internal static bool TryRunFixture(string[] args)
    {
        if (args.Length == 0 || args[0] != "--vd-update-fixture") return false;
        if (args.Length != 3 || !int.TryParse(args[2], out int pauseAfter) || pauseAfter < 1)
            throw new InvalidOperationException("Invalid application updater fixture arguments.");
        using var prepared = AppUpdateInstaller.OpenPrepared(args[1]);
        AppUpdateInstaller.Apply(prepared, count =>
        {
            if (count != pauseAfter) return;
            File.WriteAllText(Path.Combine(prepared.Directory, "fixture-signal"), "ready");
            Thread.Sleep(Timeout.Infinite);
        });
        return true;
    }

    internal static async Task RunAsync()
    {
        Run();
        await RuntimePolicyAsync();
        await CrashRecoveryAsync();
    }

    internal static void Run()
    {
        SuccessAndExternalFiles();
        Rollback();
        FailedRollbackPreservesBackup();
        InvalidArchives();
        DowngradeIsRejected();
        ForeignCollisionAndManifestChange();
        ReparseAfterPreparation();
        ConcurrentHelperIsRejected();
        LockedHelperCleanup();
        CompletedCleanup();
        Console.WriteLine("PASS: application update ZIP validation, ownership, staged apply, rollback and recovery preservation");
    }

    private static void SuccessAndExternalFiles()
    {
        using var fixture = new Fixture();
        File.WriteAllText(Path.Combine(fixture.Current, "yt-dlp.exe"), "independently updated yt-dlp");
        File.WriteAllText(Path.Combine(fixture.Current, "ffmpeg.exe"), "external ffmpeg");
        File.WriteAllText(Path.Combine(fixture.Current, "cookies.txt"), "private cookies");
        Directory.CreateDirectory(Path.Combine(fixture.Current, "downloads"));
        File.WriteAllText(Path.Combine(fixture.Current, "downloads", "video.mp4"), "user download");
        using var prepared = fixture.Prepare();
        using var reopened = AppUpdateInstaller.OpenPrepared(prepared.Directory);
        Require(reopened.TargetDirectory == fixture.Current && reopened.Version == "2.0.2", "The helper must reopen the exact prepared target/version.");
        AppUpdateInstaller.Apply(reopened);
        Require(File.ReadAllText(Path.Combine(fixture.Current, "VideoDownloader.exe")) == "new executable", "The executable must be replaced.");
        Require(!File.Exists(Path.Combine(fixture.Current, "obsolete.dll")) && File.Exists(Path.Combine(fixture.Current, "new.dll")), "Only removed owned files disappear and new package files are installed.");
        Require(File.ReadAllText(Path.Combine(fixture.Current, "ffmpeg.exe")) == "external ffmpeg" &&
            File.ReadAllText(Path.Combine(fixture.Current, "cookies.txt")) == "private cookies" &&
            File.ReadAllText(Path.Combine(fixture.Current, "downloads", "video.mp4")) == "user download", "Unknown external tools, cookies and downloads must be untouched.");
        Require(AppUpdatePackage.ReadManifest(Path.Combine(fixture.Current, AppUpdatePackage.ManifestName)).Version == "2.0.2", "The final manifest must describe the new package.");
        Failure(() => AppUpdateInstaller.Apply(reopened));
    }

    private static void Rollback()
    {
        using var fixture = new Fixture();
        var before = Snapshot(fixture.Current);
        using var prepared = fixture.Prepare();
        Failure(() => AppUpdateInstaller.Apply(prepared, count => { if (count == 2) throw new IOException("Injected apply failure"); }));
        RequireSame(before, Snapshot(fixture.Current), "Rollback must restore the original package bytes and manifest.");
        Require(File.ReadAllText(Path.Combine(prepared.BackupDirectory, "VideoDownloader.exe")) == "old executable", "Rollback must retain the complete original copy until cleanup.");
        Require(AppUpdateInstaller.FindPending(fixture.Current) is null, "Successful rollback must remove its recovery pointer.");
    }

    private static void FailedRollbackPreservesBackup()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        string backup = Path.Combine(prepared.BackupDirectory, "VideoDownloader.exe");
        Exception failure = Failure(() => AppUpdateInstaller.Apply(prepared, count =>
        {
            if (count != 3) return;
            // A third party changed a newly introduced package path. Recovery
            // must preserve those unknown bytes and keep the original backup.
            File.WriteAllText(Path.Combine(fixture.Current, "new.dll"), "foreign collision");
            throw new IOException("Injected collision during apply");
        }));
        Require(failure is AggregateException, "Incomplete rollback must report a recovery failure.");
        prepared.Dispose();
        Require(File.Exists(backup) && File.ReadAllText(backup) == "old executable", "Dispose must preserve the original backup after failed restoration.");
        Require(File.ReadAllText(Path.Combine(fixture.Current, "new.dll")) == "foreign collision", "Rollback must not delete a newly appeared foreign file.");
        Require(File.ReadAllText(Path.Combine(fixture.Current, "VideoDownloader.exe")) == "old executable", "An interrupted pre-EXE update must retain its launchable original entry point.");
        Require(Failure(() => AppUpdateInstaller.OpenPrepared(prepared.Directory)) is AggregateException,
            "Reopening an incomplete package must report unsafe recovery rather than authorize an automatic restart.");
    }

    private static void InvalidArchives()
    {
        foreach (string kind in new[] { "Traversal", "Hash", "Symlink", "DirectoryCollision", "DuplicateCase", "Version" })
        {
            using var fixture = new Fixture();
            var before = Snapshot(fixture.Current);
            if (kind != "Version")
            {
                using var archive = ZipFile.Open(fixture.Archive, ZipArchiveMode.Update);
                switch (kind)
                {
                    case "Traversal": WriteEntry(archive, "../escaped.dll", "outside"); break;
                    case "Hash": archive.GetEntry("VideoDownloader.exe")!.Delete(); WriteEntry(archive, "VideoDownloader.exe", "tampered"); break;
                    case "Symlink": WriteEntry(archive, "link.dll", "target").ExternalAttributes = unchecked((0xa000 | 0x1ff) << 16); break;
                    case "DirectoryCollision": archive.CreateEntry("new.dll/"); break;
                    case "DuplicateCase": WriteEntry(archive, "VIDEODOWNLOADER.EXE", "duplicate"); break;
                }
            }
            Failure(() => AppUpdateInstaller.Prepare(fixture.Archive, fixture.Current, fixture.Scratch,
                kind == "Version" ? "2.0.3" : "2.0.2", CancellationToken.None));
            RequireSame(before, Snapshot(fixture.Current), "Invalid ZIP must never modify the installed package: " + kind);
            Require(!Directory.EnumerateFileSystemEntries(fixture.Scratch).Any(), "Failed ZIP validation must clean its own staging directory.");
            Require(!File.Exists(Path.Combine(fixture.Root, "escaped.dll")), "ZIP traversal must not create a file outside staging.");
        }
        using (var fixture = new Fixture())
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            Failure(() => AppUpdateInstaller.Prepare(fixture.Archive, fixture.Current, fixture.Scratch, "2.0.2", cancellation.Token));
            Require(!Directory.EnumerateFileSystemEntries(fixture.Scratch).Any(), "Cancellation before preparation must create no staging files.");
        }
    }

    private static void ForeignCollisionAndManifestChange()
    {
        using (var fixture = new Fixture())
        {
            File.WriteAllText(Path.Combine(fixture.Current, "new.dll"), "foreign DLL");
            var before = Snapshot(fixture.Current);
            Failure(() => fixture.Prepare());
            RequireSame(before, Snapshot(fixture.Current), "A new package path colliding with an unknown local file must be refused.");
        }
        using (var fixture = new Fixture())
        {
            using var prepared = fixture.Prepare();
            string path = Path.Combine(fixture.Current, AppUpdatePackage.ManifestName);
            File.AppendAllText(path, " ");
            var before = Snapshot(fixture.Current);
            Failure(() => AppUpdateInstaller.Apply(prepared));
            RequireSame(before, Snapshot(fixture.Current), "Changed current ownership metadata must be rejected before mutation.");
        }
        using (var fixture = new Fixture())
        {
            File.Delete(Path.Combine(fixture.Current, AppUpdatePackage.ManifestName));
            Failure(() => fixture.Prepare());
            Require(File.ReadAllText(Path.Combine(fixture.Current, "VideoDownloader.exe")) == "old executable", "A legacy package without ownership must not be modified.");
        }
        using (var fixture = new Fixture())
        {
            var unowned = new PreparedAppUpdate(fixture.Root, Path.Combine(fixture.Root, "stage"),
                Path.Combine(fixture.Root, "backup"), fixture.Current, "2.0.2");
            Failure(() => AppUpdateInstaller.Apply(unowned));
            Require(!File.Exists(Path.Combine(fixture.Root, "apply.lock")), "An unowned update record must not create files before ownership validation.");
        }
        using (var fixture = new Fixture())
        {
            string pointer = Path.Combine(fixture.Current, ".video-downloader-update.json");
            File.WriteAllText(pointer, "foreign pointer");
            var before = Snapshot(fixture.Current);
            using var prepared = fixture.Prepare();
            Failure(() => AppUpdateInstaller.Apply(prepared));
            RequireSame(before, Snapshot(fixture.Current), "An existing foreign pointer must refuse admission before any package replacement.");
        }
    }

    private static void DowngradeIsRejected()
    {
        foreach (string version in new[] { "2.0.1", "2.0.0" })
        {
            using var fixture = new Fixture();
            var before = Snapshot(fixture.Current);
            using (var archive = ZipFile.Open(fixture.Archive, ZipArchiveMode.Update))
            {
                var entry = archive.GetEntry(AppUpdatePackage.ManifestName)!;
                AppPackageManifest manifest;
                using (var stream = entry.Open()) manifest = JsonSerializer.Deserialize<AppPackageManifest>(stream, AppUpdatePackage.Json)!;
                entry.Delete();
                WriteEntry(archive, AppUpdatePackage.ManifestName, JsonSerializer.Serialize(manifest with { Version = version }, AppUpdatePackage.Json));
            }
            Failure(() => AppUpdateInstaller.Prepare(fixture.Archive, fixture.Current, fixture.Scratch, version, CancellationToken.None));
            RequireSame(before, Snapshot(fixture.Current), "An equal or older application version must be rejected without mutation.");
        }
    }

    private static void ConcurrentHelperIsRejected()
    {
        using var fixture = new Fixture();
        using var prepared = fixture.Prepare();
        using var changed = new ManualResetEventSlim();
        using var resume = new ManualResetEventSlim();
        var applying = Task.Run(() => AppUpdateInstaller.Apply(prepared, count =>
        {
            if (count != 1) return;
            changed.Set();
            if (!resume.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("The concurrent helper fixture was not resumed.");
        }));
        try
        {
            Require(changed.Wait(TimeSpan.FromSeconds(5)), "The first updater must hold its real apply lock.");
            Require(Failure(() => AppUpdateInstaller.Apply(prepared)) is AggregateException,
                "A concurrent updater must inhibit automatic restart while another helper is changing files.");
        }
        finally { resume.Set(); applying.GetAwaiter().GetResult(); }
        Require(AppUpdatePackage.ReadManifest(Path.Combine(fixture.Current, AppUpdatePackage.ManifestName)).Version == "2.0.2", "The original updater must complete after rejecting its concurrent helper.");
    }

    private static void LockedHelperCleanup()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        string helper = Path.Combine(prepared.Directory, "Installer.exe");
        File.WriteAllText(helper, "running update helper");
        using (var held = new FileStream(helper, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            prepared.Dispose();
            Require(File.Exists(helper), "A locked helper must survive cleanup without throwing.");
        }
        prepared.Dispose();
        Require(!Directory.Exists(prepared.Directory), "Cleanup must remain safely retryable after the helper releases its handle.");
        Require(File.ReadAllText(Path.Combine(fixture.Current, "VideoDownloader.exe")) == "old executable", "Cleanup must never touch the installed application.");
    }

    private static void CompletedCleanup()
    {
        using var fixture = new Fixture();
        var prepared = fixture.Prepare();
        AppUpdateInstaller.Apply(prepared);
        AppUpdateInstaller.CleanupCompleted(fixture.Scratch, _ => true);
        Require(Directory.Exists(prepared.Directory), "A completed update with a live helper must be retained.");
        AppUpdateInstaller.CleanupCompleted(fixture.Scratch, _ => throw new IOException("Uncertain helper lease"));
        Require(Directory.Exists(prepared.Directory), "An uncertain helper lease must fail closed.");
        string pointer = Path.Combine(fixture.Current, ".video-downloader-update.json");
        File.WriteAllText(pointer, "foreign recovery pointer");
        AppUpdateInstaller.CleanupCompleted(fixture.Scratch, _ => false);
        Require(Directory.Exists(prepared.Directory), "A pending recovery pointer must retain a completed update backup.");
        File.Delete(pointer);
        AppUpdateInstaller.CleanupCompleted(fixture.Scratch, _ => false);
        Require(!Directory.Exists(prepared.Directory), "Completed update files must be removed after their helper exits.");
        Require(File.ReadAllText(Path.Combine(fixture.Current, "VideoDownloader.exe")) == "new executable", "Completed cleanup must preserve the installed application.");
    }

    private static async Task CrashRecoveryAsync()
    {
        foreach (int pauseAfter in new[] { 1, 5, 6 })
        {
            using var fixture = new Fixture();
            File.WriteAllText(Path.Combine(fixture.Current, "cookies.txt"), "private cookies");
            Directory.CreateDirectory(Path.Combine(fixture.Current, "downloads"));
            File.WriteAllText(Path.Combine(fixture.Current, "downloads", "video.mp4"), "user media");
            var before = Snapshot(fixture.Current);
            using var prepared = fixture.Prepare();
            string executable = Path.Combine(fixture.Current, "VideoDownloader.exe");
            string signal = Path.Combine(prepared.Directory, "fixture-signal");
            string host = Environment.ProcessPath ?? throw new InvalidOperationException("The test process executable is unknown.");
            var start = new System.Diagnostics.ProcessStartInfo(host)
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(typeof(AppUpdateInstallerTests).Assembly.Location);
            foreach (string argument in new[] { "--vd-update-fixture", prepared.Directory, pauseAfter.ToString(System.Globalization.CultureInfo.InvariantCulture) })
                start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                var deadline = DateTime.UtcNow.AddSeconds(20);
                while (!File.Exists(signal) && !process.HasExited && DateTime.UtcNow < deadline)
                {
                    Require(File.Exists(executable), "The application EXE must exist throughout an update, including before replacement.");
                    await Task.Delay(20);
                }
                if (!File.Exists(signal))
                    throw new InvalidOperationException("The updater child did not reach its durable mutation checkpoint: " +
                        (process.HasExited ? await stdout + await stderr : "timed out"));
                Require(File.Exists(executable), "The original or replacement EXE must remain launchable at the crash checkpoint.");
                process.Kill(true);
                await process.WaitForExitAsync();
                if(pauseAfter==1)RemoveRuntimePolicyMetadata(prepared.Directory);
                var pending = AppUpdateInstaller.FindPending(fixture.Current + Path.DirectorySeparatorChar);
                Require(pending is not null, "A killed updater must leave a validated pending recovery pointer.");
                AppUpdateInstaller.Recover(pending!);
                RequireSame(before, Snapshot(fixture.Current), "Recovery after a real process kill must restore the complete original package and preserve user data.");
                Require(AppUpdateInstaller.FindPending(fixture.Current) is null, "Completed recovery must remove its pointer.");
            }
            finally
            {
                if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); }
                await stdout;
                await stderr;
            }
        }
        Console.WriteLine("PASS: killed updater child retains an EXE and recovers the entire original package before/after EXE and manifest replacement");
    }

    private static void ReparseAfterPreparation()
    {
        using var fixture = new Fixture();
        using var prepared = fixture.Prepare();
        string outside = Path.Combine(fixture.Root, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "MainWindow.xaml"), "outside sentinel");
        string link = Path.Combine(prepared.StageDirectory, "ui");
        Directory.Delete(link, true);
        if (OperatingSystem.IsWindows())
        {
            var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (string argument in new[] { "/d", "/c", "mklink", "/J", link, outside }) start.ArgumentList.Add(argument);
            using var process = System.Diagnostics.Process.Start(start)!;
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Require(process.ExitCode == 0, "The real junction fixture must be created: " + output + error);
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            var before = Snapshot(fixture.Current);
            Failure(() => AppUpdateInstaller.Apply(prepared));
            RequireSame(before, Snapshot(fixture.Current), "A staged reparse point must be rejected before target mutation.");
            Require(File.ReadAllText(Path.Combine(outside, "MainWindow.xaml")) == "outside sentinel", "Reparse validation must leave the external target untouched.");
        }
        finally { Directory.Delete(link, false); }
    }

    private static ZipArchiveEntry WriteEntry(ZipArchive archive, string name, string text)
    {
        var entry = archive.CreateEntry(name);
        using var stream = new StreamWriter(entry.Open());
        stream.Write(text);
        return entry;
    }

    private static Dictionary<string, byte[]> Snapshot(string directory) => Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .ToDictionary(path => Path.GetRelativePath(directory, path), File.ReadAllBytes, StringComparer.OrdinalIgnoreCase);

    private static void RequireSame(Dictionary<string, byte[]> expected, Dictionary<string, byte[]> actual, string message) =>
        Require(expected.Count == actual.Count && expected.All(pair => actual.TryGetValue(pair.Key, out var bytes) && pair.Value.SequenceEqual(bytes)), message);

    private static Exception Failure(Action action)
    {
        try { action(); } catch (Exception error) { return error; }
        throw new InvalidOperationException("Application update operation unexpectedly succeeded.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Application update installer test failed: " + message); }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "vd-update-tests-" + Guid.NewGuid().ToString("N"));
        internal string Current => Path.Combine(Root, "current");
        internal string Scratch => Path.Combine(Root, "scratch");
        internal string Archive => Path.Combine(Root, "update.zip");
        internal Fixture(string? installedRuntime=null,string? bundledRuntime=null)
        {
            Directory.CreateDirectory(Scratch);
            WritePackage(Current, "2.0.1", new[] { ("VideoDownloader.exe", "old executable"), ("yt-dlp.exe", "old yt-dlp"), ("ui/MainWindow.xaml", "old UI"), ("obsolete.dll", "old removed DLL") });
            string next = Path.Combine(Root, "next");
            WritePackage(next, "2.0.2", new[] { ("VideoDownloader.exe", "new executable"), ("yt-dlp.exe", "new yt-dlp"), ("ui/MainWindow.xaml", "new UI"), ("new.dll", "new DLL") });
            if(installedRuntime is not null)ReplaceRuntime(Current,installedRuntime);
            if(bundledRuntime is not null)ReplaceRuntime(next,bundledRuntime);
            ZipFile.CreateFromDirectory(next, Archive);
        }
        internal PreparedAppUpdate Prepare() => AppUpdateInstaller.Prepare(Archive, Current + Path.DirectorySeparatorChar, Scratch, "2.0.2", CancellationToken.None);
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
        private static void ReplaceRuntime(string directory,string source)
        {
            string runtime=Path.Combine(directory,"yt-dlp.exe");File.Copy(source,runtime,true);
            string path=Path.Combine(directory,AppUpdatePackage.ManifestName);
            var manifest=AppUpdatePackage.ReadManifest(path);
            manifest=manifest with {Files=manifest.Files.Select(file=>file.Path=="yt-dlp.exe"?
                new AppPackageFile(file.Path,new FileInfo(runtime).Length,AppUpdatePackage.HashFile(runtime)):file).ToList()};
            File.WriteAllText(path,JsonSerializer.Serialize(manifest,AppUpdatePackage.Json));
        }
        private static void WritePackage(string directory, string version, (string Name, string Text)[] content)
        {
            var files = new List<AppPackageFile>();
            foreach (var item in content)
            {
                string path = Path.Combine(directory, item.Name.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, item.Text);
                files.Add(new AppPackageFile(item.Name, new FileInfo(path).Length, AppUpdatePackage.HashFile(path)));
            }
            File.WriteAllText(Path.Combine(directory, AppUpdatePackage.ManifestName), JsonSerializer.Serialize(new AppPackageManifest(1, version, files), AppUpdatePackage.Json));
        }
    }
}
