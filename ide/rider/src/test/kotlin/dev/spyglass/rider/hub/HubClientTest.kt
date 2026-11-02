// Unit tests for HubClient against a fake hub (like ide/vscode/test/hubClient.test.ts). The real hub is covered by
// RealHubTest and by the C# IdeEndpointTests.
package dev.spyglass.rider.hub

import com.google.gson.Gson
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import org.java_websocket.WebSocket
import org.java_websocket.handshake.ClientHandshake
import org.java_websocket.server.WebSocketServer
import org.junit.jupiter.api.AfterEach
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import java.net.InetSocketAddress
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.Locale
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

class FakeHub(var token: String = "secret") : WebSocketServer(InetSocketAddress("127.0.0.1", 0)) {
    val home: Path = Files.createTempDirectory("spyglass-home-")
    val received = CopyOnWriteArrayList<JsonObject>()
    val handshakes = CopyOnWriteArrayList<ClientHandshake>()
    @Volatile var socket: WebSocket? = null
    private val started = CountDownLatch(1)
    private val gson = Gson()

    init {
        isReuseAddr = true
        start()
        started.await(5, TimeUnit.SECONDS)
        writeToken(token)
    }

    fun writeToken(t: String) = Files.writeString(home.resolve("hub-$port.json"), """{"port":$port,"token":"$t"}""")

    override fun onStart() = started.countDown()

    override fun onOpen(conn: WebSocket, handshake: ClientHandshake) {
        handshakes += handshake
        socket = conn
    }

    override fun onMessage(conn: WebSocket, message: String) {
        val m = JsonParser.parseString(message).asJsonObject
        received += m
        if (m.get("method")?.asString == "ide/hello") {
            val id = m.get("id").asLong
            if (m.getAsJsonObject("params").get("token").asString != token)
                conn.send("""{"jsonrpc":"2.0","id":$id,"error":{"code":-32011,"message":"Wrong token"}}""")
            else
                conn.send("""{"jsonrpc":"2.0","id":$id,"result":{"hubVersion":"0.1","port":$port,"capabilities":["hotReload"]}}""")
        }
    }

    fun push(method: String, params: Any, id: Long? = null) {
        val o = JsonObject()
        o.addProperty("jsonrpc", "2.0")
        o.addProperty("method", method)
        o.add("params", gson.toJsonTree(params))
        if (id != null) o.addProperty("id", id)
        socket!!.send(gson.toJson(o))
    }

    override fun onClose(conn: WebSocket, code: Int, reason: String?, remote: Boolean) {}
    override fun onError(conn: WebSocket?, ex: Exception) {}

    fun close() = stop(500)
}

fun until(ms: Long = 3000, cond: () -> Boolean) {
    val end = System.currentTimeMillis() + ms
    while (!cond()) {
        if (System.currentTimeMillis() > end) throw AssertionError("timeout")
        Thread.sleep(20)
    }
}

@Timeout(20)
class HubClientTest {
    private val hubs = ArrayList<FakeHub>()
    private val clients = ArrayList<HubClient>()

    @AfterEach
    fun cleanup() {
        clients.forEach { it.stop() }
        hubs.forEach { it.close() }
    }

    private fun hub() = FakeHub().also { hubs += it }

    private fun client(hub: FakeHub, logs: MutableList<String> = CopyOnWriteArrayList()) = HubClient(HubClientOptions(
        port = hub.port, home = hub.home.toString(), ide = "test", version = "1",
        workspaceFolders = { listOf("/ws") }, command = "spyglass", autoStart = false, log = { logs += it },
    )).also { clients += it }

    @Test
    fun `hello carries the token from the hub file and workspace folders, without an Origin header`() {
        val hub = hub()
        val c = client(hub)
        c.start()
        until { c.isConnected }
        val hello = hub.received.first { it.get("method")?.asString == "ide/hello" }
        val params = hello.getAsJsonObject("params")
        assertEquals("secret", params.get("token").asString)
        assertEquals("test", params.get("ide").asString)
        assertEquals(listOf("/ws"), params.getAsJsonArray("workspaceFolders").map { it.asString })
        assertTrue(hello.get("id").asJsonPrimitive.isNumber, "numeric ids")
        assertFalse(hub.handshakes.single().hasFieldValue("Origin"), "the hub rejects any request with an Origin header")
    }

