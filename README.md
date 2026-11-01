# Video Downloader

[![Сборка Windows](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml/badge.svg)](https://github.com/bespalovnikita/video-downloader/actions/workflows/native-windows.yml)
[![Сборка Android](https://github.com/bespalovnikita/video-downloader/actions/workflows/android.yml/badge.svg)](https://github.com/bespalovnikita/video-downloader/actions/workflows/android.yml)

Приложение для скачивания видео и аудио с очередью, историей, превью и ручными подписками. Доступны два клиента: **Windows 10/11 x64** на C# / .NET 8 / WPF и **Android 8.0+** на Kotlin / Jetpack Compose. Оба используют yt-dlp; набор возможностей различается.

**[Скачать Windows](https://github.com/bespalovnikita/video-downloader/releases/latest)** · **[Установить Android](docs/user/android.md)** · **[Вся документация на русском](docs/README.md)**

<a id="документация"></a>

## С чего начать

| Кому | Руководство |
| --- | --- |
| Пользователю | [Сценарии и инструкции](docs/user/README.md) |
| Разработчику | [Сборка, тестирование и устройство проекта](docs/developer/README.md) |
| Агенту | [Обязательные правила](AGENTS.md) и [карта задач](docs/agents/README.md) |
| Участнику планирования | [Направления развития](docs/project/ROADMAP.md) и [план сближения Android с Windows](docs/project/ANDROID_PARITY.md) |

## Быстрый запуск

<a id="установка-windows-версии"></a>

### Windows

1. Скачай `VideoDownloader-Windows-x64.zip` из [последнего релиза](https://github.com/bespalovnikita/video-downloader/releases/latest).
2. Распакуй ZIP целиком. Сохрани соседние с EXE файлы и папки.
3. Запусти `VideoDownloader.exe`. Если FFmpeg и ffprobe ещё не установлены, нажми **Install FFmpeg** в разделе **Dependencies**.
4. Вставь ссылку на видео, нажми **Add**, выбери папку и нажми **Start**.

Установка .NET, Python и PowerShell пользователю не требуется. Windows-пакет автономный; FFmpeg устанавливается отдельно через приложение или находится в PATH. Для проверки целостности рядом с ZIP опубликован `SHA256SUMS.txt`.

### Android

Установи отладочный APK из [артефактов Android CI](https://github.com/bespalovnikita/video-downloader/actions/workflows/android.yml) или [собери его локально](docs/developer/android-build.md). Автоматическая публикация подписанного стабильного APK пока не настроена. После **Start** загрузкой управляет foreground service с уведомлением; переход на главный экран её не останавливает. [Ограничения Android и работа с файлами](docs/user/android.md) описаны отдельно.

<a id="руководство-по-сценариям"></a>
<a id="кнопки-быстрого-доступа"></a>

## Инструкции по задачам

| Задача | Инструкция |
| --- | --- |
| Скачать видео YouTube | [Добавить URL и запустить загрузку](docs/user/windows.md#youtube-video) |
| Скачать плейлист YouTube | [Одна задача или отдельная задача на каждый ролик](docs/user/windows.md#youtube-playlist) |
| Следить за каналом или плейлистом | [Создать подписку и проверить новые материалы](docs/user/windows.md#subscriptions) |
| Использовать RSS/Atom | [Добавить ленту к подписке](docs/user/windows.md#rss) |
| Перенести очередь между Windows и Android | [Буфер обмена и файл `.vdqueue`](docs/user/queue-transfer.md) |
| Загрузить cookies из браузера | [Получение и подключение cookies](docs/user/cookies.md) |
| Разобраться с ошибкой | [Решение проблем](docs/user/troubleshooting.md) и [журнал Windows](docs/user/windows-logging.md) |
| Собрать приложение самостоятельно | [Windows](docs/developer/windows-build.md) · [Android APK](docs/developer/android-build.md) |

<a id="подписки-на-каналы-и-плейлисты"></a>

Подписки проверяются вручную. RSS дополняет названия материалов, подтверждённых коллекцией через yt-dlp. Приём материалов в очередь и **Start** — отдельные действия. Для отдельной задачи на каждый ролик плейлист импортируется через подписку; прямой URL плейлиста в основной очереди остаётся одной задачей.

<a id="основные-возможности"></a>
<a id="профили-загрузки"></a>
<a id="превью-кеш-и-параметры"></a>
<a id="безопасность-файлов-и-история"></a>

## Что умеет приложение

Оба клиента поддерживают очередь URL, загрузку через yt-dlp, историю успешных результатов, метаданные, до 15 доступных изображений сайта, ручные подписки, cookies и перенос очереди `.vdqueue`. Готовых изображений у сайта может быть меньше 15; кадры из видео для этого не извлекаются.

В Windows также доступны четыре профиля журнала, быстрая проверка поддерживаемых коллекций с полным обходом при необходимости, общий вопрос о добавлении найденных новинок и отдельная панель материала подписки. Подробности и различия клиентов: [Windows](docs/user/windows-reference.md), [Android](docs/user/android.md), [матрица возможностей](docs/project/ANDROID_PARITY.md).

<a id="сборка-и-проверка"></a>

## Разработка и развитие

[Архитектура и диаграммы жизненного цикла](docs/developer/architecture.md) · [Проверки](docs/developer/testing.md) · [Внесение изменений](docs/developer/contributing.md) · [Публикация релизов](docs/developer/releases.md)

Windows-релиз публикуется из `main` после успешной проверки нативного приложения. Android собирается отдельным workflow. Предложения по улучшениям, приоритеты и критерии готовности находятся в [дорожной карте](docs/project/ROADMAP.md); они обозначают будущую работу.
