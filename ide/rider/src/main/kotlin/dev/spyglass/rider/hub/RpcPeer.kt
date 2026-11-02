// Minimal JSON-RPC 2.0 peer over a java.net.http WebSocket (the hub's /ide endpoint: one JSON message per text frame,
// numeric ids, requests in both directions). No IntelliJ dependency. Never sends an Origin header (the hub rejects it).
package dev.spyglass.rider.hub

import com.google.gson.Gson
import com.google.gson.JsonElement
import com.google.gson.JsonNull
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.net.URI
import java.net.http.HttpClient
import java.net.http.WebSocket
import java.time.Duration
import java.util.concurrent.CompletableFuture
import java.util.concurrent.CompletionStage
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.TimeUnit
import java.util.concurrent.TimeoutException
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicLong

class RpcException(val code: Int, message: String) : Exception(message)

class RpcPeer : WebSocket.Listener {
    private val gson = Gson()
    private val nextId = AtomicLong(1)
    private val pending = ConcurrentHashMap<Long, CompletableFuture<JsonElement>>()
    private val notifications = ConcurrentHashMap<String, (JsonElement?) -> Unit>()
    private val requests = ConcurrentHashMap<String, (JsonElement?) -> CompletionStage<Any?>>()
    private val buffer = StringBuilder()
    private val sendLock = Any()
    private var sendChain: CompletableFuture<*> = CompletableFuture.completedFuture(null)
    private val closed = AtomicBoolean(false)
    @Volatile private var socket: WebSocket? = null

    /** Called once when the connection ends (either side, or an error). */
    @Volatile var onClose: (() -> Unit)? = null

    val isOpen: Boolean get() = !closed.get() && socket?.let { !it.isOutputClosed && !it.isInputClosed } == true

    fun onNotification(method: String, handler: (JsonElement?) -> Unit) {
        notifications[method] = handler
    }

    fun onRequest(method: String, handler: (JsonElement?) -> CompletionStage<Any?>) {
        requests[method] = handler
    }

    /** Opens ws://... with this peer as the listener. Completes when the WebSocket handshake is done. */
    fun connect(http: HttpClient, uri: URI, timeout: Duration): CompletableFuture<RpcPeer> =
        http.newWebSocketBuilder().connectTimeout(timeout).buildAsync(uri, this).thenApply { this }

    fun request(method: String, params: Any?, timeoutMs: Long = 10_000): CompletableFuture<JsonElement> {
        val id = nextId.getAndIncrement()
        val f = CompletableFuture<JsonElement>()
        pending[id] = f
        f.orTimeout(timeoutMs, TimeUnit.MILLISECONDS).whenComplete { _, e ->
            pending.remove(id)
            if (e is TimeoutException) f.completeExceptionally(TimeoutException("$method timed out"))
        }
        send(JsonObject().apply {
            addProperty("jsonrpc", "2.0")
            addProperty("id", id)
            addProperty("method", method)
            add("params", gson.toJsonTree(params))
        })
        return f
    }

    fun notify(method: String, params: Any?) {
        if (!isOpen) return
        send(JsonObject().apply {
            addProperty("jsonrpc", "2.0")
            addProperty("method", method)
            add("params", gson.toJsonTree(params))
        })
    }

    fun <T> parse(json: JsonElement?, type: Class<T>): T = gson.fromJson(json ?: JsonObject(), type)

    fun close() {
        val ws = socket ?: return
        if (!ws.isOutputClosed) {
            synchronized(sendLock) {
                sendChain = sendChain.handle { _, _ -> null }.thenCompose { ws.sendClose(WebSocket.NORMAL_CLOSURE, "") }
            }
        }
        // The hub answers the close frame; if it does not, drop the connection anyway.
        CompletableFuture.delayedExecutor(1, TimeUnit.SECONDS).execute { if (!closed.get()) { ws.abort(); closed() } }
    }

    /** Drops the TCP connection without a close handshake. */
    fun abort() {
        socket?.abort()
        closed()
    }

    // ---------- WebSocket.Listener ----------

    override fun onOpen(webSocket: WebSocket) {
        socket = webSocket
        webSocket.request(1)
    }

    override fun onText(webSocket: WebSocket, data: CharSequence, last: Boolean): CompletionStage<*>? {
        buffer.append(data)
        if (last) {
            val text = buffer.toString()
            buffer.setLength(0)
            try {
                receive(text)
            } catch (_: Exception) {
                // A broken message or handler must not kill the connection.
            }
        }
        webSocket.request(1)
        return null
    }

    override fun onClose(webSocket: WebSocket, statusCode: Int, reason: String?): CompletionStage<*>? {
        closed()
        return null
    }

    override fun onError(webSocket: WebSocket, error: Throwable) {
        closed()
    }

    // ---------- internals ----------

    private fun closed() {
        if (!closed.compareAndSet(false, true)) return
        val e = RuntimeException("connection closed")
        for (f in pending.values) f.completeExceptionally(e)
        pending.clear()
        onClose?.invoke()
    }

    private fun send(message: JsonObject) {
        val ws = socket ?: return
        val text = gson.toJson(message)
        // java.net.http.WebSocket allows one outstanding send at a time: chain them.
        synchronized(sendLock) {
            sendChain = sendChain.handle { _, _ -> null }.thenCompose { ws.sendText(text, true) }
        }
    }

    private fun receive(text: String) {
        val msg = JsonParser.parseString(text) as? JsonObject ?: return
        val id = msg.get("id")?.takeUnless { it.isJsonNull }
        val method = msg.get("method")?.takeUnless { it.isJsonNull }?.asString
        if (method != null) {
            if (id == null) notifications[method]?.invoke(msg.get("params"))
            else answer(id, method, msg.get("params"))
            return
        }
        val key = id?.takeIf { it.isJsonPrimitive && it.asJsonPrimitive.isNumber }?.asLong ?: return
        val f = pending.remove(key) ?: return
        val error = msg.get("error")
        if (error is JsonObject) {
            f.completeExceptionally(RpcException(error.get("code")?.asInt ?: -32603, error.get("message")?.asString ?: "error"))
        } else {
            f.complete(msg.get("result") ?: JsonNull.INSTANCE)
        }
    }

    private fun answer(id: JsonElement, method: String, params: JsonElement?) {
        val handler = requests[method]
        if (handler == null) {
            sendError(id, -32601, "Method not found: $method")
            return
        }
        val stage = try {
            handler(params)
        } catch (e: Exception) {
            CompletableFuture.failedFuture(e)
        }
        stage.whenComplete { result, e ->
            if (e != null) {
                sendError(id, -32603, (e.cause ?: e).message ?: e.toString())
            } else {
                send(JsonObject().apply {
                    addProperty("jsonrpc", "2.0")
                    add("id", id)
                    add("result", if (result == null) JsonObject() else gson.toJsonTree(result))
                })
            }
        }
    }

    private fun sendError(id: JsonElement, code: Int, message: String) {
        send(JsonObject().apply {
            addProperty("jsonrpc", "2.0")
            add("id", id)
            add("error", JsonObject().apply {
                addProperty("code", code)
                addProperty("message", message)
            })
        })
    }
}
