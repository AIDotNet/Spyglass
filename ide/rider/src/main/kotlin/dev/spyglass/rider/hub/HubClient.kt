// Connection to the Spyglass hub's /ide endpoint (design/hot-reload/README.md §7.1, §8). No IntelliJ dependency, so it
// can be tested with plain JUnit. Mirrors ide/vscode/src/hubClient.ts: finds the hub's token in ~/.spyglass/hub-PORT.json,
// starts `spyglass serve` when nothing listens and autoStart is on, reconnects with backoff (1 s → 10 s), and passes the
// hub's pushes to a listener.
package dev.spyglass.rider.hub

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.File
import java.net.ConnectException
import java.net.URI
import java.net.http.HttpClient
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.time.Duration
import java.util.Locale
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CompletionException
import java.util.concurrent.ExecutionException
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit

enum class ConnectionState { DISCONNECTED, STARTING, CONNECTING, CONNECTED }

class HubClientOptions(
    /** 0 = SPYGLASS_PORT or 29500. */
    val port: Int = 0,
    /** null = SPYGLASS_HOME or ~/.spyglass. */
    val home: String? = null,
    val ide: String = "rider",
    val version: String = "",
    val workspaceFolders: () -> List<String> = { emptyList() },
    /** `spyglass`, a path to the executable, or a path to Spyglass.dll (run with `dotnet`). */
    val command: String = "spyglass",
    val autoStart: Boolean = true,
    /** Environment for `spyglass serve` and for finding the executable; null = this process's. */
    val environment: Map<String, String>? = null,
    val log: (String) -> Unit = {},
)

/** Callbacks run on the client's or the WebSocket's thread, never on a UI thread. */
interface HubClientListener {
    fun stateChanged(state: ConnectionState) {}
    /** null when the connection is lost. */
    fun statusChanged(status: HotReloadStatus?) {}
    fun diagnostics(diagnostics: XamlDiagnostics) {}
    /** After ide/hello succeeded: announce the open files again. */
    fun connected() {}
}

