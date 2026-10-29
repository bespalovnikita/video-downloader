# Офлайн-фикстура Android

`offline-sample.mp4` — сгенерированные синий кадр 96×64 и синусоидальный звук,
0,8 секунды. Фикстура не содержит сторонних видео и доступна локальному HTTP-серверу
инструментальных тестов. yt-dlp действительно скачивает её, FFprobe проверяет
медиапотоки, а служба Android публикует результат через обычный путь приложения.

Команда создания:

```sh
ffmpeg -f lavfi -i color=c=blue:s=96x64:r=10 \
  -f lavfi -i sine=frequency=440:sample_rate=44100 -t 0.8 \
  -c:v libx264 -preset ultrafast -crf 34 -pix_fmt yuv420p \
  -c:a aac -b:a 32k -movflags +faststart offline-sample.mp4
```
