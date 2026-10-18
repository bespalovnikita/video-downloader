# Video Downloader — GUI

WPF-интерфейс для <code>ytdl-manager-v8.ps1</code>. GUI отвечает за Queue, Preview, настройки, process control и отображение событий; скачивание media выполняет отдельный PowerShell/yt-dlp engine.

## Запуск

Рекомендуемый вариант из portable package:

~~~text
VideoDownloader.exe
~~~

Альтернатива:

~~~text
start-gui.cmd
~~~

Для обоих вариантов нужен установленный **PowerShell 7**.

## Queue

В очередь можно добавлять:

- один или несколько HTTP/HTTPS URL;
- URL из clipboard при включённом **Watch clipboard**;
- явная кнопка **Paste** рядом с **+ Add** (работает и во время загрузки);
- .txt со списком URL;
- .url Internet Shortcut;
- текст/файлы через drag & drop.

Повторяющиеся URL не добавляются.

До старта сессии доступны reorder мышью, Move Up/Down, Remove и Clear.

### Queue во время загрузки

Queue остаётся кликабельной. Можно:

- выбирать другой URL;
- смотреть Preview;
- открыть URL;
- скопировать URL.

Во время загрузки **Paste**, ввод в URL-поле с кнопкой **+ Add** и **Watch clipboard** продолжают работать. URL после проверки по истории успешных скачиваний добавляется в Queue, сразу сохраняется в session checkpoint и поступает в *текущий* engine через отдельный `incoming.txt`. Engine опрашивает его примерно каждые 500 мс. Если процесс завершился во время подтверждения, новые URL остаются `Queued` для следующего Start.

До окончания сессии по-прежнему запрещены операции с существующей очередью: reorder, drop, remove и download selected. Очередь engine `queue.txt` напрямую не изменяется из GUI.

State синхронизируется с engine и может показывать:

~~~text
Queued
Starting
Preparing
42.7%
Merging
Retrying
Finalizing
Done
<friendly error>
~~~

## Preview

Preview показывает:

- title;
- uploader;
- duration;
- thumbnail;
- extractor;
- максимальную доступную высоту;
- максимальный FPS;
- обнаруженные video codecs;
- HDR/dynamic range.

### Background prefetch

Preview не ждёт пользовательского клика.

Каждый новый URL автоматически попадает в отдельную prefetch-очередь. Один background worker последовательно получает metadata и thumbnail, пока основная загрузка работает независимо.

Если выбран URL, preview которого ещё не готов, он поднимается в начало prefetch-очереди. Дополнительный parallel metadata request по клику не создаётся.

### Cache

Успешный preview сохраняется в:

~~~text
%LOCALAPPDATA%\VideoDownloader\preview-cache
~~~

Используются RAM-cache и persistent JSON cache. Thumbnail хранится как image bytes в base64.

TTL: **72 часа (3 дня)**.

После TTL запись удаляется и metadata будет получена заново. Повреждённые cache records также удаляются автоматически.

Для совместимости WPF preview предпочитает JPEG/PNG thumbnail, скачивает его в background worker и декодирует из MemoryStream.

## Downloads

Таблица Downloads показывает активные slots и состояния текущей сессии.

Доступные действия для результата:

- Open file;
- Show in folder;
- Copy path;
- Open URL;
- Retry URL;
- Show raw yt-dlp error.

Лог занимает большую часть нижней зоны центральной колонки; Downloads — меньшую.

## Pause / Resume / Stop

Pause/Resume реализованы через suspend/resume дерева Windows processes.

Используются:

- NtSuspendProcess;
- NtResumeProcess;
- snapshot дочерних процессов через Win32_Process.

Stop сначала возобновляет paused tree, если это требуется, затем завершает process tree.

Это Windows-specific механизм.

## Profiles

Встроенные presets:

| Profile | Назначение |
| --- | --- |
| Universal | универсальные значения |
| 4K archive | 2160, MKV, metadata/chapters/thumbnail |
| MP4 compatibility | 1080, H264 preference, MP4 |
| Music MP3 | audio-only MP3 + metadata/thumbnail |
| Subtitles archive | MKV + subtitles/auto-subs/embed |

Пользовательские profiles пока не реализованы. Настройки очереди/загрузки сохраняются как часть session checkpoint и восстанавливаются после подтверждения пользователя; это не глобальные presets.

