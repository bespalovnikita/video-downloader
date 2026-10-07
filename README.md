# Video Downloader

[![Build Windows EXE](https://github.com/bespalovnikita/video-downloader/actions/workflows/build-windows.yml/badge.svg)](https://github.com/bespalovnikita/video-downloader/actions/workflows/build-windows.yml)
![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![PowerShell](https://img.shields.io/badge/PowerShell-7%2B-5391FE)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)

Windows GUI и CLI-движок поверх **yt-dlp** для пакетной загрузки видео: очередь, параллельные загрузки, retry, выбор качества/кодека/контейнера, субтитры, metadata, preview, pause/resume и machine-readable progress.

Проект состоит из WPF GUI на PowerShell 7, отдельного download engine и небольшого self-contained .NET launcher.

## Возможности

| Область | Что есть |
| --- | --- |
| Queue | несколько URL, dedupe, drag & drop reorder, импорт .txt / .url, clipboard watcher, context menu |
| Download | параллельные URL, fragment concurrency, rate limit, pause/resume, stop, transient retry с exponential backoff |
| Formats | Best / 2160 / 1440 / 1080 / 720, Auto / H264 / VP9 / AV1, Auto / MP4 / MKV / WebM |
| Subtitles | обычные и auto-generated, language selector, embed |
| Metadata | thumbnail, metadata, chapters, произвольный yt-dlp filename template |
| Preview | title, uploader, duration, thumbnail, max resolution/FPS, codecs, HDR |
| Preview prefetch | metadata и thumbnail готовятся отдельным background worker сразу после добавления URL |
| Preview cache | RAM + %LOCALAPPDATA%\VideoDownloader\preview-cache, TTL **72 часа** |
| UX | dark WPF UI, tray, notifications, session stats, actions для готовых файлов |
| Dependencies | проверка yt-dlp / FFmpeg / ffprobe, update yt-dlp, install FFmpeg |
| Integration | user-level protocol videodownloader:// |

## Быстрый старт

### Готовая Windows-сборка

Открой успешный workflow **Build Windows EXE**, скачай artifact <code>VideoDownloader-windows</code>, распакуй ZIP и запусти:

~~~text
VideoDownloader.exe
~~~

или:

~~~text
start-gui.cmd
~~~

### Требования

- Windows 10/11 x64;
- **PowerShell 7 (pwsh.exe)** — нужен даже при запуске через VideoDownloader.exe;
- yt-dlp.exe уже входит в portable package;
- FFmpeg/ffprobe нужны для merge/extract/embed и могут быть установлены через **Dependency Center**.

> VideoDownloader.exe — self-contained .NET launcher, но GUI и engine пока остаются PowerShell-скриптами. Это portable ZIP, а не автономный single-EXE runtime.

## Preview без ожидания клика

Когда URL попадает в Queue, отдельный worker сразу начинает готовить preview:

~~~text
Queue URL
   |
   +--> background preview worker --> yt-dlp metadata --> thumbnail
   |                                   |
   |                                   +--> cache, TTL 72h
   |
   +--> download engine -------------> media download
~~~

Preview-worker один и работает независимо от download threads, чтобы не плодить дополнительные тяжёлые yt-dlp процессы.

Если пользователь выбирает строку:

- готовый cache показывается сразу;
- если preview ещё не готов, URL поднимается в приоритете prefetch-очереди;
- отдельный второй metadata request по клику не запускается.

После перезапуска приложения cache сохраняется на диске 3 суток. Протухшие и повреждённые записи удаляются автоматически.

## Queue во время загрузки

Во время активной сессии Queue остаётся доступной для выбора: можно переключать строки, смотреть preview, открывать и копировать URL.

Чтобы snapshot download engine не расходился с UI, до завершения сессии блокируются операции, меняющие очередь: remove, reorder, drop и отдельный запуск выбранной строки.

## Presets

В GUI встроены:

- **Universal**
- **4K archive**
- **MP4 compatibility**
- **Music MP3**
- **Subtitles archive**

Preset применяет комбинацию quality / codec / container / subtitles / metadata. Пользовательские presets и сохранение UI settings между запусками пока не реализованы.

## CLI

GUI использует тот же engine, который можно запускать напрямую:

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 ^
  -In .\list.txt ^
  -Out D:\Videos ^
  -Threads 4 ^
  -AutoFragments ^
  -Quality 1080 ^
  -VideoCodec H264 ^
  -Container MP4 ^
  -Retries 3
~~~

> В PowerShell вместо символа ^ для переноса команды используй backtick или запиши команду одной строкой.

Полный справочник параметров: [ytdl-manager-v8-manual.md](ytdl-manager-v8-manual.md).

## Machine-readable progress

GUI не парсит обычную строку вида <code>[download] ...</code> как основной источник прогресса.

Engine использует:

- <code>--progress-template</code> с префиксом <code>__VD_PROGRESS__</code>;
- <code>--print before_dl:</code> для title;
- <code>--print after_move:</code> для итогового filepath.

Парсер находится в <code>lib/VideoDownloader.Core.psm1</code>. GUI получает JSONL events и синхронизирует Queue/Downloads.

## Retry и fallback

Для transient/network ошибок engine делает внешний retry с exponential backoff. По умолчанию:

~~~text
5s -> 10s -> 20s
~~~

<code>-Retries 3</code> означает до трёх повторных попыток после первой.

При недоступности выбранного resolution quality может снижаться:

~~~text
2160 -> 1440 -> 1080 -> 720
1440 -> 1080 -> 720
1080 -> 720
~~~

Codec selector — **предпочтение**, а не жёсткий lock: после codec-specific варианта остаётся generic fallback с тем же ограничением по высоте.

## Сборка

Нужны PowerShell 7 и .NET 8 SDK:

~~~powershell
pwsh -File .\build-exe.ps1
~~~

Результат:

~~~text
dist/
+-- VideoDownloader.exe
+-- VideoDownloader-portable.zip
+-- ytdl-manager-gui.ps1
+-- ytdl-manager-v8.ps1
+-- yt-dlp.exe
+-- ui/
+-- lib/
+-- assets/
~~~

Launcher публикуется как self-contained win-x64 .NET 8 приложение.

## Тесты

~~~powershell
pwsh -File .\tests\core-tests.ps1
pwsh -File .\tests\smoke.ps1
~~~

CI на PR и push в main выполняет:

1. core/parser/cache tests;
2. PowerShell parser + WPF/XAML/engine smoke;
3. portable build;
4. upload Windows artifact.

## Структура проекта

| Путь | Назначение |
| --- | --- |
| ytdl-manager-gui.ps1 | WPF code-behind, Queue, Preview, process control, tray, dependencies |
| ui/MainWindow.xaml | WPF layout и dark theme |
| ytdl-manager-v8.ps1 | download engine |
| lib/VideoDownloader.Core.psm1 | progress parser, retry/error helpers, codec mapping |
| lib/VideoDownloader.PreviewCache.psm1 | preview fetch + persistent cache |
| launcher/ | .NET 8 launcher |
| tests/ | core/cache tests и smoke |
| build-exe.ps1 | portable package build |
| GUI.md | подробности GUI |
| ytdl-manager-v8-manual.md | CLI manual |
| AGENTS.md | инструкции для coding agents |

## Ограничения

- Windows-only;
- PowerShell 7 пока обязателен;
- FFmpeg не bundled по умолчанию;
- FFmpeg installer скачивает архив с gyan.dev без собственной checksum/signature verification;
- single-instance/IPC пока нет, поэтому custom protocol может открыть второй экземпляр;
- settings и пользовательские presets не сохраняются;
- CLI использует входной .txt как рабочую очередь и удаляет из него завершённые URL;
- success/error txt относятся к текущему запуску, а не являются вечной историей.

## Документация

- [GUI.md](GUI.md) — интерфейс и GUI behavior;
- [ytdl-manager-v8-manual.md](ytdl-manager-v8-manual.md) — полный CLI contract;
- [AGENTS.md](AGENTS.md) — архитектурные правила для automated coding.
