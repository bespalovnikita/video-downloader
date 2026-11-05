package ru.bespalov.videodownloader.engine

import ru.bespalov.video.core.PornHubCollections
import ru.bespalov.video.core.RecentScanParser
import ru.bespalov.video.core.ScanScope
import ru.bespalov.video.core.SubscriptionScanLimits

/** Explicit collection mode; arbitrary flat metadata is never proof of a full archive. */
internal object SubscriptionScanArguments {
    fun build(sourceUrl: String, pluginRoot: String, scope: ScanScope,
        limits: SubscriptionScanLimits = SubscriptionScanLimits()): List<String> {
        val recent = scope == ScanScope.RecentWindow
        val arguments = mutableListOf("--ignore-config", "--dump-single-json", "--skip-download", "--abort-on-error",
            "--no-color", "--no-progress", "--encoding", "utf-8", "--socket-timeout", "20")
        if (PornHubCollections.listingUrl(sourceUrl) != null) {
            arguments += listOf("--no-plugin-dirs", "--plugin-dirs", pluginRoot,
                "--use-extractors", "videodownloader:pornhub:channel", "--flat-playlist",
                "--extractor-args", "VideoDownloaderPornHubChannel:max_items=${limits.maxItems};max_pages=${limits.maxChannelPages}" +
                    if (recent) ";recent_window=${RecentScanParser.RECENT_WINDOW_SIZE}" else "")
        } else {
            arguments += listOf(if (recent) "--flat-playlist" else "--no-flat-playlist", "--playlist-end",
                (if (recent) RecentScanParser.RECENT_WINDOW_SIZE else limits.maxItems + 1).toString())
        }
        return arguments
    }
}
