package ru.bespalov.video.core

import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import org.junit.Assert.*
import org.junit.Test

class JournalSanitizerTest {
    @Test fun publicVideoAndCollectionIdsSurviveWhileCredentialsAndUnknownParametersDoNot() {
        val public = "https://www.youtube.com/watch?v=AbC_de-12&list=PL123&index=2&t=30"
        assertEquals(public, JournalSanitizer.redact(public))
        val adult = "https://www.pornhub.com/view_video.php?viewkey=6ad748a10651a"
        assertEquals(adult, JournalSanitizer.redact(adult))
        val text = JournalSanitizer.redact("https://name:USER_PASSWORD@example.org/watch?token=PRIVATE_TOKEN&v=PublicVideo&%61pi_key=PRIVATE_KEY;page=3#PRIVATE_FRAGMENT")
        assertTrue(text.contains("https://[скрыто]@example.org/watch?token=[скрыто]&v=PublicVideo&%61pi_key=[скрыто];page=3#[скрыто]"))
        listOf("name:", "USER_PASSWORD", "PRIVATE_TOKEN", "PRIVATE_KEY", "PRIVATE_FRAGMENT").forEach { assertFalse(it, text.contains(it)) }
        assertEquals("token=[скрыто]", JournalSanitizer.redact("token=PRIVATE&v=ALSO_PRIVATE"))
    }

    @Test fun headersOptionsAssignmentsAndMalformedNetscapeRowsArePrivate() {
        val input = """Cookie: session=COOKIE_VALUE; other=COOKIE_OTHER
Authorization: Bearer AUTH_VALUE
Proxy-Authorization: Basic PROXY_VALUE
Set-Cookie: id=SET_VALUE
--cookies '/private/cookies/COOKIE_PATH.txt' --password="PASSWORD_VALUE" --add-header 'Header: HEADER_VALUE'
access_token=ACCESS_VALUE api-key=KEY_VALUE password='MORE PASSWORD VALUE' Bearer BEARER_VALUE
WARNING: .example.com\tTRUE\t/\tFALSE\t0\tname\tNETSCAPE_VALUE
""".replace("\\t", "\t")
        val text = JournalSanitizer.redact(input, multiline = true)
        listOf("COOKIE_VALUE", "COOKIE_OTHER", "AUTH_VALUE", "PROXY_VALUE", "SET_VALUE", "COOKIE_PATH", "PASSWORD_VALUE", "HEADER_VALUE", "ACCESS_VALUE", "KEY_VALUE", "MORE PASSWORD VALUE", "BEARER_VALUE", "NETSCAPE_VALUE").forEach {
            assertFalse("Leaked $it in $text", text.contains(it))
        }
        val escaped = JournalSanitizer.redact("WARNING: invalid Netscape format cookies file: '.example.com\\tTRUE\\t/\\tFALSE\\t0\\tname\\tCOOKIE_REPR_VALUE'")
        assertFalse(escaped.contains("COOKIE_REPR_VALUE"))
        assertTrue(escaped.contains("скрыто"))
    }

    @Test fun quotedEngineOutputIsDecodedBeforeRedactionAndMalformedJsonFailsClosed() {
        val raw = "ERROR: --password \"PRIVATE TWO WORDS\"\nCookie: COOKIE_PAYLOAD"
        val encoded = "event=output text=" + Json.encodeToString(raw)
        val result = JournalSanitizer.redact(encoded, multiline = true)
        assertFalse(result.contains("PRIVATE TWO WORDS")); assertFalse(result.contains("COOKIE_PAYLOAD"))
        assertTrue(result.contains("\n    Cookie: [скрыто]"))
        val incomplete = JournalSanitizer.redact("event=output text=\"--password \\\"TRUNCATED_PRIVATE")
        assertFalse(incomplete.contains("TRUNCATED_PRIVATE"))
        assertTrue(incomplete.contains("неполная JSON-строка"))
    }

    @Test fun escapedQuotesAndArgumentListsDoNotExposeTheRemainderOfASecret() {
        val inputs = listOf(
            """--password "PRIVATE\"PASSWORD_TAIL" --cookies '/private/COOKIE\'PATH_TAIL.txt'""",
            """["--password", "PRIVATE\"JSON_TAIL", "--cookies", "/private/JSON_COOKIE_PATH"]""",
            """['--password', 'PRIVATE\'REPR_TAIL', '--proxy', 'https://user:PRIVATE_PROXY@host']""",
            """{"password":"PRIVATE\"ASSIGNMENT_TAIL","token":"PRIVATE\\TOKEN_TAIL"}""",
            """--password PRIVATE,PASSWORD_TAIL;PATH_TAIL password=PRIVATE,ASSIGNMENT_TAIL""",
        )
        for (input in inputs) {
            val result = JournalSanitizer.redact(input)
            listOf("PRIVATE", "PASSWORD_TAIL", "PATH_TAIL", "JSON_TAIL", "JSON_COOKIE_PATH", "REPR_TAIL", "ASSIGNMENT_TAIL", "TOKEN_TAIL").forEach {
                assertFalse("Leaked $it in $result", result.contains(it))
            }
        }
    }

    @Test fun boundedInputDoesNotExposeASecretSplitInsideAnAuthorityOrQuotedOption() {
        val longAuthority = "diagnostic https://" + "PRIVATE_USERINFO".repeat(1500) + "@example.org/video"
        val text = JournalSanitizer.redact(longAuthority)
        assertTrue(text.length <= JournalSanitizer.MAX_MESSAGE_CHARS)
        assertFalse(text.contains("PRIVATE_USERINFO"))
        val quoted = "--password \"" + "PRIVATE_PASSWORD".repeat(1500) + "\""
        assertFalse(JournalSanitizer.redact(quoted).contains("PRIVATE_PASSWORD"))
        assertEquals("broken � end", JournalSanitizer.redact("broken \uD800 end"))
        assertEquals("hello\\u0000world", JournalSanitizer.redact("hello\u0000world"))
    }
}
