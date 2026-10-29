package ru.bespalov.video.core

object SubscriptionRules {
    private fun key(item: SubscriptionEntry) = item.subscriptionId to item.url

    fun applyScan(state: AppState, sourceId: String, scan: FullScan, now: Long = System.currentTimeMillis()): AppState {
        val source = state.subscriptions.singleOrNull { it.id == sourceId } ?: error("Подписка не найдена.")
        require(scan.items.size <= 10_000 && scan.items.map { it.url }.distinct().size == scan.items.size && scan.items.all { Urls.isHttp(it.url) }) {
            "Полный список содержит некорректные или повторные материалы либо превышает 10 000 записей."
        }
        val known = state.items.filter { it.subscriptionId == sourceId }.associateBy { it.url }
        val newItems = scan.items.filterNot { it.url in known }.map { media ->
            SubscriptionEntry(sourceId, media.url, media.title,
                state = if (!source.initialized && !source.downloadExisting) EntryState.BASELINE else EntryState.NEW,
                discoveredAtUtc = now, initialQueuePending = !source.initialized && source.downloadExisting)
        }
        val titles = scan.items.associate { it.url to it.title }
        val items = state.items.map { entry ->
            if (entry.subscriptionId == sourceId && (entry.title.isBlank() || entry.title == entry.url))
                entry.copy(title = titles[entry.url]?.takeIf { it.isNotBlank() && it != entry.url } ?: entry.title)
            else entry
        } + newItems
        val name = source.shortName.ifBlank {
            SubscriptionNames.create(scan.suggestedName.ifBlank { scan.title }, source.id,
                state.subscriptions.filter { it.id != sourceId }.map { it.shortName })
        }
        val changed = state.copy(items = items, subscriptions = state.subscriptions.map {
            if (it.id == sourceId) it.copy(title = scan.title.ifBlank { it.url }, shortName = name,
                feedUrl = it.feedUrl.ifBlank { scan.feedUrl }, initialized = true, lastAttemptUtc = now,
                lastSuccessUtc = now, lastError = "", lastWarning = scan.warning) else it
        })
        return reconcileSuccess(changed)
    }

    fun recordFailure(state: AppState, sourceId: String, error: String, now: Long = System.currentTimeMillis()): AppState =
        state.copy(subscriptions = state.subscriptions.map {
            if (it.id == sourceId) it.copy(lastAttemptUtc = now, lastError = error) else it
        })

    /** History proves success; only attributed records identify another source. */
    fun reconcileSuccess(state: AppState): AppState {
        val history = state.history.groupBy { it.url }
        return state.copy(items = state.items.map { item ->
            val successes = history[item.url].orEmpty()
            if (successes.isEmpty()) item
            else {
                val owner = successes.lastOrNull { it.subscriptionId == item.subscriptionId }?.subscriptionId
                    ?: successes.lastOrNull { it.subscriptionId.isNotEmpty() }?.subscriptionId.orEmpty()
                if (owner.isNotEmpty() && owner != item.subscriptionId) item.copy(
                    state = EntryState.ALREADY_PROCESSED, initialQueuePending = false, successAttributed = true,
                    duplicateSubscriptionId = owner,
                    duplicateNotified = item.state == EntryState.ALREADY_PROCESSED && item.duplicateSubscriptionId == owner && item.duplicateNotified)
                else item.copy(state = EntryState.PROCESSED, initialQueuePending = false,
                    successAttributed = owner == item.subscriptionId, duplicateSubscriptionId = "", duplicateNotified = false)
            }
        })
    }

    fun queueEntries(state: AppState, keys: Set<Pair<String, String>>): AppState {
        var result = reconcileSuccess(state)
        val sources = result.subscriptions.associateBy { it.id }
        val candidates = result.items.filter { key(it) in keys && it.state in setOf(EntryState.NEW, EntryState.SKIPPED, EntryState.QUEUED) }
        val queue = result.queue.toMutableList()
        val accepted = mutableSetOf<Pair<String, String>>()
        candidates.forEach { item ->
            val existingIndex = queue.indexOfFirst { it.url == item.url }
            if (existingIndex >= 0) {
                val existing = queue[existingIndex]
                if (existing.status == DownloadStatus.COMPLETED || existing.pendingRemoval || existing.status == DownloadStatus.CLEANUP_REQUIRED) return@forEach
                if ((existing.title.isBlank() || existing.title == existing.url) && item.title.isNotBlank() && item.title != item.url)
                    queue[existingIndex] = existing.copy(title = item.title)
            } else {
                val source = sources[item.subscriptionId] ?: return@forEach
                require(SubscriptionNames.isValid(source.shortName)) { "У подписки нет корректного краткого имени." }
                queue += DownloadTask(url = item.url, title = item.title, subscriptionId = source.id,
                    subscriptionName = source.shortName, deferredNextStart = true)
            }
            accepted += key(item)
        }
        result = result.copy(queue = queue, items = result.items.map {
            if (key(it) in accepted) it.copy(state = EntryState.QUEUED, initialQueuePending = false) else it
        })
        return result
    }

    fun recordSuccess(state: AppState, taskId: String, outputUris: List<String>, title: String? = null,
        now: Long = System.currentTimeMillis()): AppState {
        val task = state.queue.singleOrNull { it.id == taskId } ?: error("Задание не найдено.")
        require(outputUris.isNotEmpty() && outputUris.all { it.isNotBlank() }) { "Успех требует опубликованного медиафайла." }
        check(!task.pendingRemoval && task.status !in setOf(DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.PAUSED)) {
            "Отмена или очистка задания имеет приоритет перед успехом."
        }
        val changed = state.copy(queue = state.queue.map {
            if (it.id == taskId) it.copy(status = DownloadStatus.COMPLETED, progress = 100f,
                title = title?.takeIf(String::isNotBlank) ?: it.title, outputUris = outputUris,
                rawError = "", speed = "", eta = "", runId = "", stageDirectory = "") else it
        }, history = state.history + SuccessRecord(task.url, now, task.subscriptionId, outputUris))
        return reconcileSuccess(changed)
    }

    fun acknowledgeDuplicates(state: AppState, keys: Set<Pair<String, String>>): AppState = state.copy(items = state.items.map {
        if (key(it) in keys && it.state == EntryState.ALREADY_PROCESSED) it.copy(duplicateNotified = true) else it
    })

    fun skipEntries(state: AppState, keys: Set<Pair<String, String>>): AppState = state.copy(items = state.items.map {
        if (key(it) in keys && it.state == EntryState.NEW) it.copy(state = EntryState.SKIPPED, initialQueuePending = false) else it
    })

    fun removeSource(state: AppState, sourceId: String): AppState = state.copy(
        subscriptions = state.subscriptions.filterNot { it.id == sourceId }, items = state.items.filterNot { it.subscriptionId == sourceId })
}
