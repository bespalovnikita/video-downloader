# Разработчику

В репозитории два приложения с разными сборками и хранилищами: Windows на C# / .NET 8 / WPF и Android на Kotlin / Jetpack Compose. Общие части — коллекционный плагин yt-dlp, требования к точным URL и формат переноса очереди. Пользовательского CLI нет; PowerShell и Gradle нужны разработке и CI.

| Задача | Руководство |
| --- | --- |
| Собрать Windows EXE и portable ZIP локально | [Сборка Windows](windows-build.md) |
| Собрать, установить и подписать Android APK | [Сборка Android](android-build.md) |
| Разобраться с URL, превью, подписками, загрузкой и хранением Windows | [Архитектура и восемь схем](architecture.md) |
| Найти команды и границы проверок обеих платформ | [Тестирование](testing.md) |
| Подготовить изменение и PR | [Разработка и участие](contributing.md) |
| Понять автоматический Windows Release и Android-артефакт | [Выпуск и артефакты](releases.md) |

Пользовательские действия описаны отдельно: [Windows](../user/windows.md), [Android](../user/android.md), [Cookies](../user/cookies.md), [перенос очереди](../user/queue-transfer.md). Правила для coding agents — в [разделе для агентов](../agents/README.md), идеи и ограничения развития — в [дорожной карте](../project/ROADMAP.md).

## Быстрый старт

Для Windows нужны Windows 10/11 x64, .NET 8 SDK x64 и PowerShell 7. Из корня клонированного репозитория:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Build
& .\src\VideoDownloader\bin\Release\net8.0-windows\win-x64\VideoDownloader.exe
```

Для Android нужны JDK 17 и Android SDK Platform 35 / Build-Tools 35.0.0. Из корня репозитория перейди в `android/`:

```bash
cd android
./gradlew :app:assembleDebug
```

В Windows используй `.\gradlew.bat :app:assembleDebug`. APK появится в `android/app/build/outputs/apk/debug/app-debug.apk` от корня репозитория. Настройка SDK, установка на телефон и ограничения подписи разобраны в [полном гайде](android-build.md).

## Карта исходников

| Каталог | Назначение |
| --- | --- |
| [src/VideoDownloader](../../src/VideoDownloader/) | Windows-приложение, модели, точка входа |
| [UI/Views](../../src/VideoDownloader/UI/Views/) и [UI/Controllers](../../src/VideoDownloader/UI/Controllers/) | WPF-разметка, обработчики и владельцы состояния интерфейса |
| [Downloads](../../src/VideoDownloader/Downloads/) | Работники yt-dlp, отмена, staging и публикация |
| [Preview](../../src/VideoDownloader/Preview/) | Метаданные, изображения и кеш |
| [Subscriptions](../../src/VideoDownloader/Subscriptions/) | Обнаружение коллекций, RSS и состояние подписок |
| [Persistence](../../src/VideoDownloader/Persistence/) и [Compatibility](../../src/VideoDownloader/Compatibility/) | Checkpoint, история и чтение прежних данных |
| [android/core](../../android/core/) | Kotlin/JVM: модели, парсеры, хранилище и правила |
| [android/app](../../android/app/) | Compose, Android-рантайм yt-dlp, foreground service и публикация |
| [collection-plugins](../../collection-plugins/) | Исходники плагина коллекций, включаемые в обе поставки |
| [tests](../../tests/) | Windows-тесты и общие межплатформенные фикстуры |
| [scripts](../../scripts/) | Windows build/test/publish и вспомогательные проверки |
| [.github/workflows](../../.github/workflows/) | Отдельные Windows и Android CI |

## Границы платформ

Windows использует отдельные `session.json`, `subscriptions.json` и `successful-downloads.jsonl` в `%LOCALAPPDATA%\VideoDownloader`. Android сохраняет свои настройки, очередь, подписки и историю в приватном `files/state.json`; его формат и правила публикации отличаются. [Схемы Android](../user/android.md#взаимодействие-компонентов) показывают `AppGraph`, ViewModel, foreground service и хранилище.

Перенос `.vdqueue` передаёт URL, названия и порядок. Он не синхронизирует настройки, историю, cookies, права SAF и принадлежность подпискам. Оба клиента должны читать [общие фикстуры формата](../../tests/fixtures/portable-queue/README.md).
