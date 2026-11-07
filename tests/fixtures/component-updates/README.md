# Фиксированные Windows runtime для проверки обновления

`yt-dlp-releases.json` закрепляет SHA-256 двух настоящих официальных EXE:

| Версия | Официальные EXE и контрольные суммы |
| --- | --- |
| `2025.07.21` | [yt-dlp.exe](https://github.com/yt-dlp/yt-dlp/releases/download/2025.07.21/yt-dlp.exe), [SHA2-256SUMS](https://github.com/yt-dlp/yt-dlp/releases/download/2025.07.21/SHA2-256SUMS) |
| `2025.08.22` | [yt-dlp.exe](https://github.com/yt-dlp/yt-dlp/releases/download/2025.08.22/yt-dlp.exe), [SHA2-256SUMS](https://github.com/yt-dlp/yt-dlp/releases/download/2025.08.22/SHA2-256SUMS) |

Бинарники не хранятся в Git. Setup теста скачивает конкретные версии,
сверяет pinned hash с официальным checksum и файлом, после чего Windows
проверяет фактический `--version`. Это проверка обновления runtime,
а не доступности живых сайтов. Дальнейший HTTP production-политики управляется
тестовым handler: произвольные изменённые PE никогда не объявляются официальным
более новым загрузчиком. Порядок кеширования и офлайн-запуска описан в
[руководстве тестирования](../../../docs/developer/testing.md).
