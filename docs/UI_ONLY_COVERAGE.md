# Покрытие перед переходом на единственную UI-версию

Цель перехода — сохранить C#/.NET 8/WPF-приложение, пользовательские данные и
функции интерфейса, затем удалить CMD, headless CLI и резервную реализацию
PowerShell. Скрипты разработчика и тестовые subprocess остаются допустимыми.

Матрица фиксирует проверяемые контракты, а не количество тестовых файлов.
Наличие одинакового названия теста или поиск маркера в исходнике не доказывают
одинаковое поведение. «Покрыто» ниже означает наличие исполняемого сценария в
текущих исходниках; результат конкретного запуска подтверждается отдельно CI.

## Как запускается нативное покрытие

- `dotnet run --project native-tests/VideoDownloader.NativeTests.csproj -c Release`
  запускает C#-проверки моделей, хранения, процессов, scanner и загрузок.
  Набор содержит собственные fixture subprocess, а также офлайн-сценарии с
  настоящим упакованным `yt-dlp.exe`.
- `native-tests/gui-startup.ps1` запускает настоящее окно приложения.
- `VideoDownloader.exe --ui-self-test` запускает взаимодействие с настоящими
  WPF-контролами в отдельном профиле. На этапе выноса тестов этот запуск заменит
  отдельный WPF test host.
- `.github/workflows/native-windows.yml` связывает эти проверки, Python-тесты
  встроенного коллектора, проверку русских профилей и сборку пакета.

WPF, Windows Job Objects и NTFS junction проверяются на Windows. Сборка с
`EnableWindowsTargeting=true` под Linux проверяет компиляцию, но не заменяет
выполнение этих сценариев.

## Матрица сохраняемых контрактов