    @Test
    fun `status decides which files are reloadable, buffers are sent as notifications`() {
        val hub = hub()
        val c = client(hub)
        var statuses = 0
        c.addListener(object : HubClientListener {
            override fun statusChanged(status: HotReloadStatus?) { statuses++ }
        })
        c.start()
        until { c.isConnected }
        val file = Paths.get("/proj/Views/Main.axaml").toAbsolutePath().toString()
        assertFalse(c.canReload(file))
        hub.push("hotReload/status", HotReloadStatus(
            apps = listOf(AppStatus("a1", "App", 1, hotReload = true, available = true, documents = 1)), files = listOf(file)))
        until { c.canReload(file) }
        assertEquals("App", c.status!!.apps.single().name)
        assertEquals(1, statuses)
        if (!System.getProperty("os.name").lowercase(Locale.ROOT).contains("linux"))
            assertTrue(c.canReload(file.uppercase(Locale.ROOT)), "case-insensitive like the hub")
        c.didOpen(file, 1, "<a/>")
        c.didChange(file, 2, "<b/>")
        c.didClose(file)
        until { hub.received.any { it.get("method")?.asString == "xaml/didClose" } }
        val xaml = hub.received.filter { it.get("method")?.asString?.startsWith("xaml/") == true }
        assertEquals(
            listOf(Triple("xaml/didOpen", 1L, "<a/>"), Triple("xaml/didChange", 2L, "<b/>"), Triple("xaml/didClose", 0L, null)),
            xaml.map { m ->
                val p = m.getAsJsonObject("params")
                assertEquals(file, p.get("path").asString)
                Triple(m.get("method").asString, p.get("version").asLong, p.get("text")?.asString)
            })
        assertTrue(xaml.all { it.get("id") == null }, "notifications have no id")
    }

    @Test
    fun `diagnostics are passed on and editor reveal is answered`() {
        val hub = hub()
        val c = client(hub)
        val diag = CompletableFuture<XamlDiagnostics>()
        c.addListener(object : HubClientListener {
            override fun diagnostics(diagnostics: XamlDiagnostics) { diag.complete(diagnostics) }
        })
        c.revealHandler = { r -> CompletableFuture.completedFuture(r.line == 7) }
        c.start()
        until { c.isConnected }
        hub.push("xaml/diagnostics", mapOf("path" to "/p/A.axaml", "version" to 3, "summary" to "✗",
            "items" to listOf(mapOf("severity" to "error", "message" to "boom", "line" to 2, "column" to 5))))
        val d = diag.get(3, TimeUnit.SECONDS)
        assertEquals(2, d.items[0].line)
        assertEquals(5, d.items[0].column)
        assertTrue(d.items[0].isError)
        assertNull(d.items[0].code)
        assertEquals(3L, d.version)

        hub.push("editor/reveal", RevealRequest("/p/A.axaml", 7, 1), id = 99)
        until { hub.received.any { it.get("id")?.asLong == 99L } }
        assertEquals("""{"ok":true}""", hub.received.first { it.get("id")?.asLong == 99L }.get("result").toString())

        hub.push("editor/reveal", RevealRequest("/p/A.axaml", 8, 1), id = 100)
        until { hub.received.any { it.get("id")?.asLong == 100L } }
        assertEquals("""{"ok":false}""", hub.received.first { it.get("id")?.asLong == 100L }.get("result").toString())

        hub.push("no/such", mapOf<String, Any>(), id = 101)
        until { hub.received.any { it.get("id")?.asLong == 101L } }
        assertEquals(-32601, hub.received.first { it.get("id")?.asLong == 101L }.getAsJsonObject("error").get("code").asInt)
    }

    @Test
    fun `wrong token is logged and retried, not fatal`() {
        val hub = hub()
        hub.writeToken("stale")
        val logs = CopyOnWriteArrayList<String>()
        val c = client(hub, logs)
        c.start()
        until { logs.any { it.contains("Wrong token") } }
        assertFalse(c.isConnected)
        hub.writeToken("secret")
        until(5000) { c.isConnected }
    }

    @Test
    fun `losing the hub clears reloadable files and reconnects`() {
        val hub = hub()
        val c = client(hub)
        var lost = false
        c.addListener(object : HubClientListener {
            override fun statusChanged(status: HotReloadStatus?) { if (status == null) lost = true }
        })
        c.start()
        until { c.isConnected }
        val file = Paths.get("/proj/X.axaml").toAbsolutePath().toString()
        hub.push("hotReload/status", HotReloadStatus(files = listOf(file)))
        until { c.canReload(file) }
        hub.socket!!.close()
        until { !c.isConnected && !c.canReload(file) && lost }
        until(5000) { c.isConnected }
    }

    @Test
    fun `no hub and no autoStart keeps retrying quietly`() {
        val hub = hub()
        val port = hub.port
        val home = hub.home
        hub.close()
        Files.delete(home.resolve("hub-$port.json"))
        val states = CopyOnWriteArrayList<ConnectionState>()
        val c = HubClient(HubClientOptions(port = port, home = home.toString(), autoStart = false)).also { clients += it }
        c.addListener(object : HubClientListener {
            override fun stateChanged(state: ConnectionState) { states += state }
        })
        c.start()
        until { states.count { it == ConnectionState.DISCONNECTED } >= 1 }
        assertFalse(states.contains(ConnectionState.STARTING))
    }

    @Test
    fun `reconnect drops the connection and says hello again`() {
        val hub = hub()
        val c = client(hub)
        c.start()
        until { c.isConnected }
        c.reconnect()
        until(5000) { hub.received.count { it.get("method")?.asString == "ide/hello" } == 2 && c.isConnected }
    }

