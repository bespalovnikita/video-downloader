# Video Downloader GUI

Нативный Windows GUI поверх `ytdl-manager-v8.ps1`.

## Запуск

### Готовый EXE
GitHub Actions собирает portable-архив `VideoDownloader-portable.zip` с:
- `VideoDownloader.exe` — WinExe launcher без консольного окна;
- `ytdl-manager-gui.ps1`;
- `ytdl-manager-v8.ps1`;
- `yt-dlp.exe`;
- `assets/app.ico`;
- документацией.

### Из исходников
Двойной клик по `start-gui.cmd` или:

```powershell
pwsh -STA -File .\ytdl-manager-gui.ps1
```

## Возможности

- полноценная очередь URL в таблице;
- ручное добавление, удаление, очистка и дедупликация;
- drag&drop `.txt`, `.url`, обычного текста и ссылок из браузера;
- автоматическое предложение добавить URL из буфера обмена;
- базовая валидация HTTP/HTTPS до постановки в очередь;
- фоновое получение через yt-dlp названия, длительности, сайта и thumbnail;
- превью выбранного видео;
- выбор качества: Best / 2160p / 1440p / 1080p / 720p с fallback для фиксированного качества;
- лимит скорости: готовые варианты либо своё значение вроде `5M` / `750K`;
- параллельные URL и AutoFragments;
- Archive / SponsorBlock / AudioOnly / cookies;
- live-статистика: размер очереди, активные загрузки, completed/total и суммарная скорость;
- прогресс, скорость и ETA по активным слотам;
- человеческие статусы ошибок; исходная техническая ошибка остаётся в логе;
- кнопка повторного запуска только неудачных URL;
- после успешной загрузки: открыть файл, показать в проводнике, скопировать путь;
- кнопка обновления `yt-dlp.exe`;
- системный трей, сворачивание в трей и запуск из tray menu;
- Windows-уведомление и звуковой сигнал после завершения;
- success/error-файлы сохраняются рядом со скачанными видео;
- папка по умолчанию: `%USERPROFILE%\Downloads\downloaded-video`;
- временная очередь/лог/events живут в `%TEMP%\ytdl-gui-*` и удаляются после завершения;
- осиротевшие temp-папки старше 24 часов убираются при следующем старте;
- живые temp-папки защищены через `owner.pid`.

## Сборка EXE локально

```powershell
pwsh -File .\build-exe.ps1
```

Скрипт использует .NET 8 SDK, собирает self-contained `win-x64` WinExe launcher и создаёт `dist\VideoDownloader.exe` плюс portable ZIP.

## Проверки

`tests/smoke.ps1`:
- парсит GUI и CLI через PowerShell parser;
- проверяет, что иконка читается Windows/.NET;
- запускает CLI в DryRun с новыми параметрами Quality / RateLimit / ResultDir.

Workflow `.github/workflows/build-windows.yml` выполняет smoke-check, собирает EXE и публикует portable ZIP как GitHub Actions artifact.

## Зависимости

- Windows;
- PowerShell 7 — нужен GUI/движку;
- `yt-dlp.exe` рядом с приложением;
- `ffmpeg.exe` / `ffprobe.exe` рядом или доступные движку для операций, которым они нужны.
