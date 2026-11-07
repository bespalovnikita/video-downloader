# Тестирование

Проверяй поведение на той платформе, для которой оно реализовано. Windows-сборка использует WPF и Windows API; Linux cross-build не подтверждает работу окна, singleton, трея или файловых блокировок. JVM unit-тесты Android не подтверждают работу foreground service на устройстве.

[Разработчику](README.md) · [Сборка Windows](windows-build.md) · [Сборка Android](android-build.md) · [Подготовка PR](contributing.md)

## Какие проверки нужны

| Что изменено | Обязательные проверки |
| --- | --- |
| Windows-код, XAML, тесты, скрипты или упаковка | Windows `-Task Test`, зелёный Windows and Android build; для упаковки также `-Task Publish` |
| Android-код или Gradle | JVM/unit, lint, debug/release APK и instrumented APK; тесты на эмуляторе API 35; зелёный Android CI |
| `collection-plugins/` | Python collector, Windows-тест настоящего упакованного yt-dlp и оба CI |
| Формат или `tests/fixtures/portable-queue/` | Чтение общих фикстур обеими реализациями, реальный UI-импорт/экспорт в обе стороны и оба CI |
| Только Markdown | Ссылки, якоря, Mermaid и сверка команд с исходниками; docs-only PR может не запустить платформенные workflow |

После любого push в `main`, включая документацию, общий workflow запускает Windows и Android CI с последующей [публикацией единого релиза](releases.md) при успешных проверках и подписи APK. Android CI вызывается через `workflow_call` для того же SHA.

## Резервная копия профиля Windows

[ProfileBackupTests](../../tests/VideoDownloader.Tests/ProfileBackupTests.cs) создаёт и читает настоящие ZIP и профили: проверяет отсутствие cookie/path/ownership canary, точную идентичность URL и счётчики исключений, контрольные суммы, неизвестные/дублированные поля, traversal/ссылки/типы записей, размеры и степень сжатия. Merge/replace проверяются с текущей очередью, локальными файлами, коллизией ID и дописыванием истории без потери прежних байтов. Ошибка и отмена после настоящей первой atomic publication доказывают rollback; отдельный дочерний процесс принудительно завершается до/после commit point, а новый `AppPersistence` обязан восстановить согласованное состояние до чтения. Отдельно проверяются живой владелец, чужое изменение файла, повреждённый профиль/маркер и Windows read-only/sharing failures.

[ProfileBackupUiTests](../../tests/VideoDownloader.UiTests/ProfileBackupUiTests.cs) использует настоящий WPF-диалог и backend: экспорт, просмотр без записи, отмену подтверждения, применение настроек/подписок/истории, сохранение локальных путей и очереди, lifecycle guards, отмену с rollback и закрытие. Linux original-source harness полезен для ZIP и файловых сценариев, но не заменяет исполнение этих WPF-сценариев и Windows-блокировок в `-Task Test`.

В WPF runner сценарии резервной копии идут сразу после диагностики заданий, до медиа-редактора и waveform. Это позволяет отдельно увидеть результат критического восстановления профиля; все медиа-сценарии также выполняются.

Совместимость backup с расписанием проверяется на исходном `SubscriptionSchedulePlanner` и файловом `SubscriptionStore`: экспорт действительного интервала без разрешения, импорт новых источников с выключенным расписанием, сохранение локального opt-in существующих источников при merge, старые v1 подписки без полей расписания, полный интервал после явного включения. [WPF-сценарии maintenance](../../tests/VideoDownloader.UiTests/ProfileBackupScheduleUiTests.cs) используют реальный scanner worker, его отмену/ожидание и удерживаемый lease. Отдельный реальный lowerCamelCase checkpoint проходит restore→backup→restore без дублирования ключей и потери неизвестных native-полей; ZIP с DOS directory/device/volume/reparse атрибутами отклоняется целиком.

Replace дополнительно проверяется на настоящей сохранённой очереди с owned media: занятый ID другой подписки и queue-only ID резервируются, точный URL сохраняет локальный ID, импортированные материалы/duplicate provenance переназначаются, raw queue остаётся прежней. WPF IPC-сценарий подтверждает replay только непринятого суффикса после sharing failure: уже импортированный файл изменяется на диске и не читается повторно при следующей операции.

Предел хранилища проверяется реальным файлом `subscriptions.json` размером 128 МиБ минус 256 байт: добавление настоящего архива превышает предел сериализованного результата, preview отклоняется без создания transaction, исходные хеши/файлы сохраняются и текущий store остаётся читаемым. Отдельный merge в локальный `Initialized=false` источник переносит проверенный baseline/время успеха, сохраняет локальные решения и более новую попытку, не меняя очередь и owned media.

