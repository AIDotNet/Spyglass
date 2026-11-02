using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Spyglass.VisualStudio.Core;

/// <summary>An error response to a JSON-RPC request.</summary>
public sealed class JsonRpcException(int code, string message) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// Minimal JSON-RPC 2.0 peer over a WebSocket, as the hub's /ide endpoint speaks it: one JSON message per text frame,
/// numeric ids, requests in both directions. Outgoing messages go through one queue, so they reach the hub in the order
/// they were sent (didOpen always before the didChange that follows it).
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    readonly WebSocket _socket;
    readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    readonly ConcurrentDictionary<string, Action<JsonElement>> _notifications = new();
    readonly ConcurrentDictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _requests = new();
    readonly CancellationTokenSource _stop = new();
    long _nextId;
    int _disposed;
    Task? _run;

    public JsonRpcPeer(WebSocket socket) => _socket = socket;

    public bool IsOpen => _disposed == 0 && _socket.State == WebSocketState.Open && !_stop.IsCancellationRequested;

    /// <summary>Raised once when the connection ends (closed by either side, or broken).</summary>
    public event Action? Closed;

    public void OnNotification<T>(string method, Action<T> handler) =>
        _notifications[method] = p => handler(p.Deserialize<T>(IdeJson.Options)!);

    public void OnRequest<TParams, TResult>(string method, Func<TParams, CancellationToken, Task<TResult>> handler) =>
        _requests[method] = async (p, ct) => await handler(p.Deserialize<TParams>(IdeJson.Options)!, ct).ConfigureAwait(false);

    /// <summary>Starts the receive and send loops; the task completes when the connection ends.</summary>
    public Task RunAsync() => _run ??= RunCoreAsync();

    public async Task<TResult> RequestAsync<TResult>(string method, object? parameters, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = ToNode(parameters) });
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(10));
            using (cts.Token.Register(() => tcs.TrySetException(ct.IsCancellationRequested
                       ? new OperationCanceledException(ct)
                       : new TimeoutException($"{method} timed out"))))
            {
                var result = await tcs.Task.ConfigureAwait(false);
                return result.Deserialize<TResult>(IdeJson.Options)!;
            }
        }
        finally { _pending.TryRemove(id, out _); }
    }

    /// <summary>Queues a notification; dropped when the connection is not open.</summary>
    public void Notify(string method, object? parameters)
    {
        if (IsOpen) Enqueue(new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = ToNode(parameters) });
    }

    public async Task CloseAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", cts.Token).ConfigureAwait(false);
            }
        }
        catch { /* already gone */ }
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* disposed: already stopped */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        await CloseAsync().ConfigureAwait(false);
        if (_run is not null) try { await _run.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { /* best effort */ }
        _socket.Dispose();
    }

    static JsonNode? ToNode(object? value) => value is null ? null : JsonSerializer.SerializeToNode(value, value.GetType(), IdeJson.Options);

    void Enqueue(JsonObject message) => _outgoing.Writer.TryWrite(message.ToJsonString());

    async Task RunCoreAsync()
    {
        var send = SendLoopAsync();
        try { await ReceiveLoopAsync().ConfigureAwait(false); }
        catch { /* the connection broke; same as closed */ }
        finally
        {
            _stop.Cancel();
            _outgoing.Writer.TryComplete();
            foreach (var p in _pending.Values) p.TrySetException(new WebSocketException("connection closed"));
            try { await send.ConfigureAwait(false); } catch { /* closed */ }
            try { if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived) _socket.Abort(); } catch { }
            Closed?.Invoke();
        }
    }

    async Task SendLoopAsync()
    {
        try
        {
            await foreach (var text in _outgoing.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
                await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _stop.Token).ConfigureAwait(false);
        }
        catch { _stop.Cancel(); }
    }

    async Task ReceiveLoopAsync()
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (!_stop.IsCancellationRequested)
        {
            var r = await _socket.ReceiveAsync(buffer, _stop.Token).ConfigureAwait(false);
            if (r.MessageType == WebSocketMessageType.Close) return;
            message.Write(buffer, 0, r.Count);
            if (!r.EndOfMessage) continue;
            var bytes = message.ToArray();
            message.SetLength(0);
            if (r.MessageType == WebSocketMessageType.Text) Dispatch(bytes);
        }
    }

    void Dispatch(byte[] utf8)
    {
        JsonElement msg;
        try { msg = JsonDocument.Parse(utf8).RootElement; }
        catch (JsonException) { return; }
        if (msg.ValueKind != JsonValueKind.Object) return;

        var hasId = msg.TryGetProperty("id", out var id) && id.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        msg.TryGetProperty("params", out var p);
        if (msg.TryGetProperty("method", out var m) && m.GetString() is { } method)
        {
            if (!hasId)
            {
                if (_notifications.TryGetValue(method, out var n))
                    try { n(p); } catch { /* a bad notification never breaks the connection */ }
                return;
            }
            _ = AnswerAsync(id.Clone(), method, p.ValueKind == JsonValueKind.Undefined ? default : p.Clone());
            return;
        }

        if (!hasId || id.ValueKind != JsonValueKind.Number || !_pending.TryRemove(id.GetInt64(), out var tcs)) return;
        if (msg.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var code = err.TryGetProperty("code", out var c) && c.TryGetInt32(out var ci) ? ci : RpcErrorCodes.InternalError;
            var text = err.TryGetProperty("message", out var mm) ? mm.GetString() ?? "" : "";
            tcs.TrySetException(new JsonRpcException(code, text));
        }
        else tcs.TrySetResult(msg.TryGetProperty("result", out var res) ? res.Clone() : default);
    }

    async Task AnswerAsync(JsonElement id, string method, JsonElement p)
    {
        var reply = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = JsonNode.Parse(id.GetRawText()) };
        if (!_requests.TryGetValue(method, out var handler))
            reply["error"] = new JsonObject { ["code"] = RpcErrorCodes.MethodNotFound, ["message"] = $"Method not found: {method}" };
        else
        {
            try { reply["result"] = ToNode(await handler(p, _stop.Token).ConfigureAwait(false)) ?? new JsonObject(); }
            catch (Exception ex)
            {
                reply["error"] = new JsonObject { ["code"] = ex is JsonRpcException rpc ? rpc.Code : RpcErrorCodes.InternalError, ["message"] = ex.Message };
            }
        }
        if (IsOpen) Enqueue(reply);
    }
}
