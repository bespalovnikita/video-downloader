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
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext
import ru.bespalov.video.core.DownloadStatus
import ru.bespalov.video.core.DownloadTask
import ru.bespalov.video.core.SubscriptionRules
import ru.bespalov.video.core.ManualQueueAdmission
import ru.bespalov.video.core.ManualQueueCandidate
import ru.bespalov.video.core.PublicationCleanup
import ru.bespalov.video.core.JournalContext
import ru.bespalov.video.core.JournalLevel
import ru.bespalov.video.core.JournalRecord
import ru.bespalov.video.core.JournalSanitizer
import ru.bespalov.videodownloader.DownloaderApplication
import ru.bespalov.videodownloader.MainActivity
import ru.bespalov.videodownloader.R
import ru.bespalov.videodownloader.engine.EngineProgress
import ru.bespalov.videodownloader.engine.EngineStage
import ru.bespalov.videodownloader.engine.EngineTrace
import ru.bespalov.videodownloader.engine.EngineTraceLevel
import ru.bespalov.videodownloader.engine.EngineTransferCounters
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
    @Volatile private var acceptingTasks = false
    @Volatile private var lastStart = 0
    @Volatile private var destroyed = false
    private var concurrency = 1
    private val lastNotification = AtomicLong()
    private val commandsInFlight = AtomicInteger()
    private val stageRoot by lazy { File(filesDir, "staging") }
    private val tracker = SessionTracker { android.os.SystemClock.elapsedRealtime() }
    private val observationScope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var sessionTicker: Job? = null
    @Volatile private var sessionId = ""
    private val transferCounters = ConcurrentHashMap<String, EngineTransferCounters>()
    private val lastOverall = AtomicLong()
    private val shutdownScheduled = AtomicBoolean()
    private val sessionPublicationGate = Any()

    private enum class Reason { PAUSE, STOP, REMOVE }
    private class Transfer(val id: String, val sessionId: String, val videoSequence: Int, val url: String,
        val attemptOffset: Int, title: String) {
        val requestId = "download-${UUID.randomUUID()}"
        val reason = AtomicReference<Reason?>(null)
        val title = AtomicReference(title)
        val latest = AtomicReference<EngineProgress?>(null)
        val lastProgressTrace = AtomicLong()
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

    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class, kotlinx.coroutines.DelicateCoroutinesApi::class)
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (destroyed || currentService.get() !== this) {
            stopSelfResult(startId)
            return START_NOT_STICKY
        }
        // Close admission immediately, including commands still waiting for storage.
        if (intent?.action == ACTION_STOP) acceptingTasks = false
        // Main-thread admission precedes foreground activation. An idle worker must not
        // stop this service while this command waits for initialization/import/the gate.
        commandsInFlight.incrementAndGet()
        lastStart = startId
        // Required within five seconds, before initialization, storage or native binaries run.
        ServiceCompat.startForeground(this, NOTIFICATION, notification(),
            if (Build.VERSION.SDK_INT >= 29) ServiceInfo.FOREGROUND_SERVICE_TYPE_DATA_SYNC else 0)
        mutableRunning.value = true
        // Even cancellation before dispatch must run the command's finally and
        // retire its admission count, otherwise shutdown would never become idle.
        scope.launch(start = CoroutineStart.ATOMIC) {
            try {
                graph.ready.await()
                graph.dataOperation.withLock {
                    check(!graph.importing.value) { "Data import is still in progress" }
                    check(drainingServices.none { it !== this@DownloadService }) {
                        "Дождитесь окончания отмены предыдущих загрузок."
                    }
                    gate.withLock {
                        check(!graph.repository.readOnly) { graph.repository.error.ifBlank { "State is read-only" } }
                        when (intent?.action) {
                            ACTION_START -> accept(intent.getStringArrayListExtra(IDS).orEmpty())
                            ACTION_PAUSE -> pauseLocked()
                            ACTION_RESUME -> resumeLocked()
                            ACTION_STOP -> stopLocked()
                            ACTION_CANCEL -> cancelLocked(intent.getStringExtra(ID).orEmpty(), false, false)
                            ACTION_REMOVE -> cancelLocked(intent.getStringExtra(ID).orEmpty(), true, intent.getBooleanExtra(DELETE_FILES, true))
                        }
                        scheduleLocked()
                    }
                }
            } catch (cancel: CancellationException) {
                throw cancel
            } catch (error: Exception) {
                trace(JournalLevel.Error, "download", "event=command_failed\n${error.stackTraceToString()}")
                gate.withLock {
                    if (active.isEmpty()) {
                        pending.forEach { tracker.mark(sessionId, it, SessionJobState.CANCELLED) }
                        pending.clear()
                    }
                }
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
        val stagingRun = UUID.randomUUID().toString()
        val accepted = mutableSetOf<String>()
        val state = graph.repository.update { state ->
            state.copy(queue = state.queue.map { task ->
                if (task.id !in selected || task.pendingRemoval || task.status in setOf(DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.CANCELLING, DownloadStatus.RUNNING)) task
                else {
                    // An approved repeat uses a new staging area; earlier published copies remain history-owned.
                    val repeat = task.status == DownloadStatus.COMPLETED
                    val resume = !repeat && task.stageDirectory.isNotBlank()
                    accepted += task.id
                    task.copy(status = DownloadStatus.QUEUED, progress = 0f, speed = "", eta = "", rawError = "", transferredBytes = 0,
                        deferredNextStart = false, outputUris = if (repeat) emptyList() else task.outputUris,
                        primaryOutputUri = if (repeat) "" else task.primaryOutputUri,
                        runId = if (resume) task.runId else stagingRun,
                        stageDirectory = if (resume) task.stageDirectory else "",
                        settingsSnapshot = if (resume) task.settingsSnapshot ?: state.settings else state.settings,
                        archiveDate = if (resume) task.archiveDate.ifBlank { date } else date)
                }
            })
        }
        if (accepted.isEmpty()) return
        val members = state.queue.mapIndexedNotNull { index, task ->
            if (task.id in accepted) SessionMember(task.id, index + 1, task.title, task.url) else null
        }
        val current = tracker.snapshot()
        if (current?.active == true) tracker.admit(sessionId, members)
        else {
            sessionId = UUID.randomUUID().toString()
            transferCounters.clear()
            tracker.begin(sessionId, members)
            startSessionTicker()
            trace(JournalLevel.Information, "session", "event=started tasks=${members.size}")
        }
        pending.addAll(accepted)
        acceptingTasks = true
        concurrency = state.settings.threads.coerceIn(1, 4)
        paused = false
        mutablePaused.value = false
        tracker.setPaused(sessionId, false)
        publishSession(forceOverall = true)
    }

    private fun acceptsManual(expected: String): Boolean = !destroyed && acceptingTasks &&
        currentService.get() === this && sessionId == expected && tracker.snapshot()?.active == true &&
        drainingServices.none { it !== this }

    /** A direct command can only join this already-running owner; it never creates a service. */
    @OptIn(kotlinx.coroutines.DelicateCoroutinesApi::class, kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private fun requestManualAdmission(expected: String, candidates: List<ManualQueueCandidate>): Boolean {
        if (candidates.isEmpty() || !acceptsManual(expected)) return false
        commandsInFlight.incrementAndGet()
        scope.launch(start = CoroutineStart.ATOMIC) {
            try {
                graph.dataOperation.withLock {
                    gate.withLock admission@{
                        currentCoroutineContext().ensureActive()
                        if (!acceptsManual(expected) || graph.importing.value || graph.repository.readOnly) return@admission
                        admitManualLocked(expected, candidates)
                        scheduleLocked()
                    }
                }
            } catch (cancel: CancellationException) { throw cancel }
            catch (error: Exception) { trace(JournalLevel.Warning, "download", "event=live_admission_failed\n${error.stackTraceToString()}") }
            finally {
                withContext(NonCancellable) {
                    gate.withLock {
                        commandsInFlight.decrementAndGet()
                        scheduleLocked()
                    }
                }
            }
        }
        return true
    }

    private suspend fun admitManualLocked(expected: String, candidates: List<ManualQueueCandidate>) = withContext(NonCancellable) {
        val accepted = mutableSetOf<String>()
        val run = UUID.randomUUID().toString()
        val date = LocalDate.now().toString()
        val state = graph.repository.update { state ->
            if (!acceptsManual(expected)) return@update state
            val eligible = ManualQueueAdmission.eligibleIds(state, candidates)
            state.copy(queue = state.queue.map { task ->
                if (task.id !in eligible || task.id in pending || task.id in claimedTasks || active.containsKey(task.id)) task
                else {
                    accepted += task.id
                    task.copy(status = if (paused) DownloadStatus.PAUSED else DownloadStatus.QUEUED,
                        deferredNextStart = false, settingsSnapshot = state.settings,
                        runId = run, archiveDate = date)
                }
            })
        }
        if (accepted.isEmpty()) return@withContext
        pending.addAll(accepted)
        tracker.admit(expected, state.queue.mapIndexedNotNull { index, task ->
            if (task.id in accepted) SessionMember(task.id, index + 1, task.title, task.url) else null
        })
        if (paused) accepted.forEach { tracker.mark(expected, it, SessionJobState.PAUSED) }
        trace(JournalLevel.Information, "session", "event=live_admitted tasks=${accepted.size} paused=$paused")
        publishSession(forceOverall = true)
    }

    private suspend fun pauseLocked() {
        paused = true
        mutablePaused.value = true
        tracker.setPaused(sessionId, true)
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
        pending.filterNot(active::containsKey).forEach { tracker.mark(sessionId, it, SessionJobState.PAUSED) }
        trace(JournalLevel.Information, "session", "event=paused")
        publishSession(forceOverall = true)
        releaseWakeLock()
    }

    private fun resumeLocked() {
        paused = false
        mutablePaused.value = false
        tracker.setPaused(sessionId, false)
        pending.filterNot(active::containsKey).forEach { tracker.mark(sessionId, it, SessionJobState.QUEUED) }
        trace(JournalLevel.Information, "session", "event=resumed")
        publishSession(forceOverall = true)
    }

    private suspend fun stopLocked() {
        acceptingTasks = false
        val waiting = pending.toSet()
        pending.clear()
        paused = false
        mutablePaused.value = false
        tracker.setPaused(sessionId, false)
        waiting.filterNot(active::containsKey).forEach { tracker.mark(sessionId, it, SessionJobState.CANCELLED) }
        active.values.filterNot { it.committed.get() }.forEach { task ->
            if (task.reason.get() != Reason.REMOVE) task.reason.set(Reason.STOP)
            task.job?.cancel(CancellationException("Stopped by user"))
            graph.engine.cancel(task.requestId)
        }
        graph.repository.update { state -> state.copy(queue = state.queue.map {
            if (it.id in waiting && !active.containsKey(it.id)) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "")
            else if (active[it.id]?.committed?.get() == false) it.copy(status = DownloadStatus.CANCELLING) else it
        }) }
        trace(JournalLevel.Information, "session", "event=stop_requested")
        publishSession(forceOverall = true)
    }

    private suspend fun cancelLocked(id: String, remove: Boolean, deleteFiles: Boolean) {
        val running = active[id]
        check(running != null || (id !in claimedTasks && drainingServices.none { it !== this })) {
            "Дождитесь окончания отмены предыдущих загрузок."
        }
        pending.remove(id)
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
            tracker.mark(sessionId, id, SessionJobState.CANCELLED)
            val task = graph.repository.state.value.queue.firstOrNull { it.id == id } ?: return
            if (remove) removeIdleLocked(task, deleteFiles)
            else graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id == id && it.status != DownloadStatus.COMPLETED) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "") else it
            }) }
        }
        publishSession(forceOverall = true)
    }

    private suspend fun scheduleLocked() {
        if (destroyed) {
            val abandoned = pending.toSet()
            pending.clear()
            try {
                graph.repository.update { state -> state.copy(queue = state.queue.map {
                    if (it.id in abandoned && !active.containsKey(it.id)) it.copy(status = DownloadStatus.INTERRUPTED, speed = "", eta = "") else it
                }) }
            } finally {
                abandoned.filterNot(active::containsKey).forEach { tracker.mark(sessionId, it, SessionJobState.CANCELLED) }
                finishIdleLocked()
            }
            return
        }
        if (!paused && !destroyed && acceptingTasks) {
            while (active.size < concurrency) {
                val id = pending.firstOrNull { !active.containsKey(it) } ?: break
                pending.remove(id)
                val task = graph.repository.state.value.queue.firstOrNull { it.id == id }
                if (task == null || task.pendingRemoval || task.status == DownloadStatus.CLEANUP_REQUIRED) {
                    tracker.mark(sessionId, id, SessionJobState.CANCELLED)
                    continue
                }
                // An old service may still be joining native cancellation after a system timeout.
                if (!claimedTasks.add(id)) {
                    tracker.mark(sessionId, id, SessionJobState.CANCELLED)
                    continue
                }
                val sequence = graph.repository.state.value.queue.indexOfFirst { it.id == id } + 1
                val transfer = Transfer(id, sessionId, sequence, task.url, tracker.attempts(id), task.title)
                progressOwners[id] = transfer.requestId
                active[id] = transfer
                transfer.job = scope.launch(start = CoroutineStart.UNDISPATCHED) { perform(transfer) }
            }
        }
        if (active.isEmpty() && pending.isEmpty()) finishIdleLocked()
        else {
            if (currentService.get() === this) {
                mutableRunning.value = true
                mutablePaused.value = paused
            }
            if (!paused && active.isNotEmpty()) acquireWakeLock() else releaseWakeLock()
            notifyProgress(force = true)
        }
        publishSession()
    }

    private suspend fun perform(transfer: Transfer) {
        var stage: File? = null
        var owned = emptyList<String>()
        var committed = false
        try {
            val original = graph.repository.state.value.queue.single { it.id == transfer.id }
            tracker.mark(transfer.sessionId, transfer.id, SessionJobState.RUNNING)
            publishSession()
            trace(JournalLevel.Information, "download", "event=started request=${transfer.requestId}", transfer)
            val workStage = StorageRules.stage(stageRoot, original.id, original.runId, original.stageDirectory)
            stage = workStage
            graph.repository.update { state -> state.copy(queue = state.queue.map {
                if (it.id == transfer.id) it.copy(status = DownloadStatus.RUNNING, stageDirectory = workStage.absolutePath) else it
            }) }
            checkNotCancelled(transfer)
            if (!workStage.isDirectory && !workStage.mkdirs()) throw IOException("Cannot create private staging directory")
            val counter = transferCounters.getOrPut(transfer.id) { EngineTransferCounters() }
            val result = graph.engine.download(transfer.requestId, original,
                original.settingsSnapshot ?: graph.repository.state.value.settings, workStage,
                transferCounters = counter, onTrace = { event -> engineTrace(transfer, event) }) { update ->
                updateProgress(transfer, update)
            }
            val previous = transfer.latest.get() ?: EngineProgress()
            updateProgress(transfer, previous.copy(percent = 99f, stage = EngineStage.PUBLISHING,
                detail = "Сохранение проверенного файла", speed = "", eta = "", transferredBytes = result.transferredBytes), adjustedAttempt = true)
            checkNotCancelled(transfer)
            val batch = publisher.publish(workStage, result.stagedFiles, result.primary,
                original.settingsSnapshot ?: graph.repository.state.value.settings,
                original.subscriptionName, original.archiveDate, { transfer.reason.get() != null || destroyed }) { manifest ->
                owned = manifest
                graph.repository.update { state -> state.copy(queue = state.queue.map {
                    if (it.id == transfer.id) it.copy(outputUris = manifest, primaryOutputUri = "") else it
                }) }
            }
            gate.withLock {
                checkNotCancelled(transfer)
                // The last cancellation check and successful-history update are serialized with commands.
                withContext(NonCancellable) {
                    graph.repository.update { state ->
                        val progress = mutableProgress.value[transfer.id]
                        val updated = state.copy(queue = state.queue.map {
                            if (it.id == transfer.id) it.copy(transferredBytes = tracker.transferred(transfer.id),
                                title = transfer.title.get().ifBlank { it.title }, progress = progress?.percent ?: it.progress) else it
                        })
                        val success = SubscriptionRules.recordSuccess(updated, transfer.id, batch.uris, transfer.title.get(), primaryOutputUri = batch.primaryUri)
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
                                    }, outputUris = retained, primaryOutputUri = "", pendingRemoval = remove,
                                    rawError = if (reason != null && retained.isEmpty() && stageClean) "" else error.message ?: error.javaClass.simpleName,
                                    speed = "", eta = "", title = transfer.title.get().ifBlank { it.title },
                                    transferredBytes = mutableProgress.value[transfer.id]?.transferredBytes ?: it.transferredBytes)
                            }) }
                        }
                    }
                }
                if (error !is CancellationException) trace(JournalLevel.Error, "download",
                    "event=failed\n${error.stackTraceToString()}", transfer)
            }
        } finally {
            withContext(NonCancellable) {
                gate.withLock {
                    try {
                        if (committed && transfer.reason.get() == Reason.REMOVE) {
                            graph.repository.state.value.queue.firstOrNull { it.id == transfer.id }?.let {
                                removeIdleLocked(it, transfer.deleteFiles)
                            }
                        }
                    } catch (error: Exception) {
                        trace(JournalLevel.Error, "download", "event=cleanup_failed\n${error.stackTraceToString()}", transfer)
                    }
                    val resultState = when {
                        committed -> SessionJobState.COMPLETED
                        transfer.reason.get() == Reason.PAUSE && transfer.id in pending && !destroyed -> SessionJobState.PAUSED
                        transfer.reason.get() != null || destroyed -> SessionJobState.CANCELLED
                        else -> SessionJobState.FAILED
                    }
                    tracker.mark(transfer.sessionId, transfer.id, resultState)
                    trace(if (resultState == SessionJobState.FAILED) JournalLevel.Error else JournalLevel.Information,
                        "download", "event=finished state=$resultState bytes=${tracker.transferred(transfer.id)}", transfer)
                    active.remove(transfer.id)
                    // Retire the old progress before releasing the cross-Service
                    // claim, so an old finally cannot erase a new owner's sample.
                    if (progressOwners.remove(transfer.id, transfer.requestId)) mutableProgress.update { it - transfer.id }
                    claimedTasks.remove(transfer.id)
                    scheduleLocked()
                }
            }
        }
    }

    private fun checkNotCancelled(transfer: Transfer) {
        if (transfer.reason.get() != null || destroyed) throw CancellationException("Download interrupted")
    }

    private fun startSessionTicker() {
        sessionTicker?.cancel()
        val expected = sessionId
        sessionTicker = observationScope.launch {
            while (isActive) {
                val snapshot = tracker.snapshot() ?: break
                if (snapshot.id != expected || !snapshot.active) break
                publishSession(expectedSessionId = expected)
                delay(500)
            }
        }
    }

    private fun updateProgress(transfer: Transfer, update: EngineProgress, adjustedAttempt: Boolean = false) {
        if (destroyed || transfer.reason.get() != null || transfer.committed.get() ||
            transfer.sessionId != sessionId || active[transfer.id] !== transfer ||
            progressOwners[transfer.id] != transfer.requestId) return
        val previous = transfer.latest.get()
        val percent = update.percent.takeIf { it.isFinite() && it >= 0f }?.coerceAtMost(99f)
            ?: previous?.percent ?: 0f
        val invocation = update.invocation.coerceAtLeast(1)
        val attempt = if (adjustedAttempt) update.attempt else
            (transfer.attemptOffset.toLong() + invocation).coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        val next = update.copy(percent = percent, attempt = attempt,
            transferredBytes = update.transferredBytes.coerceAtLeast(previous?.transferredBytes ?: 0L))
        next.title?.takeIf(String::isNotBlank)?.let(transfer.title::set)
        transfer.latest.set(next)
        // The engine ledger already spans retries and pause/resume. All of its
        // cumulative samples (including Result) use one stable job accounting key.
        tracker.observe(transfer.sessionId, transfer.id, transfer.id, percent,
            next.transferredBytes, next.title, attempt)
        mutableProgress.update { values ->
            if (progressOwners[transfer.id] == transfer.requestId) values + (transfer.id to next) else values
        }
        notifyProgress()
    }

    private fun engineTrace(transfer: Transfer, event: EngineTrace) {
        if (destroyed || transfer.reason.get() != null || transfer.sessionId != sessionId ||
            progressOwners[transfer.id] != transfer.requestId) return
        if (event.level == EngineTraceLevel.PROGRESS && event.event == "progress") {
            val now = android.os.SystemClock.elapsedRealtime()
            val previous = transfer.lastProgressTrace.get()
            if (previous != 0L && (now - previous < 500 || !transfer.lastProgressTrace.compareAndSet(previous, now))) return
            if (previous == 0L) transfer.lastProgressTrace.set(now)
        }
        val level = when (event.level) {
            EngineTraceLevel.INFORMATION -> JournalLevel.Information
            EngineTraceLevel.WARNING -> JournalLevel.Warning
            EngineTraceLevel.ERROR -> JournalLevel.Error
            EngineTraceLevel.PROGRESS -> JournalLevel.Progress
            EngineTraceLevel.VERBOSE -> JournalLevel.Verbose
        }
        val attempt = (transfer.attemptOffset.toLong() + event.invocation.coerceAtLeast(1))
            .coerceAtMost(Int.MAX_VALUE.toLong()).toInt()
        val header = "event=${event.event} request=${transfer.requestId} invocation=${event.invocation} local_attempt=${event.attempt} source=${event.source}"
        trace(level, event.category, header + if (event.message.isBlank()) "" else "\n${event.message}",
            transfer, attempt, event.component, event.quality)
    }

    private fun trace(level: JournalLevel, category: String, message: String, transfer: Transfer? = null,
        attempt: Int = transfer?.latest?.get()?.attempt ?: 0,
        component: String = transfer?.latest?.get()?.component.orEmpty(),
        quality: String = transfer?.latest?.get()?.quality.orEmpty(),
        session: String = transfer?.sessionId ?: sessionId) {
        try {
            graph.log(JournalRecord(level, category, message, JournalContext(
                session = session,
                job = transfer?.id.orEmpty(), worker = transfer?.requestId.orEmpty(), attempt = attempt,
                videoSequence = transfer?.videoSequence,
                video = transfer?.let { it.title.get().ifBlank { it.url } }.orEmpty(),
                component = component, quality = quality)))
        } catch (_: Exception) { /* Optional diagnostics never change a transfer's outcome. */ }
    }

    private fun publishSession(forceOverall: Boolean = false, expectedSessionId: String? = null) {
        val snapshot = synchronized(sessionPublicationGate) {
            val next = tracker.snapshot() ?: return
            if (expectedSessionId != null && next.id != expectedSessionId) return
            // Serialize snapshot capture and publication: a ticker already in
            // flight cannot restore an active snapshot after the final freeze.
            mutableSession.update { visible ->
                when {
                    visible?.id == next.id && !visible.active && next.active -> visible
                    currentService.get() === this || visible?.id == next.id -> next
                    else -> visible
                }
            }
            next
        }
        val now = android.os.SystemClock.elapsedRealtime()
        val previous = lastOverall.get()
        if (!forceOverall && previous != 0L && (now - previous < 1000 || !lastOverall.compareAndSet(previous, now))) return
        if (forceOverall || previous == 0L) lastOverall.set(now)
        if (!graph.journal.isEnabled(JournalLevel.Progress)) return
        var shown = 0
        val labels = StringBuilder()
        fun safe(value: String, limit: Int): String {
            val sanitized = JournalSanitizer.redact(value)
            var size = minOf(sanitized.length, limit)
            if (size > 0 && sanitized[size - 1].isHighSurrogate()) size--
            return sanitized.take(size).replace("\\", "\\\\").replace("\"", "\\\"")
        }
        for (member in snapshot.members) {
            val label = "{video_seq=${member.sequence} job=\"${safe(member.id, 64)}\" state=${member.state} attempt=${member.attempt} pct=${member.percent} video=\"${safe(member.title.ifBlank { member.url }, 256)}\"}"
            if (labels.length + label.length + 2 > 2400) break
            if (labels.isNotEmpty()) labels.append(", ")
            labels.append(label)
            shown++
        }
        trace(JournalLevel.Progress, "overall", "event=sample total=${snapshot.total} queued=${snapshot.queued} running=${snapshot.running} " +
            "completed=${snapshot.completed} failed=${snapshot.failed} cancelled=${snapshot.cancelled} shown_pct=${snapshot.overallPercent} " +
            "transferred=${snapshot.transferredBytes} elapsed_ms=${snapshot.elapsedMillis} members_shown=$shown " +
            "members_truncated=${snapshot.total > shown} members=[$labels]", session = snapshot.id)
    }

    private fun finishSession() {
        val snapshot = tracker.snapshot()
        if (snapshot?.active == true) {
            tracker.finish(snapshot.id)
            sessionTicker?.cancel()
            publishSession(forceOverall = true)
            trace(JournalLevel.Information, "session", "event=finished tasks=${snapshot.total}")
            transferCounters.clear()
        }
    }

    private fun scheduleShutdown() {
        if (!shutdownScheduled.compareAndSet(false, true)) return
        drainingServices.add(this)
        // This observer outlives cancellation of the download scope, but exits
        // after all native joins, rollback and command finally blocks complete.
        observationScope.launch {
            try {
                withContext(NonCancellable) {
                    scope.coroutineContext[Job]?.join()
                    gate.withLock { scheduleLocked() }
                }
            } catch (error: Exception) {
                trace(JournalLevel.Error, "session", "event=shutdown_cleanup_failed\n${error.stackTraceToString()}")
            } finally {
                drainingServices.remove(this@DownloadService)
                sessionTicker?.cancel()
                observationScope.cancel()
            }
        }
    }

    private suspend fun removeIdleLocked(task: DownloadTask, deleteFiles: Boolean) {
        // Persist intent before destructive operations, retaining the owned manifest if cleanup fails.
        graph.repository.update { state -> state.copy(queue = state.queue.map {
            if (it.id == task.id) it.copy(pendingRemoval = true, status = DownloadStatus.CLEANUP_REQUIRED) else it
        }) }
        val keepPublishedFiles = !deleteFiles && PublicationCleanup.hasSuccessfulPublication(task, graph.repository.state.value.history)
        // "Keep files" applies to completed publications; incomplete publication remnants are rolled back.
        val retained = if (keepPublishedFiles) emptyList() else publisher.deleteOwned(task.outputUris)
        val clean = safeDeleteStage(task)
        graph.repository.update { state -> state.copy(queue = state.queue.mapNotNull {
            if (it.id != task.id) it
            else if (retained.isEmpty() && clean) null
            else it.copy(outputUris = if (keepPublishedFiles) task.outputUris else retained,
                primaryOutputUri = task.primaryOutputUri.takeIf { keepPublishedFiles || it in retained }.orEmpty(), status = DownloadStatus.CLEANUP_REQUIRED,
                rawError = if (keepPublishedFiles) "The download was published, but its private staging directory could not be removed."
                    else "Some owned files could not be deleted. Retry removal after restoring storage access.")
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
        if (active.isNotEmpty() || pending.isNotEmpty() || commandsInFlight.get() != 0) return
        if (destroyed) {
            finishSession()
            return
        }
        val generation = lastStart
        var finished = false
        // The gate keeps the verified idle state stable. Main-thread callbacks never wait
        // for it: a new onStartCommand can instead advance generation/count before this
        // callback runs. Foreground activation and teardown therefore cannot interleave.
        withContext(Dispatchers.Main.immediate) {
            if (!destroyed && currentService.get() === this@DownloadService &&
                generation == lastStart && commandsInFlight.get() == 0) {
                tracker.finish(sessionId)
                finished = true
                mutableRunning.value = false
                mutablePaused.value = false
                paused = false
                releaseWakeLock()
                ServiceCompat.stopForeground(this@DownloadService, ServiceCompat.STOP_FOREGROUND_REMOVE)
                stopSelfResult(generation)
            }
        }
        if (finished) {
            sessionTicker?.cancel()
            publishSession(forceOverall = true)
            trace(JournalLevel.Information, "session", "event=finished tasks=${tracker.snapshot()?.total ?: 0}")
            transferCounters.clear()
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
        val summary = tracker.snapshot()
        val average = summary?.overallPercent?.takeIf(Float::isFinite)?.toInt() ?: 0
        return NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(android.R.drawable.stat_sys_download)
            .setContentTitle(if (paused) "Загрузки приостановлены" else "Video Downloader — загрузка")
            .setContentText(if (paused) "Нажмите «Продолжить» для возобновления" else "Выполняется заданий: ${active.size}. Приложение можно свернуть.")
            .setContentIntent(open).setOngoing(true).setOnlyAlertOnce(true)
            .setCategory(NotificationCompat.CATEGORY_PROGRESS)
            .setProgress(100, average, summary == null && !paused)
            .addAction(0, if (paused) "Продолжить" else "Пауза", command(if (paused) ACTION_RESUME else ACTION_PAUSE, 1))
            .addAction(0, "Остановить", command(ACTION_STOP, 2)).build()
    }

    /** Android 15 allows dataSync foreground work for at most six hours per background day. */
    override fun onTimeout(startId: Int, fgsType: Int) {
        trace(JournalLevel.Warning, "session", "event=timeout Android stopped the dataSync foreground service after its allowed background time.")
        destroyed = true
        acceptingTasks = false
        active.values.forEach { it.reason.compareAndSet(null, Reason.STOP); graph.engine.cancel(it.requestId) }
        scope.cancel(CancellationException("Android dataSync foreground timeout"))
        scheduleShutdown()
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
        acceptingTasks = false
        active.values.forEach { it.reason.compareAndSet(null, Reason.STOP); graph.engine.cancel(it.requestId) }
        scope.cancel(CancellationException("Download service destroyed"))
        scheduleShutdown()
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
        private val drainingServices = ConcurrentHashMap.newKeySet<DownloadService>()
        private val progressOwners = ConcurrentHashMap<String, String>()
        private val mutableRunning = MutableStateFlow(false)
        private val mutablePaused = MutableStateFlow(false)
        private val mutableProgress = MutableStateFlow<Map<String, EngineProgress>>(emptyMap())
        private val mutableSession = MutableStateFlow<DownloadSessionSnapshot?>(null)
        val isRunning: StateFlow<Boolean> = mutableRunning.asStateFlow()
        val isPaused: StateFlow<Boolean> = mutablePaused.asStateFlow()
        val progress: StateFlow<Map<String, EngineProgress>> = mutableProgress.asStateFlow()
        val session: StateFlow<DownloadSessionSnapshot?> = mutableSession.asStateFlow()
        /** Ownership covers the live service, then native joins and delayed writes after onDestroy. */
        val hasOwnedTasks: Boolean get() = currentService.get() != null || claimedTasks.isNotEmpty() || drainingServices.isNotEmpty()
        /** False means the row remains queued for the next explicit Start. */
        fun admitManual(expectedSessionId: String, candidates: List<ManualQueueCandidate>): Boolean =
            currentService.get()?.requestManualAdmission(expectedSessionId, candidates) ?: false
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