## Windows: полная проверка

Нужны Windows x64, .NET 8 SDK и PowerShell 7. Команда выполняется **из корня репозитория в обычном интерактивном сеансе Windows**:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Test
```

[build.ps1](../../scripts/build.ps1) последовательно выполняет:

1. [Тесты версионирования релизов](../../scripts/release-version-tests.ps1), включая first-parent [историю Git](../../scripts/release-history-tests.ps1) и [возобновление публикации](../../scripts/release-publication-tests.ps1), затем [smoke](../../scripts/smoke.ps1).
2. Сборку приложения и [проверки русификации](../../scripts/russian-ui-tests.ps1).
3. Исполняемые unit/integration/security/legacy сценарии проекта [VideoDownloader.Tests](../../tests/VideoDownloader.Tests/).
4. Сборку [VideoDownloader.UiTests](../../tests/VideoDownloader.UiTests/), [проверку запуска GUI](../../scripts/gui-startup.ps1) и [настоящие WPF-сценарии](../../scripts/ui-tests.ps1).

Весь дочерний процесс WPF-сценариев ограничен 180 секундами: общий срок учитывает последовательное открытие всех тестовых окон. Каждая операция и assertion сохраняет собственные короткие сроки ожидания. При превышении общего срока скрипт сообщает последний этап и завершает дерево тестового процесса.

Проверки публикации моделируют отставание общего списка после создания draft и чтение конкретного Release по ID, ограниченный повтор подтверждённого `404`, смену ID/владельца/SHA и отказ API без записи. Отдельно проверяются настоящие построение аргументов `gh`, разбор paginated JSON и заголовки запрета кеша с управляемым ответом команды; доступ к GitHub и токен для этих тестов не нужны. Первый выпуск нового коммита в `main` дополнительно подтверждает полный путь создания свежего draft и загрузки файлов в реальном GitHub API.

Для release-тестов нужен полноценный Git checkout с опорным коммитом `e93a78f6b82c04a213ec2eaf9cd93855c84ef691`; shallow checkout без него недостаточен. Их можно запустить отдельно на любой платформе с Git и PowerShell 7:

```powershell
pwsh -NoProfile -File scripts/release-version-tests.ps1
```

`release-history-tests.ps1` проверяет настоящий selector на изолированном локальном Git-репозитории без сети и без изменения исходных refs. Проверки включают переставленный порядок CI, пропущенные версии, повышение/снижение baseline, сбой посередине загрузки, повтор собственного draft, чужие drafts, неполный опубликованный Release и запрет отката Latest.

Тестовые проекты — исполняемые программы со своим runner. Обычный `dotnet test` не заменяет этот запуск. Для отдельной диагностики Windows-сценариев можно выполнить из корня:

```powershell
dotnet run --project .\tests\VideoDownloader.Tests\VideoDownloader.Tests.csproj -c Release
```

Для проверки автономной поставки отдельно выполни:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Publish
```

`Publish` проверяет состав и версию пакета, но сам не запускает тесты. Он пересоздаёт `dist/native`; не храни там загрузки и свои файлы. Тестовые классы и переключатели запуска сценариев не должны попадать в приложение.

После `Test` и `Publish` проверь настоящий автономный helper и перезапуск опубликованного приложения:

```powershell
dotnet run --no-build --configuration Release --project tests/VideoDownloader.Tests -- --published-app-update-smoke dist/native
```

В Windows CI эта команда выполняется отдельным шагом **Verify portable updater handoff and restart** после `Publish`. [AppUpdateBootstrapTests](../../tests/VideoDownloader.Tests/AppUpdateBootstrapTests.cs) копирует опубликованный пакет в изолированную установку, запускает временный EXE без соседних DLL, проверяет ожидание исходного процесса, применение пакета, настоящее WPF-окно после перезапуска и сохранность профиля/посторонних файлов.

## Где искать Windows-регрессии