    @Test
    fun `large buffers survive fragmentation`() {
        val hub = hub()
        val c = client(hub)
        c.start()
        until { c.isConnected }
        val text = "<Grid>" + "<TextBlock Text=\"é ⚡ x\"/>\n".repeat(20_000) + "</Grid>"
        c.didChange("/p/Big.axaml", 1, text)
        until { hub.received.any { it.get("method")?.asString == "xaml/didChange" } }
        assertEquals(text, hub.received.first { it.get("method")?.asString == "xaml/didChange" }.getAsJsonObject("params").get("text").asString)
        // and from the hub to us
        val got = CompletableFuture<XamlDiagnostics>()
        c.addListener(object : HubClientListener {
            override fun diagnostics(diagnostics: XamlDiagnostics) { got.complete(diagnostics) }
        })
        hub.push("xaml/diagnostics", XamlDiagnostics("/p/Big.axaml", 1, emptyList(), text))
        assertEquals(text, got.get(5, TimeUnit.SECONDS).summary)
    }
}

class HelpersTest {
    @Test
    fun `pathKey normalises`() {
        val linux = System.getProperty("os.name").lowercase(Locale.ROOT).contains("linux")
        val expected = Paths.get("/a/c.axaml").toAbsolutePath().toString()
        assertEquals(if (linux) expected else expected.lowercase(Locale.ROOT), HubClient.pathKey("/a/b/../c.axaml"))
    }

    @Test
    fun `serve command line`() {
        assertEquals(listOf("spyglass", "serve", "--port", "29500", "--quiet"), HubClient.serveCommandLine("spyglass", 29500))
        assertEquals(listOf("dotnet", "/x/Spyglass.DLL", "serve", "--port", "1", "--quiet"), HubClient.serveCommandLine("/x/Spyglass.DLL", 1))
        assertEquals(listOf("spyglass"), HubClient.launchCommandLine("  "))
    }

    @Test
    fun `port and home come from the environment`() {
        assertEquals(29521, HubClient.defaultPort(mapOf("SPYGLASS_PORT" to "29521")))
        assertEquals(29500, HubClient.defaultPort(mapOf("SPYGLASS_PORT" to "nope")))
        assertEquals("/tmp/h", HubClient.defaultHome(mapOf("SPYGLASS_HOME" to "/tmp/h")))
        assertTrue(HubClient.defaultHome(emptyMap()).endsWith(".spyglass"))
    }

    @Test
    fun `diagnostic ranges`() {
        val text = "<Window>\n    <TextBlock Text=\"{Binding Titel}\" />\r\n\n</Window>"
        fun at(line: Int?, col: Int?) = DiagnosticRanges.range(text, line, col).let { text.substring(it.first, it.last + 1) }
        assertEquals("{Binding", at(2, 22))
        assertEquals("Titel}", at(2, 31))
        assertEquals("<TextBlock Text=\"{Binding Titel}\" />", at(2, null))   // no column: the line without indentation
        assertEquals("<TextBlock Text=\"{Binding Titel}\" />", at(2, 37))     // column on a space
        assertEquals("Window", at(1, 2))
        assertEquals(1, DiagnosticRanges.range(text, 3, 1).count())              // empty line: one character
        assertEquals(1, DiagnosticRanges.range(text, 99, 1).count())             // past the end
        assertEquals(0, DiagnosticRanges.range("", 1, 1).count())
    }

    @Test
    fun `project files skip build output`() {
        val root = Files.createTempDirectory("spyglass-proj-")
        Files.createDirectories(root.resolve("src/Views"))
        Files.createDirectories(root.resolve("src/bin/Debug"))
        Files.writeString(root.resolve("src/Views/Main.axaml"), "<a/>")
        Files.writeString(root.resolve("src/bin/Debug/Copy.axaml"), "<a/>")
        Files.writeString(root.resolve("src/App.csproj"), "<Project/>")
        assertEquals(listOf(root.resolve("src/Views/Main.axaml")), ProjectFiles.find(root, setOf("axaml")))
        assertEquals(listOf(root.resolve("src/App.csproj")), ProjectFiles.find(root, setOf("csproj")))
    }

    @Test
    fun `findExecutable searches the given PATH`() {
        val dir = Files.createTempDirectory("spyglass-bin-")
        val exe = dir.resolve("spyglass-test-tool")
        Files.writeString(exe, "#!/bin/sh\n")
        exe.toFile().setExecutable(true)
        if (!System.getProperty("os.name").lowercase(Locale.ROOT).startsWith("windows"))
            assertEquals(exe.toString(), HubClient.findExecutable("spyglass-test-tool", mapOf("PATH" to dir.toString())))
        assertNull(HubClient.findExecutable("spyglass-no-such-tool", mapOf("PATH" to dir.toString())))
    }
}