## Format settings

### Quality

Доступно:

~~~text
Best
2160
1440
1080
720
~~~

Для фиксированного quality engine использует ограничение height<=N и может перейти на более низкий уровень при ошибке Requested format is not available.

### Codec preference

Доступно:

~~~text
Auto
H264
VP9
AV1
~~~

Это preference: если codec-specific stream не найден, selector допускает generic fallback.

### Container

Доступно:

~~~text
Auto
MP4
MKV
WebM
~~~

Фиксированный container передаётся как <code>--merge-output-format</code>, поэтому для merge/remux может понадобиться FFmpeg.

### Rate limit

Передаётся в yt-dlp через <code>--limit-rate</code>.

### Threads и fragments

- Threads — число URL, которые engine обрабатывает параллельно;
- Fragments — yt-dlp fragment concurrency внутри одного URL;
- Auto fragments делает предварительную оценку размера и выбирает 1 / 2 / 4.

## Subtitles

GUI поддерживает:

- regular subtitles;
- auto-generated subtitles;
- SubtitleLangs;
- embed subtitles.

GUI по умолчанию ориентирован на <code>ru.*,en.*</code>. CLI engine по умолчанию использует <code>all,-live_chat</code>.

## Metadata / output

Можно включать:

- thumbnail;
- metadata;
- chapters;
- произвольный yt-dlp filename template.

Базовый engine template:

~~~text
%(title)s [%(id)s].%(ext)s
~~~

## Retry

GUI запускает engine с:

~~~text
Retries = 3
RetryDelaySeconds = 5
~~~

Retry выполняется только для transient/network классов ошибок. Backoff экспоненциальный, максимальная задержка — 60 секунд.

Permanent/auth/unavailable ошибки не должны бессмысленно повторяться.

## Machine-readable progress

Основной progress transport:

~~~text
__VD_PROGRESS__|percent|speed|eta|downloaded_bytes|total_bytes|stream_id
~~~

Title и итоговый path приходят через отдельные yt-dlp print markers.

GUI читает structured events из JSONL EventFile.

Engine явно передаёт `--progress`, поэтому `--print` не подавляет progress. GUI читает только законченные JSONL-строки, сохраняя недописанный UTF-8 хвост для следующего тика.

Queue и Downloads показывают для каждой ссылки progress bar, процент, скорость, объём и ETA. Процент относится к текущему media-компоненту (video/audio), поэтому при смене компонента и retry он может сбрасываться. В Downloads результаты сохраняются по URL, а не перезаписываются следующим заданием того же слота.

В Session видны текущая суммарная скорость и накопленный объём по progress-счётчикам всех URL, включая отдельные video/audio streams и частично скачанные неудачные задания. При завершении размер итогового файла повторно не прибавляется. На паузе суммарная скорость показывается как нулевая. Эти данные относятся к текущему запуску Start и не восстанавливаются из checkpoint как сетевой трафик прошлой сессии.

## Dependency Center

Показывает:

- PowerShell version;
- yt-dlp version;
- FFmpeg;
- ffprobe.

Действия:

- Refresh;
- Update yt-dlp;
- Install FFmpeg;
- Register protocol.

### Install FFmpeg

Скачивает <code>ffmpeg-release-essentials.zip</code> с gyan.dev и копирует ffmpeg.exe / ffprobe.exe рядом с приложением.

Нужны сеть и права записи в папку portable package.

Собственной checksum/signature verification скачанного архива сейчас нет.

## Custom protocol

Register protocol создаёт user-level handler:

~~~text
HKCU\Software\Classes\videodownloader
~~~

Пример payload:

~~~text
videodownloader://https%3A%2F%2Fexample.com%2Fvideo
~~~

Payload URL-decoded и добавляется в Queue.

Повторный запуск передаёт аргументы в работающий экземпляр через named pipe с доступом только текущего пользователя. Окно восстанавливается из трея, URL проходит обычную валидацию и добавление в очередь.

## Tray и notifications

При minimize окно скрывается в tray, но **приложение продолжает работать**: фоновые загрузки, preview prefetch и уведомления не останавливаются. Двойной клик по значку или Open возвращает окно. При нажатии **X** спрашиваем: Yes — спрятать в трей, No — выйти, Cancel — остаться. Exit в меню трея завершает приложение (и отдельно подтверждает остановку активных загрузок).

