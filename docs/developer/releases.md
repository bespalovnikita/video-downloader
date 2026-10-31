# Выпуск и артефакты

[Разработчику](README.md) · [Локальная сборка Windows](windows-build.md) · [Сборка APK](android-build.md) · [Проверки](testing.md)

Windows ZIP публикуется автоматически после успешного CI в `main`. Android workflow создаёт устанавливаемый debug APK как Actions artifact; публичный подписанный Android Release пока не настроен. Сборка этих платформ и их номера версий независимы.

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

## Ручной запуск CI

Открой **Actions → Native C# Windows build → Run workflow**, выбери нужную ветку и запусти проверку. Это собирает и тестирует Windows-приложение и сохраняет `VideoDownloader-native-windows`, но не публикует Release: job `release` разрешён только для события `push` в `refs/heads/main`.

Поэтому **This job was skipped** у `Publish GitHub Release from main` в PR или ручном workflow — ожидаемый результат. Для публикации изменение должно попасть в `main`, а запуск именно этого push — успешно пройти `native` и `release`. В интерфейсе GitHub сверяй SHA коммита и тип события, а не только название workflow.

## Как проверить публикацию

1. Найди запуск [Windows workflow для main](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml?query=branch%3Amain) с SHA нужного merge-коммита.
2. Убедись, что `native` завершился успешно и `Publish GitHub Release from main` также выполнен успешно. Если `native` упал, публикация не начинается.
3. Открой [Releases](https://github.com/bespalovnikita/video-downloader/releases) и проверь тег, целевой коммит, `VideoDownloader-Windows-x64.zip` и `SHA256SUMS.txt`.
4. После распаковки проверь совпадение версии EXE с тегом. При проверке целостности сравни SHA-256 скачанного ZIP с `SHA256SUMS.txt`.

| Симптом | Проверка |
| --- | --- |
| Release job пропущен | Это PR/ручной запуск либо зависимый `native` не завершился успешно; найди push в `main` |
| Все тесты зелёные, публикация ждёт | Проверь отдельный release job и очередь concurrency `video-downloader-main-release` |
| Публикация получила отказ в доступе | Проверь разрешения Actions: job `release` требует `contents: write`; не расширяй права PR-сборок |
| Повторный запуск сообщает, что Release уже существует | Это идемпотентный пропуск; проверь существующий тег и его SHA |
| Тег указывает на другой SHA | Workflow намеренно отказывается переносить чужой тег; исследуй конфликт, сохраняя опубликованные версии |
| Устарел локальный EXE | Локальный Publish без `-Version` использует базовую версию проекта; это отдельная сборка, не номер последнего GitHub Release |

Основной workflow, выбор тега и проверка пакета находятся в [native-windows.yml](../../.github/workflows/native-windows.yml), [ReleaseVersion.psm1](../../.github/scripts/ReleaseVersion.psm1) и [verify-package.ps1](../../scripts/verify-package.ps1). Не удаляй и не переписывай опубликованные теги или существующий draft `v1.0.0` ради повторного запуска.

## Android: артефакт и подпись

[Android Kotlin build](../../.github/workflows/android.yml) запускается по изменениям Android, общего коллектора или переносимых фикстур в PR и `main`; доступен и ручной запуск. Workflow выполняет unit/lint, сборку и instrumented tests на API 35, включая реальную загрузку после Home.

| Результат | Где находится | Назначение |
| --- | --- | --- |
| `VideoDownloader-Android-debug` | Artifacts успешного Android workflow | ZIP с устанавливаемым `app-debug.apk` |
| `Android-test-reports` | Artifacts Android workflow, если отчёты созданы | Unit, lint и device-отчёты, в том числе при неуспехе |
| Локальный debug APK | `android/app/build/outputs/apk/debug/app-debug.apk` | Установка и разработка с локальным debug-ключом |
| Локальный release APK | `android/app/build/outputs/apk/release/app-release-unsigned.apk` | Результат `assembleRelease`, который нужно подписать |

Для скачивания Actions artifact нужен вход в GitHub; это отличается от публичного Windows Release. Срок хранения артефакта определяется Actions. APK сейчас не прикрепляется к Windows Release, не публикуется в Google Play и не получает автоматический стабильный Android-тег.

Debug APK подписан автоматически. Ключ временного GitHub runner между запусками может отличаться, поэтому APK из другого запуска не всегда установится поверх прежнего с сохранением данных. Для постоянных обновлений используй один собственный ключ. Перед удалением приложения из-за несовпадения подписи экспортируй нужные данные и сохрани файлы.

Для release-сборки используй **Android Studio → Build → Generate Signed Bundle / APK → APK**, модуль `app`, свой keystore и конфигурацию `release`. Храни keystore и пароли вне Git, резервную копию ключа отдельно. Номер `versionCode` для обновления должен увеличиваться, а `versionName` задаёт отображаемую версию; оба определены в [app/build.gradle.kts](../../android/app/build.gradle.kts) и не связаны с Windows Git-тегом.

Полная последовательность сборки и установки — в [гайде APK](android-build.md#release-apk-и-подпись). Перед публичным распространением выполни требования включённых компонентов к лицензиям и соответствующим исходникам, описанные в [THIRD_PARTY_NOTICES.md](../../android/THIRD_PARTY_NOTICES.md). Обновление зависимости не меняет автоматически лицензию собственного кода проекта.
