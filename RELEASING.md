# Сборка и GitHub Releases — Video Downloader

В репозитории две отдельные сборки: Windows на C# / WPF и Android на Kotlin / Compose. Windows ZIP и Android APK не взаимозаменяемы.

## Локальная сборка Android APK

Полный [гайд по сборке APK](android/README.md) содержит установку **JDK 17**, **Android SDK Platform 35 / Build-Tools 35.0.0**, инструкции для Android Studio, Windows PowerShell, Linux/macOS, установку на телефон и подпись release-сборки.

После настройки инструментов перейди в `android/` и выполни:

```powershell
.\gradlew.bat :app:assembleDebug
```

В Linux/macOS: `./gradlew :app:assembleDebug`. Результат от корня репозитория — **`android/app/build/outputs/apk/debug/app-debug.apk`**, автоматически подписанный локальным отладочным ключом. Это устанавливаемая сборка для Android 8.0+ на arm64-v8a / x86_64. Само скачивание Windows Release для неё не требуется.

`assembleRelease` создаёт неподписанный `app-release-unsigned.apk`; для установки release-сборку нужно подписать своим ключом. Подпись и версии Android настраиваются отдельно от Windows. Ключи и пароли не хранятся в репозитории. Перед публичным распространением учитывай [лицензии Android-рантайма](android/THIRD_PARTY_NOTICES.md).

[`.github/workflows/android.yml`](.github/workflows/android.yml) собирает APK, запускает unit/lint и инструментальные тесты на эмуляторе, включая скачивание после нажатия Home. При успешном прогоне APK доступен в артефакте **VideoDownloader-Android-debug**. Workflow не публикует APK в Google Play и не создаёт подписанный Android GitHub Release. Описанное ниже автоматическое версионирование и публикация относятся к **Windows ZIP**.

## Локальная сборка Windows без скачивания релиза

Приложение можно собирать из исходников на своём компьютере. Для этого не нужны push, PR, GitHub Actions или публикация Release. Все команды ниже выполняются из корня репозитория в терминале Windows.

### Что установить один раз

- **Windows 10/11 x64**: приложение использует WPF. Скрипт сборки рассчитан на Windows, запуск WPF в Linux/macOS не поддерживается.
- **Git**, чтобы получить исходники и обновлять их.
- **.NET 8 SDK x64**, а не только Runtime: он нужен для компиляции.
- **PowerShell 7** (`pwsh`), а не встроенный Windows PowerShell 5.1.

Проверка инструментов:

```powershell
git --version
dotnet --list-sdks
pwsh --version
```

В списке SDK должна присутствовать версия `8.0.x`. Если инструмент не найден после установки, открой новый терминал. При первой сборке нужен доступ к NuGet для восстановления компонентов .NET; отдельная установка Visual Studio не требуется.

Склонируй проект один раз:

```powershell
git clone https://github.com/bespalovnikita/video-downloader.git
cd video-downloader
```

`yt-dlp.exe`, XAML, ресурсы и код коллектора уже находятся в репозитории и копируются при сборке. Python для сборки и запуска приложения не нужен.

### Рекомендуемый вариант: готовая автономная папка

