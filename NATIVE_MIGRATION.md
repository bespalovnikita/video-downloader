# Video Downloader — native C# migration

## Runtime and packaging

The WPF GUI, download engine, queue, history, session recovery, preview, cancellation and tray are now implemented in **C# / .NET 8**. The new Windows portable package has **no PowerShell 7 runtime dependency** and does not launch pwsh.exe or a PowerShell console window. yt-dlp.exe and FFmpeg (when necessary) remain separate executables.

Build with .NET 8 SDK on Windows:

~~~powershell
dotnet publish native/VideoDownloader.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/native
~~~

Expected portable package: VideoDownloader.exe, yt-dlp.exe, ui/*.xaml and assets/app.ico. No PowerShell scripts or runtime.

## Reuse of existing data

- Session: %LOCALAPPDATA%\VideoDownloader\session.json, version 1. Queue and settings are read from the old GUI. The old engine `EventsPath` is replayed for completed/failed/cancelled URLs after a crash, then any remaining Running rows become Interrupted. No downloads resume automatically; a still-live legacy `pwsh` engine blocks native recovery/start and leaves the checkpoint unchanged.
- History: %LOCALAPPDATA%\VideoDownloader\successful-downloads.jsonl, version 1. Full append-only URL history and optional CompletedAtUtc are preserved, with the same named Local\VideoDownloader.SuccessHistory mutex.
- New v1 session rows retain `OutputRoot` alongside `OwnedPaths`, so removal uses the original publication folder after changing output or restoring an older archive date. Rows from older checkpoints use the saved base output folder as their boundary; paths outside it are still refused.
- Preview: %LOCALAPPDATA%\VideoDownloader\preview-cache, SHA-256(URL) filenames and 72-hour TTL are retained.

Do not run the old and new GUIs simultaneously during migration: they have distinct single-instance mutex names and cannot coordinate previous worker ownership.

## Feature mapping

| Previously in PowerShell | Native C# |
| --- | --- |
| Dark WPF GUI | Reuses existing ui/MainWindow.xaml |
| Queue, reorder, file import, drag/drop | C# ObservableCollection and WPF events |
| Paste and clipboard watcher during downloads | Channel-based live queue; no shared text files |
| Parallel downloads | Bounded async worker pool; native yt-dlp subprocesses |
| Progress and speeds | Structured stdout progress parsing, aggregate speed |
| Per-URL cancellation, global Stop | CancellationToken + kill process tree |
| Pause/resume | Windows process-tree suspend/resume |
| Partial cleanup | Per-job staging, explicit OwnedPaths and no-overwrite move |
| Preview and cached thumbnail | Single asynchronous preview worker; cache TTL 72 hours |
| Success-history prompt | Same WPF dialog with Yes/No/YesAll/NoAll/Cancel |
| Recovery | Compatible v1 session.json and settings |
| Tray | WinForms NotifyIcon; OK → Left → Err |
| Video format, codec, subtitles, audio, metadata | Native yt-dlp arguments, fallback and transient retry |
| Dependency installs and protocol | C# .NET services and user registry |
| CLI | VideoDownloader.exe --cli --input list.txt --out D:\Videos --threads 4 |

The native command-line parser is not an exact alias for old PowerShell parameters. The C# GUI supports the full current settings UI; native --cli currently supports core input/output/thread/quality/codec/container/rate/cookies options. Legacy PowerShell CLI source remains for unusual -Watch and script-only workflows.

## Safety and limitations

Every downloaded URL uses its own staging folder. Publishing does not overwrite existing media. File removal requires a known `OwnedPaths` manifest (a bare `Path` from an old checkpoint is insufficient) and is restricted to exact owned filenames inside the session's pinned output folder, with rejection of parent symlinks, junctions and reparse points. No wildcard deletion. Cancellation uses durable in-memory tombstones and result-finalization synchronization to cover the pending-to-running and publish-to-done race windows.

Filesystem tests include NTFS junctions and outside-root sentinel files. As with the preceding version, determined hostile processes that swap paths between validation and operation may exploit a TOCTOU race; only handle-relative Win32 filesystem operations can fully eliminate that class of attack.

## Release gate

Normal exit stops ingestion, waits for download cancellation/publication rollback and staging cleanup, then saves the final checkpoint before closing the WPF window.

Before replacing the default Windows release: C# compilation, actual WPF startup and Add/Remove/clipboard-preview/.url/checkpoint interaction smoke, offline real yt-dlp completion/cancellation/live-add/pause tests, legacy result-event reconciliation, boundary security tests, and self-contained ZIP without PowerShell. Manual UX validation of drag, duplicate modal, live clipboard preview, tray actions and dependency management remains required before switching the default Windows package.

Workflow: .github/workflows/native-windows.yml. The prior PowerShell implementation remains in source as a rollback path until release is accepted.
