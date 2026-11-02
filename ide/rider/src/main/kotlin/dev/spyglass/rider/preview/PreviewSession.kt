// One previewer host process and its BSON/TCP connection (design/preview/README.md §2–§4). IntelliJ-independent so it can be
// tested against the real Avalonia.Designer.HostApp. The IDE listens first, then starts the host, which connects back,
// announces the session and waits for pixel formats / viewport / DPI and UpdateXaml.
package dev.spyglass.rider.preview

import java.awt.image.BufferedImage
import java.io.EOFException
import java.io.IOException
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.UUID
import java.util.concurrent.atomic.AtomicBoolean

/** The result of an UpdateXaml: [error] null = rendered. Line/column are 1-based when the host knows them. */
data class XamlResult(val error: String?, val line: Int?, val column: Int?)

interface PreviewSessionListener {
    /** The host connected and announced the session; XAML can be sent. */
    fun ready() {}
    fun frame(image: BufferedImage, dpi: Double) {}
    fun xamlResult(result: XamlResult) {}
    /** The process ended or the connection broke. */
    fun exited(reason: String) {}
}

class PreviewSession(
    private val project: PreviewProject,
    private val dotnet: String,
    private val listener: PreviewSessionListener,
    private val environment: Map<String, String>? = null,
    private val log: (String) -> Unit = {},
    private val connectTimeoutMs: Int = 60_000,
) {
    private val stopped = AtomicBoolean(false)
    private val writeLock = Any()
    @Volatile private var process: Process? = null
    @Volatile private var server: ServerSocket? = null
    @Volatile private var socket: Socket? = null
    @Volatile private var viewportW = 800.0
    @Volatile private var viewportH = 600.0
    @Volatile private var dpi = 96.0
    @Volatile var isReady = false
        private set
    private var pendingXaml: Pair<String, String>? = null

    /** Listens, starts the host and returns immediately; events arrive on [listener] from background threads. */
    fun start(width: Double, height: Double, dpi: Double) {
        viewportW = width.coerceAtLeast(1.0); viewportH = height.coerceAtLeast(1.0); this.dpi = dpi
        val srv = ServerSocket(0, 1, InetAddress.getLoopbackAddress())
        server = srv
        val sessionId = UUID.randomUUID().toString().replace("-", "")
        val cmd = listOf(dotnet, "exec", "--runtimeconfig", project.runtimeConfig.toString(), "--depsfile", project.depsFile.toString(),
            project.hostApp.toString(), "--transport", "tcp-bson://127.0.0.1:${srv.localPort}/", "--session-id", sessionId,
            "--method", "avalonia-remote", project.targetPath.toString())
        log("preview: ${cmd.joinToString(" ")}")
        val pb = ProcessBuilder(cmd).directory(project.projectDir.toFile())
        environment?.let { pb.environment().putAll(it) }
        val p = try { pb.start() } catch (e: IOException) {
            srv.close()
            throw PreviewUnavailable("Cannot start the previewer host: ${e.message}")
        }
        process = p
        pump(p.inputStream, "host")
        pump(p.errorStream, "host!")
        Thread({
            val code = try { p.waitFor() } catch (_: InterruptedException) { -1 }
            if (!stopped.get()) fireExited("The preview process exited (code $code).")
        }, "Spyglass preview watcher").apply { isDaemon = true; start() }
        Thread({ acceptAndRead(srv) }, "Spyglass preview reader").apply { isDaemon = true; start() }
    }

    private fun pump(stream: java.io.InputStream, tag: String) {
        Thread({
            try { stream.bufferedReader().forEachLine { log("preview $tag: $it") } } catch (_: IOException) {}
        }, "Spyglass preview $tag").apply { isDaemon = true; start() }
    }

    private fun acceptAndRead(srv: ServerSocket) {
        try {
            srv.soTimeout = connectTimeoutMs
            val s = srv.accept()
            s.tcpNoDelay = true
            socket = s
            srv.close()
            val input = s.getInputStream().buffered()
            while (!stopped.get()) {
                val msg = FrameCodec.read(input) ?: continue
                handle(msg)
            }
        } catch (e: java.net.SocketTimeoutException) {
            if (!stopped.get()) fireExited("The preview host did not connect in time.")
        } catch (e: EOFException) {
            if (!stopped.get()) fireExited("The preview connection closed.")
        } catch (e: IOException) {
            if (!stopped.get()) fireExited("The preview connection failed: ${e.message}")
        } catch (e: Exception) {
            if (!stopped.get()) fireExited("The preview protocol failed: $e")
        }
    }

    private fun handle(msg: RemoteMessage) {
        val f = msg.fields
        when (msg.type) {
            MessageType.START_DESIGNER_SESSION -> {
                send(Messages.pixelFormats(PixelFormats.BGRA8888, PixelFormats.RGBA8888))
                send(Messages.renderInfo(dpi))
                send(Messages.viewport(viewportW, viewportH, dpi))
                val pending: Pair<String, String>?
                synchronized(this) { isReady = true; pending = pendingXaml; pendingXaml = null }
                listener.ready()
                if (pending != null) sendXaml(pending.first, pending.second)
            }
            MessageType.FRAME -> {
                val seq = (f["SequenceId"] as? Number)?.toLong() ?: 0L
                val image = try { decodeFrame(f) } catch (e: Exception) { log("preview: bad frame: $e"); null }
                if (image != null) listener.frame(image, (f["DpiX"] as? Number)?.toDouble() ?: 96.0)
                send(Messages.frameReceived(seq))   // the host sends the next frame only after this
            }
            MessageType.UPDATE_XAML_RESULT -> {
                val error = f["Error"] as? String
                @Suppress("UNCHECKED_CAST")
                val ex = f["Exception"] as? Map<String, Any?>
                listener.xamlResult(XamlResult(error, (ex?.get("LineNumber") as? Number)?.toInt(), (ex?.get("LinePosition") as? Number)?.toInt()))
            }
            else -> {}   // MeasureViewport / RequestViewportResize: frames arrive at the window's own size
        }
    }

    /** Sends the buffer to render; queued until the session is ready. [projectPath] is relative to the project, with '/'. */
    fun updateXaml(xaml: String, projectPath: String) {
        synchronized(this) {
            if (!isReady) { pendingXaml = xaml to projectPath; return }
        }
        sendXaml(xaml, projectPath)
    }

    private fun sendXaml(xaml: String, projectPath: String) = send(Messages.updateXaml(xaml, project.targetPath.toString(), projectPath))

    /** The panel's size in DIPs and its scale (dpi = 96 × scale). */
    fun resize(width: Double, height: Double, dpi: Double) {
        viewportW = width.coerceAtLeast(1.0); viewportH = height.coerceAtLeast(1.0); this.dpi = dpi
        if (isReady) {
            send(Messages.renderInfo(dpi))
            send(Messages.viewport(viewportW, viewportH, dpi))
        }
    }

    fun send(message: Pair<MessageType, Map<String, Any?>>) {
        val s = socket ?: return
        try {
            synchronized(writeLock) { FrameCodec.write(s.getOutputStream(), message.first, message.second) }
        } catch (e: IOException) {
            if (!stopped.get()) fireExited("The preview connection failed: ${e.message}")
        }
    }

    /** Stops the host process. Idempotent; no further listener events afterwards. */
    fun stop() {
        if (!stopped.compareAndSet(false, true)) return
        runCatching { server?.close() }
        runCatching { socket?.close() }
        process?.let { p ->
            p.descendants().forEach { it.destroy() }
            p.destroy()
            Thread { runCatching { if (!p.waitFor(3, java.util.concurrent.TimeUnit.SECONDS)) p.destroyForcibly() } }.apply { isDaemon = true; start() }
        }
    }

    private val exitedFired = AtomicBoolean(false)
    private fun fireExited(reason: String) {
        if (exitedFired.compareAndSet(false, true)) listener.exited(reason)
    }

    companion object {
        fun decodeFrame(f: Map<String, Any?>): BufferedImage? {
            val w = (f["Width"] as? Number)?.toInt() ?: 0
            val h = (f["Height"] as? Number)?.toInt() ?: 0
            val stride = (f["Stride"] as? Number)?.toInt() ?: 0
            val format = (f["Format"] as? Number)?.toInt() ?: PixelFormats.BGRA8888
            val data = f["Data"] as? ByteArray ?: return null
            if (w <= 0 || h <= 0 || stride <= 0) return null
            val image = BufferedImage(w, h, if (format == PixelFormats.RGB565) BufferedImage.TYPE_INT_RGB else BufferedImage.TYPE_INT_ARGB_PRE)
            // Write the (already premultiplied) pixels straight into the raster; setRGB would premultiply them again.
            val target = (image.raster.dataBuffer as java.awt.image.DataBufferInt).data
            val buf = ByteBuffer.wrap(data).order(ByteOrder.LITTLE_ENDIAN)
            for (y in 0 until h) {
                val row = y * stride
                if (row + (if (format == PixelFormats.RGB565) w * 2 else w * 4) > data.size) break
                for (x in 0 until w) {
                    target[y * w + x] = when (format) {
                        PixelFormats.BGRA8888 -> buf.getInt(row + x * 4)               // 0xAARRGGBB when read little-endian
                        PixelFormats.RGBA8888 -> {
                            val v = buf.getInt(row + x * 4)                            // 0xAABBGGRR
                            (v and 0xFF00FF00.toInt()) or ((v and 0xFF) shl 16) or ((v shr 16) and 0xFF)
                        }
                        else -> {
                            val v = buf.getShort(row + x * 2).toInt() and 0xFFFF
                            val r = (v shr 11) and 0x1F; val g = (v shr 5) and 0x3F; val b = v and 0x1F
                            0xFF000000.toInt() or ((r * 255 / 31) shl 16) or ((g * 255 / 63) shl 8) or (b * 255 / 31)
                        }
                    }
                }
            }
            return image
        }
    }
}
