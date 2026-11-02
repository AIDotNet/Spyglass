using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Spyglass.Protocol;

/// <summary>
/// Symmetric JSON-RPC 2.0 endpoint over a WebSocket (design/protocol §2). Both the agent and the client use it:
/// either side can send requests and notifications. Requests are dispatched concurrently.
/// </summary>
public sealed class JsonRpcPeer : IAsyncDisposable
{
    readonly WebSocket _socket;
    readonly SemaphoreSlim _sendLock = new(1, 1);
    readonly ConcurrentDictionary<long, TaskCompletionSource<JsonRpcMessage>> _pending = new();
    readonly ConcurrentDictionary<string, Func<JsonElement?, CancellationToken, Task<JsonElement>>> _requestHandlers = new();
    readonly ConcurrentDictionary<string, Action<JsonElement?>> _notificationHandlers = new();
    readonly CancellationTokenSource _cts = new();
    long _nextId;

    public JsonRpcPeer(WebSocket socket) => _socket = socket;

    /// <summary>Raised once when the receive loop ends (remote close, error, or dispose).</summary>
    public event Action<Exception?>? Closed;

    public bool IsOpen => _socket.State == WebSocketState.Open;

    // ---------- registration ----------

    public void Handle<TParams, TResult>(string method, Func<TParams, CancellationToken, Task<TResult>> handler) =>
        _requestHandlers[method] = async (p, ct) =>
        {
            var typed = typeof(TParams) == typeof(Empty) ? (TParams)(object)Empty.Instance : SpyglassJson.Read<TParams>(p);
            return SpyglassJson.ToElement(await handler(typed, ct).ConfigureAwait(false));
        };

    public void OnNotification<TParams>(string method, Action<TParams> handler) =>
        _notificationHandlers[method] = p => handler(SpyglassJson.Read<TParams>(p));

    // ---------- outgoing ----------

    public async Task<TResult> RequestAsync<TParams, TResult>(string method, TParams @params, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonRpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        try
        {
            await SendAsync(SpyglassJson.Request(id, method, @params)).ConfigureAwait(false);
            var response = await tcs.Task.ConfigureAwait(false);
            if (response.Error is { } e) throw new SpyglassProtocolException(e.Code, e.Message);
            return typeof(TResult) == typeof(Empty) ? (TResult)(object)Empty.Instance : SpyglassJson.Read<TResult>(response.Result);
        }
        finally { _pending.TryRemove(id, out _); }
    }

    public Task NotifyAsync<TParams>(string method, TParams @params) =>
        IsOpen ? SendAsync(SpyglassJson.Notification(method, @params)) : Task.CompletedTask;

    async Task SendAsync(JsonRpcMessage message)
    {
        var bytes = SpyglassJson.Serialize(message);
        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open) throw new WebSocketException("connection closed");
            await _socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token).ConfigureAwait(false);
        }
        finally { _sendLock.Release(); }
    }

    // ---------- incoming ----------

    /// <summary>Runs the receive loop until the socket closes.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _cts.Token);
        Exception? error = null;
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open && !linked.IsCancellationRequested)
            {
                var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), linked.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;

                var payload = message.ToArray();
                message.SetLength(0);
                Dispatch(payload);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { error = ex; }
        finally
        {
            foreach (var p in _pending.Values) p.TrySetException(new WebSocketException("connection closed"));
            Closed?.Invoke(error);
        }
    }

    void Dispatch(byte[] payload)
    {
        JsonRpcMessage msg;
        try { msg = SpyglassJson.Deserialize(payload); }
        catch (Exception ex)
        {
            _ = SendSafe(SpyglassJson.ErrorResponse(null, ErrorCodes.ParseError, ex.Message));
            return;
        }

        if (msg.IsResponse)
        {
            if (_pending.TryGetValue(msg.Id!.Value, out var tcs)) tcs.TrySetResult(msg);
            return;
        }
        if (msg.IsNotification)
        {
            if (_notificationHandlers.TryGetValue(msg.Method!, out var nh))
                try { nh(msg.Params); } catch { /* a bad notification must not kill the connection */ }
            return;
        }
        if (msg.IsRequest)
            _ = Task.Run(() => HandleRequestAsync(msg));
    }

    async Task HandleRequestAsync(JsonRpcMessage msg)
    {
        var id = msg.Id!.Value;
        if (!_requestHandlers.TryGetValue(msg.Method!, out var handler))
        {
            await SendSafe(SpyglassJson.ErrorResponse(id, ErrorCodes.MethodNotFound, $"Method '{msg.Method}' not found")).ConfigureAwait(false);
            return;
        }
        try
        {
            var result = await handler(msg.Params, _cts.Token).ConfigureAwait(false);
            await SendSafe(new JsonRpcMessage { Id = id, Result = result }).ConfigureAwait(false);
        }
        catch (SpyglassProtocolException ex)
        {
            await SendSafe(SpyglassJson.ErrorResponse(id, ex.Code, ex.Message)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await SendSafe(SpyglassJson.ErrorResponse(id, ErrorCodes.InternalError, $"{ex.GetType().Name}: {ex.Message}")).ConfigureAwait(false);
        }
    }

    async Task SendSafe(JsonRpcMessage m)
    {
        try { await SendAsync(m).ConfigureAwait(false); } catch { /* connection gone */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", timeout.Token).ConfigureAwait(false);
            }
            catch { }
        }
        _cts.Cancel();
        _socket.Dispose();
    }
}