| Контракт | Исходное покрытие PowerShell | Проверка C# / общая проверка | Состояние перед удалением второй реализации |
| --- | --- | --- | --- |
| Add URL, удаление одной строки, порядок и checkpoint | `gui-review-regression-tests.ps1`, `windows-regression-tests.ps1` | `NativeUiSelfTest.Run`: реальные кнопки Add/Remove и чтение сохранённой очереди | Покрыто |
| `.txt` / `.url`, название без Start и выбора строки | `title-ingress-tests.ps1` | `NativeWindow.TestColdTitleIngress`: настоящие ingress/worker/subprocess/Dispatcher/отрисовка; `TestTitleDisplay`: готовый кеш | Добавлено на этапе 1: четыре точных URL, четыре metadata-only дочерних процесса, ни одной загрузки или смены выделения |
| Заголовок из подписки, точная пара источник+URL, сохранение при ошибке preview | `title-ingress-tests.ps1`, `session-checkpoint-tests.ps1`, `subscription-ui-tests.ps1` | `NativeWindow.TestSubscriptionsUi`, `NativeWindow.TestTitleDisplay`, `NativeUiSelfTest.VerifyStartupRecovery`, `TestColdTitleIngress` | Покрыто, включая настоящий сбой холодного metadata subprocess после немедленного назначения Title из подписки |
| Режим URL/название, tooltip и действия по точному URL | `title-display-tests.ps1` | `NativeWindow.TestTitleDisplay`: настоящие WPF labels, обе таблицы, неизвестный Title и checkpoint | Покрыто |
| Background metadata, кеш и TTL 72 часа | `core-tests.ps1`, `preview-deadline-tests.ps1`, `windows-regression-tests.ps1` | `ReviewRegressionTests.RunAsync`: worker/callback/cache restart, неполная запись, UTC TTL; `LifecycleRegressionTests.Preview` | Покрыто сервисом; холодный ingress проверяется отдельно |
| Thumbnail: fallback, повреждённое изображение, WebP/AVIF, лимиты, отмена, сбой записи кеша | `preview-deadline-tests.ps1` | `LifecycleRegressionTests.PreviewThumbnails`; `NativeWindow.TestTitleDisplay`: реальные WPF decode, малое/портретное изображение | Покрыто; часть конверсионных сценариев использует fake converter, реальные форматы дополнительно прогоняются FFmpeg |
| Clipboard Paste выбирает preview, Watch clipboard сохраняет выделение, priority-prefetch не дублирует workers | `tray-preview-tests.ps1`, `title-display-tests.ps1` | `NativeUiSelfTest.Run`, `NativeWindow.TestTitleDisplay`, `NativeWindow.TestWatchClipboard`, `PreviewSchedulingTests.RunAsync` | Добавлено на этапе 1: настоящий Windows clipboard и UI timer; отдельный gated subprocess-сценарий доказывает порядок first/C/A/B, четыре metadata-only child, отсутствие перекрытия и повторов при повторных priority-запросах |
| Live Add при модальном подтверждении | `gui-review-regression-tests.ps1` | `NativeWindow.TestLiveAddDuringDuplicateDialog`: вложенный Add, окончание/смена сессии | Покрыто реальными modal dispatchers |
| Все решения о повторной загрузке и отмена без изменения очереди | `download-history-tests.ps1` | `NativeWindow.TestDuplicateHistoryChoices`: реальные modal WPF buttons и production planner; `QueueUiSelfTest`: повторный вход; `StartupDownloadRegressionTests`: разрешённые копии | Добавлено на этапе 1: Yes/No/YesAll/NoAll/Cancel/X, откат предшествующего No, оба режима removeRejected, неизменность истории и checkpoint при отмене |
| Русские профили и неизменные persisted IDs | `russian-ui-tests.ps1`, `cross-runtime-contract-tests.ps1` | `NativeUiSelfTest.Run`: все 25 переходов профилей и восстановление старого ID; `LifecycleRegressionTests.Profiles` | Поведение покрыто. Общий PS-скрипт CI пока импортирует удаляемый модуль — переписать на этапе 3 |
| Параллельность, queued/running cancel, late Add, pause/resume и завершение с cleanup | `engine-integration-tests.ps1`, `cancellation-tests.ps1`, `live-queue-tests.ps1` | `native-tests/Program.cs`: настоящий `yt-dlp.exe`; `LifecycleRegressionTests.Cancellation`; `NativeUiSelfTest.Run`: ожидание подтверждения Remove/Exit | Покрыто. Append-only `incoming.txt` — транспорт удаляемого runtime, переносить его как отдельную возможность не требуется |
| Структурированный progress, сумма transfer с retry/reset | `core-tests.ps1`, `windows-regression-tests.ps1` | `ReviewRegressionTests`: настоящий fixture subprocess с `__VD_PROGRESS__`, повторы/сброс счётчиков; `native-tests/Program.cs`: speed units | Покрыты переносимые события; PS JSONL tail/byte cursor не используется в нативном live download transport |
| Аргументы yt-dlp: codec fallback, качество, auto-subs язык, cookies/rate/fragments/metadata | `core-tests.ps1`, `windows-regression-tests.ps1`, `engine-review-regression-tests.ps1`, `smoke.ps1` | `NativeContractTests.ArgumentContracts`: фактические аргументы subprocess через настоящий Coordinator; `StartupDownloadRegressionTests`: merge/remux/расширения | Добавлено на этапе 1: codec preference с generic fallback, quality fallback, sub-langs и остальные опции, публикация/история результата fixture |
| Permanent ошибки не retry; transient HTTP/TLS retry | `core-tests.ps1`, `windows-regression-tests.ps1` | `ReviewRegressionTests.RunAsync`: subprocess permanent/transient/TLS и остановка после решения о retry | Покрыто; тест намеренно не ждёт весь backoff |
| Код 0 без валидного media не является успехом | `engine-review-regression-tests.ps1`, `download-history-tests.ps1` | `ReviewRegressionTests`: no-media/unreported/missing/external media; `StartupDownloadRegressionTests`: неверный контейнер | Покрыто фактической публикацией и проверкой истории |
| Staging, no-overwrite, сохранение OwnedPaths, rollback и повтор cleanup | `cancellation-tests.ps1`, `file-boundary-safety-tests.ps1`, `engine-review-regression-tests.ps1` | `native-tests/Program.cs`, `LifecycleRegressionTests.Cleanup/Rollback/Cancellation`, `StartupDownloadRegressionTests` | Покрыто, включая реальные locked files на Windows и соседние результаты |
| Escape/sibling/root boundaries и NTFS reparse | `file-boundary-safety-tests.ps1` | `native-tests/Program.cs`: traversal, sibling, drive/UNC roots, inner output junction; `LifecycleRegressionTests.SubscriptionFolders`: junction папки подписки; `NativeContractTests.JunctionBoundaries`: root/staging junction | Добавлено на этапе 1: root junction для удаления/публикации, nested staging junction до движения файлов и recursive cleanup; обязательные junction fixtures на Windows |
| Принадлежность дерева worker; аварийный выход владельца не затрагивает независимый процесс | В старом runtime собственные process/cancellation проверки | `StartupDownloadRegressionTests.Crash`, аргументы/окружение/оба pipe через `ManagedChildProcess` | Покрыто на Windows; сохранять и не заменять только тестом штатного Dispose |
| Установка зависимостей: deadline заголовков+тела, cancel, cleanup, retry | `gui-review-regression-tests.ps1` и прежние startup проверки | `DependencyInstallTests.RunAsync` | Покрыто настоящим локальным HTTP fixture |
| Подписки: baseline/скачать всё, все состояния, история обычного URL, уведомление один раз | `subscription-tests.ps1`, `cross-runtime-contract-tests.ps1` | `SubscriptionStoreTests.InitialSnapshotsAndFailures/SuccessfulProcessingAndNotices/CrashRecovery` | Покрыто, включая unattributed global success и сохранённое происхождение после удаления источника |
| Полный обход, RSS только обогащает полный набор, пределы и отмена | `subscription-tests.ps1` | `SubscriptionScannerTests.RunAsync`, `SubscriptionStoreTests.LargeCompleteScan/CancellationBeforeCommit` | Покрыто subprocess и HTTP fixtures; fixture не является проверкой доступности живого сайта |
| Имена папок подписок, закреплённый снимок при enqueue и фактический OutputRoot | `subscription-folder-tests.ps1`, `session-checkpoint-tests.ps1` | `SubscriptionStoreTests.NamesAndLegacyMigration`, `LifecycleRegressionTests.SubscriptionFolders`, `NativeWindow.TestSubscriptionsUi` | Покрыто смешанной очередью, переименованием, restart, archive, повтором и отменой |
| Прогресс проверки: этапы/счётчики, поздние callbacks, cancel/закрытие/reopen | `subscription-ui-tests.ps1` | `SubscriptionScannerTests.RunAsync`, `NativeWindow.TestSubscriptionsUi` | Покрыто; общий progress — реальные завершённые источники, отдельная анимация не выдаётся за процент коллекции |
| Коллектор Pornhub и загрузка встроенного plugin | `pornhub-channel-tests.ps1`, `pornhub-collector-tests.py` | `PornHubChannelTests.RunAsync`, общий `pornhub-collector-tests.py` с закреплённым yt-dlp | Покрыто офлайн; сохранить Python-тесты разработчика и packaged `.py` |
| Сессия/история/подписки/кеш старой версии, Result replay и незавершённая очистка | `session-checkpoint-tests.ps1`, `download-history-tests.ps1`, `cross-runtime-contract-tests.ps1` | `LegacyCompatibilityTests.Run`: статичные `tests/fixtures/legacy/` через production readers; `native-tests/Program.cs`, `ReviewRegressionTests`, `LifecycleRegressionTests.Migration`, `SubscriptionStoreTests` | Добавлено на этапе 1: фиксированные v1 данные, точные URL и source identity, очистка прежнего staging и сохранение исходных fixtures |
| Повреждённые данные сохраняются, восстановление не перезаписывает их через таймер | `session-checkpoint-tests.ps1`, `subscription-tests.ps1` | `NativeUiSelfTest.VerifyStartupRecovery`: реальный modal dialog/save guard; `SubscriptionStoreTests.StrictValidation`; `LifecycleRegressionTests.History` | Покрыто; fixture-набор дополнительно фиксирует реальные форматы |
| Живой legacy worker блокирует recovery/start, PID reuse не блокирует | `session-checkpoint-tests.ps1` | `native-tests/Program.cs`, `ReviewRegressionTests`: настоящий pwsh, locked EventsPath и latched guard | Покрыто; `pwsh` здесь тестовая fixture старого процесса, а не зависимость пользователя |
| Tray tooltip, hide/minimize/restore и Exit | `tray-lifecycle-tests.ps1`, `tray-preview-tests.ps1` | `native-tests/Program.cs`: формат tooltip; `NativeUiSelfTest.Run`: Exit после cleanup; `NativeWindow.TestTrayAndProtocolIngress`: реальные StateChanged, Tray button и menu Open | Добавлено на этапе 1: hide→restore с сохранением очереди, выделения, preview и session |
| Передача аргументов существующему окну и `videodownloader://` | `windows-regression-tests.ps1`: настоящий PS IPC client/server | `NativeWindow.TestTrayAndProtocolIngress`, `NativeIpcFixture`: production named-pipe client в отдельном test EXE процессе, server с настоящими Dispatcher/ReceiveArguments, изолированное имя pipe | Добавлено на этапе 1: полные массивы аргументов, Unicode/spaced filename, повторные/пустые сообщения, ordinal URL, restore скрытого окна. Полный запуск второго GUI через singleton остаётся приёмкой entry point этапа 2 |
| Сборка, версия, ресурсы и состав единственного пакета | `smoke.ps1`, `russian-ui-tests.ps1`, `release-version-tests.ps1`, `build-windows.yml` | `native-windows.yml`: build/publish, WPF smoke, plugin, запрет PS runtime/scripts, версия release и SHA-256 | Нативный пакет проверяется. Перенести нужный smoke в единый workflow и добавить запрет CMD/launcher при этапе 3 |

