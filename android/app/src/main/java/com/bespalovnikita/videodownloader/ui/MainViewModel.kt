package com.bespalovnikita.videodownloader.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import com.bespalovnikita.videodownloader.VideoDownloaderApp
import com.bespalovnikita.videodownloader.model.DownloadSettings
import com.bespalovnikita.videodownloader.service.DownloadService
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as VideoDownloaderApp
    val state = app.store.state
    val engineInitError: String get() = app.engineInitError

    fun addText(text: String) {
        val urls = Regex("""https?://\S+""")
            .findAll(text)
            .map { it.value.trimEnd('.', ',', ';', ')', ']', '}') }
            .toList()

        if (urls.isEmpty()) return
        viewModelScope.launch {
            val added = app.store.addUrls(urls)
            added.forEach { app.previewPrefetcher.enqueue(it.url) }
        }
    }

    fun remove(id: String) {
        viewModelScope.launch { app.store.removeQueueItem(id) }
    }

    fun move(id: String, delta: Int) {
        viewModelScope.launch { app.store.moveQueueItem(id, delta) }
    }

    fun retry(id: String) {
        viewModelScope.launch {
            app.store.retryItem(id)
            DownloadService.start(app)
        }
    }

    fun clearDone() {
        viewModelScope.launch { app.store.clearDone() }
    }

    fun prioritizePreview(url: String) {
        if (engineInitError.isBlank()) app.previewPrefetcher.prioritize(url)
    }

    fun updateSettings(transform: (DownloadSettings) -> DownloadSettings) {
        viewModelScope.launch { app.store.updateSettings(transform) }
    }

    fun startDownloads() {
        if (engineInitError.isBlank()) DownloadService.start(app)
    }

    fun pauseDownloads() = DownloadService.pause(app)
    fun resumeDownloads() = DownloadService.resume(app)
    fun stopDownloads() = DownloadService.stop(app)
}
