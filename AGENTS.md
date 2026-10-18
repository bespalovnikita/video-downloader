# AGENTS.md

Инструкции для coding agents, работающих с этим репозиторием.

## Цель проекта

Video Downloader — Windows-приложение поверх yt-dlp:

- WPF UI: <code>ui/MainWindow.xaml</code>;
- GUI orchestration: <code>ytdl-manager-gui.ps1</code>;
- download engine: <code>ytdl-manager-v8.ps1</code>;
- общие helper-функции: <code>lib/</code>;
- launcher: небольшой .NET 8 WinExe, который запускает GUI через PowerShell 7.

Не считай VideoDownloader.exe полноценной заменой PowerShell runtime: текущая архитектура требует установленный <code>pwsh.exe</code>.

## Архитектурные инварианты

### 1. Progress должен оставаться machine-readable

Не возвращай парсинг обычных строк <code>[download] ...</code> как основной механизм прогресса.

Engine использует:

- <code>--progress-template</code> с <code>__VD_PROGRESS__</code>;
- <code>--print before_dl:__VD_TITLE__:</code>;
- <code>--print after_move:__VD_FILE__:</code>.

Structured parser находится в <code>lib/VideoDownloader.Core.psm1</code>.

Human-readable stdout допустимо анализировать только для вспомогательных событий вроде merge/error diagnostics.

`--progress` обязателен вместе с `--print`. EventFile читается по завершённым строкам с byte cursor; недописанный UTF-8/JSONL хвост нельзя пропускать. UI показывает progress/скорость/объём/ETA в обеих таблицах. Result не должен повторно прибавлять размер файла к уже учтённому session transfer volume. Все конкретные media paths проверяй через `-LiteralPath`.

### 2. Preview prefetch-ится заранее

URL должен попадать в preview-prefetch queue сразу после добавления в Queue.

Контракт:

- один независимый background preview worker;
- Clipboard URLs во время загрузки получают приоритет в очереди prefetch, а ручной Paste сразу показывает выбранную ссылку в preview панели; Watch clipboard не крадёт текущее выделение;
- metadata + thumbnail запрашиваются заранее;
- клик по строке читает cache;
- если preview ещё не готов, клик только повышает приоритет URL;
- клик не создаёт второй тяжёлый metadata request;
- persistent cache: <code>%LOCALAPPDATA%\VideoDownloader\preview-cache</code>;
- TTL: **72 часа**;
- cache key: SHA-256 URL.

Fetch/cache логика находится в <code>lib/VideoDownloader.PreviewCache.psm1</code>.

### 3. Queue selectable во время download session

Во время активной загрузки пользователь должен иметь возможность:

- выбирать строки;
- смотреть preview;
- open/copy URL.

Во время активной сессии **Remove разрешён**: пользователь может убрать URL из очереди, а для уже начатого/законченного задания — после подтверждения отменить конкретный native yt-dlp и удалить его собственные файлы. Другая очередь и процессы остаются активными.

Reorder / drag/drop / download selected остаются заблокированными, добавление через Paste, + Add или Watch clipboard по-прежнему разрешено.

- На каждую удаляемую активную ссылку GUI атомарно выставляет маркер в `-CancelDir`; engine проверяет его до запуска, пока читает stdout/stderr yt-dlp и перед публикацией результата.
- Каждый cancellable job пишет файлы в **отдельную staging-директорию**. Отменённый URL теряет только собственные частичные файлы; успешные результаты публикуются с `OwnedPaths`.
- Удаление готового медиа использует **точный путь из результата/чекпоинта**, без wildcard, с проверкой принадлежности каталогу вывода. Любой симлинк/junction/reparse point в пути к root, его родителях или вложенных каталогах означает отказ операции.
- Публикация из staging запрещена при существующем целевом файле (иначе чужой файл можно ошибочно принять за скачанный и удалить при Remove). Предварительно проверяется весь batch и ссылки в staging. Никакой overwrite чужих файлов.
- Независимый security suite `file-boundary-safety-tests.ps1` проверяет реальные NTFS junction, обход `../`, соседние каталоги, preexisting и сохраняет контрольные файлы снаружи неизменными.
- Оговорка безопасности: проверки выполняются непосредственно перед файловой операцией; при намеренной параллельной подмене путей остаётся класс TOCTOU. Если нужна полная защита от враждебной подмены directory handles, необходима отдельно реализованная handle-based Windows FS layer.
- Результат отмены — `Cancelled`, это не `Failed` и не `Completed`; в постоянную историю успехов он не пишется.
- Event для уже удалённого URL не должен снова добавлять строку в GUI.
- Любой недостаток прав при удалении — явное предупреждение, строка остаётся для повторной попытки.
- Визуальное подтверждение требуется перед удалением начатых или завершённых файлов.

