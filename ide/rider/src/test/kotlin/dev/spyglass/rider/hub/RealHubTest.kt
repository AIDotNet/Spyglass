// End-to-end test of HubClient against the real hub and the sample app. Runs only when SPYGLASS_REPO points at a
// Spyglass checkout whose Debug binaries are built:
//   SPYGLASS_REPO=/path/to/Spyglass ./gradlew test --tests '*RealHubTest*'
// It lets HubClient auto-start `dotnet Spyglass.dll serve` (the autoStart path) on port 29521 with a temporary
// SPYGLASS_HOME, starts the sample app, sends unsaved text (never touching the file on disk), and checks the result
// through the hub's MCP endpoint and xaml/diagnostics.
package dev.spyglass.rider.hub

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import org.junit.jupiter.api.Assertions.assertArrayEquals
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertNotNull
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test
import org.junit.jupiter.api.Timeout
import org.junit.jupiter.api.condition.EnabledIf
import java.net.URI
import java.net.http.HttpClient
import java.net.http.HttpRequest
import java.net.http.HttpResponse
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.Paths
import java.util.concurrent.CopyOnWriteArrayList
import java.util.concurrent.TimeUnit

@EnabledIf("enabled")
@Timeout(180)
class RealHubTest {
    companion object {
        const val PORT = 29521
        private val repo: String? = System.getenv("SPYGLASS_REPO")?.takeIf { it.isNotBlank() }

        @JvmStatic
        fun enabled() = repo != null
    }

    private fun log(s: String) = println("[RealHubTest] $s")

    private fun mcp(http: HttpClient, tool: String, args: String): JsonObject {
        val body = """{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"$tool","arguments":$args}}"""
        val res = http.send(HttpRequest.newBuilder(URI("http://127.0.0.1:$PORT/mcp"))
            .header("Content-Type", "application/json")
            .header("Accept", "application/json, text/event-stream")
            .POST(HttpRequest.BodyPublishers.ofString(body)).build(), HttpResponse.BodyHandlers.ofString())
        val json = res.body().lineSequence().firstOrNull { it.startsWith("data:") }?.removePrefix("data:")?.trim() ?: res.body()
        return JsonParser.parseString(json).asJsonObject
    }

    @Test
    fun `unsaved edits reach the running sample app and XAML errors come back as diagnostics`() {
        val root = Paths.get(repo!!)
        val hubDll = root.resolve("src/Spyglass.App/bin/Debug/net10.0/Spyglass.dll")
        val sampleDll = root.resolve("samples/Spyglass.Sample/bin/Debug/net10.0/Spyglass.Sample.dll")
        val xaml = root.resolve("samples/Spyglass.Sample/MainWindow.axaml")
        assertTrue(Files.exists(hubDll) && Files.exists(sampleDll), "build Spyglass first")
        val diskBefore = Files.readAllBytes(xaml)
        val original = String(diskBefore)

        val home: Path = Files.createTempDirectory("spyglass-rider-e2e-")
        val env = System.getenv() + mapOf("SPYGLASS_HOME" to home.toString(), "SPYGLASS_PORT" to PORT.toString())
        val logs = CopyOnWriteArrayList<String>()
        val received = CopyOnWriteArrayList<XamlDiagnostics>()
        val client = HubClient(HubClientOptions(
            port = PORT, home = home.toString(), ide = "rider", version = "test",
            workspaceFolders = { listOf(root.toString()) }, command = hubDll.toString(), autoStart = true,
            environment = env, log = { log(it); logs += it },
        ))
        client.addListener(object : HubClientListener {
            override fun diagnostics(diagnostics: XamlDiagnostics) {
                log("diagnostics v${diagnostics.version}: ${diagnostics.summary} ${diagnostics.items}")
                received.add(diagnostics)
            }
        })

        var servePid: Long? = null
        var sample: Process? = null
        try {
            val t0 = System.currentTimeMillis()
            client.start()
            until(60_000) { client.isConnected }
            log("connected after ${System.currentTimeMillis() - t0} ms (auto-started spyglass serve)")
            assertTrue(logs.any { it.startsWith("Starting dotnet ") }, "HubClient started the hub itself")
            servePid = JsonParser.parseString(Files.readString(home.resolve("hub-$PORT.json"))).asJsonObject.get("pid").asLong

            sample = ProcessBuilder("dotnet", sampleDll.toString())
                .apply { environment().putAll(env) }
                .redirectOutput(home.resolve("sample.out").toFile()).redirectErrorStream(true).start()
            log("sample pid ${sample.pid()}")
            until(60_000) { client.canReload(xaml.toString()) }
            val status = client.status!!
            log("status: apps=${status.apps} files=${status.files}")
            assertTrue(status.files.any { HubClient.pathKey(it) == HubClient.pathKey(xaml.toString()) })
            assertTrue(status.apps.any { it.hotReload && it.available })

            // didOpen with the disk text, then an unsaved change.
            client.didOpen(xaml.toString(), 1, original)
            val modified = original.replace("<TextBox x:Name=\"NameBox\"", "<TextBlock Text=\"From Rider\" />\n    <TextBox x:Name=\"NameBox\"")
            check(modified != original)
            val sent = System.currentTimeMillis()
            client.didChange(xaml.toString(), 2, modified)
            until(30_000) { received.any { it.version == 2L } }
            val ok = received.first { it.version == 2L }
            log("reload result after ${System.currentTimeMillis() - sent} ms: ${ok.summary}")
            assertTrue(ok.items.none { it.isError }, "no errors: ${ok.items}")

            val found = mcp(HttpClient.newHttpClient(), "ui_find", """{"query":"text=From Rider"}""")
            log("ui_find: ${found.toString().take(600)}")
            val result = found.getAsJsonObject("result")
            assertNotNull(result, "MCP result: $found")
            assertTrue(result.toString().contains("From Rider"), "the app shows the new TextBlock: $found")
            assertTrue(result.get("isError")?.asBoolean != true, "ui_find succeeded: $found")

            // A XAML error: the hub answers with an error diagnostic at a line, and the app keeps the last good UI.
            val broken = modified.replace("{Binding Title}", "{Binding Titel}")
            check(broken != modified)
            val brokenLine = broken.lines().indexOfFirst { it.contains("Titel") } + 1
            client.didChange(xaml.toString(), 3, broken)
            until(30_000) { received.any { it.version == 3L } }
            val bad = received.first { it.version == 3L }
            log("error result: ${bad.summary} ${bad.items}")
            val err = bad.items.first { it.isError }
            assertNotNull(err.line, "error has a line")
            assertEquals(brokenLine, err.line, "error is on the line with the typo")
            val stillThere = mcp(HttpClient.newHttpClient(), "ui_find", """{"query":"text=From Rider"}""")
            assertTrue(stillThere.toString().contains("From Rider"), "last good UI kept")

            // Back to the disk text: diagnostics clear.
            client.didChange(xaml.toString(), 4, original)
            until(30_000) { received.any { it.version == 4L } }
            assertTrue(received.first { it.version == 4L }.items.none { it.isError })
            client.didClose(xaml.toString())
            Thread.sleep(300)
        } finally {
            client.stop()
            sample?.let { p -> p.destroy(); if (!p.waitFor(10, TimeUnit.SECONDS)) p.destroyForcibly() }
            servePid?.let { pid -> ProcessHandle.of(pid).ifPresent { h -> h.destroy(); h.onExit().get(15, TimeUnit.SECONDS) } }
            assertArrayEquals(diskBefore, Files.readAllBytes(xaml), "the file on disk was never changed")
        }
    }
}
