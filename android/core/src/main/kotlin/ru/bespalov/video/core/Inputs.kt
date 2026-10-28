package ru.bespalov.video.core

import java.net.URI
import java.text.Normalizer

object Urls {
    fun isHttp(value: String): Boolean = try {
        val uri = URI(value)
        value.isNotBlank() && value.none { it.isWhitespace() || it.isISOControl() } &&
            uri.scheme?.lowercase() in setOf("http", "https") && !uri.host.isNullOrBlank() &&
            uri.rawUserInfo == null
    } catch (_: Exception) { false }

    /** Values remain exact: case, query, escaping and fragment are identity. */
    fun extract(text: String): List<String> = text.split(Regex("\\s+"))
        .filter(::isHttp).distinct()

    fun fromFile(name: String, text: String): List<String> = when {
        name.endsWith(".url", ignoreCase = true) -> text.lineSequence()
            .map(String::trimStart).filter { it.startsWith("URL=", ignoreCase = true) }
            .flatMap { extract(it.substringAfter('=')).asSequence() }.distinct().toList()
        name.endsWith(".txt", ignoreCase = true) -> extract(text)
        else -> emptyList()
    }
}

object FilenameTemplates {
    fun validate(template: String) {
        require(template.isNotBlank() && template.length <= 1000) { "Укажите относительный шаблон имени файла." }
        require(!template.startsWith('/') && !template.startsWith('\\') &&
            !Regex("^[A-Za-z]:").containsMatchIn(template) &&
            !Regex("^[a-zA-Z][a-zA-Z0-9+.-]*://").containsMatchIn(template) &&
            template.none { it.isISOControl() } &&
            template.split('/', '\\').none { it == ".." || it.isEmpty() }) {
            "Шаблон имени должен быть относительным, без «..», буквы диска и пустых папок."
        }
    }
}

/** Validate persisted and pinned options before handing any values to the native engine. */
object SettingsRules {
    fun validate(settings: Settings) {
        require(settings.profile in Profiles.names) { "Неизвестный профиль." }
        require(settings.quality in setOf("Best", "2160", "1440", "1080", "720")) { "Неизвестное качество." }
        require(settings.codec in setOf("Auto", "H264", "VP9", "AV1")) { "Неизвестный кодек." }
        require(settings.container in setOf("Auto", "MP4", "MKV", "WebM")) { "Неизвестный контейнер." }
        require(settings.threads in 1..4 && settings.fragments in 1..8) { "Некорректная параллельность." }
        require(settings.rateLimit == "Unlimited" || Regex("^[1-9][0-9]*(?:\\.[0-9]+)?[KMG]?$", RegexOption.IGNORE_CASE)
            .matches(settings.rateLimit)) { "Некорректное ограничение скорости." }
        require(settings.subtitleLangs.length <= 1000 && settings.subtitleLangs.none { it.isISOControl() }) { "Некорректные языки субтитров." }
        require(settings.cookiesFile.none { it.isISOControl() } && settings.outputTreeUri.none { it.isISOControl() }) { "Некорректный путь настроек." }
        FilenameTemplates.validate(settings.filenameTemplate)
    }
}

object SubscriptionNames {
    fun isValid(value: String): Boolean = value.isNotBlank() && value.length <= 80 && value !in setOf(".", "..") &&
        !value.endsWith('.') && value.none { it.isISOControl() || it in "/\\:*?\"<>|" }

    fun create(suggestion: String, id: String, existing: Collection<String> = emptyList()): String {
        val normalized = Normalizer.normalize(suggestion, Normalizer.Form.NFC)
            .replace(Regex("[\\p{Cntrl}/\\\\:*?\"<>|]"), " ")
            .replace(Regex("\\s+"), " ").trim().trimEnd('.').take(70).trim()
        val base = normalized.takeIf(::isValid) ?: id.take(32).takeIf(::isValid) ?: "Подписка"
        val occupied = existing.map(String::lowercase).toSet()
        if (base.lowercase() !in occupied) return base
        var suffix = 2
        while ("$base ($suffix)".lowercase() in occupied) suffix++
        return "$base ($suffix)"
    }
}

object Profiles {
    val names: Map<String, String> = linkedMapOf(
        "Universal" to "Универсальный", "4K archive" to "Архив 4K",
        "MP4 compatibility" to "Совместимый MP4", "Music MP3" to "Музыка MP3",
        "Subtitles archive" to "Архив с субтитрами",
    )

    fun apply(settings: Settings, id: String): Settings {
        require(id in names) { "Неизвестный профиль: $id" }
        val clean = settings.copy(profile = id, quality = "Best", codec = "Auto", container = "Auto",
            audioOnly = false, archive = false, writeSubs = false, autoSubs = false, embedSubs = false,
            embedThumbnail = false, embedMetadata = true, embedChapters = true)
        return when (id) {
            "4K archive" -> clean.copy(quality = "2160", container = "MKV", archive = true, embedThumbnail = true)
            "MP4 compatibility" -> clean.copy(quality = "1080", codec = "H264", container = "MP4", embedThumbnail = true)
            "Music MP3" -> clean.copy(audioOnly = true, embedThumbnail = true)
            "Subtitles archive" -> clean.copy(container = "MKV", writeSubs = true, autoSubs = true, embedSubs = true)
            else -> clean
        }
    }
}
