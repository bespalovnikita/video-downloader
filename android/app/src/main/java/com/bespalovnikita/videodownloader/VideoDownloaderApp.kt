package com.bespalovnikita.videodownloader

import android.app.Application
import com.bespalovnikita.videodownloader.data.AppStore
import com.bespalovnikita.videodownloader.data.PreviewCache
import com.bespalovnikita.videodownloader.downloader.MediaPublisher
import com.bespalovnikita.videodownloader.downloader.PreviewPrefetcher
import com.bespalovnikita.videodownloader.downloader.YtDlpClient
import com.yausername.ffmpeg.FFmpeg
import com.yausername.youtubedl_android.YoutubeDL
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

class VideoDownloaderApp : Application() {
    val appScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    lateinit var store: AppStore
        private set
    lateinit var previewCache: PreviewCache
        private set
    lateinit var ytDlpClient: YtDlpClient
        private set
    lateinit var mediaPublisher: MediaPublisher
        private set
    lateinit var previewPrefetcher: PreviewPrefetcher
        private set

    var engineInitError: String = ""
        private set

    override fun onCreate() {
        super.onCreate()

        store = AppStore(this)
        previewCache = PreviewCache(this)
        ytDlpClient = YtDlpClient()
        mediaPublisher = MediaPublisher(this)

        engineInitError = runCatching {
            YoutubeDL.getInstance().init(this)
            FFmpeg.getInstance().init(this)
        }.exceptionOrNull()?.let { it.message ?: it.javaClass.simpleName }.orEmpty()

        previewPrefetcher = PreviewPrefetcher(
            store = store,
            cache = previewCache,
            client = ytDlpClient,
            scope = appScope
        )

        if (engineInitError.isBlank()) {
            previewPrefetcher.start()
        }

        appScope.launch {
            store.recoverInterrupted()
            if (engineInitError.isBlank()) {
                store.state.value.queue.forEach { previewPrefetcher.enqueue(it.url) }
            }
        }
    }
}
