package ru.bespalov.videodownloader.download

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineStart
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.SubscriptionRules
import ru.bespalov.videodownloader.DownloaderApplication
import ru.bespalov.videodownloader.MainActivity
import ru.bespalov.videodownloader.R
import ru.bespalov.videodownloader.engine.EngineProgress
import java.io.File
import java.io.IOException
import java.time.LocalDate
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * The foreground Service, rather than an Activity/ViewModel, owns every native download.
 * Home, screen rotation, and removing the Activity from Recents do not cancel its scope.
 * Force-stop/process death do not restart transfers silently: the persisted queue is recovered
 * as interrupted on the next explicit launch. Android 15's dataSync time limit also applies.
 */
class DownloadService : Service() {
    private val graph get() = (application as DownloaderApplication).graph
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val gate = Mutex()
    private val pending = LinkedHashSet<String>()
    private val active = ConcurrentHashMap<String, Transfer>()
    private lateinit var publisher: MediaPublisher
    private lateinit var wakeLock: PowerManager.WakeLock
    @Volatile private var paused = false
    @Volatile private var lastStart = 0
    @Volatile private var destroyed = false
    private var concurrency = 1
    private val lastNotification = AtomicLong()
    private val commandsInFlight = AtomicInteger()
    private val stageRoot by lazy { File(filesDir, "staging") }

    private enum class Reason { PAUSE, STOP, REMOVE }
    private class Transfer(val id: String) {
        val requestId = "download-${UUID.randomUUID()}"
        val reason = AtomicReference<Reason?>(null)
        val title = AtomicReference("")
        val committed = AtomicBoolean(false)
        var job: Job? = null
        var deleteFiles = true
    }