## Проверки рабочего PR этапа 1

До удаления runtime нужны следующие исполняемые C#-сценарии:

1. Холодное получение Title после всех четырёх путей добавления без Start,
   выбора строки и заранее подготовленного preview cache.
2. Чтение фиксированных legacy session/subscriptions/history/cache/Result и
   cleanup fixtures текущими production-компонентами; исходные fixtures при
   прогоне не изменяются.
3. Остальные решения duplicate dialog и отсутствие изменения очереди при
   Cancel/X, включая уже отклонённые до отмены элементы.
4. Фактически переданные аргументы yt-dlp и обязательные дополнительные NTFS
   junction-сценарии из таблицы.
5. Tray hide/minimize→restore и доставка протокольного URL через нативный
   client/server существующему окну. Это особенно нужно до упрощения entry point.
6. Приоритет clipboard-prefetch без второго metadata worker и повторного
   subprocess; Watch clipboard сохраняет выбранную строку.

Для всех шести пунктов добавлены сценарии, перечисленные в матрице. Так закрыты
выявленные при сопоставлении сохраняемые контракты пользовательских функций.
Порядок и единственность preview-запросов проверяются gates и логом настоящих
дочерних процессов, Watch clipboard — настоящим таймером и буфером Windows.

Границы проверки: IPC выполняется между двумя процессами через production
client/server; этап 2 добавляет `GuiInstanceTests`: два процесса через полный
путь `Program.RunGui`, singleton, доставку ссылок и восстановление того же окна.
Startup smoke запускает релизный EXE с изолированным `VD_DATA_DIRECTORY`.
File symlink отдельно не проверяется: обязательные
NTFS junction покрывают root, вложенный output и staging. Сам этот документ не
снимает условие зелёного Windows CI; успешная компиляция под Linux не является
подтверждением прогона новых WPF/NTFS сценариев.

На этапе 3 также необходимо переписать `russian-ui-tests.ps1`, smoke и workflow,
которые всё ещё читают удаляемые PS-файлы. Проверки самого PS AST, ThreadJob
ingress, CLI DryRun и двусторонней записи C#→PS теряют объект после удаления
версии. Их заменяют проверяемые пользовательские контракты и чтение legacy
fixtures; создавать заново runtime ради запуска этих тестов не требуется.