| Поведение | Основные тесты |
| --- | --- |
| Активные загрузки сверху: живые переходы статусов, пауза, ручная сортировка, выделение и неизменность исходных коллекций | [DownloadsActiveFirstUiTests](../../tests/VideoDownloader.UiTests/DownloadsActiveFirstUiTests.cs), [реальный fast/slow coordinator](../../tests/VideoDownloader.UiTests/QueuePlaybackAndTimerUiTests.cs) |
| Аргументы yt-dlp, обработка процесса, финальный файл и отмена | [NativeContractTests](../../tests/VideoDownloader.Tests/NativeContractTests.cs), [LifecycleRegressionTests](../../tests/VideoDownloader.Tests/LifecycleRegressionTests.cs), [ReviewRegressionTests](../../tests/VideoDownloader.Tests/ReviewRegressionTests.cs) |
| Языки звука, оригинальная дорожка, отсутствие подстановки и настройки UI | [AudioTrackSelectionTests](../../tests/VideoDownloader.Tests/AudioTrackSelectionTests.cs), [AudioTrackUiTests](../../tests/VideoDownloader.UiTests/AudioTrackUiTests.cs) |
| Диапазон времени, каталог реальных форматов, снимок попытки, строгие ID и временные повторы | [DownloadSelectionTests](../../tests/VideoDownloader.Tests/DownloadSelectionTests.cs) |
| Несколько диапазонов одного URL, неизменяемый снимок, полнота batch и реальные отдельные отрывки | [MultipleFragmentTests](../../tests/VideoDownloader.Tests/MultipleFragmentTests.cs) |
| Реальная склейка: порядок цветных кадров и частот звука, stream copy, явная нормализация, языки/обложка/главы, контейнер, отмена и активный срок | [FragmentMergeTests](../../tests/VideoDownloader.Tests/FragmentMergeTests.cs), [дорожки](../../tests/VideoDownloader.Tests/FragmentMergeTests.Tracks.cs), [жизненный цикл](../../tests/VideoDownloader.Tests/FragmentMergeTests.Lifecycle.cs), [отказ и явный повтор](../../tests/VideoDownloader.Tests/FragmentMergeTests.Coordinator.cs), [Opus/Ogg](../../tests/VideoDownloader.Tests/FragmentMergeTests.Opus.cs) |
| Настоящие WPF-переключатели склейки, согласие на перекодирование, локальный черновик, checkpoint и защита Completed | [FragmentMergeUiTests](../../tests/VideoDownloader.UiTests/FragmentMergeUiTests.cs) |
| Список фрагментов, редактирование, порядок, перекрытия и сохранность Completed через WPF | [MultipleFragmentUiTests](../../tests/VideoDownloader.UiTests/MultipleFragmentUiTests.cs) |
| Окно форматов/фрагмента, точный URL, холодные метаданные, восстановление выбора и защита готового результата | [MediaSelectionUiTests](../../tests/VideoDownloader.UiTests/MediaSelectionUiTests.cs) |
| Две карточки кадров фрагмента, задержка ввода, отмена и отбрасывание позднего ответа | [MediaSectionFrameUiTests](../../tests/VideoDownloader.UiTests/MediaSectionFrameUiTests.cs) |
| Дробный FPS без дрейфа, миллисекундная граница длительности и ограничение шага | [FragmentTimelineTests](../../tests/VideoDownloader.Tests/FragmentTimelineTests.cs) |
| 40 реальных однокадровых экспортов, 80 PNG и 10 коротких клипов: decoded barcode IDs при 24, 25, 30, 30000/1001, 60 FPS, начало/keyframe/конец | [FragmentFrameConsistencyTests](../../tests/VideoDownloader.Tests/FragmentFrameConsistencyTests.cs) |
| Настоящая WPF-кнопка +кадр → реальные boundary PNG → Apply → checkpoint → coordinator → decoded export для 0.042–0.083 и 0.083–0.125 с | [FragmentFrameConsistencyUiTests](../../tests/VideoDownloader.UiTests/FragmentFrameConsistencyUiTests.cs) |
| Перетаскивание ручек шкалы, клавиатура, поля/кадры, выбранная длительность и read-only | [FragmentTimelineUiTests](../../tests/VideoDownloader.UiTests/FragmentTimelineUiTests.cs) |
| Библиотека истории: совместимые события, поиск по всему файлу, повреждение/append и ограниченные проверки доступности | [HistoryLibraryTests](../../tests/VideoDownloader.Tests/HistoryLibraryTests.cs), confirmed worker metadata в [NativeContractTests](../../tests/VideoDownloader.Tests/NativeContractTests.cs) |
| Настоящее окно истории: поиск, legacy-пути, явное открытие, недоступный диск, закрытие без изменений данных | [HistoryLibraryUiTests](../../tests/VideoDownloader.UiTests/HistoryLibraryUiTests.cs) |
| Реальный HTTP Range для кадров, области cookies/заголовков, лимиты и отмена соединений | [MediaFrameProxyTests](../../tests/VideoDownloader.Tests/MediaFrameProxyTests.cs) |
| Настоящий поиск кадра FFmpeg, конечная граница, приватный кеш источников и освобождение работника | [MediaFrameServiceTests](../../tests/VideoDownloader.Tests/MediaFrameServiceTests.cs) |
| Кадры HLS/DASH, init/byte ranges, шифрование TS, выбор времени и запрет обхода сетевого лимита | [MediaManifestFrameTests](../../tests/VideoDownloader.Tests/MediaManifestFrameTests.cs) |
| Настоящие короткие progressive/HLS/DASH AV-клипы, раздельный звук, PCM RMS, поиск, лимиты и владение кешем | [MediaClipServiceTests](../../tests/VideoDownloader.Tests/MediaClipServiceTests.cs) |
| Реальный MediaElement, кнопки воспроизведения/паузы/стопа/поиска, события, поздние ответы и освобождение Source | [MediaClipPlaybackUiTests](../../tests/VideoDownloader.UiTests/MediaClipPlaybackUiTests.cs) |
| Реальный FFmpeg PCM: тишина/уровни/12 кГц/противофазное стерео, позднее начало muxed/отдельного звука при нулевом и ненулевом seek, исходное время, аудио без видео, отсутствующий звук, лимиты, joined cancellation и retention клипа | [MediaWaveformServiceTests](../../tests/VideoDownloader.Tests/MediaWaveformServiceTests.cs) |
| Настоящее окно WPF: прокрутка карточки в viewport, кадр отрисовки, снимок целого окна и различимые peak/RMS-пиксели после перерисовки и изменения ширины; нативные mouse move/down/up через HWND с подтверждённой позицией WPF MouseDevice, непрерывный захваченный Thumb, клавиатура, поля границ, metadata refresh, поколения, read-only и смена диапазона. Изолированный PNG окна сохраняется в CI-артефакт Waveform-rendering-diagnostics | [MediaWaveformUiTests](../../tests/VideoDownloader.UiTests/MediaWaveformUiTests.cs) |
| Свободное место, резерв TEMP/результата, неизвестные размеры, лимит OS-запросов и предупреждения | [DiskSpaceTests](../../tests/VideoDownloader.Tests/DiskSpaceTests.cs) |
| Реальные окна Cancel/Continue, смена плана во время проверки и неблокирующее предупреждение активной сессии | [DiskSpaceUiTests](../../tests/VideoDownloader.UiTests/DiskSpaceUiTests.cs) |
| Стабильный Release, SHA-256 ZIP, отмена тела HTTP-ответа, таймаут и ожидание исходного процесса | [AppUpdateClientTests](../../tests/VideoDownloader.Tests/AppUpdateClientTests.cs) |
| Владение файлами пакета, чужие коллизии, ZIP traversal, откат и восстановление после убийства установщика | [AppUpdateInstallerTests](../../tests/VideoDownloader.Tests/AppUpdateInstallerTests.cs) |
| Настоящие кнопки обновления, занятость, повторная проверка очистки перед handoff и отмена при Dispose | [AppUpdateUiTests](../../tests/VideoDownloader.UiTests/AppUpdateUiTests.cs) |
| Свёрнутая панель поиска, освобождение места, индикатор активных фильтров и сохранение условий, статусы/подписки, URL identity, сохранность фрагментов/готовых файлов, видимое выделение и глобальные действия | [QueueFilterUiTests](../../tests/VideoDownloader.UiTests/QueueFilterUiTests.cs) |
| Атомарная замена checkpoint, временные и постоянные Windows-блокировки | [AtomicWriteTests](../../tests/VideoDownloader.Tests/AtomicWriteTests.cs) |
| Чтение старых checkpoint и результатов | [LegacyCompatibilityTests](../../tests/VideoDownloader.Tests/LegacyCompatibilityTests.cs), [legacy fixtures](../../tests/fixtures/legacy/) |
| Источники, окно новинок, полный fallback, RSS и состояния | [SubscriptionScannerTests](../../tests/VideoDownloader.Tests/SubscriptionScannerTests.cs), [SubscriptionStoreTests](../../tests/VideoDownloader.Tests/SubscriptionStoreTests.cs) |
| Превью до Start по всем путям ввода, без готового кеша | [ColdTitleIngressUiTests](../../tests/VideoDownloader.UiTests/ColdTitleIngressUiTests.cs), [SubscriptionPreviewPrefetchUiTests](../../tests/VideoDownloader.UiTests/SubscriptionPreviewPrefetchUiTests.cs), [PreviewSchedulingTests](../../tests/VideoDownloader.Tests/PreviewSchedulingTests.cs) |
| Ограничение сырых stdout/stderr preview, вывод без LF, остановка собственного job и повтор после отказа | [PreviewOutputLimitTests](../../tests/VideoDownloader.Tests/PreviewOutputLimitTests.cs) |
| Галерея и отдельная панель подписки | [GalleryUiTests](../../tests/VideoDownloader.UiTests/GalleryUiTests.cs), [SubscriptionDetailsPreviewUiTests](../../tests/VideoDownloader.UiTests/SubscriptionDetailsPreviewUiTests.cs) |
| Локальный запасной кадр завершённого видео: настоящий FFmpeg/ffprobe, цвет/время, запрет audio-only/attached_pic/manifest, image-only merge/TTL, приоритет уже выполняющегося HTTP-превью, лимиты очереди/pipe и joined PID cancellation | [LocalPosterTests](../../tests/VideoDownloader.Tests/LocalPosterTests.cs) |
| Настоящее WPF: завершение невыбранной строки, автоматическая галерея с проверкой пикселей, stale/maintenance admission, повтор после обновления компонентов, 330 одновременных завершений, Remove/закрытие с ожиданием читателя и перекрытие Clear/Start во время join | [LocalPosterUiTests](../../tests/VideoDownloader.UiTests/LocalPosterUiTests.cs) |
| Интервал, часы, сон/backoff, старый/повреждённый JSON, реальные scan/cancel, WPF включение/очередь/трей/выход | [SubscriptionScheduleTests](../../tests/VideoDownloader.Tests/SubscriptionScheduleTests.cs), [SubscriptionScheduleUiTests](../../tests/VideoDownloader.UiTests/SubscriptionScheduleUiTests.cs) |
| Общее предложение поставить новинки в очередь | [SubscriptionQueueOfferUiTests](../../tests/VideoDownloader.UiTests/SubscriptionQueueOfferUiTests.cs) |
| Компонентный прогресс и журнал | [DownloadProgressPlanTests](../../tests/VideoDownloader.Tests/DownloadProgressPlanTests.cs), [JournalTests](../../tests/VideoDownloader.Tests/JournalTests.cs), [JournalUiTests](../../tests/VideoDownloader.UiTests/JournalUiTests.cs) |
| Диагностика задания: настоящий JSONL, полная идентичность, попытки/параметры координатора, лимиты, приватный ZIP и отмена собственных version-процессов | [JobDiagnosticsTests](../../tests/VideoDownloader.Tests/JobDiagnosticsTests.cs) |
| Окно диагностики: захваченная строка, фильтры, живые события, копирование, экспорт и закрытие | [JobDiagnosticsUiTests](../../tests/VideoDownloader.UiTests/JobDiagnosticsUiTests.cs) |
| Перенос очереди | [PortableQueueTests](../../tests/VideoDownloader.Tests/PortableQueueTests.cs), [PortableQueueUiTests](../../tests/VideoDownloader.UiTests/PortableQueueUiTests.cs) |

