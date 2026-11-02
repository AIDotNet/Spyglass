using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Reflection;

namespace S4.Network;

/// <summary>One captured exchange. Mirrors what the Network panel needs.</summary>
public sealed class CapturedRequest
{
    public long Id { get; init; }
    public required string Method { get; init; }
    public required Uri Uri { get; init; }
    public Dictionary<string, string[]> RequestHeaders { get; init; } = new();
    public BodyCapture? RequestBody { get; set; }
    public int? Status { get; set; }
    public Dictionary<string, string[]> ResponseHeaders { get; set; } = new();
    public BodyCapture? ResponseBody { get; set; }
    public string? Error { get; set; }
    public string? Initiator { get; set; }
    public long StartTs { get; init; }
    public long? HeadersTs { get; set; }
    public long? EndTs { get; set; }
    public string HttpVersion { get; set; } = "";
    public bool IsUpgrade { get; set; }

    public double Ms(long? ts) => ts is { } t ? Stopwatch.GetElapsedTime(StartTs, t).TotalMilliseconds : double.NaN;
}

/// <summary>Bounded copy of a body. Bytes beyond <see cref="Limit"/> are counted, not stored.</summary>
public sealed class BodyCapture(int limit)
{
    readonly MemoryStream _buffer = new();
    public int Limit { get; } = limit;
    public long TotalBytes { get; private set; }
    public bool Completed { get; private set; }
    public bool Truncated => TotalBytes > Limit;
    public event Action? OnCompleted;

    public void Append(ReadOnlySpan<byte> data)
    {
        TotalBytes += data.Length;
        var room = Limit - (int)_buffer.Length;
        if (room > 0) _buffer.Write(data[..Math.Min(room, data.Length)]);
    }

    public void Complete()
    {
        if (Completed) return;
        Completed = true;
        OnCompleted?.Invoke();
    }

    public byte[] Bytes => _buffer.ToArray();
}

