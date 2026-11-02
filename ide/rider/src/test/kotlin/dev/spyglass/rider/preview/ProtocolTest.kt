package dev.spyglass.rider.preview

import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertThrows
import org.junit.jupiter.api.Test
import java.io.ByteArrayInputStream
import java.io.EOFException
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.nio.file.Files

class ProtocolTest {
    private fun hex(b: ByteArray) = b.joinToString("") { "%02x".format(it) }

    @Test
    fun `bson matches the specification examples`() {
        // From bsonspec.org: {"hello": "world"}
        assertEquals("160000000268656c6c6f0006000000776f726c640000", hex(Bson.write(linkedMapOf("hello" to "world"))))
        // {"BSON": ["awesome", 5.05, 1986]}
        val doc = linkedMapOf<String, Any?>("BSON" to listOf("awesome", 5.05, 1986))
        assertEquals("310000000442534f4e002600000002300008000000617765736f6d65000131003333333333331440103200c20700000000", hex(Bson.write(doc)))
    }

    @Test
    fun `bson round trips every supported type`() {
        val doc = linkedMapOf<String, Any?>(
            "s" to "héllo", "i" to 7, "l" to (1L shl 40), "d" to 2.5, "t" to true, "f" to false, "n" to null,
            "bytes" to byteArrayOf(1, 2, 3), "list" to listOf(1, 2, 3), "doc" to linkedMapOf("x" to 1),
        )
        val back = Bson.read(Bson.write(doc))
        assertEquals("héllo", back["s"]); assertEquals(7, back["i"]); assertEquals(1L shl 40, back["l"]); assertEquals(2.5, back["d"])
        assertEquals(true, back["t"]); assertEquals(false, back["f"]); assertNull(back["n"]); assertEquals(true, back.containsKey("n"))
        assertArrayEquals(byteArrayOf(1, 2, 3), back["bytes"] as ByteArray)
        assertEquals(listOf(1, 2, 3), back["list"]); assertEquals(mapOf("x" to 1), back["doc"])
    }

    @Test
    fun `guids use the dotnet byte order`() {
        assertEquals("cf8748859426b64eb4997461b6fb96c7", hex(MessageType.START_DESIGNER_SESSION.guidBytes))
        assertEquals("2e9aec9a1563664 0b4bae9a9efd0f8cc".replace(" ", ""), hex(MessageType.UPDATE_XAML.guidBytes))
    }

    @Test
    fun `frames are length, guid, bson`() {
        val (type, fields) = Messages.frameReceived(5)
        val bytes = FrameCodec.encode(type, fields)
        val body = Bson.write(fields)
        assertEquals(20 + body.size, bytes.size)
        assertEquals(body.size, ByteBuffer.wrap(bytes, 0, 4).order(ByteOrder.LITTLE_ENDIAN).int)
        assertArrayEquals(type.guidBytes, bytes.copyOfRange(4, 20))
        val msg = FrameCodec.read(ByteArrayInputStream(bytes))!!
        assertEquals(MessageType.FRAME_RECEIVED, msg.type)
        assertEquals(5L, msg.fields["SequenceId"])
    }

    @Test
    fun `unknown guid is skipped and truncated input throws`() {
        val unknown = ByteBuffer.allocate(20 + 5).order(ByteOrder.LITTLE_ENDIAN)
        unknown.putInt(5); unknown.put(ByteArray(16) { 1 }); unknown.put(byteArrayOf(5, 0, 0, 0, 0))
        val next = FrameCodec.encode(MessageType.FRAME_RECEIVED, mapOf("SequenceId" to 1L))
        val input = ByteArrayInputStream(unknown.array() + next)
        assertNull(FrameCodec.read(input))
        assertNotNull(FrameCodec.read(input))
        assertThrows(EOFException::class.java) { FrameCodec.read(input) }
    }

