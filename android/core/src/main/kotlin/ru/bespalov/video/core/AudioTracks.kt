package ru.bespalov.video.core

/** Strict selection shared by settings validation and native request construction. */
object AudioTracks {
    val names: Map<String, String> = linkedMapOf(
        "Default" to "По умолчанию", "Original" to "Оригинальная", "Languages" to "По языкам",
    )

    private fun languages(settings: Settings): List<String> {
        require(settings.audioLanguages.length <= 128 && settings.audioLanguages.none { it.isISOControl() }) {
            "Укажите не более восьми кодов языков аудио без управляющих символов, например ru,en."
        }
        val values = settings.audioLanguages.split(',').map(String::trim)
        require(values.size in 1..8 && values.all { Regex("[a-zA-Z]{2,3}(?:-[a-zA-Z0-9]{2,8})*").matches(it) }) {
            "Укажите коды языков аудио через запятую, например ru, en или pt-BR."
        }
        return values.map { it.lowercase(java.util.Locale.ROOT) }.distinct()
    }

    fun validate(settings: Settings) {
        require(settings.audioTrack in names) { "Неизвестный режим выбора аудиодорожки." }
        if (settings.audioTrack != "Languages" || languages(settings).size <= 1) return
        require(!settings.audioOnly) { "MP3 поддерживает одну аудиодорожку. Выберите один язык или отключите «Только аудио MP3»." }
        require(settings.container in setOf("Auto", "MKV")) { "Для нескольких аудиодорожек выберите контейнер Auto или MKV." }
    }

    fun multiple(settings: Settings): Boolean = settings.audioTrack == "Languages" && languages(settings).size > 1
    fun container(settings: Settings): String = if (multiple(settings)) "MKV" else settings.container

    fun formatSelector(settings: Settings, quality: String): String? {
        validate(settings)
        val height = if (quality == "best") "" else "[height<=$quality]"
        val codec = when (settings.codec) { "H264" -> "avc1"; "VP9" -> "vp9"; "AV1" -> "av01"; else -> "" }
        if (settings.audioTrack == "Default") {
            if (settings.audioOnly) return null
            val generic = "bv*$height+ba/b$height"
            return if (codec.isEmpty()) generic else "bv*$height[vcodec^=$codec]+ba/b$height[vcodec^=$codec]/$generic"
        }
        // An unmarked/default track is not evidence of the original language.
        val filters = if (settings.audioTrack == "Original") listOf("[format_note~='(?i)(?:^original$|\\(original\\))']")
            else languages(settings).map { "[language~='(?i)^$it(?:-|$)']" }
        val audio = filters.joinToString("+") { "ba$it" }
        if (settings.audioOnly) return "$audio/b${filters.single()}"
        fun video(codecFilter: String): String {
            val bound = height + codecFilter
            // bv excludes embedded audio, which could displace the chosen track.
            val result = "bv$bound+$audio"
            return if (filters.size == 1) "$result/b$bound${filters.single()}" else result
        }
        return if (codec.isEmpty()) video("") else "${video("[vcodec^=$codec]")}/${video("")}"
    }
}
