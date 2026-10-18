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
| Queue | несколько URL, dedupe, drag & drop reorder, импорт .txt / .url, clipboard watcher, кнопка Paste, live additions во время скачивания |
| Download | параллельные URL, fragment concurrency, rate limit, pause/resume, stop, transient retry с exponential backoff |
| Formats | Best / 2160 / 1440 / 1080 / 720, Auto / H264 / VP9 / AV1, Auto / MP4 / MKV / WebM |
| Subtitles | обычные и auto-generated, language selector, embed |
| Metadata | thumbnail, metadata, chapters, произвольный yt-dlp filename template |
| Preview | title, uploader, duration, thumbnail, max resolution/FPS, codecs, HDR |
| Preview prefetch | metadata и thumbnail готовятся отдельным background worker сразу после добавления URL |
| Preview cache | RAM + %LOCALAPPDATA%\VideoDownloader\preview-cache, TTL **72 часа** |
| UX | dark WPF UI, tray, notifications, session stats, actions для готовых файлов |
| Recovery | атомарный session checkpoint, восстановление незавершённой очереди и параметров с подтверждением |
| Download history | сохраняет URL и время каждого успеха; перед повтором показывает дату последнего скачивания и Yes/No/Yes to all/No to all |
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

Во время скачивания разрешены **Paste**, ручное добавление URL через **+ Add** и **Watch clipboard**. Новые ссылки попадают в активный engine через отдельный безопасный append-only ingress и могут скачиваться в том же сеансе. Параметры загрузки для них остаются теми же. Перед добавлением уже скачанных ссылок запрашивается подтверждение из постоянной истории. Если engine уже завершился, ссылка остаётся в очереди для следующего Start.

**Remove теперь доступен во время скачивания**: выбор строки и Remove отменяет только выбранное задание (включая скачиваемые части) и после подтверждения удаляет его сохранённые файлы. Другие URL продолжают скачиваться. Для точной очистки движок использует индивидуальную staging-папку и список принадлежащих скачиванию файлов. История успешных URL сохраняется.

Reorder, drag/drop и отдельный запуск выбранной строки по-прежнему недоступны до завершения сессии.

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

В обеих таблицах для каждого URL видны progress bar, процент, скорость, объём текущего media-компонента и ETA. Downloads сохраняет отдельную строку для каждого URL, даже при повторном использовании worker slot. В Session постоянно обновляются суммарная скорость и накопленный объём по progress-счётчикам; размер готового файла второй раз не прибавляется. При разных video/audio streams учитываются оба компонента. Процент может начаться заново при переходе от видео к аудио или при retry.

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
pwsh -File .\tests\cancellation-tests.ps1
pwsh -File .\tests\live-queue-tests.ps1
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
- Windows GUI работает в одном экземпляре; повторный запуск передаёт URL/списки в открытое окно через user-only named pipe;
- пользовательские presets пока не сохраняются; настройки активной очереди сохраняются в session checkpoint;
- CLI использует входной .txt как рабочую очередь и удаляет из него завершённые URL;
- долговременная история успешных URL хранится отдельно: `%LOCALAPPDATA%\\VideoDownloader\\successful-downloads.jsonl`; при наличии предыдущей загрузки GUI показывает диалог выбора перед Start/Retry (в CLI вопросов нет);
- success/error txt относятся к текущему запуску, а не являются вечной историей.

## Документация

- [GUI.md](GUI.md) — интерфейс и GUI behavior;
- [ytdl-manager-v8-manual.md](ytdl-manager-v8-manual.md) — полный CLI contract;
- [AGENTS.md](AGENTS.md) — архитектурные правила для automated coding.
## Recovery and parallel downloads

Parallel downloads preserve waiting workers up to the configured thread count. Explicit retries restore removed items to the saved queue, and preview cache lifetime is measured in elapsed UTC time.
