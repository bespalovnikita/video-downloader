# YT-DLP Download Manager v8 — CLI manual

<code>ytdl-manager-v8.ps1</code> — PowerShell 7 engine для пакетной загрузки URL через bundled <code>yt-dlp.exe</code>.

## Быстрый пример

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\list.txt -Out D:\Videos -Threads 4 -AutoFragments -Quality 1080 -VideoCodec H264 -Container MP4
~~~

## Важная модель работы

Входной <code>-In</code> файл — **рабочая очередь**, а не immutable source.

После завершения каждого URL engine переписывает input file и оставляет в нём только незавершённые элементы. Если исходный список нужен как архив, сохрани копию отдельно.

Для <code>list.txt</code> создаются:

~~~text
list-success.txt
list-error.txt
~~~

По умолчанию они лежат рядом с input file. Параметр <code>-ResultDir</code> меняет эту папку.

Перед обычным запуском старые success/error files текущего имени удаляются.

## Параметры

| Параметр | Default | Назначение |
| --- | --- | --- |
| -In | .\list.txt | входной файл с URL |
| -Out | текущая папка | media output directory |
| -Threads | 4 | число URL, обрабатываемых параллельно |
| -Fragments | 1 | yt-dlp -N, значение clamp 1..8 |
| -AutoFragments | off | автоматический выбор 1/2/4 fragments |
| -Watch | off | продолжать следить за input file |
| -WatchInterval | 2 | период проверки input file в секундах |
| -Archive | off | добавить к output подпапку yyyy-MM-dd |
| -Cookies | — | cookies.txt для yt-dlp |
| -SponsorBlock | off | удалить SponsorBlock category sponsor |
| -AudioOnly | off | extract MP3 |
| -NoProgress | off | не рисовать PowerShell progress bars |
| -DryRun | off | проверить очередь без media download |
| -Log | — | текстовый log file |
| -EventFile | — | JSONL events для внешнего UI |
| -ResultDir | рядом с input | папка success/error files |
| -Quality | Best | Best / 2160 / 1440 / 1080 / 720 |
| -RateLimit | — | значение для yt-dlp --limit-rate |
| -Container | Auto | Auto / MP4 / MKV / WebM |
| -VideoCodec | Auto | Auto / H264 / VP9 / AV1 preference |
| -WriteSubtitles | off | write regular subtitles |
| -WriteAutoSubtitles | off | write auto-generated subtitles |
| -SubtitleLangs | all,-live_chat | yt-dlp subtitle language selector |
| -EmbedSubtitles | off | write + embed subtitles |
| -EmbedThumbnail | off | write + embed thumbnail |
| -EmbedMetadata | off | embed metadata |
| -EmbedChapters | off | embed chapters |
| -FilenameTemplate | %(title)s [%(id)s].%(ext)s | yt-dlp output template |
| -Retries | 3 | дополнительные transient retries, диапазон 0..10 |
| -RetryDelaySeconds | 5 | стартовая retry delay, диапазон 1..120 |

## Input и output

### -In

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\anime.txt
~~~

Пустые строки игнорируются. URL дедуплицируются.

### -Out

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -Out D:\Videos
~~~

Если параметр не указан, используется текущая рабочая директория.

### -Archive

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -Out D:\Videos -Archive
~~~

Фактический output:

~~~text
D:\Videos\2026-10-07\
~~~

## Параллелизм

### -Threads

Число URL, одновременно выполняемых как PowerShell ThreadJob.

~~~powershell
-Threads 8
~~~

Engine не задаёт отдельный upper ValidateRange для Threads. GUI ограничивает пользовательское значение диапазоном 1..32.

### -Fragments

Передаётся в yt-dlp как <code>-N</code>.

~~~powershell
-Fragments 4
~~~

Engine clamp-ит значение в 1..8.

### -AutoFragments

Перед стартом URL выполняется filesize probe.

| Оценка размера | Fragments |
| ---: | ---: |
| < 100 MB | 1 |
| 100–500 MB | 2 |
| >= 500 MB | 4 |

Если <code>-Fragments</code> был указан явно, он имеет приоритет над <code>-AutoFragments</code>.

## Quality fallback

<code>-Quality 2160</code> не означает exact 2160. Selector использует ограничение <code>height<=2160</code>.

При ошибке Requested format is not available последовательность fallback:

