# Android AGENTS.md

Rules for changes under android/.

## Architecture

- Compose owns UI only.
- AppStore owns persistent queue/settings/history state.
- PreviewPrefetcher is the only proactive metadata worker.
- DownloadService owns active downloads.
- YtDlpClient is the only direct yt-dlp wrapper.
- MediaPublisher is the only layer that publishes files to public Downloads.

## Invariants

- Adding a URL must schedule preview prefetch immediately.
- A UI click must not create a parallel metadata fetch; it can only prioritize existing prefetch work.
- Preview cache TTL stays 72 hours unless requirements change explicitly.
- Downloads must run in a foreground service.
- Avoid broad storage permissions; stage privately and publish through MediaStore.
- Codec selection is a preference with generic fallback, matching Windows behavior.
- Retry only transient/network failures.
- Do not use Windows PowerShell scripts or invoke a remote Windows backend from the Android app.

## Tests

Run:

    gradle -p android :app:testDebugUnitTest :app:assembleDebug

Keep pure selection/retry logic outside Android framework classes so it can be covered by local unit tests.
