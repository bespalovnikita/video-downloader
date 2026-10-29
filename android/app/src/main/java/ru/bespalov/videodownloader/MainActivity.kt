package ru.bespalov.videodownloader

import android.content.Intent
import android.net.Uri
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.viewModels
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import ru.bespalov.videodownloader.ui.DownloaderScreen

class MainActivity : ComponentActivity() {
    private val model: DownloaderViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            MaterialTheme(colorScheme = if (isSystemInDarkTheme()) darkColorScheme() else lightColorScheme()) {
                DownloaderScreen(model.ui.collectAsStateWithLifecycle().value, model)
            }
        }
        if (savedInstanceState == null) receive(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        receive(intent)
    }

    private fun receive(intent: Intent?) {
        if (intent == null) return
        val text = when (intent.action) {
            Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT)
            Intent.ACTION_SEND_MULTIPLE -> intent.getStringArrayListExtra(Intent.EXTRA_TEXT)?.joinToString("\n")
                ?: intent.getStringExtra(Intent.EXTRA_TEXT)
            Intent.ACTION_VIEW -> intent.dataString?.takeIf { it.startsWith("videodownloader://", true) }
                ?.substringAfter("://")?.let(Uri::decode)
            else -> null
        }
        if (!text.isNullOrBlank()) model.addUrls(text)
    }
}
