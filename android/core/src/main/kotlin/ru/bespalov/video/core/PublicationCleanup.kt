package ru.bespalov.video.core

/** Only a committed current publication may survive private staging cleanup. */
object PublicationCleanup {
    fun hasSuccessfulPublication(task: DownloadTask, history: List<SuccessRecord>): Boolean =
        // Start resets progress to zero and native progress is capped at 99.
        // Only the durable success transaction writes 100. URI history alone
        // cannot prove success when a SAF provider reuses a deleted document ID.
        task.progress == 100f && task.outputUris.isNotEmpty() && history.any { record ->
            record.url == task.url && record.outputUris.containsAll(task.outputUris)
        }
}
