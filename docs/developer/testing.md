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

## Где искать Windows-регрессии

| Поведение | Основные тесты |
| --- | --- |
| Аргументы yt-dlp, обработка процесса, финальный файл и отмена | [NativeContractTests](../../tests/VideoDownloader.Tests/NativeContractTests.cs), [LifecycleRegressionTests](../../tests/VideoDownloader.Tests/LifecycleRegressionTests.cs), [ReviewRegressionTests](../../tests/VideoDownloader.Tests/ReviewRegressionTests.cs) |
| Языки звука, оригинальная дорожка, отсутствие подстановки и настройки UI | [AudioTrackSelectionTests](../../tests/VideoDownloader.Tests/AudioTrackSelectionTests.cs), [AudioTrackUiTests](../../tests/VideoDownloader.UiTests/AudioTrackUiTests.cs) |
| Атомарная замена checkpoint, временные и постоянные Windows-блокировки | [AtomicWriteTests](../../tests/VideoDownloader.Tests/AtomicWriteTests.cs) |
| Чтение старых checkpoint и результатов | [LegacyCompatibilityTests](../../tests/VideoDownloader.Tests/LegacyCompatibilityTests.cs), [legacy fixtures](../../tests/fixtures/legacy/) |
| Источники, окно новинок, полный fallback, RSS и состояния | [SubscriptionScannerTests](../../tests/VideoDownloader.Tests/SubscriptionScannerTests.cs), [SubscriptionStoreTests](../../tests/VideoDownloader.Tests/SubscriptionStoreTests.cs) |
| Превью до Start по всем путям ввода, без готового кеша | [ColdTitleIngressUiTests](../../tests/VideoDownloader.UiTests/ColdTitleIngressUiTests.cs), [SubscriptionPreviewPrefetchUiTests](../../tests/VideoDownloader.UiTests/SubscriptionPreviewPrefetchUiTests.cs), [PreviewSchedulingTests](../../tests/VideoDownloader.Tests/PreviewSchedulingTests.cs) |
| Ограничение сырых stdout/stderr preview, вывод без LF, остановка собственного job и повтор после отказа | [PreviewOutputLimitTests](../../tests/VideoDownloader.Tests/PreviewOutputLimitTests.cs) |
| Галерея и отдельная панель подписки | [GalleryUiTests](../../tests/VideoDownloader.UiTests/GalleryUiTests.cs), [SubscriptionDetailsPreviewUiTests](../../tests/VideoDownloader.UiTests/SubscriptionDetailsPreviewUiTests.cs) |
| Общее предложение поставить новинки в очередь | [SubscriptionQueueOfferUiTests](../../tests/VideoDownloader.UiTests/SubscriptionQueueOfferUiTests.cs) |
| Компонентный прогресс и журнал | [DownloadProgressPlanTests](../../tests/VideoDownloader.Tests/DownloadProgressPlanTests.cs), [JournalTests](../../tests/VideoDownloader.Tests/JournalTests.cs), [JournalUiTests](../../tests/VideoDownloader.UiTests/JournalUiTests.cs) |
| Перенос очереди | [PortableQueueTests](../../tests/VideoDownloader.Tests/PortableQueueTests.cs), [PortableQueueUiTests](../../tests/VideoDownloader.UiTests/PortableQueueUiTests.cs) |

Тесты используют изолированные профили, loopback-серверы и фиксированные данные. Отдельные интеграционные сценарии запускают настоящие yt-dlp и медиа-инструменты. Успех на офлайн-фикстурах не подтверждает доступ к живому сайту: такую проверку описывай отдельно с URL, временем, версией движка и результатом.

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

[Инструментальные тесты](../../android/app/src/androidTest/) запускают установленное приложение и Android-рантайм. Сценарий `foregroundDownloadCompletesAfterPressingHome` скачивает реальное видео с медленного локального HTTP-сервера, нажимает Home, ждёт завершения, проверяет читаемость опубликованного файла и историю. Это проверяет передачу данных после сворачивания, а не только наличие записи службы в manifest.

Дополнительно `LiveQueueInstrumentedTest` проверяет настоящий ручной Add в активную и приостановленную сессию, отложенные переносимую очередь и подписку, Home/поворот, отмену одного задания, сохранность прежнего и постороннего файла. Отдельный сценарий подтверждает историю и проверяет Stop во время вопроса: позднее «Да» не начинает новую сессию. Чистые `ManualQueueAdmissionTest` проверяют повторную сверку ID/URL/истории и границы владения.

[PublicationCleanupInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/PublicationCleanupInstrumentedTest.kt) проверяет настоящие MediaStore MP4, сохранённый checkpoint после неудачной приватной очистки, флажок удаления в UI и retry с сохранением медиа/истории. [SafCleanupInstrumentedTest](../../android/app/src/androidTest/java/ru/bespalov/videodownloader/SafCleanupInstrumentedTest.kt) использует Binder DocumentsProvider из test APK: grant revoke/regrant, неизвестный доступ, отказ удаления и повтор по сохранённым URI. Обычная успешная проверка JVM не заменяет эти device-сценарии.

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
| [Windows and Android build](../../.github/workflows/native-windows.yml) | PR по Windows/Android/общим путям, любой push в `main`, ручной запуск | Артефакты обеих платформ, `Subscription-progress-preview`; после успешных проверок и подписи на push в `main` — общий Release |
| [Android Kotlin build](../../.github/workflows/android.yml) | `workflow_call` из общего CI, отдельный ручной запуск | `VideoDownloader-Android-debug` после успешных device-тестов; `Android-test-reports` независимо от итога, если отчёты созданы |

Пропущенный `release` job в PR и ручном общем workflow — штатное поведение: публикация разрешена только на push в `main`. Отсутствие CI у Markdown PR возможно из-за `paths`, а не из-за сбоя сборки. Перед merge документации проверь каждый изменённый относительный путь, переход к заголовку и отрисовку всех изменённых Mermaid-схем.
