# Документация Video Downloader

Выбери раздел по своей задаче. Руководства написаны на русском языке; названия кнопок и полей сохранены как в приложении.

| Раздел | Что внутри |
| --- | --- |
| [Пользователю](user/README.md) | Установка, видео и плейлисты, подписки и RSS, cookies, перенос очереди, ошибки |
| [Разработчику](developer/README.md) | Локальная сборка Windows и APK, архитектура, тестирование, внесение изменений, релизы |
| [Агенту](agents/README.md) | Обязательные правила и контракты платформ; начни с корневого [AGENTS.md](../AGENTS.md) |
| [Развитие проекта](project/README.md) | Приоритетные улучшения, новые функции и сближение возможностей Android с Windows |

## Быстрые ссылки

- **Первый запуск:** [Windows](user/windows.md) · [Android](user/android.md).
- **Четыре основных сценария Windows:** [YouTube-видео](user/windows.md#youtube-video), [плейлист](user/windows.md#youtube-playlist), [подписка](user/windows.md#subscriptions), [RSS](user/windows.md#rss).
- **Перенос и доступ к сайтам:** [очередь между устройствами](user/queue-transfer.md) · [cookies из браузера](user/cookies.md).
- **Диагностика:** [решение проблем](user/troubleshooting.md) · [профили журнала Windows](user/windows-logging.md).
- **Своя сборка:** [Windows](developer/windows-build.md) · [Android APK](developer/android-build.md).
- **Внутреннее устройство:** [архитектура и диаграммы](developer/architecture.md) · [проверки](developer/testing.md) · [релизы](developer/releases.md).
- **Будущие задачи:** [дорожная карта](project/ROADMAP.md) · [Android и Windows: различия и этапы](project/ANDROID_PARITY.md).

## Как организованы документы

В `user/` описаны действия в приложении. В `developer/` — устройство проекта и работа с исходниками. В `agents/` — обязательные инженерные контракты. В `project/` — предложения и планы, которые ещё не означают готовую функцию.

Windows и Android имеют разные интерфейсы и ограничения. Страница с названием платформы описывает именно эту платформу; общие форматы и способы переноса отмечены отдельно.

Прежние пути `GUI.md`, `RELEASING.md`, `BACKLOG.md`, `docs/USER_GUIDE.md` и другие сохранены как страницы перехода. Актуальные инструкции находятся в разделах выше.

Документы рядом с [тестовыми данными Windows](../tests/fixtures/legacy/README.md), [межплатформенными примерами очереди](../tests/fixtures/portable-queue/README.md) и [Android test assets](../android/app/src/androidTest/assets/README.md) остаются рядом с этими файлами: они описывают их происхождение и контракты. [Сторонние лицензии Android](../android/THIRD_PARTY_NOTICES.md) также сохраняются в Android-проекте.

[На главную страницу проекта](../README.md)
