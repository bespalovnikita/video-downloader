package ru.bespalov.videodownloader.download

import java.util.Collections

data class SessionMember(val id: String, val sequence: Int, val title: String, val url: String)

enum class SessionJobState { QUEUED, RUNNING, PAUSED, COMPLETED, FAILED, CANCELLED }

data class SessionMemberSnapshot(
    val id: String,
    val sequence: Int,
    val title: String,
    val url: String,
    val state: SessionJobState = SessionJobState.QUEUED,
    val percent: Float = 0f,
    val transferredBytes: Long = 0,
    val attempt: Int = 0,
)

data class DownloadSessionSnapshot(
    val id: String,
    val active: Boolean,
    val paused: Boolean,
    val elapsedMillis: Long,
    val total: Int,
    val queued: Int,
    val running: Int,
    val completed: Int,
    val failed: Int,
    val cancelled: Int,
    val transferredBytes: Long,
    val overallPercent: Float,
    val members: List<SessionMemberSnapshot>,
)

/** Process-local accounting owned by the download service, independent of any Activity. */
class SessionTracker(private val clock: () -> Long = { System.nanoTime() / 1_000_000L }) {
    private class Member(val input: SessionMember) {
        var title = input.title
        var state = SessionJobState.QUEUED
        var percent = 0f
        var transferredBytes = 0L
        var attempt = 0
        val invocations = mutableMapOf<String, Long>()
    }

    private var id: String? = null
    private var active = false
    private var paused = false
    private var startedAt = 0L
    private var elapsed = 0L
    private val members = linkedMapOf<String, Member>()

    @Synchronized
    fun begin(id: String, members: List<SessionMember>) {
        this.id = id
        active = true
        paused = false
        startedAt = clock()
        elapsed = 0
        this.members.clear()
        addMembers(members)
    }

    @Synchronized
    fun admit(expectedSessionId: String, members: List<SessionMember>) {
        if (!accepts(expectedSessionId)) return
        addMembers(members)
    }

    @Synchronized
    fun observe(expectedSessionId: String, jobId: String, invocationId: String, percent: Float,
        transferredBytes: Long, title: String? = null, attempt: Int = 0) {
        if (!accepts(expectedSessionId)) return
        val member = members[jobId]?.takeUnless { it.state.terminal } ?: return
        // A resumed native invocation starts its own absolute byte counter.
        // Progress and the final result may report the same count more than once.
        if (transferredBytes >= 0) {
            val previous = member.invocations[invocationId] ?: 0L
            if (transferredBytes > previous) {
                member.invocations[invocationId] = transferredBytes
                member.transferredBytes = addSaturated(member.transferredBytes, transferredBytes - previous)
            }
        }
        if (percent.isFinite() && percent >= 0f) member.percent = percent.coerceAtMost(99f)
        if (!title.isNullOrBlank()) member.title = title
        member.attempt = maxOf(member.attempt, attempt)
    }

    @Synchronized
    fun mark(expectedSessionId: String, jobId: String, state: SessionJobState) {
        if (!accepts(expectedSessionId)) return
        val member = members[jobId]?.takeUnless { it.state.terminal } ?: return
        member.state = state
        if (state == SessionJobState.COMPLETED) member.percent = 100f
    }

    @Synchronized
    fun setPaused(expectedSessionId: String, paused: Boolean) {
        if (accepts(expectedSessionId)) this.paused = paused
    }

    @Synchronized
    fun finish(expectedSessionId: String) {
        if (!accepts(expectedSessionId)) return
        updateElapsed()
        active = false
        paused = false
    }

    @Synchronized
    fun snapshot(): DownloadSessionSnapshot? {
        val sessionId = id ?: return null
        if (active) updateElapsed()
        val all = members.values
        val shown = all.take(32).map { member ->
            SessionMemberSnapshot(member.input.id, member.input.sequence, member.title, member.input.url,
                member.state, member.percent, member.transferredBytes, member.attempt)
        }
        return DownloadSessionSnapshot(
            id = sessionId,
            active = active,
            paused = paused,
            elapsedMillis = elapsed,
            total = all.size,
            queued = all.count { it.state == SessionJobState.QUEUED || it.state == SessionJobState.PAUSED },
            running = all.count { it.state == SessionJobState.RUNNING },
            completed = all.count { it.state == SessionJobState.COMPLETED },
            failed = all.count { it.state == SessionJobState.FAILED },
            cancelled = all.count { it.state == SessionJobState.CANCELLED },
            transferredBytes = all.fold(0L) { bytes, member -> addSaturated(bytes, member.transferredBytes) },
            overallPercent = if (all.isEmpty()) 0f else (all.sumOf { it.percent.toDouble() } / all.size).toFloat(),
            members = Collections.unmodifiableList(shown),
        )
    }

    @Synchronized
    fun transferred(jobId: String): Long = members[jobId]?.transferredBytes ?: 0

    @Synchronized
    fun attempts(jobId: String): Int = members[jobId]?.attempt ?: 0

    private fun accepts(expectedSessionId: String) = active && id == expectedSessionId

    private fun addMembers(incoming: List<SessionMember>) {
        incoming.forEach { member -> members.putIfAbsent(member.id, Member(member)) }
    }

    private fun updateElapsed() {
        val now = clock()
        val sinceStart = if (now < startedAt) 0L else {
            try { Math.subtractExact(now, startedAt) } catch (_: ArithmeticException) { Long.MAX_VALUE }
        }
        elapsed = maxOf(elapsed, sinceStart)
    }

    private fun addSaturated(left: Long, right: Long) = if (Long.MAX_VALUE - left < right) Long.MAX_VALUE else left + right

    private val SessionJobState.terminal: Boolean get() = this == SessionJobState.COMPLETED ||
        this == SessionJobState.FAILED || this == SessionJobState.CANCELLED
}