Тесты используют изолированные профили, loopback-серверы и фиксированные данные. Отдельные интеграционные сценарии запускают настоящие yt-dlp и медиа-инструменты. Успех на офлайн-фикстурах не подтверждает доступ к живому сайту: такую проверку описывай отдельно с URL, временем, версией движка и результатом.

`FragmentMergeTests` запускает настоящие FFmpeg/FFprobe: синий и красный отрезки с тонами 880/440 Гц проверяются после склейки по цвету кадра и спектру звука в двух точках общей шкалы. Для `.opus` отдельно проверяются настоящий контейнер Ogg, stream copy, отказ несовместимых заголовков без согласия и явная нормализация с сохранением порядка тонов. Проверяются длительность/контейнер/главы, несовместимое разрешение без согласия и с явным fallback, две языковые дорожки, MP3 с обложкой, отказ при девятибитных пикселях, чужой/повторный/некорректный вход, отмена и активный срок реального собственного процесса. Отдельная дочерняя fixture создаёт настоящие несовместимые медиа: полный координатор отказывает без неявной повторной передачи, очищает staging, затем явный Retry с согласием безопасно публикует один новый файл при коллизии. В `MultipleFragmentTests` тот же упакованный yt-dlp скачивает диапазоны с локального HTTP-сервера: склейка публикует один файл, фиксирует исходный выбор, сохраняет прежний файл при Retry и не пополняет полную историю. `FragmentMergeUiTests` проверяет реальные WPF-события и сохранение через `Применить`, включая запрет read-only, отказ одного диапазона/субтитров и сохранность Completed.

