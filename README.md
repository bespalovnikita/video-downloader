# Video Downloader

**Стабильная версия в `main` — нативный C# / .NET 8 WPF Video Downloader, без PowerShell 7.** Начиная с `v2.0.0`, каждый коммит в `main` после успешного Native Windows CI автоматически публикуется как **GitHub Release** с готовым portable ZIP и контрольной суммой SHA-256.

Скачать готовую программу: **[Releases](https://github.com/bespalovnikita/video-downloader/releases/latest)**. Подробности сборки и перехода с PowerShell — [NATIVE_MIGRATION.md](NATIVE_MIGRATION.md).


[![Native C# Windows build](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml/badge.svg)](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml)
![Windows](https://img.shields.io/badge/platform-Windows%2010%2F11-0078D4)
![Runtime](https://img.shields.io/badge/runtime-.NET%208-512BD4)
![UI](https://img.shields.io/badge/UI-WPF-512BD4)

Windows GUI и CLI-движок поверх **yt-dlp** для пакетной загрузки видео: очередь, параллельные загрузки, retry, выбор качества/кодека/контейнера, субтитры, metadata, preview, pause/resume и machine-readable progress.

Основное приложение состоит из нативного C# WPF-интерфейса и C# download engine, управляющего `yt-dlp.exe`. Прежняя версия на PowerShell сохранена в исходниках как резервный вариант.

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

Открой **[GitHub Releases](https://github.com/bespalovnikita/video-downloader/releases/latest)**, скачай `VideoDownloader-Windows-x64.zip`, распакуй его в папку и запусти `VideoDownloader.exe`.

Рядом с архивом опубликован `SHA256SUMS.txt` для проверки целостности. Каждый успешно протестированный коммит в `main` получает отдельную стабильную версию `v2.0.0`, `v2.0.1` и т. д. Архивы из Actions остаются доступными для тестирования PR, но официальное скачивание идёт через Releases.

### Требования

- Windows 10/11 x64;
- **PowerShell 7 не требуется** для нативной сборки из Releases;
- yt-dlp.exe уже входит в portable package;
- FFmpeg/ffprobe нужны для merge/extract/embed и могут быть установлены через **Dependency Center**.

> Прежний PowerShell GUI и CLI доступны в исходниках для отката, но не входят в новый релизный ZIP.

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

## Статус в трее

Наведи курсор на значок Video Downloader в трее: tooltip покажет OK / Left / Err (сколько успешно, сколько ошибок, сколько осталось), а во время работы — текущую скорость. Статусы Queue и Downloads выделены цветом: Done — зелёный, Queued — серый, Running — голубой, Interrupted — жёлтый, Failed — красный. Превью новых ссылок из буфера при работающем движке ставится в приоритет; ручной Paste сразу показывает панель превью новой ссылки.

## Queue во время загрузки

Во время активной сессии Queue остаётся доступной для выбора: можно переключать строки, смотреть preview, открывать и копировать URL.

Во время скачивания разрешены **Paste**, ручное добавление URL через **+ Add** и **Watch clipboard**. Новые ссылки попадают в активный engine через отдельный безопасный append-only ingress и могут скачиваться в том же сеансе. Параметры загрузки для них остаются теми же. Перед добавлением уже скачанных ссылок запрашивается подтверждение из постоянной истории. Если engine уже завершился, ссылка остаётся в очереди для следующего Start.

**Remove теперь доступен во время скачивания**: выбор строки и Remove отменяет только выбранное задание (включая скачиваемые части) и после подтверждения удаляет его сохранённые файлы. Другие URL продолжают скачиваться. Для точной очистки движок использует индивидуальную staging-папку и список принадлежащих скачиванию файлов. История успешных URL сохраняется. При удалении проверяются не только пути, но и NTFS junction / symbolic link / reparse points во всех родительских каталогах; выход за папку загрузок или коллизия с чужим файлом приводит к отказу. Проверяется отдельным тестом `tests/file-boundary-safety-tests.ps1`. При намеренной параллельной подмене каталогов остаётся теоретическое TOCTOU окно.

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

Нативная сборка поддерживает базовый командный режим:

~~~powershell
.\VideoDownloader.exe --cli --input .\list.txt --out D:\Videos --threads 4 --quality 1080
~~~

Расширенные параметры прежнего PowerShell CLI пока доступны только через `ytdl-manager-v8.ps1` из исходников. Его справочник: [ytdl-manager-v8-manual.md](ytdl-manager-v8-manual.md).

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

Нужен .NET 8 SDK на Windows:

~~~powershell
dotnet publish native/VideoDownloader.Native.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o dist/native
~~~

`dist/native` содержит `VideoDownloader.exe`, `yt-dlp.exe` и UI-ресурсы. GitHub Actions автоматически создаёт релизный архив из этой папки. Подробная политика тегов и публикации — [RELEASING.md](RELEASING.md).

## Тесты

~~~powershell
pwsh -File .\tests\core-tests.ps1
pwsh -File .\tests\cancellation-tests.ps1
pwsh -File .\tests\file-boundary-safety-tests.ps1
pwsh -File .\tests\tray-preview-tests.ps1
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
