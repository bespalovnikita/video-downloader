package ru.bespalov.video.core

/** Display strings never participate in state, retry or identity decisions. */
object ErrorText {
    private val urls = Regex("https?://[^\\s<>]+", RegexOption.IGNORE_CASE)
    private val http = Regex("\\bHTTP(?:/\\d(?:\\.\\d)?\\s+|\\s+(?:Error\\s+)?)([45]\\d\\d)\\b", RegexOption.IGNORE_CASE)
    private val permanent = Regex("private video|sign in|login required|video unavailable|video has been removed|requested format is not available", RegexOption.IGNORE_CASE)
    private val temporary = Regex("timeout|timed out|temporary failure|connection reset|connection aborted|connection refused|network is unreachable|dns|too many requests|server error|temporarily unavailable|tls|ssl", RegexOption.IGNORE_CASE)

    fun isTransient(diagnostic: String): Boolean {
        val text = urls.replace(diagnostic, "")
        val codes = http.findAll(text).map { it.groupValues[1].toInt() }.toList()
        if (codes.any { it in setOf(400, 401, 403, 404, 405, 410, 451) } || permanent.containsMatchIn(text)) return false
        return codes.any { it == 429 || it >= 500 } || temporary.containsMatchIn(text)
    }

    fun display(raw: String, language: String = "ru"): String {
        if (!language.lowercase().startsWith("ru") || raw.isBlank()) return raw
        if (Regex("^[А-Яа-яЁё]").containsMatchIn(raw)) return raw
        val text = urls.replace(raw, "")
        val codes = http.findAll(text).map { it.groupValues[1].toInt() }.toList()
        val code = codes.firstOrNull { it in setOf(400, 401, 403, 404, 405, 410, 451) } ?: codes.firstOrNull()
        if (code != null) return when (code) {
            401 -> "Нужен вход в аккаунт (HTTP 401). Подключите актуальный файл cookies."
            403 -> "Сайт запретил доступ (HTTP 403). Проверьте видео в браузере, cookies и версию yt-dlp."
            404, 410 -> "Видео не найдено или удалено (HTTP $code). Проверьте исходную ссылку."
            429 -> "Слишком много запросов (HTTP 429). Подождите и уменьшите параллельность."
            in 500..599 -> "Ошибка сервера (HTTP $code). Повторите позже."
            else -> "Сайт отклонил запрос (HTTP $code). Проверьте ссылку и исходную диагностику."
        }
        fun contains(pattern: String) = Regex(pattern, RegexOption.IGNORE_CASE).containsMatchIn(text)
        return when {
            contains("sign in|login required|private video|confirm your age|not a bot") -> "Сайт требует авторизацию или проверку пользователя. Откройте видео в браузере и подключите актуальный файл cookies."
            contains("unsupported URL|no suitable extractor") -> "Адрес не поддерживается текущим yt-dlp. Проверьте ссылку и обновите приложение."
            contains("DRM[- ]protected|protected (with|by) DRM") -> "Материал защищён DRM и не может быть скачан обычным способом."
            contains("requested format is not available|no suitable quality") -> "Выбранное качество или кодек недоступны. Выберите другой профиль."
            contains("video unavailable|removed|deleted") -> "Видео недоступно или удалено. Проверьте ссылку в браузере."
            contains("timeout|timed out") -> "Истекло время ожидания. Проверьте сеть и повторите действие."
            contains("permission denied|access denied") -> "Нет доступа к файлу или папке. Выберите папку заново."
            contains("no space left|disk full|not enough space") -> "Недостаточно места. Освободите память устройства."
            contains("dns|name resolution|connection|network is unreachable") -> "Не удалось подключиться к сайту. Проверьте сеть."
            else -> "Не удалось выполнить операцию. Откройте исходную диагностику для подробностей."
        }
    }
}