- Перед передачей нового URL проверяется общая история успехов; Cancel не меняет очередь.
- Новые ссылки отправляются в отдельный append-only `incoming.txt` через `Add-VdLiveQueueUrls` с межпроцессной синхронизацией.
- Engine читает `-LiveQueueFile` и dedup по точному URL (без перезаписи рабочего `queue.txt`).
- Добавление после завершения движка сохраняется как `Queued` до следующего Start.
- Статус `Running` для новой ссылки выставляется только после `Started` event от engine.
- Новые URL сохраняются в session checkpoint, а история успешных скачиваний продолжает пополняться из engine.

Причина: engine по-прежнему использует snapshot собственного рабочего `queue.txt`; live ingress отделён от него.

### 4. Codec — preference, не strict lock

Для H264/VP9/AV1 format selector сначала пытается codec-specific вариант, затем generic fallback в рамках выбранного ограничения quality.

Не документируй и не реализуй это как строгий codec lock без отдельного изменения требований.

### 5. Retry только transient ошибок

Классификация находится в <code>VideoDownloader.Core.psm1</code>.

Не retry permanent/auth/unavailable ошибки бессмысленно. По умолчанию внешний backoff:

~~~text
5s, 10s, 20s
~~~

Максимальная задержка — 60 секунд.

## Session checkpoint

- Очередь, конечные статусы и параметры сохраняются атомарно в `%LOCALAPPDATA%\\VideoDownloader\\session.json`, не в `%TEMP%`.
- Ни в коем случае не перезапускай успешно завершённые URL обычной кнопкой Start; ручной Retry допускается.
- При запуске восстановление требует явного подтверждения пользователя и никогда не начинает загрузку автоматически.
- Не перезапускай задания, пока ещё жив старый engine, сопоставляй PID с StartTime, чтобы защититься от PID reuse.
- При изменении checkpoint logic дополняй `tests/session-checkpoint-tests.ps1`.

## Global successful-download history

- Все успешные URL из Windows GUI и CLI engine записываются append-only в `%LOCALAPPDATA%\\VideoDownloader\\successful-downloads.jsonl`.
- `CompletedAtUtc` хранит время успеха; в диалог повтора передаётся последняя дата, а не дата импорта. Legacy-запись без времени остаётся с неизвестной датой.
- История независима от session checkpoint и удаления Queue: **никогда не очищай её** при завершении сеанса.
- Сравнение URL точное (ordinal), без сомнительной нормализации параметров.
- Перед **каждым** download start/explicit retry проверяй кандидатов в истории и применяй Yes/No/YesAll/NoAll. No удаляет соответствующий URL из Queue. Cancel/X не запускает процесс и не меняет очередь.
- Не записывай failed/dry-run URL как успешные; UI проверяет историю *до* запуска engine.
- Файл историй может пополняться из нескольких CLI процессов. Используй межпроцессную синхронизацию и не проглатывай ошибки при чтении повреждённой истории.
- При изменениях запускай `tests/download-history-tests.ps1` вместе с остальным CI.

## Tray status / state colors

- Tooltip NotifyIcon.Text обновляется из session stats, содержит OK/Err/Left и скорость при активном скачивании; длина ограничена 63 символами.
- Queue и Downloads статусы раскрашиваются WPF DataTriggers по стабильному `Status`, а не по тексту ошибок или процентам.
- Цвета доступны в тёмной теме: completed=green, queued=grey, running=cyan, interrupted=amber, failed=red.

## WPF

