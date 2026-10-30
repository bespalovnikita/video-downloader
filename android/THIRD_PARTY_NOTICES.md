# Сторонние компоненты Android-версии

Этот файл относится к Android-проекту в `android/`. Он фиксирует основные компоненты, их источники и лицензионные сведения; он не меняет лицензию собственного кода проекта и Windows-приложения.

## Android-рантайм yt-dlp и FFmpeg

В `app/build.gradle.kts` закреплены Maven Central зависимости:

- `io.github.junkfood02.youtubedl-android:library:0.18.1`;
- `io.github.junkfood02.youtubedl-android:ffmpeg:0.18.1`.

Оба опубликованных POM указывают автора **yausername**, исходники [yausername/youtubedl-android](https://github.com/yausername/youtubedl-android) и **GNU GPL version 3**. См. [POM library 0.18.1](https://repo.maven.apache.org/maven2/io/github/junkfood02/youtubedl-android/library/0.18.1/library-0.18.1.pom), [POM ffmpeg 0.18.1](https://repo.maven.apache.org/maven2/io/github/junkfood02/youtubedl-android/ffmpeg/0.18.1/ffmpeg-0.18.1.pom) и [оригинальный LICENSE](https://github.com/yausername/youtubedl-android/blob/master/LICENSE). Копия текста лицензии находится в [`licenses/youtubedl-android-GPL-3.0.txt`](licenses/youtubedl-android-GPL-3.0.txt).

Исходники опубликованной Java/Kotlin-части доступны также как [library-0.18.1-sources.jar](https://repo.maven.apache.org/maven2/io/github/junkfood02/youtubedl-android/library/0.18.1/library-0.18.1-sources.jar). Инструкции upstream по нативным компонентам: [BUILD_PYTHON.md](https://github.com/yausername/youtubedl-android/blob/master/BUILD_PYTHON.md), [BUILD_FFMPEG.md](https://github.com/yausername/youtubedl-android/blob/master/BUILD_FFMPEG.md). Упакованные нативные компоненты и их собственные зависимости сохраняют свои лицензии.

Рантайм включает Python, yt-dlp, JavaScript-рантайм и FFmpeg/ffprobe для Android. Их upstream-проекты:

Ресурс yt-dlp в APK заменяется официальным Unix zipimport релиза [2026.10.26](https://github.com/yt-dlp/yt-dlp/releases/tag/2026.10.26), закреплённым в [`yt-dlp.properties`](yt-dlp.properties) с SHA-256. Исходники именно этой версии доступны по [тегу 2026.10.26](https://github.com/yt-dlp/yt-dlp/tree/2026.10.26); архив включает EJS. Изменение extractor не меняет версии Python/QuickJS/FFmpeg из Android wrapper.

| Компонент | Исходники и лицензионные сведения |
| --- | --- |
| yt-dlp | [yt-dlp/yt-dlp](https://github.com/yt-dlp/yt-dlp), [LICENSE](https://github.com/yt-dlp/yt-dlp/blob/master/LICENSE) |
| Python | [python/cpython](https://github.com/python/cpython), [LICENSE](https://github.com/python/cpython/blob/main/LICENSE) |
| QuickJS | [bellard/quickjs](https://github.com/bellard/quickjs), [LICENSE](https://github.com/bellard/quickjs/blob/master/LICENSE) |
| FFmpeg и ffprobe | [FFmpeg](https://ffmpeg.org/), [исходники](https://git.ffmpeg.org/ffmpeg.git), [правила лицензирования сборок](https://ffmpeg.org/legal.html) |

Не следует определять лицензию готового APK только по лицензии самого yt-dlp: APK включает GPL-рантайм и нативные библиотеки. Версия yt-dlp внутри пользовательского хранилища может измениться после команды обновления; это не обновляет автоматически остальные нативные компоненты APK.

## Интерфейс и Kotlin

| Компоненты | Источник | Лицензия upstream |
| --- | --- | --- |
| Kotlin, kotlinx.coroutines, kotlinx.serialization | [Kotlin](https://github.com/JetBrains/kotlin), [coroutines](https://github.com/Kotlin/kotlinx.coroutines), [serialization](https://github.com/Kotlin/kotlinx.serialization) | Apache License 2.0 |
| AndroidX, Jetpack Compose, Material 3 | [AndroidX](https://android.googlesource.com/platform/frameworks/support/), [Apache 2.0](https://www.apache.org/licenses/LICENSE-2.0) | Apache License 2.0 |
| Coil 2.7.0 | [coil-kt/coil](https://github.com/coil-kt/coil), [LICENSE](https://github.com/coil-kt/coil/blob/2.7.0/LICENSE.txt) | Apache License 2.0 |

Точный набор транзитивных зависимостей можно получить из `android/` командой `./gradlew :app:dependencies --configuration releaseRuntimeClasspath` (Windows: `.\gradlew.bat`). Уведомления и лицензии транзитивных и нативных компонентов также должны учитываться при распространении.

## Перед публичным распространением APK

Перед публикацией подписанного APK необходимо определить GPL-совместимые условия для Android-приложения, сохранить применимые copyright/license notices и предоставить Corresponding Source для распространяемой сборки, включая необходимые изменения и материалы сборки нативных компонентов. Ссылка на этот файл или на текущую ветку upstream сама по себе не заменяет эти обязательства. В репозитории пока не задана общая лицензия собственного кода; этот файл не выдаёт такое разрешение от имени владельца проекта.

Разработка и локальная сборка описаны в [README Android-проекта](README.md). Автоматическая публикация подписанного APK или в Google Play в текущем workflow не настроена.
