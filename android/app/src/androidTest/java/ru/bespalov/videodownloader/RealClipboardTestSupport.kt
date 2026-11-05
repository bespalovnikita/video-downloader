package ru.bespalov.videodownloader

import android.app.Activity
import android.app.KeyguardManager
import android.content.ClipboardManager
import android.os.Build
import android.view.WindowManager
import android.view.inspector.WindowInspector
import androidx.compose.ui.test.assertIsDisplayed
import androidx.compose.ui.test.assertIsEnabled
import androidx.compose.ui.test.junit4.ComposeTestRule
import androidx.compose.ui.test.onNodeWithTag
import androidx.compose.ui.test.performClick
import androidx.test.core.app.ActivityScenario
import androidx.test.platform.app.InstrumentationRegistry
import androidx.test.uiautomator.UiDevice
import java.security.MessageDigest

/** Keeps the real Android clipboard assertion, with evidence when the device rejects or replaces it. */
internal fun <A : Activity> copyAndAwaitClipboardText(
    compose: ComposeTestRule,
    activity: ActivityScenario<A>,
    copyTag: String,
    timeoutMillis: Long,
    label: String,
    predicate: (String) -> Boolean,
): String {
    compose.onNodeWithTag(copyTag).assertIsDisplayed().assertIsEnabled()
    compose.waitForIdle()
    awaitAppWindowFocus(compose, activity, "$label before copy")
    compose.onNodeWithTag(copyTag).performClick()
    var text = ""
    var metadata = "clipboard not read"
    try {
        compose.waitUntil(timeoutMillis) {
            activity.onActivity { current ->
                val clip = current.getSystemService(ClipboardManager::class.java).primaryClip
                text = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString().orEmpty()
                val description = clip?.description
                val mimeTypes = description?.let { (0 until it.mimeTypeCount).map(it::getMimeType) }.orEmpty()
                metadata = "items=${clip?.itemCount ?: 0}, mime=$mimeTypes, " +
                    "label=${fingerprint(description?.label?.toString().orEmpty())}, text=${fingerprint(text)}"
            }
            predicate(text)
        }
    } catch (failure: Exception) {
        throw AssertionError("$label: actual ClipboardManager did not match within ${timeoutMillis}ms; " +
            "$metadata; ${deviceWindowState(activity)}", failure)
    }
    return text
}

/** Dialog model state and semantics may be available before its input window receives focus. */
internal fun <A : Activity> awaitAppWindowFocus(
    compose: ComposeTestRule,
    activity: ActivityScenario<A>,
    label: String,
    timeoutMillis: Long = 5_000,
    dialog: Boolean = false,
) {
    try {
        compose.waitUntil(timeoutMillis) {
            var focused = false
            activity.onActivity { current ->
                focused = windowViews(current).any { view ->
                    view.isShown && view.hasWindowFocus() &&
                        (!dialog || (view.layoutParams as? WindowManager.LayoutParams)?.type == WindowManager.LayoutParams.TYPE_APPLICATION)
                }
            }
            focused
        }
    } catch (failure: Exception) {
        throw AssertionError("$label: app ${if (dialog) "dialog " else ""}window did not receive focus within " +
            "${timeoutMillis}ms; ${deviceWindowState(activity)}", failure)
    }
}

private fun windowViews(activity: Activity) = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
    WindowInspector.getGlobalWindowViews()
} else listOf(activity.window.decorView)

private fun <A : Activity> deviceWindowState(activity: ActivityScenario<A>): String {
    var state = "activity unavailable"
    runCatching {
        activity.onActivity { current ->
            val keyguard = current.getSystemService(KeyguardManager::class.java)
            val roots = windowViews(current).joinToString { view ->
                "${view.javaClass.simpleName}(type=${(view.layoutParams as? WindowManager.LayoutParams)?.type}," +
                    "shown=${view.isShown},focused=${view.hasWindowFocus()})"
            }
            state = "activityFocus=${current.hasWindowFocus()}, keyguardLocked=${keyguard.isKeyguardLocked}, " +
                "deviceLocked=${keyguard.isDeviceLocked}, roots=[$roots]"
        }
    }.onFailure { state = "activity diagnostic error=${it.javaClass.simpleName}" }
    val foreground = runCatching {
        UiDevice.getInstance(InstrumentationRegistry.getInstrumentation()).currentPackageName
    }.getOrElse { "error:${it.javaClass.simpleName}" }
    return "activeWindowPackage=$foreground, $state"
}

/** Never puts clipboard contents, URLs, arbitrary labels or secrets into the test report. */
private fun fingerprint(text: String): String = "length=${text.length},sha256=" +
    MessageDigest.getInstance("SHA-256").digest(text.toByteArray(Charsets.UTF_8))
        .joinToString("") { "%02x".format(it) }
