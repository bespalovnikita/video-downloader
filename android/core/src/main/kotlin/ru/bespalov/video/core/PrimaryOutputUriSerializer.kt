package ru.bespalov.video.core

import kotlinx.serialization.KSerializer
import kotlinx.serialization.descriptors.PrimitiveKind
import kotlinx.serialization.descriptors.PrimitiveSerialDescriptor
import kotlinx.serialization.encoding.Decoder
import kotlinx.serialization.encoding.Encoder
import kotlinx.serialization.json.JsonDecoder
import kotlinx.serialization.json.JsonPrimitive

/** Optional display metadata must not make an otherwise usable older snapshot unreadable. */
object PrimaryOutputUriSerializer : KSerializer<String> {
    override val descriptor = PrimitiveSerialDescriptor("PrimaryOutputUri", PrimitiveKind.STRING)
    override fun serialize(encoder: Encoder, value: String) = encoder.encodeString(value)
    override fun deserialize(decoder: Decoder): String {
        if (decoder !is JsonDecoder) return decoder.decodeString()
        val value = decoder.decodeJsonElement() as? JsonPrimitive
        return value?.takeIf { it.isString }?.content.orEmpty()
    }
}
