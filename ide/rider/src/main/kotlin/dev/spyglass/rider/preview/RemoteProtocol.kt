// Avalonia previewer wire protocol (Avalonia.Remote.Protocol, Avalonia 12.1.4; GUIDs/properties from the decompiled assembly):
// frame = [int32 LE body length][16-byte GUID (.NET Guid.ToByteArray order)][BSON body].
package dev.spyglass.rider.preview

import java.io.DataInputStream
import java.io.EOFException
import java.io.InputStream
import java.io.OutputStream
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID

/** Pixel formats of FrameMessage / ClientSupportedPixelFormatsMessage. */
object PixelFormats {
    const val RGB565 = 0
    const val RGBA8888 = 1
    const val BGRA8888 = 2
}

/** InputModifiers values (each array element is one modifier, not a bit set). */
object Modifiers {
    const val ALT = 0
    const val CONTROL = 1
    const val SHIFT = 2
    const val META = 3
    const val LEFT_BUTTON = 4
    const val RIGHT_BUTTON = 5
    const val MIDDLE_BUTTON = 6
}

object MouseButtons {
    const val NONE = 0
    const val LEFT = 1
    const val RIGHT = 2
    const val MIDDLE = 3
}

/** A message with a GUID; [fields] is the BSON document. */
class RemoteMessage(val type: MessageType, val fields: Map<String, Any?>)

enum class MessageType(guid: String) {
    START_DESIGNER_SESSION("854887CF-2694-4EB6-B499-7461B6FB96C7"),
    UPDATE_XAML("9AEC9A2E-6315-4066-B4BA-E9A9EFD0F8CC"),
    UPDATE_XAML_RESULT("B7A70093-0C5D-47FD-9261-22086D43A2E2"),
    CLIENT_RENDER_INFO("7A3c25d3-3652-438D-8EF1-86E942CC96C0"),
    CLIENT_SUPPORTED_PIXEL_FORMATS("63481025-7016-43FE-BADC-F2FD0F88609E"),
    CLIENT_VIEWPORT_ALLOCATED("BD7A8DE6-3DB8-4A13-8583-D6D4AB189A31"),
    FRAME("F58313EE-FE69-4536-819D-F52EDF201A0E"),
    FRAME_RECEIVED("68014F8A-289D-4851-8D34-5367EDA7F827"),
    MEASURE_VIEWPORT("6E3C5310-E2B1-4C3D-8688-01183AA48C5B"),
    REQUEST_VIEWPORT_RESIZE("9B47B3D8-61DF-4C38-ACD4-8C1BB72554AC"),
    KEY_EVENT("1C3B691E-3D54-4237-BFB0-9FEA83BC1DB8"),
    POINTER_MOVED("6228F0B9-99F2-4F62-A621-414DA2881648"),
    POINTER_PRESSED("7E9E2818-F93F-411A-800E-6B1AEB11DA46"),
    POINTER_RELEASED("4ADC84EE-E7C8-4BCF-986C-DE3A2F78EDE4"),
    SCROLL("79301A05-F02D-4B90-BB39-472563B504AE"),
    TEXT_INPUT("C174102E-7405-4594-916F-B10B8248A17D"),
    HTML_TRANSPORT_STARTED("53778004-78fa-4381-8ec3-176a6f2328b6");

    val guidBytes: ByteArray = dotNetGuidBytes(guid)

    companion object {
        private val byGuid = entries.associateBy { it.guidBytes.toList() }
        fun of(guid: ByteArray): MessageType? = byGuid[guid.toList()]
    }
}

/** .NET `Guid.ToByteArray()`: the first three groups little-endian, the rest as written. */
internal fun dotNetGuidBytes(text: String): ByteArray {
    val u = UUID.fromString(text)
    val b = ByteBuffer.allocate(16)
    b.order(ByteOrder.LITTLE_ENDIAN)
    b.putInt((u.mostSignificantBits ushr 32).toInt())
    b.putShort((u.mostSignificantBits ushr 16).toShort())
    b.putShort(u.mostSignificantBits.toShort())
    b.order(ByteOrder.BIG_ENDIAN)
    b.putLong(u.leastSignificantBits)
    return b.array()
}