/// <summary>
/// Subscribes to System.Net.Http's DiagnosticListener only while enabled. When nobody subscribes,
/// HttpClient skips its diagnostics path entirely, so a disabled capture costs nothing.
/// </summary>
public sealed class NetworkCapture : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    const string ListenerName = "HttpHandlerDiagnosticListener";
    const string StartEvent = "System.Net.Http.HttpRequestOut.Start";
    const string StopEvent = "System.Net.Http.HttpRequestOut.Stop";
    const string ExceptionEvent = "System.Net.Http.Exception";

    readonly ConcurrentDictionary<HttpRequestMessage, CapturedRequest> _inflight = new(ReferenceEqualityComparer.Instance);
    readonly Func<Uri, bool> _exclude;
    IDisposable? _allListeners;
    IDisposable? _httpSubscription;
    long _nextId;

    public ConcurrentQueue<CapturedRequest> Captured { get; } = new();
    public int BodyLimit { get; set; } = 1024 * 1024;
    public bool CaptureInitiator { get; set; } = true;
    public bool IsEnabled => _allListeners is not null;

    /// <param name="exclude">Requests to ignore, e.g. the agent's own connection to the Spyglass client.</param>
    public NetworkCapture(Func<Uri, bool> exclude) => _exclude = exclude;

    public void Enable() => _allListeners ??= DiagnosticListener.AllListeners.Subscribe(this);

    public void Disable()
    {
        _httpSubscription?.Dispose();
        _httpSubscription = null;
        _allListeners?.Dispose();
        _allListeners = null;
        _inflight.Clear();
    }

    // ---- IObserver<DiagnosticListener> ----
    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == ListenerName)
            _httpSubscription = listener.Subscribe(this, (name, _, _) =>
                name is "System.Net.Http.HttpRequestOut" or StartEvent or StopEvent or ExceptionEvent);
    }
    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }

    // ---- IObserver<KeyValuePair<string, object?>> ----
    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> evt)
    {
        switch (evt.Key)
        {
            case StartEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req:
                OnStart(req);
                break;
            case StopEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req:
                OnStop(req, Get<HttpResponseMessage>(evt.Value, "Response"), Get<TaskStatus>(evt.Value, "RequestTaskStatus"));
                break;
            case ExceptionEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req
                                     && _inflight.TryGetValue(req, out var c):
                c.Error = Get<Exception>(evt.Value, "Exception")?.GetBaseException().Message;
                break;
        }
    }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    void OnStart(HttpRequestMessage req)
    {
        if (req.RequestUri is null || _exclude(req.RequestUri)) return;
        var c = new CapturedRequest
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = req.Method.Method,
            Uri = req.RequestUri,
            RequestHeaders = Headers(req.Headers, req.Content?.Headers),
            StartTs = Stopwatch.GetTimestamp(),
            Initiator = CaptureInitiator ? UserFrame(new StackTrace(1, false)) : null,
        };
        if (req.Content is { } content)
        {
            var body = new BodyCapture(BodyLimit);
            c.RequestBody = body;
            req.Content = new TeeContent(content, body);   // the handler serializes our wrapper after this event
        }
        _inflight[req] = c;
    }

    void OnStop(HttpRequestMessage req, HttpResponseMessage? resp, TaskStatus status)
    {
        if (!_inflight.TryRemove(req, out var c)) return;
        c.HeadersTs = Stopwatch.GetTimestamp();
        if (resp is null)
        {
            c.EndTs = c.HeadersTs;
            c.Error ??= status.ToString();
            Captured.Enqueue(c);
            return;
        }
        c.Status = (int)resp.StatusCode;
        c.HttpVersion = resp.Version.ToString();
        c.ResponseHeaders = Headers(resp.Headers, resp.Content.Headers);
        // Protocol upgrades (WebSocket over HTTP/1.1 101, or HTTP/2 extended CONNECT) hand the raw duplex
        // connection stream to the caller through Content. Wrapping it breaks the socket, so record the handshake only.
        if (resp.StatusCode == HttpStatusCode.SwitchingProtocols || req.Headers.Protocol is not null)
        {
            c.IsUpgrade = true;
            c.EndTs = c.HeadersTs;
            Captured.Enqueue(c);
            return;
        }
        var body = new BodyCapture(BodyLimit);
        body.OnCompleted += () => c.EndTs = Stopwatch.GetTimestamp();
        c.ResponseBody = body;
        resp.Content = new TeeContent(resp.Content, body); // Stop fires before HttpClient sees the response
        Captured.Enqueue(c);
    }

    static Dictionary<string, string[]> Headers(params System.Net.Http.Headers.HttpHeaders?[] sets)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var set in sets)
            if (set is not null)
                foreach (var h in set) d[h.Key] = h.Value.ToArray();
        return d;
    }

    /// <summary>First stack frame outside System.* / Microsoft.* / this capture code.</summary>
    static string? UserFrame(StackTrace st)
    {
        foreach (var f in st.GetFrames())
        {
            var m = f.GetMethod();
            var ns = m?.DeclaringType?.Namespace ?? "";
            if (m is null || ns.StartsWith("System") || ns.StartsWith("Microsoft") || m.DeclaringType == typeof(NetworkCapture)) continue;
            return $"{m.DeclaringType?.FullName}.{m.Name}";
        }
        return null;
    }

    // DiagnosticListener payloads are anonymous types; read them by name.
    static T? Get<T>(object? payload, string name) =>
        payload?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload) is T v ? v : default;
}

/// <summary>Wraps HttpContent and copies bytes into a <see cref="BodyCapture"/> as they flow, without buffering or delaying the stream.</summary>
sealed class TeeContent : HttpContent
{
    readonly HttpContent _inner;
    readonly BodyCapture _capture;

    public TeeContent(HttpContent inner, BodyCapture capture)
    {
        _inner = inner;
        _capture = capture;
        foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await using var src = await _inner.ReadAsStreamAsync();
        await using var tee = new TeeStream(src, _capture);
        await tee.CopyToAsync(stream);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new TeeStream(await _inner.ReadAsStreamAsync(), _capture);

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? -1;
        return length >= 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }
}

sealed class TeeStream(Stream inner, BodyCapture capture) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Track(buffer.AsSpan(offset, count), inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct);
        return Track(buffer.Span, n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    int Track(Span<byte> buffer, int n)
    {
        if (n > 0) capture.Append(buffer[..n]);
        else capture.Complete();
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { capture.Complete(); inner.Dispose(); }
        base.Dispose(disposing);
    }
}