class HubClient(private val options: HubClientOptions) {
    private val executor = Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "Spyglass hub client").apply { isDaemon = true } }
    private val http: HttpClient = HttpClient.newBuilder().connectTimeout(Duration.ofSeconds(3)).build()
    private val listeners = java.util.concurrent.CopyOnWriteArrayList<HubClientListener>()
    @Volatile private var peer: RpcPeer? = null
    @Volatile private var stopped = false
    @Volatile private var files: Set<String> = emptySet()
    private var retryMs = 1000L
    private var retry: ScheduledFuture<*>? = null
    private var lastServeStart = 0L

    @Volatile var state: ConnectionState = ConnectionState.DISCONNECTED
        private set
    @Volatile var status: HotReloadStatus? = null
        private set

    /** Called for editor/reveal; completes with true when the file was shown. */
    @Volatile var revealHandler: ((RevealRequest) -> CompletableFuture<Boolean>)? = null

    val port: Int get() = if (options.port in 1..65535) options.port else defaultPort(options.environment)

    val tokenFile: Path get() = Paths.get(options.home ?: defaultHome(options.environment), "hub-$port.json")

    val isConnected: Boolean get() = state == ConnectionState.CONNECTED && peer?.isOpen == true

    fun addListener(l: HubClientListener) { listeners += l }

    /** True when some connected app can hot-reload this file. */
    fun canReload(file: String): Boolean = files.contains(pathKey(file))

    fun start() {
        stopped = false
        executor.execute { connectLoop() }
    }

    /** Disconnects for good and releases the threads; create a new client to connect again. */
    fun stop() {
        stopped = true
        executor.execute {
            retry?.cancel(false)
            peer?.close()
            setState(ConnectionState.DISCONNECTED)
        }
        executor.shutdown()
        CompletableFuture.delayedExecutor(2, TimeUnit.SECONDS).execute { http.shutdownNow() }
    }

    /** Drops the connection and connects again now. */
    fun reconnect() {
        executor.execute {
            retry?.cancel(false)
            retryMs = 1000
            val p = peer
            if (p != null && p.isOpen) p.close() else connectLoop()
        }
    }

    fun didOpen(file: String, version: Long, text: String) = peer?.notify(Methods.XAML_DID_OPEN, XamlDocumentParams(file, version, text))

    fun didChange(file: String, version: Long, text: String) = peer?.notify(Methods.XAML_DID_CHANGE, XamlDocumentParams(file, version, text))

    fun didClose(file: String) = peer?.notify(Methods.XAML_DID_CLOSE, XamlDocumentParams(file, 0))

    // ---------- connection ----------

    private fun setState(s: ConnectionState) {
        if (state == s) return
        state = s
        listeners.forEach { it.stateChanged(s) }
    }

    private fun schedule(delayMs: Long) {
        if (stopped || executor.isShutdown) return
        retry?.cancel(false)
        retry = executor.schedule({ connectLoop() }, delayMs, TimeUnit.MILLISECONDS)
    }

    private fun connectLoop() {
        if (stopped || state == ConnectionState.CONNECTING || state == ConnectionState.CONNECTED) return
        try {
            connectOnce()
            retryMs = 1000
        } catch (e: Exception) {
            setState(ConnectionState.DISCONNECTED)
            val cause = unwrap(e)
            if (cause is RpcException) options.log("Spyglass refused the connection: ${cause.message}")
            schedule(retryMs)
            retryMs = minOf(retryMs * 2, 10_000)
        }
    }

    private class NoHubException : Exception("no hub")

    private fun connectOnce() {
        setState(ConnectionState.CONNECTING)
        var token: String? = readToken()
        val p = try {
            if (token == null) throw NoHubException()
            open()
        } catch (e: Exception) {
            // Nothing listens (no token file, or a stale one from a hub that exited): start one if allowed.
            val c = unwrap(e)
            if (!options.autoStart || (c !is ConnectException && c !is NoHubException)) throw e
            if (System.currentTimeMillis() - lastServeStart < 30_000) throw e
            startServe()
            token = waitForToken(15_000)
            open()
        }

        p.onNotification(Methods.HOT_RELOAD_STATUS) { json ->
            val s = p.parse(json, HotReloadStatus::class.java)
            status = s
            files = s.files.map(::pathKey).toSet()
            listeners.forEach { it.statusChanged(s) }
        }
        p.onNotification(Methods.XAML_DIAGNOSTICS) { json ->
            val d = p.parse(json, XamlDiagnostics::class.java)
            listeners.forEach { it.diagnostics(d) }
        }
        p.onRequest(Methods.EDITOR_REVEAL) { json ->
            val r = p.parse(json, RevealRequest::class.java)
            val handler = revealHandler
            (handler?.invoke(r) ?: CompletableFuture.completedFuture(false))
                .exceptionally { false }
                .thenApply<Any?> { RevealResult(it) }
        }
        p.onClose = {
            executor.execute {
                if (peer === p) {
                    peer = null
                    files = emptySet()
                    status = null
                    setState(ConnectionState.DISCONNECTED)
                    listeners.forEach { it.statusChanged(null) }
                    schedule(retryMs)
                }
            }
        }

        try {
            p.request(Methods.IDE_HELLO, IdeHelloParams(options.ide, options.version, options.workspaceFolders(), token)).get()
        } catch (e: Exception) {
            p.abort()
            throw e
        }
        peer = p
        setState(ConnectionState.CONNECTED)
        options.log("Connected to Spyglass on port $port.")
        listeners.forEach { it.connected() }
        // The hub may have pushed status before `peer` was set; nothing is lost because status handling does not need it.
    }

    private fun open(): RpcPeer = RpcPeer().connect(http, URI("ws://127.0.0.1:$port/ide"), Duration.ofSeconds(3)).get(5, TimeUnit.SECONDS)

    private fun readToken(): String? = try {
        val j = JsonParser.parseString(Files.readString(tokenFile)) as? JsonObject
        j?.get("token")?.takeIf { it.isJsonPrimitive }?.asString
    } catch (_: Exception) {
        null
    }

    private fun waitForToken(timeoutMs: Long): String {
        val before = readToken()
        val until = System.currentTimeMillis() + timeoutMs
        while (System.currentTimeMillis() < until && !stopped) {
            val t = readToken()
            if (t != null && t != before) return t
            Thread.sleep(200)
        }
        return readToken() ?: throw IllegalStateException("spyglass serve did not start (no $tokenFile)")
    }

    private fun startServe() {
        lastServeStart = System.currentTimeMillis()
        setState(ConnectionState.STARTING)
        val argv = serveCommandLine(options.command, port)
        options.log("Starting ${argv.joinToString(" ")}")
        try {
            val exe = findExecutable(argv[0], options.environment) ?: argv[0]
            val pb = ProcessBuilder(listOf(exe) + argv.drop(1))
                .redirectOutput(ProcessBuilder.Redirect.DISCARD)
                .redirectError(ProcessBuilder.Redirect.DISCARD)
            options.environment?.let { env -> pb.environment().clear(); pb.environment().putAll(env) }
            // Detached in effect: nothing ties its lifetime to ours (stdin at EOF, output discarded, never waited for).
            pb.start().outputStream.close()
        } catch (e: Exception) {
            options.log(
                if (findExecutable(argv[0], options.environment) == null)
                    "Cannot start Spyglass: '${argv[0]}' not found. Install it with `dotnet tool install -g TKCode.Spyglass`, or set the Spyglass command in Settings › Tools › Spyglass."
                else "Cannot start Spyglass: ${e.message}"
            )
        }
    }

    companion object {
        const val DEFAULT_PORT = 29500

        fun defaultPort(env: Map<String, String>? = null): Int {
            val p = (env ?: System.getenv())["SPYGLASS_PORT"]?.toIntOrNull()
            return if (p != null && p in 1..65535) p else DEFAULT_PORT
        }

        fun defaultHome(env: Map<String, String>? = null): String =
            (env ?: System.getenv())["SPYGLASS_HOME"]?.takeIf { it.isNotEmpty() }
                ?: Paths.get(System.getProperty("user.home"), ".spyglass").toString()

        private val caseInsensitive = !System.getProperty("os.name").lowercase(Locale.ROOT).contains("linux")

        /** Paths as the hub compares them: case-insensitive on Windows and macOS. */
        fun pathKey(p: String): String {
            val n = Paths.get(p).toAbsolutePath().normalize().toString()
            return if (caseInsensitive) n.lowercase(Locale.ROOT) else n
        }

        /** `spyglass serve --port N --quiet`, or `dotnet path/Spyglass.dll serve …`, or a custom command. */
        fun serveCommandLine(command: String, port: Int): List<String> = launchCommandLine(command) + listOf("serve", "--port", port.toString(), "--quiet")

        /** The command that starts Spyglass (the GUI when run without arguments). */
        fun launchCommandLine(command: String): List<String> {
            val cmd = command.trim().ifEmpty { "spyglass" }
            return if (cmd.lowercase(Locale.ROOT).endsWith(".dll")) listOf("dotnet", cmd) else listOf(cmd)
        }

        /**
         * Resolves a command name against PATH plus the places `dotnet` and dotnet tools usually live (an IDE started from
         * the Dock or a desktop launcher often has a minimal PATH). Returns the argument when it already is a path.
         */
        fun findExecutable(name: String, env: Map<String, String>? = null): String? {
            val e = env ?: System.getenv()
            if (name.contains('/') || name.contains('\\')) return name.takeIf { File(it).exists() }
            val windows = System.getProperty("os.name").lowercase(Locale.ROOT).startsWith("windows")
            val home = System.getProperty("user.home")
            val dirs = (e["PATH"] ?: e["Path"] ?: "").split(File.pathSeparatorChar).filter { it.isNotEmpty() } +
                listOfNotNull(
                    e["DOTNET_ROOT"],
                    Paths.get(home, ".dotnet", "tools").toString(),
                    Paths.get(home, ".dotnet").toString(),
                ) +
                (if (windows) listOf("C:\\Program Files\\dotnet") else listOf("/usr/local/share/dotnet", "/usr/share/dotnet", "/opt/homebrew/bin", "/usr/local/bin"))
            val exts = if (windows) listOf(".exe", ".cmd", ".bat", "") else listOf("")
            for (d in dirs) for (x in exts) {
                val f = File(d, name + x)
                if (f.isFile && (windows || f.canExecute())) return f.absolutePath
            }
            return null
        }

        private fun unwrap(e: Throwable): Throwable {
            var c = e
            while ((c is CompletionException || c is ExecutionException) && c.cause != null) c = c.cause!!
            return c
        }
    }
}
