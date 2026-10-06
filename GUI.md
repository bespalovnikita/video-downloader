# Video Downloader WPF

Windows GUI поверх `ytdl-manager-v8.ps1`. Интерфейс переведён с WinForms на WPF/XAML, движок остаётся отдельным PowerShell/yt-dlp процессом.

## Основное

- очередь URL с drag&drop reorder, удалением, дедупликацией и контекстным меню;
- Pause / Resume и Stop для дерева downloader-процессов;
- автоматический retry только transient/network ошибок с exponential backoff;
- preview: title, uploader, duration, thumbnail, максимальное разрешение, FPS, codecs и HDR/dynamic range;
- quality: Best / 2160 / 1440 / 1080 / 720;
- codec preference: Auto / H264 / VP9 / AV1;
- container: Auto / MP4 / MKV / WebM;
- subtitles: обычные, auto-generated, языки, embed;
- embed thumbnail / metadata / chapters;
- настраиваемый yt-dlp filename template;
- профили: Universal, 4K archive, MP4 compatibility, Music MP3, Subtitles archive;
- Watch Clipboard: URL автоматически добавляются в очередь;
- session stats: success/error, bytes, elapsed, average speed;
- контекстное меню результата: открыть файл, показать в папке, копировать путь, открыть URL, retry, raw error;
- системный tray, уведомления и звуковой сигнал.

## Machine-readable progress

Движок больше не извлекает процент/скорость/ETA из обычных строк `[download] ...`.

Используются `--progress-template` с префиксом `__VD_PROGRESS__`, `--print before_dl:` для title и `--print after_move:` для итогового пути. Парсер находится в `lib/VideoDownloader.Core.psm1` и покрыт отдельными тестами.

## Dependency Center

Показывает версии/наличие PowerShell, yt-dlp, ffmpeg и ffprobe. Доступны Refresh, Update yt-dlp, Install FFmpeg и Register protocol.

Install FFmpeg скачивает Windows essentials ZIP с gyan.dev и кладёт `ffmpeg.exe`/`ffprobe.exe` рядом с portable-приложением.

## Custom protocol

Register protocol создаёт user-level схему `videodownloader://`. Launcher передаёт входной URL в WPF GUI. Администраторские права для регистрации в HKCU не требуются.

## Сборка

`pwsh -File .\build-exe.ps1`

Portable ZIP включает `VideoDownloader.exe`, GUI/engine, `ui/`, `lib/`, `assets/` и `yt-dlp.exe`.

## Tests

- `tests/core-tests.ps1`: progress parser, speed parser, transient/permanent retry classification, friendly errors, codec selector;
- `tests/smoke.ps1`: PowerShell parser, XML/XAML validation, WPF feature markers, engine options и CLI DryRun;
- GitHub Actions запускает оба теста, затем собирает Windows portable artifact.