Tray menu:

- Open;
- Open downloads;
- Exit.

После завершения сессии GUI показывает системное уведомление или balloon fallback и проигрывает звуковой сигнал.

## Session checkpoint / восстановление

- При изменениях очереди, статусов и настроек GUI сохраняет атомарный checkpoint в `%LOCALAPPDATA%\\VideoDownloader\\session.json`. Это отдельный файл от `%TEMP%` engine.
- При следующем запуске, если остались `Queued`, `Interrupted` или `Failed` элементы, GUI спрашивает, восстановить ли список и настройки. Отказ удаляет checkpoint; скачивание **не** стартует автоматически.
- `Running` из предыдущего процесса становится `Interrupted`. `Completed` не перекачивается при обычном Start; явное Download selected / Retry URL позволяет вручную повторить.
- При аварии GUI, пока старый yt-dlp/PowerShell engine ещё работает, новый запуск загрузок блокируется до его завершения. После этого приложение сверяет итоговые Result-события JSONL перед возобновлением очереди (если event-файл сохранился).
- Для защиты checkpoint работает один экземпляр GUI в пользовательской сессии. Повторная protocol invocation передаёт URL первому экземпляру через IPC.
- Хранится путь к cookies-файлу, но не его содержимое. Скачанные media и `.part` не переносятся; докачка зависит от yt-dlp/сервера.
- Битый checkpoint сохраняется как `.invalid-<timestamp>`, восстановление пропускается с предупреждением.

## Постоянная история успешно скачанных URL

- При **успешной** загрузке URL добавляется в `%LOCALAPPDATA%\\VideoDownloader\\successful-downloads.jsonl`. Запись идёт из CLI engine (поэтому учитываются и скачивания без GUI), журнал не очищается при закрытии, остановке или очистке очереди. Ошибки и DryRun в историю не попадают.
- Каждая запись хранит `CompletedAtUtc`. Диалог повтора показывает самое позднее успешное скачивание в локальном времени с часовым поясом. Старые записи без даты и импорт из старого checkpoint показываются как «время неизвестно»; дата импорта не выдаётся за дату скачивания.
- При первом запуске обновлённой версии дополнительно импортируются ссылки со статусом `Completed` из последнего доступного session checkpoint; старые истории, которые ранее нигде не сохранялись, восстановить невозможно.
- Перед **каждым запуском загрузки**, включая Retry/Download selected, кандидаты проверяются по точному совпадению URL с историей. На каждое совпадение предлагаются `Yes` (повторно скачать), `No` (убрать из Queue), `Yes to all`, `No to all`. `Cancel`/крестик отменяет весь запуск **без изменений очереди**.
- «Для всех» применяются только к совпадениям текущего запуска. URL, отсутствующие в истории, продолжают скачиваться без дополнительных вопросов.
- История не является проверкой существования файлов: даже после удаления скачанного файла URL остаётся известным.
- Если журнал повреждён или недоступен, загрузка не начинается, история не сбрасывается молча. Файл JSONL можно резервировать и просматривать вручную.

## Session stats

Показываются:

- active slots;
- aggregate current speed;
- completed/total;
- success/fail;
- downloaded bytes;
- elapsed;
- average throughput.

## Временные файлы

Каждая download session использует:

~~~text
%TEMP%\ytdl-gui-<guid>
~~~

Внутри создаются queue, log, events и owner PID.

При старте приложение пытается очищать stale temp dirs старше 24 часов, если owner process уже не жив.

## Сборка и тесты

~~~powershell
pwsh -File .\tests\core-tests.ps1
pwsh -File .\tests\smoke.ps1
pwsh -STA -File .\tests\tray-lifecycle-tests.ps1
pwsh -File .\tests\session-checkpoint-tests.ps1
pwsh -File .\tests\download-history-tests.ps1
pwsh -File .\tests\live-queue-tests.ps1
pwsh -File .\build-exe.ps1
~~~

GitHub Actions выполняет тесты перед сборкой portable ZIP.
## Reliability of retries and previews

Retry URL restores a removed Downloads item to Queue before saving the session, so it remains recoverable after a restart. Cancelling the duplicate prompt leaves Queue unchanged. Preview cache expires after 72 elapsed hours regardless of the local timezone.