    @Test
    fun `bgra and rgba frames decode to premultiplied argb`() {
        // One pixel: B=0x10 G=0x20 R=0x30 A=0x40 (premultiplied).
        val bgra = PreviewSession.decodeFrame(mapOf("Width" to 1, "Height" to 1, "Stride" to 4, "Format" to PixelFormats.BGRA8888,
            "Data" to byteArrayOf(0x10, 0x20, 0x30, 0x40)))!!
        assertEquals(0x40302010, (bgra.raster.dataBuffer as java.awt.image.DataBufferInt).data[0])
        val rgba = PreviewSession.decodeFrame(mapOf("Width" to 1, "Height" to 1, "Stride" to 4, "Format" to PixelFormats.RGBA8888,
            "Data" to byteArrayOf(0x30, 0x20, 0x10, 0x40)))!!
        assertEquals(0x40302010, (rgba.raster.dataBuffer as java.awt.image.DataBufferInt).data[0])
        // Stride larger than the row is honoured.
        val padded = PreviewSession.decodeFrame(mapOf("Width" to 1, "Height" to 2, "Stride" to 8, "Format" to PixelFormats.BGRA8888,
            "Data" to byteArrayOf(1, 2, 3, 4, 0, 0, 0, 0, 5, 6, 7, 8, 0, 0, 0, 0)))!!
        assertEquals(0x08070605, (padded.raster.dataBuffer as java.awt.image.DataBufferInt).data[1])
        assertNull(PreviewSession.decodeFrame(mapOf("Width" to 0, "Height" to 1, "Stride" to 4, "Data" to ByteArray(4))))
    }

    @Test
    fun `input messages carry modifiers as an array of single values`() {
        val (_, f) = Messages.pointerPressed(10.0, 20.0, MouseButtons.LEFT, listOf(Modifiers.CONTROL, Modifiers.LEFT_BUTTON))
        val back = Bson.read(Bson.write(f))
        assertEquals(listOf(1, 4), back["Modifiers"]); assertEquals(10.0, back["X"]); assertEquals(1, back["Button"])
    }

    @Test
    fun `msbuild properties become a project or a clear reason`() {
        val dir = Files.createTempDirectory("spyglass-preview")
        val csproj = dir.resolve("App.csproj"); Files.writeString(csproj, "<Project/>")
        val host = dir.resolve("tools/net8.0/designer/Avalonia.Designer.HostApp.dll").also { Files.createDirectories(it.parent); Files.writeString(it, "x") }
        fun json(host: String?, target: String?, rc: String?, deps: String?) =
            """{"Properties":{"AvaloniaPreviewerNetCoreToolPath":${q(host)},"TargetPath":${q(target)},"ProjectRuntimeConfigFilePath":${q(rc)},"ProjectDepsFilePath":${q(deps)}}}"""

        val dll = dir.resolve("bin/App.dll").also { Files.createDirectories(it.parent); Files.writeString(it, "x") }
        val rc = dir.resolve("bin/App.runtimeconfig.json").also { Files.writeString(it, "{}") }
        val deps = dir.resolve("bin/App.deps.json").also { Files.writeString(it, "{}") }

        val ok = PreviewProjects.parseProperties(json("${host.parent}/../designer/Avalonia.Designer.HostApp.dll", "$dll", "$rc", "$deps"), csproj)
        assertEquals(host, ok.hostApp); assertEquals(dll, ok.targetPath)
        assertEquals("Views/Main.axaml", PreviewProjects.projectRelativePath(ok, dir.resolve("Views/Main.axaml")))

        assertThrows(PreviewUnavailable::class.java) { PreviewProjects.parseProperties(json("", "$dll", "$rc", "$deps"), csproj) }.also {
            assertEquals(false, it.needsBuild); assert(it.reason.contains("Avalonia")) }
        assertThrows(PreviewUnavailable::class.java) { PreviewProjects.parseProperties(json("$host", "$dll", "", ""), csproj) }.also {
            assert(it.reason.contains("executable")) }
        Files.delete(dll)
        assertThrows(PreviewUnavailable::class.java) { PreviewProjects.parseProperties(json("$host", "$dll", "$rc", "$deps"), csproj) }.also {
            assertEquals(true, it.needsBuild) }
        assertThrows(PreviewUnavailable::class.java) { PreviewProjects.parseProperties("not json", csproj) }
    }

    @Test
    fun `the project is the nearest csproj above the file`() {
        val dir = Files.createTempDirectory("spyglass-preview")
        Files.createDirectories(dir.resolve("src/App/Views"))
        Files.writeString(dir.resolve("src/App/App.csproj"), "<Project/>")
        assertEquals(dir.resolve("src/App/App.csproj"), PreviewProjects.findProjectFile(dir.resolve("src/App/Views/A.axaml")))
    }

    private fun q(s: String?) = if (s == null) "null" else "\"" + s.replace("\\", "\\\\") + "\""
}