Закрой работающий Video Downloader, включая экземпляр в трее. Затем выполни:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Publish
& .\dist\native\VideoDownloader.exe
```

Команда создаёт:

| Результат | Путь от корня репозитория |
| --- | --- |
| Готовое приложение со всеми файлами поставки | `dist/native/` |
| Исполняемый файл для запуска | `dist/native/VideoDownloader.exe` |
| Архив для переноса на другой компьютер | `dist/VideoDownloader-native.zip` |
| Контрольную сумму архива | `dist/SHA256SUMS.txt` |

Для запуска достаточно папки `dist/native`; распаковывать созданный ZIP у себя повторно не нужно. Сборка автономная: на компьютере, где её запускают, установленный .NET Runtime не требуется. При переносе копируй папку целиком: одному EXE нужны соседние `yt-dlp.exe`, `ui/`, `assets/` и `collection-plugins/`.

**FFmpeg и ffprobe не входят в сборку.** Установи их кнопкой **Install FFmpeg** в приложении или размести в постоянной папке, добавленной в `PATH`. Для регулярной пересборки удобнее постоянная папка: `Publish` каждый раз удаляет и создаёт заново `dist/native`, включая установленные туда FFmpeg/ffprobe. Не сохраняй скачанные видео в каталог сборки; выбери отдельную папку загрузок.

Настройки, очередь, подписки и история по умолчанию остаются в `%LOCALAPPDATA%\VideoDownloader`. Локальная и скачанная релизная сборки используют эти же данные. Закрытие прежнего экземпляра также нужно, чтобы новый EXE открыл новую сборку, а не передал управление уже работающему окну.

Без `-Version` локальная сборка получает базовую версию из `src/VideoDownloader/VideoDownloader.csproj`; она может отличаться от последнего GitHub Release. Можно явно указать номер:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Publish -Version 2.0.0
```

Разрешён формат `число.число.число`. Локальная команда сама не создаёт Git-тег и ничего не загружает на GitHub.

### Быстрая сборка для разработки

Если нужна обычная компиляция без автономного пакета и ZIP:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Build
& .\src\VideoDownloader\bin\Release\net8.0-windows\win-x64\VideoDownloader.exe
```

Это сборка с использованием установленного **.NET 8 Desktop Runtime x64**. Он входит в Windows-установку .NET 8 SDK; если EXE просит установить среду выполнения, проверь `dotnet --list-runtimes` на наличие `Microsoft.WindowsDesktop.App 8.0.x` либо используй автономный вариант `Publish`.

Для отладки укажи конфигурацию отдельно:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Build -Configuration Debug
& .\src\VideoDownloader\bin\Debug\net8.0-windows\win-x64\VideoDownloader.exe
```

### Как обновлять свою локальную сборку

После первого клонирования достаточно получить изменения и пересобрать приложение:

```powershell
git switch main
git pull --ff-only
pwsh -NoProfile -File .\scripts\build.ps1 -Task Publish
& .\dist\native\VideoDownloader.exe
```

Перед этим закрой приложение. Если у тебя есть собственные правки, сначала сохрани их в своей ветке; при отказе Git от обновления не удаляй изменения ради выполнения инструкции. Для повторной сборки собственных правок достаточно `Publish`, без переключения на `main` и `git pull`.

### Проверки перед передачей изменений

`Build` и `Publish` не запускают тесты автоматически. Полный прогон C# и настоящего WPF-интерфейса запускается отдельно, в обычном интерактивном сеансе Windows:

```powershell
pwsh -NoProfile -File .\scripts\build.ps1 -Task Test
```

Тесты используют изолированные профили. Для отдельных Python-тестов встроенного коллектора дополнительно установи Python и версию yt-dlp, закреплённую в [.github/workflows/native-windows.yml](.github/workflows/native-windows.yml), затем выполни `python tests/pornhub-collector-tests.py`. Для обычной локальной сборки эти проверки необязательны.

### Если сборка или запуск не удались

| Симптом | Что сделать |
| --- | --- |
| `pwsh` не найден | Установи PowerShell 7 и открой новый терминал. |
| `dotnet` не найден или подходящего SDK нет | Установи .NET 8 SDK x64; проверь `dotnet --list-sdks`. |
| Ошибка восстановления NuGet | Проверь сеть, прокси и доступ к `api.nuget.org`, затем повтори команду. |
| Не удаётся заменить EXE / файл занят | Заверши приложение, в том числе через Exit в трее, и повтори сборку. |
| После запуска видна прежняя версия | Закрой старый экземпляр и запусти EXE из нужной папки сборки. |
| Start сообщает об отсутствии FFmpeg / ffprobe | Установи оба инструмента через интерфейс или проверь их доступность в `PATH`. |

## Правило

**Каждый коммит в ветке main — кандидат на стабильный релиз.** Workflow `.github/workflows/native-windows.yml` запускается на любой push в main, включая изменения документации.

