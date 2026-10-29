package ru.bespalov.video.core

import java.util.UUID

/**
 * A user-selected JSON document restores logical data, never grants ownership of
 * files, provider documents, cookies, or an old native process. Only the running
 * installation can acquire those capabilities again after explicit actions.
 */
object BackupImport {
    fun sanitize(imported: AppState): AppState {
        JsonStateRepository.validate(imported)
        val sanitized = imported.copy(
            settings = imported.settings.copy(cookiesFile = "", outputTreeUri = ""),
            queue = imported.queue.map { task -> task.copy(
                // No other model references queue IDs. A fresh local identity
                // also invalidates stale commands and UUID-case path aliases.
                id = UUID.randomUUID().toString(),
                status = if (task.status == DownloadStatus.COMPLETED) DownloadStatus.COMPLETED else DownloadStatus.INTERRUPTED,
                progress = 0f,
                speed = "",
                downloaded = "",
                eta = "",
                transferredBytes = 0,
                rawError = "",
                outputUris = emptyList(),
                stageDirectory = "",
                runId = "",
                deferredNextStart = true,
                pendingRemoval = false,
                settingsSnapshot = null,
                archiveDate = "",
            ) },
            history = imported.history.map { it.copy(outputUris = emptyList()) },
        )
        JsonStateRepository.validate(sanitized)
        return sanitized
    }
}
