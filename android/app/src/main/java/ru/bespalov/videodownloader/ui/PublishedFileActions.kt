package ru.bespalov.videodownloader.ui

import android.content.Context
import android.widget.Toast
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.launch

internal data class PublishedFileActions(val open: () -> Unit, val choose: () -> Unit)

/** Queue, task menu and history share the same persisted-primary and legacy selection rules. */
@Composable
internal fun rememberPublishedFileActions(uris: List<String>, primaryUri: String): PublishedFileActions {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var choices by remember(uris, primaryUri) { mutableStateOf<List<OutputViewing.OutputFile>?>(null) }
    val open: (String) -> Unit = { uri -> scope.launch { openPublishedFile(context, uri) }; Unit }
    choices?.let { files ->
        AlertDialog(onDismissRequest = { choices = null }, title = { Text("Файлы загрузки") },
            text = {
                LazyColumn(Modifier.heightIn(max = 420.dp)) {
                    itemsIndexed(files) { index, file ->
                        TextButton(onClick = { choices = null; open(file.uri) },
                            modifier = Modifier.fillMaxWidth().testTag("publishedFile:$index")) {
                            Text(file.name + if (file.uri == primaryUri) " (основной)" else "")
                        }
                    }
                }
            },
            confirmButton = { TextButton(onClick = { choices = null }) { Text("Закрыть") } })
    }
    return PublishedFileActions(
        open = {
            scope.launch {
                val primary = OutputViewing.primary(context, primaryUri, uris)
                if (primary != null) openPublishedFile(context, primary)
                else choices = OutputViewing.files(context, uris)
            }
        },
        choose = { scope.launch { choices = OutputViewing.files(context, uris) }; Unit },
    )
}

private suspend fun openPublishedFile(context: Context, uri: String) {
    try {
        context.startActivity(OutputViewing.publishedIntent(context, uri))
    } catch (cancel: CancellationException) { throw cancel }
    catch (_: Exception) { Toast.makeText(context, "Не удалось открыть файл. Проверьте доступ и приложение для его открытия.", Toast.LENGTH_LONG).show() }
}
