package com.bespalovnikita.videodownloader.service

import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat
import com.bespalovnikita.videodownloader.MainActivity
import com.bespalovnikita.videodownloader.VideoDownloaderApp
import com.bespalovnikita.videodownloader.downloader.DownloadPolicy
import com.bespalovnikita.videodownloader.model.HistoryItem
import com.bespalovnikita.videodownloader.model.QueueItem
import com.bespalovnikita.videodownloader.model.QueueStatus
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.cancel
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.File
import java.util.concurrent.ConcurrentHashMap
import kotlin.math.pow

class DownloadService : Service() {
    private val serviceScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val activeProcesses = ConcurrentHashMap<String, String>()
    private var runner: Job? = null
    @Volatile private var paused = false
    @Volatile private var stopping = false

    private val app: VideoDownloaderApp get() = application as VideoDownloaderApp

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        ensureForeground()
        when (intent?.action ?: ACTION_START) {
            ACTION_START -> startRunner()
            ACTION_PAUSE -> pauseDownloads()
            ACTION_RESUME -> resumeDownloads()
            ACTION_STOP -> stopDownloads()
        }
        return START_NOT_STICKY
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onDestroy() {
        activeProcesses.values.forEach { app.ytDlpClient.stop(it) }
        serviceScope.cancel()
        super.onDestroy()
    }

    private fun startRunner() {
        if (runner?.isActive == true) return
        stopping = false
        runner = serviceScope.launch {
            processQueue()
        }
    }