1. Windows CI выполняет компиляцию C# / WPF, тесты GUI, сохранения данных, безопасности файлов, а также настоящие офлайн загрузки через yt-dlp.
2. При успешных тестах job **Publish GitHub Release from main** получает разрешение на публикацию. В PR и при ручном запуске CI этот этап не выполняется.
3. Job последовательно выбирает свободный стабильный Git-тег, собирает EXE с версией тега, формирует portable ZIP и SHA-256.
4. GitHub публикует Release с Release Notes, файлом `VideoDownloader-Windows-x64.zip` и `SHA256SUMS.txt`.

**Скачать:** https://github.com/bespalovnikita/video-downloader/releases/latest

## Версии

- Линейка начинается с `v2.0.0` — базовой версии C#-проекта `src/VideoDownloader/VideoDownloader.csproj`.
- Следующие успешные main commits получают `v2.0.1`, `v2.0.2` и т. д.
- Изменение базовой версии проекта, например на `2.1.0`, начинает новую minor-линейку `v2.1.0`. Уже опубликованные версии не переиспользуются.
- Существующий черновик `v1.0.0` остаётся без изменений.
- Перезапуск CI на том же SHA использует прежний тег и **не создаёт второй релиз**.
- Нумерация вычисляется модулем `.github/scripts/ReleaseVersion.psm1`, проверяется `scripts/release-version-tests.ps1`.
- Отдельная concurrency-группа блокирует одновременную выдачу одинаковых тегов при нескольких push в main.

**Проваливший CI коммит не публикуется.** Предыдущий стабильный релиз остаётся доступен.

## Артефакты и доступ

В релизе находится автономная C# / .NET 8 / WPF Windows x64 сборка: `VideoDownloader.exe`, `yt-dlp.exe`, XAML, ресурсы и `collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py`. Последний файл обязателен для импорта публикаций Pornhub через подписки; CI проверяет его наличие в пакете. Пользователю не нужны PowerShell, Python или установленный .NET Runtime. FFmpeg и ffprobe проверяются перед началом загрузок; они устанавливаются через интерфейс или размещаются рядом с приложением/в PATH.

Пакет собирается единой командой `pwsh -NoProfile -File scripts/build.ps1 -Task Publish`. В main-only job ей передаётся выбранная версия через `-Version`, имя релизного архива и путь `SHA256SUMS.txt`. Проверка состава отклоняет CMD, пользовательские PowerShell-скрипты и `pwsh.exe`; приложение запускается непосредственно через EXE. Скрипты `scripts/` используются только для разработки и CI.

`SHA256SUMS.txt` позволяет проверить целостность скачанного архива.

- Для обычной PR-сборки сохраняется CI artifact `VideoDownloader-native-windows` в GitHub Actions, без автоматического Release.
- При успешном push в main публикуется **GitHub Release** с долговечными файлами для скачивания без авторизации.
- По умолчанию workflow имеет `contents: read`. Только main-only job `release` получает `contents: write`.
- Если GitHub откажет в публикации, проверь Settings → Actions → General → Workflow permissions. Личный PAT для стандартной публикации не нужен.

## Использование

1. Для скачивания используй страницу Releases → Latest.
2. Для обновлений отправляй изменения в PR и после проверок мержи в main.
3. Для отката скачай предыдущий стабильный ZIP — опубликованные теги и архивы не перезаписываются.
4. Перед обновлением заверши приложение, распакуй новый ZIP целиком; пользовательские данные в `%LOCALAPPDATA%\VideoDownloader` сохраняются. Живой работник старой PowerShell-версии блокирует восстановление и новый Start, пока он не завершён.
5. Перед выпуском выполни `pwsh -NoProfile -File scripts/build.ps1 -Task Test` и дождись зелёного workflow `native-windows.yml`, включая тесты коллектора. Кнопки интерфейса, русские профили и сохранение старых идентификаторов контролируются `scripts/russian-ui-tests.ps1` и настоящими WPF-тестами. Публичная документация проекта ведётся на русском языке.

Релизы не требуют отдельного ручного подтверждения: **всё, что готово к merge в main, считается готовым к выпуску**.
