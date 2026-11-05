package ru.bespalov.video.core

/** A decision approves an exact queued row and the successful history the user saw. */
data class ManualQueueCandidate(val id: String, val url: String, val history: List<SuccessRecord>)

object ManualQueueAdmission {
    fun eligibleIds(state: AppState, candidates: List<ManualQueueCandidate>): Set<String> {
        val requested = candidates.groupBy { it.id }.filterValues { it.size == 1 }.mapValues { it.value.single() }
        val history = state.history.groupBy { it.url }
        return state.queue.filter { task ->
            val expected = requested[task.id]
            expected != null && expected.url == task.url && history[task.url].orEmpty() == expected.history &&
                task.status == DownloadStatus.QUEUED && task.deferredNextStart && !task.pendingRemoval &&
                task.subscriptionId.isBlank() && task.subscriptionName.isBlank() &&
                task.stageDirectory.isBlank() && task.runId.isBlank() && task.outputUris.isEmpty() && task.settingsSnapshot == null
        }.mapTo(linkedSetOf()) { it.id }
    }
}
