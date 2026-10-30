package ru.bespalov.video.core

import org.w3c.dom.Element
import org.xml.sax.InputSource
import org.xml.sax.SAXException
import java.io.StringReader
import javax.xml.XMLConstants
import javax.xml.parsers.DocumentBuilderFactory

object RssParser {
    const val MAX_BYTES = 2 * 1024 * 1024

    fun parse(xml: String, maxItems: Int = 10_000): List<DiscoveredMedia> {
        require(maxItems in 1..10_000 && xml.toByteArray(Charsets.UTF_8).size <= MAX_BYTES) { "Лента превышает допустимый размер." }
        // Android's DOM provider does not support every desktop-JVM feature.
        // Reject declarations before parsing and forbid all entity resolution.
        require(!Regex("<!\\s*(DOCTYPE|ENTITY)", RegexOption.IGNORE_CASE).containsMatchIn(xml)) { "DTD и внешние сущности в ленте запрещены." }
        val factory = DocumentBuilderFactory.newInstance().apply {
            isNamespaceAware = true
            isExpandEntityReferences = false
            runCatching { setFeature(XMLConstants.FEATURE_SECURE_PROCESSING, true) }
            runCatching { setFeature("http://apache.org/xml/features/disallow-doctype-decl", true) }
            runCatching { setFeature("http://xml.org/sax/features/external-general-entities", false) }
            runCatching { setFeature("http://xml.org/sax/features/external-parameter-entities", false) }
            runCatching { setAttribute(XMLConstants.ACCESS_EXTERNAL_DTD, "") }
            runCatching { setAttribute(XMLConstants.ACCESS_EXTERNAL_SCHEMA, "") }
        }
        val builder = factory.newDocumentBuilder()
        builder.setEntityResolver { _, _ -> throw SAXException("Внешние сущности запрещены.") }
        val root = builder.parse(InputSource(StringReader(xml))).documentElement ?: error("Лента пуста.")
        val entries = when (root.localName ?: root.tagName) {
            "feed" -> root.children("entry")
            "rss" -> root.children("channel").flatMap { it.children("item") }
            "RDF" -> root.children("item")
            else -> error("Ответ не является RSS или Atom лентой.")
        }
        require(entries.size <= maxItems) { "В ленте больше допустимого числа материалов." }
        val result = linkedMapOf<String, DiscoveredMedia>()
        for (entry in entries) {
            val link = entry.children("link").firstNotNullOfOrNull { element ->
                if (element.getAttribute("rel").ifBlank { "alternate" } != "alternate") null
                else element.getAttribute("href").ifBlank { element.textContent }.trim().takeIf(Urls::isHttp)
            } ?: entry.children("enclosure").firstNotNullOfOrNull { it.getAttribute("url").takeIf(Urls::isHttp) }
            require(link != null) { "Материал ленты не содержит корректного HTTP(S) URL." }
            result.putIfAbsent(link, DiscoveredMedia(link, entry.children("title").firstOrNull()?.textContent?.trim().orEmpty()))
        }
        return result.values.toList()
    }

    fun enrich(scan: FullScan, feedItems: List<DiscoveredMedia>): FullScan {
        val titles = feedItems.associate { it.url to it.title }
        val full = scan.items.map { it.url }.toSet()
        val unconfirmed = feedItems.count { it.url !in full }
        val warning = if (unconfirmed == 0) scan.warning else listOf(scan.warning,
            "Элементов ленты, не подтверждённых полным обходом: $unconfirmed.").filter(String::isNotBlank).joinToString(" ")
        return scan.copy(items = scan.items.map {
            if (it.title.isBlank() || it.title == it.url) it.copy(title = titles[it.url]?.takeIf(String::isNotBlank) ?: it.title) else it
        }, warning = warning)
    }

    private fun Element.children(name: String): List<Element> = (0 until childNodes.length).mapNotNull { childNodes.item(it) as? Element }
        .filter { (it.localName ?: it.tagName) == name }
}