- основной UI framework — WPF/XAML;
- WinForms используется точечно для tray и стандартных dialogs;
- новые controls должны корректно выглядеть в dark theme;
- не полагайся на системные default templates для ComboBox/DataGrid, если они конфликтуют с dark UI;
- Queue/Downloads DataGrid read-only;
- при изменении XAML обновляй smoke markers, если они отражают проверяемый контракт.

## PowerShell

Минимальная версия — PowerShell 7.

Правила:

- для build/tests используй <code>$ErrorActionPreference = "Stop"</code>;
- делай явный path resolution и проверки required files;
- не блокируй UI thread сетевыми операциями;
- тяжёлые yt-dlp/dependency операции выполняй вне UI thread;
- dispose process/stream/http objects;
- если после interpolated variable сразу идёт двоеточие, используй braces вокруг имени переменной.

## Download engine

Основной файл: <code>ytdl-manager-v8.ps1</code>.

Важные свойства:

- URL parallelism через Start-ThreadJob;
- ThrottleLimit соответствует Threads; NotStarted нельзя удалять как завершённый job. Незавершённый запуск возвращает ненулевой exit code;
- fragment concurrency через yt-dlp <code>-N</code>;
- input file — рабочая очередь и переписывается по мере завершения URL;
- success/error files относятся к текущему запуску;
- EventFile — JSONL transport для GUI;
- DryRun не должен скачивать media.

При добавлении нового engine parameter:

1. добавь parameter в engine;
2. прокинь его из GUI, если это GUI feature;
3. добавь DryRun/smoke coverage;
4. обнови <code>ytdl-manager-v8-manual.md</code>;
5. при необходимости обнови README/GUI.md.

## Dependencies

- yt-dlp.exe хранится рядом с приложением;
- FFmpeg/ffprobe могут лежать рядом с приложением или находиться в PATH;
- GUI умеет скачать FFmpeg с gyan.dev;
- не утверждай, что FFmpeg bundled, если это не было отдельно изменено;
- engine и preview используют <code>--impersonate chrome</code>, что зависит от возможностей текущей сборки yt-dlp.

## Launcher и packaging

<code>launcher/VideoDownloader.csproj</code>:

- .NET 8;
- Windows x64;
- self-contained;
- single-file launcher.

Но portable package всё равно содержит PowerShell scripts, XAML, lib и yt-dlp рядом с launcher.

<code>build-exe.ps1</code> создаёт <code>dist/VideoDownloader-portable.zip</code>.

Не превращай packaging в «single EXE» через скрытую временную распаковку, если пользователь явно не попросил именно такую архитектуру.

## Тесты перед PR/merge

Минимум:

~~~powershell
pwsh -File .\tests\core-tests.ps1
pwsh -File .\tests\smoke.ps1
pwsh -STA -File .\tests\tray-lifecycle-tests.ps1
pwsh -File .\tests\session-checkpoint-tests.ps1
pwsh -File .\tests\download-history-tests.ps1
pwsh -File .\tests\live-queue-tests.ps1
pwsh -File .\tests\cancellation-tests.ps1
pwsh -File .\tests\file-boundary-safety-tests.ps1
pwsh -File .\tests\tray-preview-tests.ps1
pwsh -STA -File .\tests\windows-regression-tests.ps1
pwsh -File .\tests\engine-integration-tests.ps1
~~~

Если менялась упаковка:

~~~powershell
pwsh -File .\build-exe.ps1
~~~

GitHub Actions должен быть зелёным перед merge.

Smoke проверяет в том числе:

- PowerShell syntax;
- XAML как XML;
- ключевые WPF controls/styles;
- engine feature markers;
- preview prefetch/cache contract;
- CLI DryRun.

## Git workflow

Для нетривиальных изменений:

1. branch от актуального main;
2. небольшие осмысленные commits;
3. PR;
4. дождаться CI;
5. merge после зелёных проверок.

Не коммить build artifacts вроде dist, если задача явно не требует этого.

## Документация

При изменении поведения синхронизируй:

- <code>README.md</code> — overview и quick start;
- <code>GUI.md</code> — GUI behavior;
- <code>ytdl-manager-v8-manual.md</code> — CLI contract;
- <code>AGENTS.md</code> — архитектурные rules/invariants.

Документация должна описывать текущую реализацию и реальные ограничения, а не планируемые функции.
