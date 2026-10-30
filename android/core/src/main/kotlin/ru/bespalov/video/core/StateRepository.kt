package ru.bespalov.video.core

import kotlinx.coroutines.flow.StateFlow

interface StateRepository {
    val state: StateFlow<AppState>
    val readOnly: Boolean
    val error: String
    suspend fun update(transform: (AppState) -> AppState): AppState
}