    private suspend fun processQueue() {
        while (serviceScope.isActive && !stopping) {
            if (paused) {
                delay(250)
                continue
            }

            val state = app.store.state.value
            val pending = state.queue.filter { it.status == QueueStatus.QUEUED }
            if (pending.isEmpty()) break

            val parallel = state.settings.parallelDownloads.coerceIn(1, 4)
            coroutineScope {
                pending.take(parallel).map { item ->
                    async { downloadOne(item) }
                }.awaitAll()
            }
        }

        if (!paused && !stopping) {
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private suspend fun downloadOne(item: QueueItem) {
        val settings = app.store.state.value.settings
        val base = getExternalFilesDir(null) ?: filesDir
        val stagingDir = File(base, "staging/" + item.id).apply {
            deleteRecursively()
            mkdirs()
        }

        for (attempt in 0..settings.retries) {
            if (paused || stopping) return

            if (attempt > 0) {
                val delaySeconds = minOf(
                    60.0,
                    settings.retryDelaySeconds * 2.0.pow((attempt - 1).toDouble())
                ).toLong()

                app.store.updateQueueItem(item.id) {
                    it.copy(
                        status = QueueStatus.RETRYING,
                        statusLine = "Retry " + attempt + "/" + settings.retries + " in " + delaySeconds + "s"
                    )
                }
                updateNotification()
                delay(delaySeconds * 1000L)
                if (paused || stopping) return
            }

            val processId = "download-" + item.id + "-" + attempt
            activeProcesses[item.id] = processId

            try {
                app.store.updateQueueItem(item.id) {
                    it.copy(
                        status = QueueStatus.STARTING,
                        progress = 0f,
                        etaSeconds = -1,
                        statusLine = "Starting",
                        error = ""
                    )
                }

                val result = app.ytDlpClient.download(
                    url = item.url,
                    settings = settings,
                    stagingDir = stagingDir,
                    processId = processId
                ) { progress ->
                    serviceScope.launch {
                        app.store.updateQueueItem(item.id) { current ->
                            current.copy(
                                status = QueueStatus.DOWNLOADING,
                                progress = progress.percent.coerceIn(0f, 100f),
                                etaSeconds = progress.etaSeconds,
                                statusLine = progress.line.take(180)
                            )
                        }
                        updateNotification()
                    }
                }

                app.store.updateQueueItem(item.id) {
                    it.copy(status = QueueStatus.FINALIZING, statusLine = "Publishing to Downloads")
                }

                val published = app.mediaPublisher.publish(stagingDir, result.finalPath)
                val title = app.store.state.value.queue.firstOrNull { it.id == item.id }?.title
                    ?.takeIf { it.isNotBlank() }
                    ?: published.primaryFileName

                app.store.updateQueueItem(item.id) {
                    it.copy(
                        status = QueueStatus.DONE,
                        progress = 100f,
                        etaSeconds = 0,
                        statusLine = "Done",
                        outputUri = published.primaryUri,
                        error = ""
                    )
                }

                app.store.addHistory(
                    HistoryItem(
                        sourceUrl = item.url,
                        title = title,
                        outputUri = published.primaryUri,
                        fileName = published.primaryFileName,
                        mimeType = published.primaryMimeType
                    )
                )
                updateNotification()
                return
            } catch (t: Throwable) {
                val message = t.message ?: t.javaClass.simpleName

                if (paused) {
                    app.store.updateQueueItem(item.id) {
                        it.copy(status = QueueStatus.PAUSED, statusLine = "Paused")
                    }
                    return
                }

                if (stopping) {
                    app.store.updateQueueItem(item.id) {
                        it.copy(status = QueueStatus.QUEUED, statusLine = "Stopped")
                    }
                    return
                }

                val canRetry = attempt < settings.retries && DownloadPolicy.isTransientError(message)
                if (!canRetry) {
                    app.store.updateQueueItem(item.id) {
                        it.copy(
                            status = QueueStatus.ERROR,
                            statusLine = "Error",
                            error = message
                        )
                    }
                    updateNotification()
                    return
                }
            } finally {
                activeProcesses.remove(item.id)
            }
        }
    }

    private fun pauseDownloads() {
        paused = true
        activeProcesses.values.toList().forEach { app.ytDlpClient.stop(it) }
        serviceScope.launch {
            activeProcesses.keys.toList().forEach { id ->
                app.store.updateQueueItem(id) {
                    it.copy(status = QueueStatus.PAUSED, statusLine = "Paused")
                }
            }
            updateNotification()
        }
    }

    private fun resumeDownloads() {
        serviceScope.launch {
            app.store.resumePaused()
            paused = false
            updateNotification()
            startRunner()
        }
    }

    private fun stopDownloads() {
        stopping = true
        paused = false
        activeProcesses.values.toList().forEach { app.ytDlpClient.stop(it) }
        runner?.cancel()
        serviceScope.launch {
            activeProcesses.keys.toList().forEach { id ->
                app.store.updateQueueItem(id) {
                    it.copy(status = QueueStatus.QUEUED, statusLine = "Stopped")
                }
            }
            stopForeground(STOP_FOREGROUND_REMOVE)
            stopSelf()
        }
    }

    private fun ensureForeground() {
        startForeground(NOTIFICATION_ID, buildNotification())
    }

    private fun updateNotification() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.notify(NOTIFICATION_ID, buildNotification())
    }

    private fun buildNotification(): android.app.Notification {
        val state = app.store.state.value
        val total = state.queue.size
        val done = state.queue.count { it.status == QueueStatus.DONE }
        val active = state.queue.filter {
            it.status == QueueStatus.STARTING ||
                it.status == QueueStatus.DOWNLOADING ||
                it.status == QueueStatus.RETRYING ||
                it.status == QueueStatus.FINALIZING
        }
        val average = if (active.isEmpty()) 0 else active.map { it.progress }.average().toInt()

        val openIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
        )

        fun serviceAction(action: String, requestCode: Int): PendingIntent =
            PendingIntent.getService(
                this,
                requestCode,
                Intent(this, DownloadService::class.java).setAction(action),
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE
            )

        val builder = NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle("Video Downloader")
            .setContentText(
                if (paused) "Paused · $done/$total"
                else active.size.toString() + " active · " + done + "/" + total
            )
            .setContentIntent(openIntent)
            .setOngoing(!stopping)
            .setOnlyAlertOnce(true)
            .setProgress(100, average, active.isEmpty() && done < total)

        if (paused) {
            builder.addAction(0, "Resume", serviceAction(ACTION_RESUME, 2))
        } else {
            builder.addAction(0, "Pause", serviceAction(ACTION_PAUSE, 1))
        }
        builder.addAction(0, "Stop", serviceAction(ACTION_STOP, 3))
        return builder.build()
    }

    private fun createNotificationChannel() {
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                "Downloads",
                NotificationManager.IMPORTANCE_LOW
            )
        )
    }

    companion object {
        private const val CHANNEL_ID = "downloads"
        private const val NOTIFICATION_ID = 1001

        const val ACTION_START = "com.bespalovnikita.videodownloader.START"
        const val ACTION_PAUSE = "com.bespalovnikita.videodownloader.PAUSE"
        const val ACTION_RESUME = "com.bespalovnikita.videodownloader.RESUME"
        const val ACTION_STOP = "com.bespalovnikita.videodownloader.STOP"

        fun start(context: Context) = send(context, ACTION_START)
        fun pause(context: Context) = send(context, ACTION_PAUSE)
        fun resume(context: Context) = send(context, ACTION_RESUME)
        fun stop(context: Context) = send(context, ACTION_STOP)

        private fun send(context: Context, action: String) {
            ContextCompat.startForegroundService(
                context,
                Intent(context, DownloadService::class.java).setAction(action)
            )
        }
    }
}