object FrameCodec {
    fun encode(type: MessageType, fields: Map<String, Any?>): ByteArray {
        val body = Bson.write(fields)
        val buf = ByteBuffer.allocate(20 + body.size).order(ByteOrder.LITTLE_ENDIAN)
        buf.putInt(body.size)
        buf.put(type.guidBytes)
        buf.put(body)
        return buf.array()
    }

    fun write(out: OutputStream, type: MessageType, fields: Map<String, Any?>) {
        out.write(encode(type, fields))
        out.flush()
    }

    /** Reads one message; null for a GUID this client does not know (the body is skipped). Throws EOFException at end of stream. */
    fun read(input: InputStream): RemoteMessage? {
        val data = DataInputStream(input)
        val head = ByteArray(20)
        data.readFully(head)
        val length = ByteBuffer.wrap(head).order(ByteOrder.LITTLE_ENDIAN).int
        if (length < 0 || length > 256 * 1024 * 1024) throw EOFException("Bad frame length $length")
        val body = ByteArray(length)
        data.readFully(body)
        val type = MessageType.of(head.copyOfRange(4, 20)) ?: return null
        return RemoteMessage(type, Bson.read(body))
    }
}

/** Builders for the messages the IDE sends. */
object Messages {
    fun pixelFormats(vararg formats: Int) = MessageType.CLIENT_SUPPORTED_PIXEL_FORMATS to mapOf<String, Any?>("Formats" to formats.toList())
    fun renderInfo(dpi: Double) = MessageType.CLIENT_RENDER_INFO to mapOf<String, Any?>("DpiX" to dpi, "DpiY" to dpi)
    fun viewport(width: Double, height: Double, dpi: Double) =
        MessageType.CLIENT_VIEWPORT_ALLOCATED to mapOf<String, Any?>("Width" to width, "Height" to height, "DpiX" to dpi, "DpiY" to dpi)
    fun updateXaml(xaml: String, assemblyPath: String, projectPath: String) =
        MessageType.UPDATE_XAML to mapOf<String, Any?>("Xaml" to xaml, "AssemblyPath" to assemblyPath, "XamlFileProjectPath" to projectPath)
    fun frameReceived(sequenceId: Long) = MessageType.FRAME_RECEIVED to mapOf<String, Any?>("SequenceId" to sequenceId)

    private fun pointer(x: Double, y: Double, modifiers: List<Int>, extra: Map<String, Any?> = emptyMap()): Map<String, Any?> =
        linkedMapOf<String, Any?>("Modifiers" to modifiers, "X" to x, "Y" to y).also { it.putAll(extra) }

    fun pointerMoved(x: Double, y: Double, m: List<Int>) = MessageType.POINTER_MOVED to pointer(x, y, m)
    fun pointerPressed(x: Double, y: Double, button: Int, m: List<Int>) = MessageType.POINTER_PRESSED to pointer(x, y, m, mapOf("Button" to button))
    fun pointerReleased(x: Double, y: Double, button: Int, m: List<Int>) = MessageType.POINTER_RELEASED to pointer(x, y, m, mapOf("Button" to button))
    fun scroll(x: Double, y: Double, dx: Double, dy: Double, m: List<Int>) =
        MessageType.SCROLL to pointer(x, y, m, mapOf("DeltaX" to dx, "DeltaY" to dy))
    fun key(down: Boolean, key: Int, symbol: String?, m: List<Int>) =
        MessageType.KEY_EVENT to mapOf<String, Any?>("Modifiers" to m, "IsDown" to down, "Key" to key, "PhysicalKey" to 0, "KeySymbol" to symbol)
    fun text(text: String, m: List<Int>) = MessageType.TEXT_INPUT to mapOf<String, Any?>("Modifiers" to m, "Text" to text)
}
