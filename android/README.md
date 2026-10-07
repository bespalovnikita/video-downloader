# Video Downloader Android

Experimental Android port of the Windows Video Downloader, isolated on the feature/android-app branch.

## Stack

- Kotlin
- Jetpack Compose
- Android Gradle Plugin 9.4
- compile/target SDK 36
- min SDK 29
- youtubedl-android 0.18.1
- bundled FFmpeg module from youtubedl-android
- foreground data-sync service
- MediaStore publishing to Downloads/VideoDownloader

## Implemented in the first MVP

- add one or many URLs;
- Android Share Target (Share -> Video Downloader);
- persistent queue/settings/history;
- automatic preview prefetch when a URL enters the queue;
- preview title/uploader/duration/thumbnail/resolution/FPS/codecs/HDR;
- local preview thumbnail cache;
- preview cache TTL: 72 hours;
- configurable quality: Best / 2160 / 1440 / 1080 / 720;
- codec preference: Auto / H264 / VP9 / AV1 with generic fallback;
- container: Auto / MP4 / MKV / WebM;
- parallel downloads;
- fragment concurrency;
- rate limit;
- MP3-only mode;
- SponsorBlock;
- subtitles / auto-subs / embed subtitles;
- embed thumbnail / metadata / chapters;
- filename template;
- transient retries with exponential backoff;
- foreground notification with Pause / Resume / Stop;
- completed download history;
- publish downloaded files through MediaStore.

## Preview architecture

Preview is proactive, matching the Windows app's current behavior:

    URL added
       |
       +--> single background preview worker
       |       |
       |       +--> yt-dlp --dump-single-json
       |       +--> thumbnail download
       |       +--> persistent cache (72h)
       |
       +--> download queue

Selecting a queue card can raise its preview priority, but does not create a second metadata request.

## Download storage

yt-dlp writes into an app-private staging directory first. After a successful download, files are copied into:

    Downloads/VideoDownloader

through Android MediaStore and the private staging directory is removed.

This avoids relying on broad storage permissions.

## Pause semantics

Android cannot reuse the Windows NtSuspendProcess approach.

Pause stops the active yt-dlp processes. Resume requeues the paused items. yt-dlp normally resumes partial files when possible, so the user-facing result is close to pause/resume but it is not process suspension.

## Build

The branch contains an Android CI workflow. Locally, with JDK 17 + Android SDK 36:

    gradle -p android :app:testDebugUnitTest :app:assembleDebug

The APK is produced under:

    android/app/build/outputs/apk/debug/

## Current limitations

- no cookies file/browser-cookie UI yet;
- no Storage Access Framework custom output-folder picker yet;
- no download archive / duplicate-video ID archive yet;
- no Android-specific user presets editor yet;
- foreground dataSync services are subject to Android platform background/timeout rules;
- only arm64-v8a and x86_64 ABIs are enabled in the MVP;
- the youtubedl-android dependency is GPL-3.0; review distribution/license obligations before publishing the APK publicly.

## Why this is a separate branch

The Windows app remains PowerShell/WPF and is untouched. Android is a real port, not a wrapper around the Windows scripts, so it has its own runtime and UI while preserving the same product behavior where Android permits it.
