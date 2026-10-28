# Android: сборка APK из исходников

Android-версия написана на Kotlin с Jetpack Compose. Она собирается отдельно от Windows-приложения: открой каталог `android/`, а не C#-проект. Поддерживаются **Android 8.0+ (API 26)** и устройства **arm64-v8a / x86_64**. Для 32-битных ARM-устройств APK не предназначен.

[Как пользоваться приложением и как работают фоновые загрузки](../docs/ANDROID.md) · [Cookies из браузера](../docs/COOKIES.md#android-cookies) · [Сторонние компоненты](THIRD_PARTY_NOTICES.md)

## Что установить один раз

| Инструмент | Требование |
| --- | --- |
| Git | Для получения и обновления исходников |
| JDK | **17**, с `java`, `javac` и `keytool` |
| Android SDK | Android SDK Platform **35**, Build-Tools **35.0.0**, Platform-Tools |
| Android Studio | Удобный способ установить SDK и собрать APK; для командной строки необязательна |

Gradle **8.11.1** загружается самим `gradlew`; глобально устанавливать Gradle не нужно. Android Gradle Plugin **8.9.2** и Kotlin **2.1.20** закреплены в проекте. Первая сборка требует доступа к серверам Google, Maven Central и Gradle. Python, FFmpeg и yt-dlp для сборки на компьютере устанавливать не требуется: Android-рантайм входит в зависимости APK.

1. Установи [JDK 17](https://adoptium.net/temurin/releases/?version=17) и [Android Studio](https://developer.android.com/studio).
2. В Android Studio открой **Settings → Languages & Frameworks → Android SDK**. На вкладке **SDK Platforms** установи **Android 15 / API 35**. На вкладке **SDK Tools** установи **Android SDK Build-Tools 35.0.0**, **Android SDK Platform-Tools** и **Android SDK Command-line Tools (latest)**. Для выбора версии включи **Show Package Details**. Сохрани изменения и прими лицензии компонентов.
3. Запомни **Android SDK Location** из этого окна. Обычно это `%LOCALAPPDATA%\Android\Sdk` в Windows, `$HOME/Android/Sdk` в Linux или `$HOME/Library/Android/sdk` в macOS.
4. Получи исходники:

```bash
git clone https://github.com/bespalovnikita/video-downloader.git
cd video-downloader/android
```

Если проект уже склонирован, достаточно перейти в его каталог `android/`. Для обновления исходников используй свою обычную ветку и `git pull --ff-only`; сохрани собственные правки перед переключением веток.

## Вариант 1: Android Studio

1. Нажми **Open** и выбери `video-downloader/android`.
2. В **Settings → Build, Execution, Deployment → Build Tools → Gradle** выбери **Gradle JDK 17**. Встроенная Java другой версии не заменяет эту настройку.
3. Дождись **Gradle Sync**. Если IDE просит путь к SDK, укажи установленный Android SDK; IDE запишет локальный `local.properties`.
4. Выбери вариант сборки **debug** модуля **app** и выполни **Build → Build App Bundle(s) / APK(s) → Build APK(s)**. В новых версиях IDE команда может называться **Generate APKs**. Альтернатива с точной командой: во встроенном Terminal выполни команду из следующего раздела.
5. Получи APK: **`android/app/build/outputs/apk/debug/app-debug.apk`** от корня репозитория. Кнопка **Run** также собирает и устанавливает приложение на выбранное устройство.

## Вариант 2: командная строка

Все следующие команды выполняются из **`video-downloader/android`**.

### Windows / PowerShell

Укажи фактический каталог JDK 17 вместо примера. Переменные ниже действуют в текущем терминале:

```powershell
$env:JAVA_HOME = 'C:\Program Files\Eclipse Adoptium\jdk-17.0.17.10-hotspot'
$env:ANDROID_HOME = "$env:LOCALAPPDATA\Android\Sdk"
$env:Path = "$env:JAVA_HOME\bin;$env:ANDROID_HOME\platform-tools;$env:Path"
java -version
javac -version
.\gradlew.bat --version
.\gradlew.bat :app:assembleDebug
```

Если SDK расположен в другой папке, измени `ANDROID_HOME`. В выводе `java`, `javac` и Gradle должна быть Java **17**.

### Linux / macOS

Подставь фактические пути. В macOS стандартный путь SDK обычно `$HOME/Library/Android/sdk`:

```bash
export JAVA_HOME=/path/to/jdk-17
export ANDROID_HOME="$HOME/Android/Sdk"
export PATH="$JAVA_HOME/bin:$ANDROID_HOME/platform-tools:$PATH"
java -version
javac -version
chmod +x gradlew
./gradlew --version
./gradlew :app:assembleDebug
```

Если SDK ставится без Android Studio, после установки [Command-line Tools](https://developer.android.com/studio#command-tools) выполни `sdkmanager` из `cmdline-tools/latest/bin`:

```bash
sdkmanager --licenses
sdkmanager "platform-tools" "platforms;android-35" "build-tools;35.0.0"
```

Прочитай и прими лицензии в интерактивном диалоге. В PowerShell используй `sdkmanager.bat` с теми же аргументами. Если эта команда не находится в `PATH`, укажи полный путь внутри SDK.

Вместо `ANDROID_HOME` можно создать **`android/local.properties`** с одной строкой `sdk.dir=...`. Для Windows используй прямые слеши, например `sdk.dir=C:/Users/Nikita/AppData/Local/Android/Sdk`. Файл уже исключён из Git; не отправляй в репозиторий свои абсолютные пути.

Результат обеих команд: **`app/build/outputs/apk/debug/app-debug.apk`** относительно каталога `android/`. При повторной сборке достаточно снова выполнить `assembleDebug`; `clean` каждый раз не нужен.

## Установка на телефон

**Через файл:** перенеси `app-debug.apk` на телефон и открой его. Если Android запросит разрешение устанавливать приложения из этого источника, разреши его для выбранного файлового менеджера или браузера.

**Через USB:** включи на телефоне режим разработчика и **Отладку по USB**, подключи устройство и подтверди доверие этому компьютеру:

```bash
adb devices
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

Ключ `-r` обновляет приложение с сохранением его данных, если подпись совпадает. Если подключено несколько устройств, используй `adb -s SERIAL install -r ...`. Для запуска можно открыть значок **Video Downloader** либо выполнить:

```bash
adb shell am start -n ru.bespalov.videodownloader/.MainActivity
```

Debug APK автоматически подписывается локальным отладочным ключом Android. Это пригодный для установки файл, но не подписанный публичный релиз. На другом компьютере отладочный ключ обычно отличается: не удаляй приложение ради обхода ошибки подписи, пока не экспортировал нужные настройки и не сохранил файлы из каталога приложения.

## APK из GitHub Actions

Собирать на своём компьютере необязательно:

1. Открой [Actions → Android Kotlin build](https://github.com/bespalovnikita/video-downloader/actions/workflows/android.yml).
2. Выбери успешный запуск для нужного коммита `main` или PR. Проверки и сборка выполняются автоматически; запуск вручную доступен через **Run workflow**.
3. Внизу страницы, в **Artifacts**, скачай **VideoDownloader-Android-debug**. Для скачивания нужен вход в GitHub.
4. Распакуй ZIP и установи находящийся внутри `app-debug.apk` по инструкции выше.

APK сохраняется после успешных тестов на Android. Отчёты доступны отдельно в **Android-test-reports**. Это отладочная сборка: на временных GitHub runners отладочный ключ создаётся заново, поэтому подпись APK между запусками может отличаться. Для обновлений с сохранением данных используй один постоянный ключ; порядок подписи описан ниже. Перед удалением приложения ради смены подписи экспортируй данные и сохрани нужные файлы.

## Проверки

На Windows замени `./gradlew` на `.\gradlew.bat`:

```bash
./gradlew :core:test :app:testDebugUnitTest :app:lintDebug :app:assembleDebug
```

Локальные unit-тесты и lint не проверяют работу Android-службы на устройстве. Для настоящих инструментальных тестов подключи телефон или запусти эмулятор **API 35, x86_64**, дождись его загрузки, затем выполни:

```bash
adb devices
./gradlew :app:connectedDebugAndroidTest
```

Тесты запускают реальный Android-рантайм и используют локальный тестовый медиафайл. Проверку сворачивания выполняют на длительной загрузке через фоновую службу. Для воспроизведения вручную используй [сценарий проверки фоновой загрузки](../docs/ANDROID.md#background-check).

Отчёты: `core/build/reports/tests/`, `app/build/reports/tests/`, `app/build/reports/androidTests/` и `app/build/reports/lint-results-debug.html`. Аналогичные команды запускает [Android CI](../.github/workflows/android.yml); успешный запуск сохраняет отладочный APK в артефакте **VideoDownloader-Android-debug**. Этот workflow не публикует Android-приложение в Google Play и не создаёт подписанный Android Release.

## Release APK и подпись

```bash
./gradlew :app:assembleRelease
```

В текущей конфигурации команда создаёт **`app/build/outputs/apk/release/app-release-unsigned.apk`**. Неподписанный APK нельзя установить как готовое приложение. Для своей устанавливаемой release-сборки используй в Android Studio **Build → Generate Signed Bundle / APK → APK**, выбери модуль `app` и свой существующий keystore либо создай новый. Храни ключ и пароли вне репозитория, а резервную копию ключа — отдельно: следующие обновления должны иметь ту же подпись. Файлы `*.jks` и `*.keystore` исключены из Git.

Публичное распространение APK требует также учёта лицензий включённого рантайма и предоставления соответствующих исходников; см. [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md). Android-сборка не использует подпись или автоматическую нумерацию Windows ZIP: её `versionCode` и `versionName` находятся в `app/build.gradle.kts`.

## Если сборка не удалась

| Сообщение или симптом | Что проверить |
| --- | --- |
| `JAVA_HOME is not set`, `javac` не найден | Установлена JDK 17 целиком; `JAVA_HOME` указывает на её каталог, а не на `bin` |
| `SDK location not found` | `ANDROID_HOME` или `android/local.properties`, наличие SDK по этому пути |
| `License ... not accepted` / нет API 35 | SDK Manager или `sdkmanager --licenses`; Platform 35 и Build-Tools 35.0.0 |
| Ошибка загрузки Gradle или Maven-зависимостей | Сеть и настройки прокси для Gradle; первая сборка не работает полностью офлайн |
| `Permission denied: ./gradlew` | `chmod +x gradlew` в Linux/macOS |
| `INSTALL_FAILED_NO_MATCHING_ABIS` | Устройство должно поддерживать arm64-v8a или x86_64 |
| `INSTALL_FAILED_UPDATE_INCOMPATIBLE` | Уже установлен APK с другой подписью; используй прежний ключ для обновления |
| Нужен только APK без эмулятора | `:app:assembleDebug` не требует запущенного устройства |

Вернуться к [README проекта](../README.md) или [правилам сборки и выпуска](../RELEASING.md).