[`SubtitleTimelineTests`](../../tests/VideoDownloader.Tests/SubtitleTimelineTests.cs) запускает полный координатор, упакованный yt-dlp и FFmpeg на собственном 20-секундном HLS/WebVTT. Для диапазона 5–7 с проверяются отдельные VTT и фактически извлечённые встроенные cues: реплика 5.5–6.5 с становится 0.5–1.5 с, внешние реплики исключаются, пересекающие границы обрезаются. Проверяются только sidecar, только embedding, обе опции, два отдельных диапазона MKV, языки без совпадающих cues и отсутствующий язык. Реальные SRT/ASS/VTT → WebM проверяют одновременные реплики. Неподдерживаемый формат, unowned path, неправильный manifest, ошибка преобразования и отмена не публикуют новые медиа и не пополняют историю; прежний файл сохраняется. Управляемый собственный дочерний процесс проверяет joined cancellation/timeout и исключение паузы из активного срока. Комбинация удаления SponsorBlock с частичными субтитрами отклоняется до запуска.

`JobDiagnosticsTests` читает фактические `job-events*.jsonl`, моделирует совпадение
короткого `job` при разных полных SHA-256 и проверяет разделение сессий и
invocation. Настоящий координатор с собственным процессом фиксирует прежний
выбор после правки строки и actual quality fallback при профиле `Errors`.
При admission настоящей строки склейки отдельно проверяются неизменяемые
`FragmentOutputMode`, согласие на перекодирование и порядок диапазонов.
Параллельная запись/чтение проверяет общий бюджет текста и типизированных
данных, парную ротацию и счётчик вытесненных событий. Отдельная проверка счётчика:
2 001 событие одновременно на диске и в памяти
означает один пропуск, затем проверяются перезапуск и ротация при лимите 2 МиБ.
Тест раскрывает ZIP и проверяет все пять entries на canary-токены, cookies, argv, абсолютные пути с
пробелами и кириллицей; существующий файл переживает отказ и отмену экспорта.
Фикстура версии — реальный owned child: проверяются нормальный, ошибочный,
слишком большой вывод, таймаут и завершение процесса до возврата отменённой операции.

