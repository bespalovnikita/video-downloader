package ru.bespalov.video.core

object SubscriptionRules {
    private fun key(item: SubscriptionEntry) = item.subscriptionId to item.url

    fun applyScan(state: AppState, sourceId: String, scan: FullScan, now: Long = System.currentTimeMillis()): AppState {
        val source = state.subscriptions.singleOrNull { it.id == sourceId } ?: error("Подписка не найдена.")
        require(scan.items.size <= 10_000 && scan.items.map { it.url }.distinct().size == scan.items.size && scan.items.all { Urls.isHttp(it.url) }) {
            "Полный список содержит некорректные или повторные материалы либо превышает 10 000 записей."
        }
        val known = state.items.filter { it.subscriptionId == sourceId }.associateBy { it.url }
        if (scan.scope == ScanScope.RecentWindow) require(source.initialized &&
            scan.items.size <= RecentScanParser.RECENT_WINDOW_SIZE && scan.items.any { it.url in known }) {
            "Быстрая проверка не пересекается с сохранённым снимком; требуется полный обход."
        }
        val ordered = scan.items.map { media ->
            val previous = known[media.url]
            when {
                previous == null -> SubscriptionEntry(sourceId, media.url, media.title,
                    state = if (!source.initialized && !source.downloadExisting) EntryState.BASELINE else EntryState.NEW,
                    discoveredAtUtc = now, initialQueuePending = !source.initialized && source.downloadExisting)
                previous.title.isBlank() || previous.title == previous.url -> previous.copy(
                    title = media.title.takeIf { it.isNotBlank() && it != media.url } ?: previous.title)
                else -> previous
            }
        }
        // Full defines current source order. A recent result replaces only the
        // verified prefix; both preserve absent history and earlier decisions.
        val currentUrls = scan.items.mapTo(mutableSetOf()) { it.url }
        val historical = state.items.filter { it.subscriptionId == sourceId && it.url !in currentUrls }
        val firstSourceIndex = state.items.indexOfFirst { it.subscriptionId == sourceId }
        val items = state.items.filterNot { it.subscriptionId == sourceId }.toMutableList().apply {
            addAll(if (firstSourceIndex < 0) size else firstSourceIndex, ordered + historical)
        }
        val name = if (source.shortName.isBlank() && scan.scope == ScanScope.Full) {
            SubscriptionNames.create(scan.suggestedName.ifBlank { scan.title }, source.id,
                state.subscriptions.filter { it.id != sourceId }.map { it.shortName })
        } else source.shortName
        val changed = state.copy(items = items, subscriptions = state.subscriptions.map {
            if (it.id == sourceId) it.copy(title = scan.title.takeIf { title -> title.isNotBlank() && title != it.url }
                ?: it.title.ifBlank { it.url }, shortName = name,
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
        val candidates = result.items.filter { key(it) in keys && it.state in setOf(EntryState.BASELINE, EntryState.NEW, EntryState.SKIPPED, EntryState.QUEUED) }
        val queue = result.queue.toMutableList()
        val accepted = mutableSetOf<Pair<String, String>>()
        candidates.forEach { item ->
            val existingIndex = queue.indexOfFirst { it.url == item.url }
            if (existingIndex >= 0) {
                val existing = queue[existingIndex]
                if (existing.pendingRemoval || existing.status in setOf(DownloadStatus.COMPLETED, DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED)) return@forEach
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

    /** The batch supplies only sources whose scan was successfully applied. */
    fun pendingNewKeys(state: AppState, successfulSourceIds: Set<String>): Set<Pair<String, String>> {
        val current = reconcileSuccess(state)
        val eligibleSources = current.subscriptions.filter {
            it.id in successfulSourceIds && it.initialized && !it.paused
        }.mapTo(mutableSetOf()) { it.id }
        return current.items.filter {
            it.subscriptionId in eligibleSources && it.state == EntryState.NEW && !it.initialQueuePending
        }.mapTo(linkedSetOf(), ::key)
    }

    /** Revalidate a previously shown offer against current decisions/history. */
    fun queueNewEntries(state: AppState, requestedKeys: Set<Pair<String, String>>): AppState {
        val sourceIds = requestedKeys.mapTo(mutableSetOf()) { it.first }
        val stillNew = pendingNewKeys(state, sourceIds).intersect(requestedKeys)
        return queueEntries(state, stillNew)
    }

    fun recordSuccess(state: AppState, taskId: String, outputUris: List<String>, title: String? = null,
        now: Long = System.currentTimeMillis(), primaryOutputUri: String = ""): AppState {
        val task = state.queue.singleOrNull { it.id == taskId } ?: error("Задание не найдено.")
        require(outputUris.isNotEmpty() && outputUris.all { it.isNotBlank() }) { "Успех требует опубликованного медиафайла." }
        require(primaryOutputUri.isEmpty() || primaryOutputUri in outputUris) { "Основной файл должен входить в опубликованные результаты." }
        check(!task.pendingRemoval && task.status !in setOf(DownloadStatus.CANCELLING, DownloadStatus.CLEANUP_REQUIRED, DownloadStatus.PAUSED)) {
            "Отмена или очистка задания имеет приоритет перед успехом."
        }
        val changed = state.copy(queue = state.queue.map {
            if (it.id == taskId) it.copy(status = DownloadStatus.COMPLETED, progress = 100f,
                title = title?.takeIf(String::isNotBlank) ?: it.title, outputUris = outputUris, primaryOutputUri = primaryOutputUri,
                rawError = "", speed = "", eta = "", runId = "", stageDirectory = "") else it
        }, history = state.history + SuccessRecord(task.url, now, task.subscriptionId, outputUris, primaryOutputUri))
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
