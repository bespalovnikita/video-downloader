# AGENTS.md

Обязательные инструкции для агентов, работающих с Video Downloader. Корневой файл задаёт маршрут чтения; подробные контракты находятся в `docs/agents/` и имеют ту же обязательную силу.

## Сначала прочитай

Для **любой задачи** прочитай полностью:

1. [CORE.md](docs/agents/CORE.md) — общие инварианты, workflow, проверки и политика релизов.
2. [SHARED.md](docs/agents/SHARED.md) — межплатформенные границы и формат переноса очереди.

Затем прочитай документы по всем затронутым областям:

| Пути или тема | Обязательное чтение |
| --- | --- |
| `src/VideoDownloader/`, `tests/VideoDownloader.Tests/`, `tests/VideoDownloader.UiTests/`, `scripts/`, Windows CI/пакет | [WINDOWS.md](docs/agents/WINDOWS.md) |
| `android/`, `.github/workflows/android.yml`, APK, Android runtime/service/данные | [ANDROID.md](docs/agents/ANDROID.md) |
| `collection-plugins/`, `tests/fixtures/portable-queue/`, перенос данных, изменения обеих платформ | [WINDOWS.md](docs/agents/WINDOWS.md) и [ANDROID.md](docs/agents/ANDROID.md) |
| Любые Markdown-правки или изменение пользовательского поведения | [DOCUMENTATION.md](docs/agents/DOCUMENTATION.md) |

Если область задачи расширилась, дочитай соответствующий контракт до изменения этой области. Общая карта — [документация для агентов](docs/agents/README.md).

## Проект и границы

Video Downloader имеет два пользовательских приложения: Windows на C# / .NET 8 / WPF и Android на Kotlin / Compose. Пользовательского CLI, CMD-launcher и резервного GUI нет. Платформенные проекты и локальные хранилища разделены.

- Windows: [`src/VideoDownloader/VideoDownloader.csproj`](src/VideoDownloader/VideoDownloader.csproj); UI — `UI/Views/`, владельцы UI-состояния — `UI/Controllers/`, доменная логика — `Downloads/`, `Preview/`, `Subscriptions/`, `Persistence/`, `Compatibility/`.
- Android: [`android/`](android/), `:core` — JVM-модели и правила, `:app` — Compose, runtime, служба и публикация.
- Формат обмена очередью: [`tests/fixtures/portable-queue/README.md`](tests/fixtures/portable-queue/README.md). Это не формат backup и не перенос локального владения файлами.
- Архитектура и жизненные циклы: [руководство разработчика](docs/developer/architecture.md).

## Инварианты, которые нельзя потерять

Это указатель на подробные требования, а не замена обязательному чтению:

- Точный URL — основа идентичности; материал подписки определяется парой `SubscriptionId` + URL. Произвольная нормализация или media-ID dedup несовместимы с контрактом.
- Успех — проверенное медиа после успешной публикации. Код 0, обнаружение или очередь сами по себе не пополняют историю.
- Публикация/отмена/очистка согласованы; удаление и rollback затрагивают только подтверждённые собственные файлы. Импортированный путь не доказывает владение.
- Подписки проверяются вручную, очередь скачивается только после Start. Восстановление и импорт не начинают загрузку.
- Preview работает независимо от скачивания и выбора строки. UI не выполняет тяжёлую сеть/диск; кеш, таймауты и фоновые владельцы сохраняют платформенные ограничения.
- Повреждённые пользовательские данные не перезаписываются автоматически; прежние форматы читаются совместимо.
- Перевод меняет отображение, а не машинные состояния или исходную диагностику. Секреты маскируются до хранения/копирования журнала.

## Быстрый выбор проверок

Полная матрица и критерии доказательства — [CORE.md](docs/agents/CORE.md#проверки-по-области-изменения) и [testing.md](docs/developer/testing.md).

| Изменение | Команды и условие merge |
| --- | --- |
| Windows-код, тесты, скрипты или сборка | `pwsh -NoProfile -File scripts/build.ps1 -Task Test`; зелёный Windows CI |
| Windows packaging | Дополнительно `pwsh -NoProfile -File scripts/build.ps1 -Task Publish` |
| Android | Из `android/`: `./gradlew :core:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug :app:assembleDebugAndroidTest`; device-сценарии по [ANDROID.md](docs/agents/ANDROID.md), зелёный Android CI |
| Общий коллектор или portable queue | Обе платформы; фикстуры и реальные UI-сценарии по контракту |
| Только Markdown | Локальные ссылки, якоря, Mermaid, соответствие исходникам; runtime-тесты не требуются |

Не подменяй реальные WPF/device/transfer-сценарии поиском маркеров. Windows и Android CI дополняют друг друга.

## Выпуск и рабочий процесс

Любой push в `main`, включая документацию, после полного успешного Windows CI создаёт стабильный `v2.0.N` Release. PR не публикует релиз; write-permissions разрешены только main-only release job. Версия EXE совпадает с тегом. Не изменяй прежние релизы и draft `v1.0.0`.

Работай от актуального `main`, делай осмысленные коммиты, PR и применимые проверки. Merge выполняется после успешных проверок в пределах поручения пользователя. Эти документы не вводят повторного запроса уже предоставленных полномочий. Не коммить секреты и build artifacts без явной задачи.

## Документация по ролям

- [Пользователю](docs/user/README.md): русские сценарии Windows/Android, cookies, журнал и перенос очереди.
- [Разработчику](docs/developer/README.md): архитектура, локальная сборка, тесты и выпуск.
- [Агенту](docs/agents/README.md): подробные обязательные контракты.
- [Планы проекта](docs/project/README.md): улучшения, новые функции и паритет Android; планы не считаются реализованным поведением.