`DownloadSelectionTests` проверяет не только построение строк: собственный процесс принимает реальные аргументы, а упакованный yt-dlp разрешает точные ID по офлайн-метаданным. Проверяются обновление старого preview-кеша через одного работника, сохранение выбора в checkpoint, отсутствие quality fallback ручного выбора и сохранение диапазона/ID при временном повторе. Реальное медиа с локального HTTP-сервера проходит FFmpeg и публикацию: фрагмент остаётся Completed с собственными файлами, но не пополняет историю полного URL.

`MultipleFragmentTests` передаёт реальному дочернему процессу несколько диапазонов,
меняет план после admission и проверяет сохранённый снимок. Неполные/повторённые
маркеры, неверный контейнер и ошибка после первого отрывка не разрешают публикацию
или повтор всего transfer. Отмена убирает частичный staging. Настоящие yt-dlp,
FFmpeg и ffprobe на loopback-видео подтверждают два различных по цвету отрывка,
отдельные длительности/видео/аудио, пользовательский порядок и точное владение.
Явный повтор с изменённым диапазоном сохраняет байты прежних результатов и
публикует новый набор с отдельным владением и проверенной длительностью.
`MultipleFragmentUiTests` открывает настоящий диалог и проверяет добавление,
правку, перестановку, удаление, перекрытия, синхронизацию выбранной строки со шкалой, отмену подготовки и освобождение проигрываемого предпросмотра при смене диапазона, checkpoint и read-only guards без
запуска очереди или потери прежнего Completed-файла.

Проверки обновления используют изолированные пакеты и HTTP-ответы. Установщик должен сохранить посторонние FFmpeg/cookies/загрузки, отклонить понижение версии и пакет без манифеста, восстановить прежние байты после ошибки и сохранить backup при отказе отката. Дочерний установщик действительно завершается принудительно в трёх точках транзакции; повторно открытый указатель и backup должны восстановить прежние байты. Настоящие Windows sharing locks и NTFS junction проверяются на Windows; запуск переносимого подмножества на Linux их не заменяет. После изменения упаковки отдельно выполняй `-Task Publish` и smoke опубликованного helper, включая проверку `windows-package.json` и хешей файлов.

`AppUpdateUiTests` нажимает реальные WPF-кнопки с управляемым сервисом обновления и наблюдаемым handoff: он не устанавливает Release поверх тестового приложения. Сетевые проверки, замена файлов и rollback покрываются отдельными клиентскими и установочными сценариями. `MediaSelectionUiTests` проверяет отображаемые ячейки реальных метаданных, ошибки диапазона, URL-область открытого окна, сохранение checkpoint и неизменность ранее опубликованного результата.

`MediaSectionFrameUiTests` использует настоящее WPF-окно и управляемый источник
PNG: проверяет пиксели двух `Image`, подписи времени, независимые поля,
задержку 350 мс, отмену и ответы в обратном порядке, смену выбранного формата,
пустой/непригодный ввод, коллекцию, read-only и закрытие. Ошибка кадра должна
оставлять доступным применение пригодного диапазона. Этот сценарий проверяет
отображение и владение UI-запросами; поиск кадра FFmpeg и HTTP-передачу нужно
подтверждать отдельной интеграцией.