    override fun onCreate() {
        super.onCreate()
        currentService.set(this)
        publisher = MediaPublisher(this)
        wakeLock = (getSystemService(POWER_SERVICE) as PowerManager)
            .newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "$packageName:downloads").apply { setReferenceCounted(false) }
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(NotificationChannel(CHANNEL, getString(R.string.download_channel_name), NotificationManager.IMPORTANCE_LOW).apply {
            description = getString(R.string.download_channel_description)
        })
        scope.launch {
            while (true) {
                delay(WAKE_RENEW_MILLIS)
                gate.withLock {
                    if (!destroyed && !paused && active.isNotEmpty()) acquireWakeLock()
                    else releaseWakeLock()
                }
            }
        }
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (destroyed || currentService.get() !== this) {
            stopSelfResult(startId)
            return START_NOT_STICKY
        }
        // Main-thread admission precedes foreground activation. An idle worker must not
        // stop this service while this command waits for initialization/import/the gate.
        commandsInFlight.incrementAndGet()
        lastStart = startId
        // Required within five seconds, before initialization, storage or native binaries run.
        ServiceCompat.startForeground(this, NOTIFICATION, notification(),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0)
        mutableRunning.value = true
        scope.launch {
            try {
                graph.ready.await()
                graph.dataOperation.withLock {
                    check(!graph.importing.value) { "Data import is still in progress" }
                    gate.withLock {
                        check(!graph.repository.readOnly) { graph.repository.error.ifBlank { "State is read-only" } }
                        when (intent?.action) {
                            ACTION_START -> accept(intent.getStringArrayListExtra(IDS).orEmpty())
                            ACTION_PAUSE -> pauseLocked()
                            ACTION_RESUME -> { paused = false; mutablePaused.value = false }
                            ACTION_STOP -> stopLocked()
                            ACTION_CANCEL -> cancelLocked(intent.getStringExtra(ID).orEmpty(), false, false)
                            ACTION_REMOVE -> cancelLocked(intent.getStringExtra(ID).orEmpty(), true, intent.getBooleanExtra(DELETE_FILES, true))
                        }
                        scheduleLocked()
                    }
                }
            } catch (error: Exception) {
                graph.log("Download command: ${error.stackTraceToString()}")
                gate.withLock { if (active.isEmpty()) pending.clear() }
            } finally {
                withContext(NonCancellable) {
                    gate.withLock {
                        commandsInFlight.decrementAndGet()
                        if (active.isEmpty() && pending.isEmpty()) finishIdleLocked()
                    }
                }
            }
        }
        // A killed process may have unfinished files; recovery requires a visible user decision.
        return START_NOT_STICKY
    }

    private suspend fun accept(ids: List<String>) {
        check(graph.runtimeReady.value) { graph.startupError.value.ifBlank { "Download runtime is not ready" } }
        val selected = ids.distinct().filter { id -> !active.containsKey(id) && id !in pending && id !in claimedTasks }.toSet()
        if (selected.isEmpty()) return
        val date = LocalDate.now().toString()
        val session = UUID.randomUUID().toString()
        val accepted = mutableSetOf<String>()
        val state = graph.repository.update { state ->
            state.copy(queue = state.queue.map { task ->
                if (task.id !in selected || task.pendingRemoval || task.status in setOf(DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.CANCELLING, DownloadStatus.RUNNING)) task
                else {
                    // An approved repeat uses a new staging area; earlier published copies remain history-owned.
                    val repeat = task.status == DownloadStatus.COMPLETED
                    val resume = !repeat && task.stageDirectory.isNotBlank()
                    accepted += task.id
                    task.copy(status = DownloadStatus.QUEUED, progress = 0f, speed = "", eta = "", rawError = "",
                        deferredNextStart = false, outputUris = if (repeat) emptyList() else task.outputUris,
                        runId = if (resume) task.runId else session,
                        stageDirectory = if (resume) task.stageDirectory else "",
                        settingsSnapshot = if (resume) task.settingsSnapshot ?: state.settings else state.settings,
                        archiveDate = if (resume) task.archiveDate.ifBlank { date } else date)
                }
            })
        }
        pending.addAll(accepted)
        concurrency = state.settings.threads.coerceIn(1, 4)
        paused = false
        mutablePaused.value = false
    }

    private suspend fun pauseLocked() {
        paused = true
        mutablePaused.value = true
        active.values.forEach { task ->
            if (!task.committed.get() && task.reason.compareAndSet(null, Reason.PAUSE)) {
                pending += task.id
                task.job?.cancel(CancellationException("Paused by user"))
                graph.engine.cancel(task.requestId)
            }
        }
        graph.repository.update { state -> state.copy(queue = state.queue.map {
            if (it.id in pending && !active.containsKey(it.id)) it.copy(status = DownloadStatus.PAUSED) else it
        }) }
        releaseWakeLock()
    }

    private suspend fun stopLocked() {
        val waiting = pending.toSet()
        pending.clear()
        paused = false
        mutablePaused.value = false
        active.values.filterNot { it.committed.get() }.forEach { task ->
            if (task.reason.get() != Reason.REMOVE) task.reason.set(Reason.STOP)
            task.job?.cancel(CancellationException("Stopped by user"))
            graph.engine.cancel(task.requestId)
        }
        graph.repository.update { state -> state.copy(queue = state.queue.map {
            if (it.id in waiting && !active.containsKey(it.id)) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "")
            else if (active[it.id]?.committed?.get() == false) it.copy(status = DownloadStatus.CANCELLING) else it
        }) }
    }

    private suspend fun cancelLocked(id: String, remove: Boolean, deleteFiles: Boolean) {
        pending.remove(id)
        val running = active[id]
        if (running != null) {
            if (running.committed.get() && !remove) return
            running.deleteFiles = deleteFiles
            running.reason.set(if (remove) Reason.REMOVE else Reason.STOP)
            graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id == id) it.copy(status = DownloadStatus.CANCELLING, pendingRemoval = remove) else it
            }) }
            running.job?.cancel(CancellationException("URL cancelled by user"))
            graph.engine.cancel(running.requestId)
        } else {
            val task = graph.repository.state.value.queue.firstOrNull { it.id == id } ?: return
            if (remove) removeIdleLocked(task, deleteFiles)
            else graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id == id && it.status != DownloadStatus.COMPLETED) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "") else it
            }) }
        }
    }

    private suspend fun scheduleLocked() {
        if (destroyed) {
            val abandoned = pending.toSet()
            pending.clear()
            graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id in abandoned && !active.containsKey(it.id)) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "") else it
            }) }
            return
        }
        if (!paused && !destroyed) {
            while (active.size < concurrency) {
                val id = pending.firstOrNull { !active.containsKey(it) } ?: break
                pending.remove(id)
                val task = graph.repository.state.value.queue.firstOrNull { it.id == id }
                if (task == null || task.pendingRemoval || task.status == DownloadStatus.CLEANUP_REQUIRED) continue
                // An old service may still be joining native cancellation after a system timeout.
                if (!claimedTasks.add(id)) continue
                val transfer = Transfer(id)
                active[id] = transfer
                transfer.job = scope.launch(start = CoroutineStart.UNDISPATCHED) { perform(transfer) }
            }
        }
        if (active.isEmpty() && pending.isEmpty()) finishIdleLocked()
        else {
            mutableRunning.value = true
            mutablePaused.value = paused
            if (!paused && active.isNotEmpty()) acquireWakeLock() else releaseWakeLock()
            notifyProgress(force = true)
        }
    }

    private suspend fun perform(transfer: Transfer) {
        var stage: File? = null
        var owned = emptyList<String>()
        var committed = false
        try {
            val original = graph.repository.state.value.queue.single { it.id == transfer.id }
            val workStage = StorageRules.stage(stageRoot, original.id, original.runId, original.stageDirectory)
            stage = workStage
            graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id == transfer.id) it.copy(status = DownloadStatus.RUNNING, stageDirectory = workStage.absolutePath) else it
            }) }
            checkNotCancelled(transfer)
            if (!workStage.isDirectory && !workStage.mkdirs()) throw IOException("Cannot create private staging directory")
            val result = graph.engine.download(transfer.requestId, original, original.settingsSnapshot ?: graph.repository.state.value.settings, workStage) { update ->
                update.title?.takeIf(String::isNotBlank)?.let(transfer.title::set)
                mutableProgress.update { values ->
                    val previous = values[transfer.id]
                    val next = if (update.totalBytes == 0L && update.downloadedBytes == 0L && update.percent == 0f && previous != null)
                        previous.copy(title = update.title ?: previous.title, transferredBytes = maxOf(previous.transferredBytes, update.transferredBytes)) else update
                    values + (transfer.id to next)
                }
                notifyProgress()
            }
            checkNotCancelled(transfer)
            val batch = publisher.publish(workStage, result.stagedFiles, result.primary,
                original.settingsSnapshot ?: graph.repository.state.value.settings,
                original.subscriptionName, original.archiveDate, { transfer.reason.get() != null || destroyed }) { manifest ->
                owned = manifest
                graph.repository.update { state -> state.copy(queue = state.queue.map {
                    if (it.id == transfer.id) it.copy(outputUris = manifest) else it
                }) }
            }
            gate.withLock {
                checkNotCancelled(transfer)
                // The last cancellation check and successful-history update are serialized with commands.
                withContext(NonCancellable) {
                    graph.repository.update { state ->
                        val progress = mutableProgress.value[transfer.id]
                        val updated = state.copy(queue = state.queue.map {
                            if (it.id == transfer.id) it.copy(transferredBytes = result.transferredBytes,
                                title = transfer.title.get().ifBlank { it.title }, progress = progress?.percent ?: it.progress) else it
                        })
                        val success = SubscriptionRules.recordSuccess(updated, transfer.id, batch.uris, transfer.title.get())
                        // Keep private cleanup ownership durable until removal is confirmed.
                        success.copy(queue = success.queue.map {
                            if (it.id == transfer.id) it.copy(stageDirectory = workStage.absolutePath, runId = original.runId) else it
                        })
                    }
                    committed = true
                    transfer.committed.set(true)
                }
            }
            // No native process remains. Staging contains only our verified private files.
            withContext(NonCancellable) {
                val cleaned = deleteStage(workStage)
                graph.repository.update { state -> state.copy(queue = state.queue.map {
                    if (it.id != transfer.id) it
                    else if (cleaned) it.copy(stageDirectory = "", runId = "")
                    else it.copy(status = DownloadStatus.CLEANUP_REQUIRED,
                        rawError = "The download was published, but its private staging directory could not be removed.")
                }) }
            }
        } catch (error: Throwable) {
            withContext(NonCancellable) {
                gate.withLock {
                    if (!committed) {
                        val retained = publisher.deleteOwned(owned)
                        val task = graph.repository.state.value.queue.firstOrNull { it.id == transfer.id }
                        val reason = transfer.reason.get()
                        if (task != null) {
                            val remove = reason == Reason.REMOVE || task.pendingRemoval
                            val stageClean = if (remove) stage?.let(::deleteStage) ?: safeDeleteStage(task) else true
                            graph.repository.update { state -> state.copy(queue = state.queue.mapNotNull {
                                if (it.id != transfer.id) it
                                else if (remove && retained.isEmpty() && stageClean) null
                                else it.copy(
                                    status = when {
                                        retained.isNotEmpty() || !stageClean -> DownloadStatus.CLEANUP_REQUIRED
                                        reason == Reason.PAUSE -> DownloadStatus.PAUSED
                                        reason != null || error is CancellationException -> DownloadStatus.INTERRUPTED
                                        else -> DownloadStatus.FAILED
                                    }, outputUris = retained, pendingRemoval = remove,
                                    rawError = if (reason != null && retained.isEmpty() && stageClean) "" else error.message ?: error.javaClass.simpleName,
                                    speed = "", eta = "", title = transfer.title.get().ifBlank { it.title },
                                    transferredBytes = mutableProgress.value[transfer.id]?.transferredBytes ?: it.transferredBytes)
                            }) }
                        }
                    }
                }
                if (error !is CancellationException) graph.log("Download ${transfer.id}: ${error.stackTraceToString()}")
            }
        } finally {
            withContext(NonCancellable) {
                gate.withLock {
                    if (committed && transfer.reason.get() == Reason.REMOVE) {
                        graph.repository.state.value.queue.firstOrNull { it.id == transfer.id }?.let {
                            removeIdleLocked(it, transfer.deleteFiles)
                        }
                    }
                    active.remove(transfer.id)
                    claimedTasks.remove(transfer.id)
                    mutableProgress.update { it - transfer.id }
                    scheduleLocked()
                }
            }
        }
    }

    private fun checkNotCancelled(transfer: Transfer) {
        if (transfer.reason.get() != null || destroyed) throw CancellationException("Download interrupted")
    }

    private suspend fun removeIdleLocked(task: DownloadTask, deleteFiles: Boolean) {
        // Persist intent before destructive operations, retaining the owned manifest if cleanup fails.
        graph.repository.update { state -> state.copy(queue = state.queue.map {
            if (it.id == task.id) it.copy(pendingRemoval = true, status = DownloadStatus.CLEANUP_REQUIRED) else it
        }) }
        val publishedSuccessfully = graph.repository.state.value.history.any { record ->
            record.url == task.url && task.outputUris.isNotEmpty() && record.outputUris.containsAll(task.outputUris)
        }
        // "Keep files" applies to completed publications; incomplete publication remnants are rolled back.
        val retained = if (deleteFiles || !publishedSuccessfully) publisher.deleteOwned(task.outputUris) else emptyList()
        val clean = safeDeleteStage(task)
        graph.repository.update { state -> state.copy(queue = state.queue.mapNotNull {
            if (it.id != task.id) it
            else if (retained.isEmpty() && clean) null
            else it.copy(outputUris = retained, status = DownloadStatus.CLEANUP_REQUIRED,
                rawError = "Some owned files could not be deleted. Retry removal after restoring storage access.")
        }) }
    }

    private fun safeDeleteStage(task: DownloadTask): Boolean = try {
        if (task.stageDirectory.isBlank()) true
        else deleteStage(StorageRules.stage(stageRoot, task.id, task.runId, task.stageDirectory))
    } catch (error: Exception) { graph.log("Staging cleanup refused: ${error.message}"); false }

    private fun deleteStage(stage: File): Boolean = try {
        StorageRules.child(stageRoot, stage)
        if (!stage.exists()) true else {
            // Never walk a replaced symlink, including ones introduced inside the private tree.
            stage.walkBottomUp().onEnter { folder -> StorageRules.child(stageRoot, folder); true }.all { child ->
                StorageRules.child(stageRoot, child)
                child.delete() || !child.exists()
            }
        }
    } catch (_: Exception) { false }

    private suspend fun finishIdleLocked() {
        if (destroyed || active.isNotEmpty() || pending.isNotEmpty() || commandsInFlight.get() != 0) return
        val generation = lastStart
        // The gate keeps the verified idle state stable. Main-thread callbacks never wait
        // for it: a new onStartCommand can instead advance generation/count before this
        // callback runs. Foreground activation and teardown therefore cannot interleave.
        withContext(Dispatchers.Main.immediate) {
            if (!destroyed && currentService.get() === this@DownloadService &&
                generation == lastStart && commandsInFlight.get() == 0) {
                mutableRunning.value = false
                mutablePaused.value = false
                paused = false
                releaseWakeLock()
                ServiceCompat.stopForeground(this@DownloadService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelfResult(generation)
            }
        }
    }

    private fun acquireWakeLock() {
        // Reacquiring a non-reference-counted WakeLock replaces its timeout. Renew a short
        // bounded lease while transfers run, including queues longer than a single download.
        // The service scope stops the heartbeat on destruction; the lease also expires if it stalls.
        synchronized(wakeLock) {
            if (!destroyed && !paused && active.isNotEmpty()) wakeLock.acquire(WAKE_LEASE_MILLIS)
        }
    }
    private fun releaseWakeLock() {
        if (::wakeLock.isInitialized) synchronized(wakeLock) {
            if (wakeLock.isHeld) wakeLock.release()
        }
    }

    private fun notifyProgress(force: Boolean = false) {
        val now = android.os.SystemClock.elapsedRealtime()
        val previous = lastNotification.get()
        if (!force && (now - previous < 1000 || !lastNotification.compareAndSet(previous, now))) return
        if (force) lastNotification.set(now)
        if (!destroyed && currentService.get() === this) getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification())
    }

    private fun notification(): Notification {
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        fun command(action: String, code: Int) = PendingIntent.getForegroundService(this, code,
            Intent(this, DownloadService::class.java).setAction(action), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val values = mutableProgress.value.values
        val average = values.map { it.percent.coerceIn(0f, 100f) }.average().takeIf { it.isFinite() }?.toInt() ?: 0
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(if (paused) "Загрузки приостановлены" else "Video Downloader — загрузка")
            .setContentText(if (paused) "Нажмите «Продолжить» для возобновления" else "Выполняется заданий: ${active.size}. Приложение можно свернуть.")
            .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .setProgress(100, average, values.isEmpty() && !paused)
            .addAction(0, if (paused) "Продолжить" else "Пауза", command(if (paused) ACTION_RESUME else ACTION_PAUSE, 1))
            .addAction(0, "Остановить", command(ACTION_STOP, 2)).build()
    }

    /** Android 15 allows dataSync foreground work for at most six hours per background day. */
    override fun onTimeout(startId: Int, fgsType: Int) {
        graph.log("Android stopped the dataSync foreground service after its allowed background time.")
        destroyed = true
        active.values.forEach { it.reason.compareAndSet(null, Reason.STOP); graph.engine.cancel(it.requestId) }
        scope.cancel(CancellationException("Android dataSync foreground timeout"))
        if (currentService.get() === this) {
            mutableRunning.value = false
            mutablePaused.value = false
            ServiceCompat.stopForeground(this, ServiceCompat.STOP_FOREGROUND_REMOVE)
        }
        releaseWakeLock()
        stopSelf()
    }

    override fun onDestroy() {
        destroyed = true
        active.values.forEach { it.reason.compareAndSet(null, Reason.STOP); graph.engine.cancel(it.requestId) }
        scope.cancel(CancellationException("Download service destroyed"))
        if (currentService.compareAndSet(this, null)) {
            mutableRunning.value = false
            mutablePaused.value = false
        }
        releaseWakeLock()
        super.onDestroy()
    }

    companion object {
        private const val WAKE_LEASE_MILLIS = 2 * 60 * 1000L
        private const val WAKE_RENEW_MILLIS = 60 * 1000L
        private const val CHANNEL = "downloads"
        private const val NOTIFICATION = 1401
        private const val IDS = "task_ids"
        private const val ID = "task_id"
        private const val DELETE_FILES = "delete_files"
        private const val ACTION_START = "ru.bespalov.videodownloader.START"
        private const val ACTION_PAUSE = "ru.bespalov.videodownloader.PAUSE"
        private const val ACTION_RESUME = "ru.bespalov.videodownloader.RESUME"
        private const val ACTION_STOP = "ru.bespalov.videodownloader.STOP"
        private const val ACTION_CANCEL = "ru.bespalov.videodownloader.CANCEL"
        private const val ACTION_REMOVE = "ru.bespalov.videodownloader.REMOVE"
        private val currentService = AtomicReference<DownloadService?>(null)
        private val claimedTasks = ConcurrentHashMap.newKeySet<String>()
        private val mutableRunning = MutableStateFlow(false)
        private val mutablePaused = MutableStateFlow(false)
        private val mutableProgress = MutableStateFlow<Map<String, EngineProgress>>(emptyMap())
        val isRunning: StateFlow<Boolean> = mutableRunning.asStateFlow()
        val isPaused: StateFlow<Boolean> = mutablePaused.asStateFlow()
        val progress: StateFlow<Map<String, EngineProgress>> = mutableProgress.asStateFlow()
        private fun send(context: Context, action: String, configure: Intent.() -> Unit = {}) {
            ContextCompat.startForegroundService(context, Intent(context, DownloadService::class.java).setAction(action).apply(configure))
        }
        fun start(context: Context, taskIds: List<String>) { if (taskIds.isNotEmpty()) send(context, ACTION_START) { putStringArrayListExtra(IDS, ArrayList(taskIds)) } }
        fun pauseAll(context: Context) = send(context, ACTION_PAUSE)
        fun resumeAll(context: Context) = send(context, ACTION_RESUME)
        fun stopAll(context: Context) = send(context, ACTION_STOP)
        fun cancelTask(context: Context, id: String) = send(context, ACTION_CANCEL) { putExtra(ID, id) }
        fun removeTask(context: Context, id: String, deleteFiles: Boolean = true) = send(context, ACTION_REMOVE) { putExtra(ID, id); putExtra(DELETE_FILES, deleteFiles) }
    }
}
