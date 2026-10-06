# YT-DLP Download Manager v8

## Базовый запуск

``` powershell
.\ytdl-manager-v8.ps1
```

Использует:

-   `list.txt`
-   `list-success.txt`
-   `list-error.txt`
-   4 потока
-   загрузку видео

------------------------------------------------------------------------

## Ключи

### `-In`

Входной файл со списком URL.

``` powershell
.\ytdl-manager-v8.ps1 -In ".\anime.txt"
```

Будут созданы:

``` text
anime-success.txt
anime-error.txt
```

------------------------------------------------------------------------

### `-Out`

Папка для сохранения файлов.

``` powershell
.\ytdl-manager-v8.ps1 -Out ".\downloads"
```

или

``` powershell
.\ytdl-manager-v8.ps1 -Out "D:\Videos"
```

------------------------------------------------------------------------

### `-Threads`

Количество одновременно скачиваемых URL.

``` powershell
.\ytdl-manager-v8.ps1 -Threads 8
```

По умолчанию:

``` text
4
```

------------------------------------------------------------------------

### `-Fragments`

Количество потоков внутри одного видео.

Диапазон:

``` text
1..4
```

Пример:

``` powershell
.\ytdl-manager-v8.ps1 -Threads 4 -Fragments 3
```

------------------------------------------------------------------------

### `-AutoFragments`

Автоматически выбирает число фрагментов:

  Размер         Fragments
  ------------ -----------
  \<100 MB               1
  100-500 MB             2
  \>500 MB               4

Пример:

``` powershell
.\ytdl-manager-v8.ps1 -AutoFragments
```

Если одновременно указаны `-Fragments` и `-AutoFragments`, приоритет
имеет `-Fragments`.

------------------------------------------------------------------------

### `-Archive`

Создаёт подпапку по текущей дате.

``` powershell
.\ytdl-manager-v8.ps1 -Archive
```

Результат:

``` text
2026-06-08\
```

или

``` text
D:\Downloads\2026-06-08\
```

------------------------------------------------------------------------

### `-Cookies`

Использовать cookies.

``` powershell
.\ytdl-manager-v8.ps1 -Cookies ".\cookies.txt"
```

Передаётся в:

``` text
--cookies cookies.txt
```

------------------------------------------------------------------------

### `-SponsorBlock`

Удаляет спонсорские вставки.

``` powershell
.\ytdl-manager-v8.ps1 -SponsorBlock
```

Использует:

``` text
--sponsorblock-remove sponsor
```

------------------------------------------------------------------------

### `-AudioOnly`

Скачивание только аудио.

``` powershell
.\ytdl-manager-v8.ps1 -AudioOnly
```

Использует:

``` text
-x --audio-format mp3
```

------------------------------------------------------------------------

### `-NoProgress`

Отключает прогресс-бары.

``` powershell
.\ytdl-manager-v8.ps1 -NoProgress
```

Останутся только:

``` text
START
DONE
ERROR
```

------------------------------------------------------------------------

### `-DryRun`

Проверка очереди без скачивания.

``` powershell
.\ytdl-manager-v8.ps1 -DryRun
```

------------------------------------------------------------------------

### `-Log`

Логирование в файл.

``` powershell
.\ytdl-manager-v8.ps1 -Log ".\run.log"
```

------------------------------------------------------------------------

### `-Watch`

Режим наблюдения за входным файлом.

``` powershell
.\ytdl-manager-v8.ps1 -Watch
```

Скрипт:

-   не завершается после опустошения очереди;
-   ждёт новые URL;
-   автоматически подхватывает новые строки.

Остановка:

``` text
Ctrl+C
```

------------------------------------------------------------------------

### `-WatchInterval`

Период проверки файла в секундах.

``` powershell
.\ytdl-manager-v8.ps1 -Watch -WatchInterval 3
```

По умолчанию:

``` text
2
```

------------------------------------------------------------------------

## Рекомендуемые сценарии

### Обычное скачивание

``` powershell
.\ytdl-manager-v8.ps1
```

### Быстрое скачивание

``` powershell
.\ytdl-manager-v8.ps1 -Threads 4 -Fragments 3
```

### Автоматический выбор фрагментов

``` powershell
.\ytdl-manager-v8.ps1 -Threads 4 -AutoFragments
```

### Музыка

``` powershell
.\ytdl-manager-v8.ps1 -AudioOnly -Out ".\Music"
```

### Фоновая очередь загрузок

``` powershell
.\ytdl-manager-v8.ps1 -Watch -AutoFragments -Threads 4
```