`MediaFrameProxyTests` запускает настоящие loopback HTTP-серверы: сверяет Range и
байты ответа, домен/путь/Secure cookies, очистку приватных заголовков при смене
origin, предел передачи и запросов. Сервер, игнорирующий ненулевой Range, должен
быть отклонён до чтения тела. Отмена закрывает соединения и дожидается всех
обработчиков, сохраняя возможность следующего запроса.

`MediaFrameServiceTests` создаёт настоящее красно-синее видео FFmpeg, получает
кадры через HTTP Range и проверяет их пиксели, включая конец на длительности.
Metadata-фикстура проверяет общий процесс, смену cookies и отсутствие подписанных
адресов/секретов в дисковом кеше. Интеграция также проверяет замаскированный
манифест без вторичных запросов, отмену/таймаут реального потока и успешный
следующий кадр на том же работнике без записи истории.

`MediaManifestFrameTests` создаёт настоящее видео со звуком и разными цветами,
упаковывает его в HLS TS/fMP4/AES-128 и DASH MP4/WebM. Настоящий FFmpeg
проверяет красный/синий/конечный кадр. Дополнительно проверяются master,
SegmentTimeline, duration/Number, SegmentList/init byte ranges, SegmentBase,
переход на другой origin с cookies без приватных заголовков, общий byte cap,
отмена зависшего сегмента, запрет file-URI/XXE/live и замаскированного
вложенного плейлиста без вторичного запроса. Отдельный проход использует
настоящий упакованный yt-dlp для HLS/DASH, а не только подготовленный DTO.

## Коллектор и общие фикстуры

Python требуется только разработчику для отдельного теста коллектора, не пользователю приложения. Из корня репозитория:

```powershell
python -m pip install yt-dlp==2026.08.19
python tests/pornhub-collector-tests.py
```

Версия соответствует [Windows CI](../../.github/workflows/native-windows.yml). При обновлении сверяй её с совместимостью упакованного `yt-dlp.exe`; Android extractor закреплён отдельно в [yt-dlp.properties](../../android/yt-dlp.properties).

Общий контракт переноса находится в [tests/fixtures/portable-queue/README.md](../../tests/fixtures/portable-queue/README.md). C# и Kotlin должны одинаково читать URL с учётом регистра, Unicode-названия, порядок, допустимые и недопустимые URL, повреждённые и слишком большие пакеты. Сравнивай распакованные данные: побайтовое совпадение GZIP от разных реализаций не требуется. Импорт должен оставлять новые строки до ручного Start.

## Android: unit, lint и сборка

После [настройки JDK 17 и SDK 35](android-build.md) выполняй команды **из каталога `android/`**. В Windows замени `./gradlew` на `.\gradlew.bat`:

```bash
./gradlew :core:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug :app:assembleDebugAndroidTest
```

[`core`-тесты](../../android/core/src/test/) проверяют правила и хранение без Android-устройства, включая [PublicationCleanupTest](../../android/core/src/test/kotlin/ru/bespalov/video/core/PublicationCleanupTest.kt) с точным доказательством успешной публикации. [`App` unit-тесты](../../android/app/src/test/) проверяют приватные пути, аргументы движка и runtime: [BoundedProcessRunnerTest](../../android/app/src/test/java/ru/bespalov/videodownloader/engine/BoundedProcessRunnerTest.kt) — общий бюджет потоков и строки callback до накопления, [RuntimeUpdaterTest](../../android/app/src/test/java/ru/bespalov/videodownloader/engine/RuntimeUpdaterTest.kt) — HTTP-отмену/таймауты, хеш, ZIP/версию и сохранение старого архива. Lint и сборка выявляют платформенные ошибки, но не доказывают поведение установленного APK.

## Android: устройство и Home

`AudioTrackInstrumentedTest` выполняет реальные запросы установленного yt-dlp в режиме `--simulate --load-info-json` на общих [фикстурах аудиодорожек](../../tests/fixtures/audio-tracks/CONTRACT.md), проверяет отсутствие подстановки другого языка и сохранение настроек через Compose. Эта проверка выбора формата не скачивает медиа и не заменяет проверку фоновой загрузки ниже.

Запусти эмулятор **API 35, x86_64**, дождись загрузки и выполни из `android/`:

```bash
adb devices
./gradlew :app:connectedDebugAndroidTest
```

Сценарии копирования читают настоящий `ClipboardManager` приложения после готовности окна к вводу. При таймауте отчёт содержит состояние фокуса, число элементов, MIME-типы, длины и хеши буфера; его текст не записывается в отчёт. Перед системной клавишей Back тест должен дождаться отображения диалога и завершения Compose-кадра; готовность модели сама по себе не означает готовность окна к вводу.

