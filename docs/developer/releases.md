# Выпуск и артефакты

[Разработчику](README.md) · [Локальная сборка Windows](windows-build.md) · [Сборка APK](android-build.md) · [Проверки](testing.md)

GitHub Release содержит Windows portable ZIP и Android release APK с одной версией. Выпуск начинается после успешных проверок обеих платформ для одного коммита `main`. Подпись Android требует [однократной настройки постоянного ключа](#android-артефакт-и-подпись); без обязательных секретов выпуск завершается ошибкой.

## Правило

**Каждый коммит в ветке main — кандидат на стабильный релиз.** Workflow `.github/workflows/native-windows.yml` с названием **Windows and Android build** запускается на любой push в `main`, включая изменения документации.

1. Job `native` выполняет компиляцию C# / WPF, тесты GUI, сохранения данных, безопасности файлов и настоящие офлайн загрузки через yt-dlp.
2. Переиспользуемый `.github/workflows/android.yml` выполняет Android unit/lint, debug/release-сборки и instrumented tests на API 35, включая настоящую загрузку после Home.
3. Только после обеих успешных проверок job **Publish GitHub Release from main** получает разрешение на публикацию. В PR и при ручном запуске этот этап не выполняется.
4. Job последовательно выбирает стабильный Git-тег, собирает EXE и release APK с версией тега, подписывает APK постоянным ключом и проверяет подпись через `apksigner verify`.
5. GitHub публикует Release с Release Notes, `VideoDownloader-Windows-x64.zip`, `VideoDownloader-Android.apk`, общим `SHA256SUMS.txt`, `THIRD_PARTY_NOTICES.md` и `youtubedl-android-GPL-3.0.txt`.

**Скачать:** https://github.com/bespalovnikita/video-downloader/releases/latest

## Версии

- Линейка начинается с `v2.0.0` — базовой версии C#-проекта `src/VideoDownloader/VideoDownloader.csproj`.
- Следующие успешные main commits получают `v2.0.1`, `v2.0.2` и т. д.
- Изменение базовой версии проекта, например на `2.1.0`, начинает новую minor-линейку `v2.1.0`. Уже опубликованные версии не переиспользуются.
- Версии EXE и Android `versionName` совпадают с тегом без `v`. Android `versionCode` равен `major * 1000000 + minor * 10000 + patch`: например, `2.0.1` даёт `2000001`.
- Для Android допустимы `minor` от 0 до 99, `patch` от 0 до 9999 и итоговый `versionCode` от 1 до 2100000000. При достижении границы patch нужно поднять базовую minor-версию; несовместимая версия останавливает выпуск.
- Локальная сборка без переопределения сохраняет значения проекта. Для Android параметры `-PvdVersionName` и `-PvdVersionCode` передаются вместе; в Release их задаёт скрипт `scripts/build-android-release.ps1`.
- Существующий черновик `v1.0.0` остаётся без изменений. Прежние опубликованные релизы, включая выпуски только с Windows ZIP, автоматически не дополняются.
- Перезапуск CI на том же SHA использует прежний тег и **не создаёт второй релиз**. Существующий Release пропускается.
- Нумерация вычисляется модулем `.github/scripts/ReleaseVersion.psm1`, проверяется `scripts/release-version-tests.ps1`.
- Concurrency-группа `video-downloader-main-release` блокирует одновременную выдачу одинаковых тегов при нескольких push в `main`.

**Проваливший проверки, сборку или подпись коммит не публикуется.** Предыдущий стабильный релиз остаётся доступен.

## Артефакты и доступ

| Файл Release | Назначение |
| --- | --- |
| `VideoDownloader-Windows-x64.zip` | Автономное Windows x64 приложение: EXE, yt-dlp, XAML, ресурсы и общий коллектор |
| `VideoDownloader-Android.apk` | Подписанный release APK для Android 8.0+, arm64-v8a и x86_64 |
| `SHA256SUMS.txt` | SHA-256 Windows ZIP и подписанного APK |
| `THIRD_PARTY_NOTICES.md` | Сведения о сторонних компонентах Android и их исходниках |
| `youtubedl-android-GPL-3.0.txt` | Текст GPL для Android-рантайма |

Windows-пакет не требует установленного PowerShell, Python или .NET Runtime. FFmpeg и ffprobe устанавливаются через интерфейс или размещаются рядом с приложением/в PATH. Проверка пакета требует `collection-plugins/videodownloader/yt_dlp_plugins/extractor/vd_pornhub.py` и отклоняет CMD, пользовательские PowerShell-скрипты и `pwsh.exe`.

Windows ZIP собирается командой `pwsh -NoProfile -File scripts/build.ps1 -Task Publish` с выбранной версией и именем архива. Скрипт `scripts/build-android-release.ps1 -Version VERSION` собирает и подписывает APK; итоговый checksum-файл создаётся после сборки ZIP и подписи APK. Ключ извлекается только во временный каталог runner и удаляется после использования.

PR и ручной CI сохраняют Actions artifacts **VideoDownloader-native-windows**, **VideoDownloader-Android-debug** и отчёты **Android-test-reports**. Для их скачивания может потребоваться вход в GitHub; срок хранения ограничен. Debug APK не прикрепляется к Release, его подпись между запусками может отличаться.

По умолчанию workflow имеет `contents: read`. Только main-only job `release` получает `contents: write` и секреты подписи. Личный PAT для стандартной публикации не нужен. При отказе доступа проверь **Settings → Actions → General → Workflow permissions**, сохраняя PR-сборки без write-полномочий.

## Использование

1. Для установки используй **Releases → Latest** и файл нужной платформы.
2. Для обновлений отправляй изменения в PR, дождись проверок обеих платформ и мержи в `main`.
3. Перед обновлением Windows заверши приложение и распакуй новый ZIP целиком; данные в `%LOCALAPPDATA%\VideoDownloader` сохраняются. Живой работник старой PowerShell-версии блокирует восстановление и новый Start, пока не завершён.
4. Android обновляется поверх прежней установки при совпадении подписи и допустимом `versionCode`. Переход с debug APK может потребовать предварительного [экспорта данных](../user/android.md#установка-apk-и-обновление). Предыдущий APK с меньшим `versionCode` обычно нельзя установить как обновление.
5. Для отката Windows скачай предыдущий стабильный ZIP. Опубликованные теги и файлы не перезаписываются.

Перед выпуском выполни применимые [проверки](testing.md), включая `pwsh -NoProfile -File scripts/build.ps1 -Task Test`, и дождись CI. Релизы не требуют отдельного ручного подтверждения: **всё, что готово к merge в main, считается готовым к выпуску**.

## Ручной запуск CI

Открой **Actions → Windows and Android build → Run workflow**, выбери ветку и запусти проверку обеих платформ. Для отдельных Android-проверок доступен **Android Kotlin build → Run workflow**. Эти запуски сохраняют Actions artifacts, но не публикуют Release: job `release` разрешён только для события `push` в `refs/heads/main`.

Поэтому **This job was skipped** у `Publish GitHub Release from main` в PR или ручном workflow — ожидаемый результат. Для публикации найди push нужного SHA в `main`, успешно прошедший Windows, Android и release job.

## Как проверить публикацию

1. Найди запуск [общего workflow для main](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml?query=branch%3Amain) с SHA нужного merge-коммита.
2. Проверь успешность Windows и Android jobs, затем **Publish GitHub Release from main**. При провале любой платформы публикация не начинается.
3. Открой [Releases](https://github.com/bespalovnikita/video-downloader/releases), проверь тег, SHA и все файлы из таблицы выше.
4. Проверь совпадение версии EXE и Android `versionName` с тегом. Сверь SHA-256 скачанных ZIP и APK с `SHA256SUMS.txt`; подпись APK можно проверить `apksigner verify --verbose --print-certs VideoDownloader-Android.apk`.

| Симптом | Проверка |
| --- | --- |
| Release job пропущен | Это PR/ручной запуск либо Windows или Android job не завершился успешно; найди push в `main` |
| Все тесты зелёные, публикация ждёт | Проверь release job и очередь concurrency `video-downloader-main-release` |
| Не хватает параметров подписи Android | Настрой четыре GitHub Secrets из следующего раздела; debug APK не используется как замена |
| Ошибка подписи APK | Проверь keystore, alias и оба пароля; сохрани прежний ключ для обновлений |
| Публикация получила отказ в доступе | Job `release` требует `contents: write`; не расширяй права PR-сборок |
| Повторный запуск сообщает, что Release уже существует | Это идемпотентный пропуск; проверь существующий тег и его SHA |
| Тег указывает на другой SHA | Workflow отказывается переносить чужой тег; исследуй конфликт, сохраняя опубликованные версии |
| Устарела локальная версия | Сборка без параметра версии использует значения проекта, а не номер последнего Release |

Основные файлы: [native-windows.yml](../../.github/workflows/native-windows.yml), [android.yml](../../.github/workflows/android.yml), [ReleaseVersion.psm1](../../.github/scripts/ReleaseVersion.psm1), [build-android-release.ps1](../../scripts/build-android-release.ps1) и [verify-package.ps1](../../scripts/verify-package.ps1).

## Android: артефакт и подпись

Перед первым автоматическим выпуском администратор репозитория один раз задаёт постоянный ключ. Если приложение уже распространялось с release-подписью, используй прежний keystore: новый ключ не позволит обновлять те установки с сохранением данных.

| GitHub Actions secret | Значение |
| --- | --- |
| `ANDROID_KEYSTORE_BASE64` | Base64 полного файла keystore |
| `ANDROID_KEYSTORE_PASSWORD` | Пароль keystore |
| `ANDROID_KEY_ALIAS` | Alias ключа внутри keystore |
| `ANDROID_KEY_PASSWORD` | Пароль ключа |

Создай secrets в **Settings → Secrets and variables → Actions → Repository secrets**. Ключ и его резервную копию храни вне репозитория; не отправляй их в issue, PR или чат. Для создания нового ключа нужны JDK 17 с `keytool` и приватный каталог вне рабочей копии. Пример для Bash, где путь нужно заменить своим:

```bash
VD_KEYSTORE=/absolute/private/path/video-downloader-release.jks
keytool -genkeypair -keystore "$VD_KEYSTORE" -storetype PKCS12 -alias video-downloader -keyalg RSA -keysize 4096 -validity 10000
```

`keytool` запросит пароль и сведения о владельце. Для PKCS12 пароль ключа совпадает с паролем хранилища; укажи его в обоих password secrets. Сохрани защищённую резервную копию keystore и паролей: потерянный ключ нельзя восстановить из APK.

Для записи через авторизованный GitHub CLI выполни следующие команды. Base64 передаётся через stdin без вывода в терминал, password secrets вводятся по запросу `gh`:

```bash
python -c 'import base64,sys; sys.stdout.write(base64.b64encode(open(sys.argv[1], "rb").read()).decode())' "$VD_KEYSTORE" | gh secret set ANDROID_KEYSTORE_BASE64 --repo bespalovnikita/video-downloader
gh secret set ANDROID_KEYSTORE_PASSWORD --repo bespalovnikita/video-downloader
gh secret set ANDROID_KEY_ALIAS --body video-downloader --repo bespalovnikita/video-downloader
gh secret set ANDROID_KEY_PASSWORD --repo bespalovnikita/video-downloader
```

Release job прекращает выпуск, если секрет отсутствует или подпись не проходит проверку. В PR секреты не передаются. Локальный `assembleRelease` по-прежнему создаёт unsigned APK; [гайд сборки](android-build.md#release-apk-и-подпись) описывает локальную подпись.

Перед публичным распространением выполни требования [THIRD_PARTY_NOTICES.md](../../android/THIRD_PARTY_NOTICES.md): GPL-совместимые условия Android-приложения, применимые notices и Corresponding Source, включая необходимые материалы сборки нативных компонентов. Вложенные notices и текст GPL не заменяют эти требования. Лицензия собственного кода этой настройкой не изменяется; Google Play workflow не используется.
