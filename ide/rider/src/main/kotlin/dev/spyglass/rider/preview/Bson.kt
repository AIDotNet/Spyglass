// Minimal BSON reader/writer for the Avalonia previewer protocol (the host serializes with Metsys.Bson: property names as
// declared, enums as int32, byte[] as binary subtype 2, collections as arrays). Only the types the protocol uses.
package dev.spyglass.rider.preview

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder

object Bson {
    private const val DOUBLE = 0x01
    private const val STRING = 0x02
    private const val DOCUMENT = 0x03
    private const val ARRAY = 0x04
    private const val BINARY = 0x05
    private const val BOOL = 0x08
    private const val NULL = 0x0A
    private const val INT32 = 0x10
    private const val INT64 = 0x12

    /** Serializes a document. Values: null, Boolean, Int, Long, Double, String, ByteArray, Map<String, *>, List<*>, Enum (as its ordinal). */
    fun write(doc: Map<String, Any?>): ByteArray {
        val out = ByteArrayOutputStream()
        writeDocument(out, doc)
        return out.toByteArray()
    }

    /** Parses a document into Map (documents), List (arrays), ByteArray (binary), Int/Long/Double/Boolean/String/null. */
    fun read(bytes: ByteArray): Map<String, Any?> {
        val buf = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
        return readDocument(buf)
    }

    private fun writeDocument(out: ByteArrayOutputStream, doc: Map<String, Any?>) {
        val body = ByteArrayOutputStream()
        for ((name, value) in doc) writeElement(body, name, value)
        writeInt(out, body.size() + 5)
        out.write(body.toByteArray())
        out.write(0)
    }

    private fun writeArray(out: ByteArrayOutputStream, list: List<*>) =
        writeDocument(out, list.mapIndexed { i, v -> i.toString() to v }.toMap(LinkedHashMap()))

    private fun writeElement(out: ByteArrayOutputStream, name: String, value: Any?) {
        fun head(type: Int) {
            out.write(type)
            out.write(name.toByteArray(Charsets.UTF_8))
            out.write(0)
        }
        when (value) {
            null -> head(NULL)
            is Boolean -> { head(BOOL); out.write(if (value) 1 else 0) }
            is Int -> { head(INT32); writeInt(out, value) }
            is Long -> { head(INT64); writeLong(out, value) }
            is Double -> { head(DOUBLE); writeLong(out, java.lang.Double.doubleToRawLongBits(value)) }
            is Float -> { head(DOUBLE); writeLong(out, java.lang.Double.doubleToRawLongBits(value.toDouble())) }
            is Enum<*> -> { head(INT32); writeInt(out, value.ordinal) }
            is String -> {
                head(STRING)
                val b = value.toByteArray(Charsets.UTF_8)
                writeInt(out, b.size + 1)
                out.write(b)
                out.write(0)
            }
            is ByteArray -> {
                head(BINARY)
                writeInt(out, value.size + 4)
                out.write(2)
                writeInt(out, value.size)
                out.write(value)
            }
            is Map<*, *> -> {
                head(DOCUMENT)
                @Suppress("UNCHECKED_CAST")
                writeDocument(out, value as Map<String, Any?>)
            }
            is List<*> -> { head(ARRAY); writeArray(out, value) }
            else -> throw IllegalArgumentException("Unsupported BSON value ${value::class}")
        }
    }

    private fun writeInt(out: ByteArrayOutputStream, v: Int) {
        out.write(v and 0xFF); out.write((v shr 8) and 0xFF); out.write((v shr 16) and 0xFF); out.write((v shr 24) and 0xFF)
    }

    private fun writeLong(out: ByteArrayOutputStream, v: Long) {
        for (i in 0 until 8) out.write(((v shr (8 * i)) and 0xFF).toInt())
    }

    private fun readDocument(buf: ByteBuffer): Map<String, Any?> {
        val start = buf.position()
        val length = buf.int
        val end = start + length
        val result = LinkedHashMap<String, Any?>()
        while (buf.position() < end - 1) {
            val type = buf.get().toInt()
            val name = readCString(buf)
            result[name] = readValue(buf, type)
        }
        buf.get()   // terminator
        return result
    }

    private fun readValue(buf: ByteBuffer, type: Int): Any? = when (type) {
        DOUBLE -> buf.double
        STRING -> {
            val len = buf.int
            val b = ByteArray(len - 1)
            buf.get(b)
            buf.get()
            String(b, Charsets.UTF_8)
        }
        DOCUMENT -> readDocument(buf)
        ARRAY -> readDocument(buf).values.toList()
        BINARY -> {
            val len = buf.int
            val subtype = buf.get().toInt()
            if (subtype == 2) {
                val inner = buf.int
                ByteArray(inner).also { buf.get(it) }
            } else ByteArray(len).also { buf.get(it) }
        }
        BOOL -> buf.get().toInt() != 0
        NULL -> null
        INT32 -> buf.int
        INT64 -> buf.long
        else -> throw IllegalArgumentException("Unsupported BSON type 0x${type.toString(16)}")
    }

    private fun readCString(buf: ByteBuffer): String {
        val out = ByteArrayOutputStream()
        while (true) {
            val b = buf.get().toInt()
            if (b == 0) break
            out.write(b)
        }
        return String(out.toByteArray(), Charsets.UTF_8)
    }
}
