package ru.bespalov.video.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertThrows
import org.junit.Test

class PornHubVideoTest {
    private val watchUrl = "https://www.pornhub.com/view_video.php?viewkey=abcdef1234567"
    private val profile = "https://www.pornhub.com/pornstar/lookup-fixture"

    @Test fun filenameIdAndWatchLinkResolveToTheSameVideo() {
        for (input in listOf(
            "abcdef1234567",
            "Скачанное видео [abcdef1234567]",
            "\"C:\\Video\\Clip [abcdef1234567].mp4\"",
            "https://rt.pornhub.com/view_video.php?src=x&viewkey=abcdef1234567#clip",
        )) assertEquals(input, watchUrl, PornHubVideo.watchUrl(input))

        for (input in listOf(
            profile,
            "Clip [2160p].mp4",
            "https://example.com/[abcdef1234567]",
            "https://www.pornhub.com/view_video.php?viewkey=abcdef1234567&viewkey=other",
        )) assertNull(input, PornHubVideo.watchUrl(input))
    }

    @Test fun profilesMustBelongToTheRequestedVideoAndSite() {
        val json = """{"id":"abcdef1234567","vd_ph_profiles":["$profile","$profile"]}"""
        assertEquals(listOf(profile), PornHubVideo.parseProfiles(json, watchUrl))
        for (invalid in listOf(
            json.replace("abcdef1234567", "different"),
            json.replace("pornhub.com", "pornhub.com.example"),
            json.replace("/pornstar/", "/channels/"),
        )) assertThrows(IllegalArgumentException::class.java) { PornHubVideo.parseProfiles(invalid, watchUrl) }
    }
}