[Инструментальные тесты](../../android/app/src/androidTest/) запускают установленное приложение и Android-рантайм. Сценарий `foregroundDownloadCompletesAfterPressingHome` скачивает реальное видео с медленного локального HTTP-сервера, нажимает Home, ждёт завершения, проверяет читаемость опубликованного файла и историю. Это проверяет передачу данных после сворачивания, а не только наличие записи службы в manifest.

Дополнительно `LiveQueueInstrumentedTest` проверяет настоящий ручной Add в активную и приостановленную сессию, отложенные переносимую очередь и подписку, Home/поворот, отмену одного задания, сохранность прежнего и постороннего файла. Отдельный сценарий подтверждает историю и проверяет Stop во время вопроса: позднее «Да» не начинает новую сессию. Чистые `ManualQueueAdmissionTest` проверяют повторную сверку ID/URL/истории и границы владения.

[PublicationCleanupInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/PublicationCleanupInstrumentedTest.kt) проверяет настоящие MediaStore MP4, сохранённый checkpoint после неудачной приватной очистки, флажок удаления в UI и retry с сохранением медиа/истории. [SafCleanupInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/SafCleanupInstrumentedTest.kt) использует Binder DocumentsProvider из test APK: grant revoke/regrant, неизвестный доступ, отказ удаления и повтор по сохранённым URI. Обычная успешная проверка JVM не заменяет эти device-сценарии.

[PrimaryOutputInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/PrimaryOutputInstrumentedTest.kt) проводит основной URI через настоящую native-загрузку с субтитрами впереди медиа, MediaStore, повторное чтение `state.json` и действия открытия в Compose. SAF-сценарий использует отдельный test-only DocumentsProvider с непрозрачными ID, проверяет явный выбор среди нескольких медиа и доступность субтитров через список файлов. Заблокированные Binder-запросы подтверждают, что таймаут ожидания не освобождает два занятых слота до возврата provider. [PrimaryOutputTest](../../android/core/src/test/kotlin/ru/bespalov/video/core/PrimaryOutputTest.kt) проверяет порядок файлов, recovery, старые снимки и удаление capabilities при импорте; это JVM-проверка, не устройство.

[RuntimeProcessInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/RuntimeProcessInstrumentedTest.kt) проверяет настоящие Python-процессы из APK: stdout/stderr без LF, сохранение UTF-8/CRLF, отмену группы с уже завершившимся родителем, сохранение независимой группы и отмену HTTP-обновления до освобождения engine admission.

Также проверяются runtime `--version` и миграция, перенос очереди и открытие результата. Тестовый [offline-sample.mp4](../../android/app/src/androidTest/assets/offline-sample.mp4) и [описание фикстуры](../../android/app/src/androidTest/assets/README.md) находятся в репозитории. Проверка на собственном телефоне полезна дополнительно: [ручной сценарий Home](../user/android.md#background-check). Она не заменяет воспроизводимую конфигурацию CI.

Отчёты относительно `android/`:

| Проверка | Путь |
| --- | --- |
| JVM core | `core/build/reports/tests/` |
| App unit | `app/build/reports/tests/` |
| Instrumented tests | `app/build/reports/androidTests/`, `app/build/outputs/androidTest-results/` |
| Lint | `app/build/reports/lint-results-debug.html`, `app/build/reports/lint-results-release.html` |

## CI и интерпретация результата

| Workflow | Когда запускается | Что сохраняет |
| --- | --- | --- |
| [Windows and Android build](../../.github/workflows/native-windows.yml) | PR по Windows/Android/общим путям, любой push в `main`, ручной запуск | Артефакты обеих платформ, `Subscription-progress-preview`; после успешных проверок и подписи на push или ручном запуске в `main` — общий Release |
| [Android Kotlin build](../../.github/workflows/android.yml) | `workflow_call` из общего CI, отдельный ручной запуск | `VideoDownloader-Android-debug` после успешных device-тестов; `Android-test-reports` независимо от итога, если отчёты созданы |

Пропущенный `release` job в PR и ручном общем workflow — штатное поведение: публикация разрешена только на push в `main`. Отсутствие CI у Markdown PR возможно из-за `paths`, а не из-за сбоя сборки. Перед merge документации проверь каждый изменённый относительный путь, переход к заголовку и отрисовку всех изменённых Mermaid-схем.

`MediaClipServiceTests` декодирует фактический звук FFmpeg в PCM и проверяет длительность и ненулевой RMS; это подтверждает аудиодорожку подготовленного клипа. WPF-тесты отдельно проверяют настоящий MediaElement и реальные контролы с управляемыми событиями транспорта. Наличие физического аудиоустройства или слышимость динамиков Windows CI эти проверки не подтверждают.