| Requested | Последовательность |
| --- | --- |
| 2160 | 2160 -> 1440 -> 1080 -> 720 |
| 1440 | 1440 -> 1080 -> 720 |
| 1080 | 1080 -> 720 |
| 720 | 720 |
| Best | best |

## Codec preference

Mapping:

| Значение | yt-dlp prefix |
| --- | --- |
| H264 | avc1 |
| VP9 | vp9 |
| AV1 | av01 |

Codec не является strict lock.

Например H264 selector сначала пробует H264 streams, затем допускает generic fallback с тем же ограничением height.

## Container

Для MP4/MKV/WebM добавляется:

~~~text
--merge-output-format <container>
~~~

Для merge/remux обычно требуется FFmpeg.

В AudioOnly video format/container selection не используется.

## Audio only

~~~powershell
-AudioOnly
~~~

Эквивалентная часть yt-dlp:

~~~text
-x --audio-format mp3
~~~

## Subtitles

Пример:

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -WriteSubtitles -WriteAutoSubtitles -SubtitleLangs "ru.*,en.*" -EmbedSubtitles
~~~

<code>-EmbedSubtitles</code> автоматически включает write regular subtitles.

## Metadata

~~~powershell
-EmbedThumbnail -EmbedMetadata -EmbedChapters
~~~

Соответствующие yt-dlp operations:

~~~text
--write-thumbnail --embed-thumbnail
--embed-metadata
--embed-chapters
~~~

Некоторые postprocessors требуют FFmpeg.

## Filename template

~~~powershell
-FilenameTemplate "%(uploader)s - %(title)s.%(ext)s"
~~~

Template объединяется с <code>-Out</code> и передаётся yt-dlp через output template.

## Rate limit

~~~powershell
-RateLimit 5M
~~~

Передаётся как:

~~~text
--limit-rate 5M
~~~

## Cookies

~~~powershell
-Cookies .\cookies.txt
~~~

Файл проверяется до запуска.

## SponsorBlock

~~~powershell
-SponsorBlock
~~~

Сейчас удаляется только category:

~~~text
sponsor
~~~

## Retry

Retry выполняется приложением поверх полного запуска yt-dlp для URL.

~~~powershell
-Retries 3 -RetryDelaySeconds 5
~~~

Default schedule после transient failures:

~~~text
1st failure -> 5s
2nd failure -> 10s
3rd failure -> 20s
~~~

Задержка capped на 60 секунд.

Permanent ошибки вроде unavailable/auth не относятся к transient и не должны retry-иться этим механизмом.

## Machine-readable events

При <code>-EventFile</code> engine дописывает JSON object на строку.

GUI использует эти события для progress/state.

Основные EventType:

~~~text
Title
Progress
File
Merge
Retry
Fallback
Error
Done
~~~

Финальный result object содержит:

~~~text
Slot
Url
Success
Error
FriendlyError
Height
Path
FileSize
~~~

Progress формируется из machine-readable yt-dlp progress-template, а не из regex по обычной строке [download].

## Log

~~~powershell
-Log .\run.log
~~~

Создаёт или очищает log file перед запуском и пишет основные engine messages.

## ResultDir

~~~powershell
-ResultDir D:\Videos\_results
~~~

Меняет расположение success/error txt, но не media output.

## Watch

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -Watch -WatchInterval 3
~~~

После опустошения pending queue engine продолжает проверять input file и добавляет новые неизвестные URL.

Остановка: Ctrl+C.

## DryRun

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\list.txt -DryRun
~~~

Печатает DRY entries и выходит без скачивания media.

DryRun используется smoke tests для проверки CLI mapping.

## Exit codes

- 0 — запуск завершился без failed results;
- 2 — были failed results;
- другие non-zero значения возможны при PowerShell/runtime setup errors.

## Практические примеры

### 1080p MP4 с H264 preference

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\list.txt -Out D:\Videos -Quality 1080 -VideoCodec H264 -Container MP4
~~~

### Архив с metadata и субтитрами

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\list.txt -Out D:\Archive -Archive -Quality Best -Container MKV -WriteSubtitles -WriteAutoSubtitles -SubtitleLangs "ru.*,en.*" -EmbedSubtitles -EmbedMetadata -EmbedChapters -EmbedThumbnail
~~~

### Музыка

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\music.txt -Out D:\Music -AudioOnly -EmbedMetadata -EmbedThumbnail
~~~

### Watch queue

~~~powershell
pwsh -File .\ytdl-manager-v8.ps1 -In .\queue.txt -Watch -Threads 4 -AutoFragments
~~~
