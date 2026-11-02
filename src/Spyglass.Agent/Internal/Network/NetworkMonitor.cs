using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Agent.Internal.Network;

/// <summary>
/// HTTP capture for the Network panel (design/network/, spikes S4/S5). Subscribes to System.Net.Http's
/// DiagnosticListener only between Enable and Disable: when off, HttpClient skips its diagnostics path entirely.
/// Thread-safe (callbacks arrive on arbitrary threads).
/// </summary>
internal sealed class NetworkMonitor : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
{
    const string ListenerName = "HttpHandlerDiagnosticListener";
    const string StartEvent = "System.Net.Http.HttpRequestOut.Start";
    const string StopEvent = "System.Net.Http.HttpRequestOut.Stop";
    const string ExceptionEvent = "System.Net.Http.Exception";
    const int MaxRequests = 2000;
    const long MaxBodyBytes = 50L * 1024 * 1024;

    static readonly string[] DefaultSensitive = ["Authorization", "Proxy-Authorization", "Cookie", "Set-Cookie", "X-Api-Key"];

    sealed class Entry
    {
        public required long Id;
        public required string Method;
        public required string Url;
        public required DateTimeOffset StartTime;
        public required long StartTs;
        public Guid Activity;
        public List<HeaderDto> RequestHeaders = [];
        public BodyCapture? RequestBody;
        public InitiatorDto? Initiator;
        public long? RedirectedFromId;
        public int? Status;
        public string? StatusText;
        public string? HttpVersion;
        public List<HeaderDto> ResponseHeaders = [];
        public string? MimeType;
        public BodyCapture? ResponseBody;
        public bool IsUpgrade;
        public long? HeadersTs;
        public long? EndTs;
        public string? Error;
        public string? ErrorDetail;
        public bool Canceled;
        public bool Evicted;
        public NetworkTimingDto? Timing;
        public string? RemoteAddress;
        public long? ConnectionId;
        public NetworkState State;
    }

    readonly Func<Uri, bool> _exclude;
    readonly Action<string, IReadOnlyList<NetworkRequestDto>> _publish;
    readonly ConcurrentDictionary<HttpRequestMessage, Entry> _inflight = new(ReferenceEqualityComparer.Instance);
    readonly ConditionalWeakTable<HttpRequestMessage, Entry> _lastHop = new();
    readonly ConcurrentDictionary<long, Entry> _entries = new();
    readonly ConcurrentQueue<(string Method, long Id)> _dirty = new();
    readonly object _flushLock = new();
    IDisposable? _allListeners;
    IDisposable? _httpSubscription;
    NetTelemetry? _telemetry;
    Timer? _flushTimer;
    NetworkEnableParams _options = new();
    HashSet<string> _sensitive = new(DefaultSensitive, StringComparer.OrdinalIgnoreCase);
    long _nextId;

    public NetworkMonitor(Func<Uri, bool> exclude, Action<string, IReadOnlyList<NetworkRequestDto>> publish)
    {
        _exclude = exclude;
        _publish = publish;
    }

    public bool IsEnabled => _allListeners is not null;
    public int Count => _entries.Count;

    public void Enable(NetworkEnableParams options)
    {
        _options = options;
        _sensitive = new HashSet<string>(DefaultSensitive.Concat(options.SensitiveHeaders ?? []), StringComparer.OrdinalIgnoreCase);
        if (IsEnabled) return;
        _telemetry = new NetTelemetry();
        _flushTimer = new Timer(_ => Flush(), null, 100, 100);
        _allListeners = DiagnosticListener.AllListeners.Subscribe(this);
    }

    /// <summary>Stops intercepting immediately. Requests already in flight finish being recorded.</summary>
    public void Disable()
    {
        _httpSubscription?.Dispose();
        _httpSubscription = null;
        _allListeners?.Dispose();
        _allListeners = null;
        _telemetry?.Dispose();
        _telemetry = null;
        _flushTimer?.Dispose();
        _flushTimer = null;
        Flush();
    }

    public void Clear()
    {
        foreach (var e in _entries.Values) { e.RequestBody?.Release(); e.ResponseBody?.Release(); }
        _entries.Clear();
    }

    public void Dispose() => Disable();

    // ---------------------------------------------------------------- DiagnosticListener

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener listener)
    {
        if (listener.Name == ListenerName)
            _httpSubscription = listener.Subscribe(this, (name, _, _) =>
                name is "System.Net.Http.HttpRequestOut" or StartEvent or StopEvent or ExceptionEvent);
    }
    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> evt)
    {
        try
        {
            switch (evt.Key)
            {
                case StartEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req:
                    OnStart(req);
                    break;
                case StopEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req:
                    OnStop(req, Get<HttpResponseMessage>(evt.Value, "Response"), Get<TaskStatus>(evt.Value, "RequestTaskStatus"));
                    break;
                case ExceptionEvent when Get<HttpRequestMessage>(evt.Value, "Request") is { } req && _inflight.TryGetValue(req, out var e):
                    (e.Error, e.ErrorDetail, e.Canceled) = Describe(Get<Exception>(evt.Value, "Exception"));
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Spyglass] network capture error: {ex}");   // never break the app's HTTP call
        }
    }

    void OnStart(HttpRequestMessage req)
    {
        if (req.RequestUri is not { } uri || _exclude(uri)) return;
        var e = new Entry
        {
            Id = Interlocked.Increment(ref _nextId),
            Method = req.Method.Method,
            Url = uri.ToString(),   // snapshot: redirects mutate RequestUri on the same message (spike S5)
            StartTime = DateTimeOffset.Now,
            StartTs = Stopwatch.GetTimestamp(),
            Activity = EventSource.CurrentThreadActivityId,
            RequestHeaders = Headers(req.Headers, req.Content?.Headers),
            Initiator = _lastHop.TryGetValue(req, out var previous)
                ? new InitiatorDto("Redirect", previous.Initiator?.Stack ?? [])   // Chrome: initiator "Redirect"
                : _options.CaptureInitiator ? InitiatorOf(new StackTrace(2, false)) : null,
            RedirectedFromId = previous?.Id,
            State = NetworkState.Pending,
        };
        if (req.Content is { } content && _options.CaptureBodies)
        {
            e.RequestBody = new BodyCapture(_options.BodyLimit);
            req.Content = new TeeContent(content, e.RequestBody);   // the handler serializes our wrapper after this event
        }
        _inflight[req] = e;
        _lastHop.AddOrUpdate(req, e);
        Track(e);
        MarkDirty(Methods.NetworkRequestStarted, e);
    }

    void OnStop(HttpRequestMessage req, HttpResponseMessage? resp, TaskStatus status)
    {
        if (!_inflight.TryRemove(req, out var e)) return;
        e.HeadersTs = Stopwatch.GetTimestamp();
        if (resp is null)
        {
            if (e.Error is null)
            {
                e.Canceled = status == TaskStatus.Canceled;
                e.Error = e.Canceled ? null : "net::ERR_FAILED";
            }
            Finish(e, NetworkState.Failed);
            return;
        }

        e.Status = (int)resp.StatusCode;
        e.StatusText = resp.ReasonPhrase;
        e.HttpVersion = resp.Version.Major >= 2 ? $"h{resp.Version.Major}" : $"http/{resp.Version.Major}.{resp.Version.Minor}";
        e.ResponseHeaders = Headers(resp.Headers, resp.Content.Headers);
        e.MimeType = resp.Content.Headers.ContentType?.MediaType;
        MarkDirty(Methods.NetworkResponseReceived, e);

        // WebSocket / HTTP/2 extended CONNECT hand the raw duplex stream to the caller: never wrap it (spike S4, N8).
        if (resp.StatusCode == HttpStatusCode.SwitchingProtocols || req.Headers.Protocol is not null)
        {
            e.IsUpgrade = true;
            Finish(e, NetworkState.Finished);
            return;
        }
        if (!_options.CaptureBodies || resp.Content.Headers.ContentLength == 0 || req.Method == HttpMethod.Head)
        {
            e.ResponseBody = new BodyCapture(0);
            Finish(e, NetworkState.Finished);
            return;
        }
        var body = new BodyCapture(_options.BodyLimit);
        body.OnCompleted += () => Finish(e, NetworkState.Finished);
        e.ResponseBody = body;
        resp.Content = new TeeContent(resp.Content, body);   // Stop fires before HttpClient sees the response
    }

    void Finish(Entry e, NetworkState state)
    {
        if (e.State != NetworkState.Pending) return;
        e.EndTs = Stopwatch.GetTimestamp();
        e.State = state;
        e.Timing = TimingOf(e);
        EnforceLimits();
        MarkDirty(state == NetworkState.Failed ? Methods.NetworkFailed : Methods.NetworkFinished, e);
    }

    // ---------------------------------------------------------------- timing (spike S5)

    NetworkTimingDto TimingOf(Entry e)
    {
        double Ms(long from, long to) => Math.Round(Stopwatch.GetElapsedTime(from, to).TotalMilliseconds, 2);
        var end = e.EndTs ?? Stopwatch.GetTimestamp();
        var headers = e.HeadersTs ?? end;
        var events = _telemetry?.For(e.Activity, e.StartTs, headers) ?? [];
        long? At(string name) => events.FirstOrDefault(x => x.Name == name)?.Ts;
        double Span(string start, string stop) => At(start) is { } a && At(stop) is { } b && b >= a ? Ms(a, b) : -1;

        if (events.FirstOrDefault(x => x.Name == "ConnectionEstablished") is { } ce)
        {
            e.RemoteAddress = ce.Get("remoteAddress")?.ToString();
            e.ConnectionId = ce.Get("connectionId") is { } cid ? Convert.ToInt64(cid) : null;
        }
        else if (events.FirstOrDefault(x => x.Name == "RequestHeadersStart") is { } rh && rh.Get("connectionId") is { } cid2)
        {
            e.ConnectionId = Convert.ToInt64(cid2);
            e.RemoteAddress = _telemetry?.RemoteOf(e.ConnectionId.Value);
        }

        // Connection opened before recording started: an IP-literal URL still tells us the remote end.
        if (e.RemoteAddress is null && Uri.TryCreate(e.Url, UriKind.Absolute, out var u) && u.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
            e.RemoteAddress = $"{u.Host}:{u.Port}";
        var queue = events.FirstOrDefault(x => x.Name == "RequestLeftQueue")?.Get("timeOnQueueMilliseconds") is { } q ? Math.Round(Convert.ToDouble(q), 2) : -1;
        var sentAt = At("RequestHeadersStop") ?? At("RequestContentStop");
        var send = At("RequestHeadersStart") is { } hs && sentAt is { } he && he >= hs ? Ms(hs, he) : -1;
        var wait = sentAt is { } s ? Ms(s, headers) : -1;
        return new NetworkTimingDto(
            Queueing: queue,
            Dns: Span("ResolutionStart", "ResolutionStop"),
            Connect: Span("ConnectStart", "ConnectStop"),
            Ssl: Span("HandshakeStart", "HandshakeStop"),
            Send: send,
            Wait: wait,
            Download: e.HeadersTs is { } h && e.EndTs is { } en ? Ms(h, en) : -1,
            Total: Ms(e.StartTs, end));
    }

    // ---------------------------------------------------------------- queries

    public NetworkBodyResult GetBody(NetworkGetBodyParams p)
    {
        if (!_entries.TryGetValue(p.Id, out var e))
            throw new SpyglassProtocolException(ErrorCodes.NodeNotFound, $"Request {p.Id} is not in the capture (cleared or too old).");
        var request = p.Part.Equals("request", StringComparison.OrdinalIgnoreCase);
        var body = request ? e.RequestBody : e.ResponseBody;
        if (e.Evicted) throw new SpyglassProtocolException(ErrorCodes.BodyEvicted, "The body was evicted from the agent's buffer (50 MB limit).");
        if (body is null) return new NetworkBodyResult(null, "text", "", 0, false);
        var mime = request ? e.RequestHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value : e.MimeType;
        var bytes = body.Bytes;
        return IsText(mime, bytes)
            ? new NetworkBodyResult(mime, "text", Encoding.UTF8.GetString(bytes), body.TotalBytes, body.Truncated)
            : new NetworkBodyResult(mime, "base64", Convert.ToBase64String(bytes), body.TotalBytes, body.Truncated);
    }

    public NetworkSearchResult Search(NetworkSearchParams p)
    {
        var options = p.MatchCase ? RegexOptions.None : RegexOptions.IgnoreCase;
        var regex = new Regex(p.Regex ? p.Query : Regex.Escape(p.Query), options, TimeSpan.FromSeconds(1));
        var matches = new List<NetworkSearchMatch>();
        void Try(Entry e, string part, string? text)
        {
            if (text is null || matches.Count >= p.Limit) return;
            var m = regex.Match(text);
            if (!m.Success) return;
            var start = Math.Max(0, m.Index - 30);
            matches.Add(new NetworkSearchMatch(e.Id, part, text.Substring(start, Math.Min(90, text.Length - start)).Replace('\n', ' ')));
        }
        foreach (var e in _entries.Values.OrderBy(x => x.Id))
        {
            Try(e, "url", e.Url);
            Try(e, "requestHeaders", string.Join("\n", e.RequestHeaders.Select(h => $"{h.Name}: {h.Value}")));
            Try(e, "responseHeaders", string.Join("\n", e.ResponseHeaders.Select(h => $"{h.Name}: {h.Value}")));
            if (e.RequestBody is { } rb && IsText(null, rb.Bytes)) Try(e, "request", Encoding.UTF8.GetString(rb.Bytes));
            if (e.ResponseBody is { } b && IsText(e.MimeType, b.Bytes)) Try(e, "response", Encoding.UTF8.GetString(b.Bytes));
        }
        return new NetworkSearchResult(matches);
    }

    public IReadOnlyList<NetworkRequestDto> Snapshot() => _entries.Values.OrderBy(e => e.Id).Select(ToDto).ToList();

    // ---------------------------------------------------------------- publishing

    void MarkDirty(string method, Entry e) => _dirty.Enqueue((method, e.Id));

    /// <summary>100 ms batches per notification kind (design/network §6). Each item is a full snapshot.</summary>
    void Flush()
    {
        lock (_flushLock)
        {
            if (_dirty.IsEmpty) return;
            var batch = new List<(string Method, long Id)>();
            while (_dirty.TryDequeue(out var d)) batch.Add(d);
            foreach (var group in batch.GroupBy(b => b.Method))
            {
                var dtos = group.Select(g => g.Id).Distinct()
                    .Select(id => _entries.TryGetValue(id, out var e) ? ToDto(e) : null).OfType<NetworkRequestDto>().ToList();
                if (dtos.Count > 0) _publish(group.Key, dtos);
            }
        }
    }

    NetworkRequestDto ToDto(Entry e) => new()
    {
        Id = e.Id,
        Method = e.Method,
        Url = e.Url,
        StartTime = e.StartTime,
        State = e.State,
        RequestHeaders = e.RequestHeaders,
        RequestBodySize = e.RequestBody?.TotalBytes,
        Status = e.Status,
        StatusText = e.StatusText,
        HttpVersion = e.HttpVersion,
        ResponseHeaders = e.ResponseHeaders,
        MimeType = e.MimeType,
        ResponseSize = e.ResponseBody?.TotalBytes,
        ResponseTruncated = e.ResponseBody?.Truncated ?? false,
        BodiesEvicted = e.Evicted,
        IsUpgrade = e.IsUpgrade,
        RedirectedFromId = e.RedirectedFromId,
        RemoteAddress = e.RemoteAddress,
        ConnectionId = e.ConnectionId,
        Error = e.Error,
        ErrorDetail = e.ErrorDetail,
        Canceled = e.Canceled,
        Initiator = e.Initiator,
        Timing = e.Timing,
        EndTime = e.EndTs is { } t ? e.StartTime + Stopwatch.GetElapsedTime(e.StartTs, t) : null,
    };

    // ---------------------------------------------------------------- limits

    void Track(Entry e)
    {
        _entries[e.Id] = e;
        while (_entries.Count > MaxRequests)
        {
            var oldest = _entries.Keys.Min();
            if (_entries.TryRemove(oldest, out var old)) { old.RequestBody?.Release(); old.ResponseBody?.Release(); }
        }
    }

    void EnforceLimits()
    {
        long total = _entries.Values.Sum(x => (x.RequestBody?.StoredBytes ?? 0) + (x.ResponseBody?.StoredBytes ?? 0));
        foreach (var old in _entries.Values.OrderBy(x => x.Id))
        {
            if (total <= MaxBodyBytes) break;
            if (old.State == NetworkState.Pending || old.Evicted) continue;
            total -= (old.RequestBody?.StoredBytes ?? 0) + (old.ResponseBody?.StoredBytes ?? 0);
            old.RequestBody?.Release();
            old.ResponseBody?.Release();
            old.Evicted = true;
        }
    }

    // ---------------------------------------------------------------- helpers

    List<HeaderDto> Headers(params System.Net.Http.Headers.HttpHeaders?[] sets)
    {
        var list = new List<HeaderDto>();
        foreach (var set in sets)
            if (set is not null)
                foreach (var h in set)
                    list.Add(new HeaderDto(h.Key, string.Join(", ", h.Value), _sensitive.Contains(h.Key)));
        return list;
    }

    /// <summary>Chrome-style error names from .NET exceptions (design/network/02-chrome-parity.md §3.3).</summary>
    static (string? Error, string? Detail, bool Canceled) Describe(Exception? ex)
    {
        if (ex is null) return ("net::ERR_FAILED", null, false);
        var detail = $"{ex.GetType().Name}: {ex.GetBaseException().Message}";
        if (ex is TaskCanceledException { InnerException: TimeoutException }) return ("net::ERR_TIMED_OUT", detail, false);
        if (ex is OperationCanceledException) return (null, detail, true);
        var socket = (ex as SocketException) ?? ex.InnerException as SocketException ?? ex.GetBaseException() as SocketException;
        if (ex is HttpRequestException hre)
        {
            var name = hre.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError => "net::ERR_NAME_NOT_RESOLVED",
                HttpRequestError.ConnectionError when socket?.SocketErrorCode == SocketError.ConnectionRefused => "net::ERR_CONNECTION_REFUSED",
                HttpRequestError.ConnectionError => "net::ERR_CONNECTION_FAILED",
                HttpRequestError.SecureConnectionError => "net::ERR_SSL_PROTOCOL_ERROR",
                HttpRequestError.ResponseEnded => "net::ERR_EMPTY_RESPONSE",
                HttpRequestError.InvalidResponse => "net::ERR_INVALID_HTTP_RESPONSE",
                HttpRequestError.HttpProtocolError => "net::ERR_HTTP2_PROTOCOL_ERROR",
                HttpRequestError.ProxyTunnelError => "net::ERR_TUNNEL_CONNECTION_FAILED",
                _ => "net::ERR_FAILED",
            };
            return (name, detail, false);
        }
        return (socket?.SocketErrorCode == SocketError.ConnectionRefused ? "net::ERR_CONNECTION_REFUSED" : "net::ERR_FAILED", detail, false);
    }

    /// <summary>
    /// Managed call stack (trim-safe: parsed from StackTrace text, no reflection). The first frame outside
    /// System/Microsoft/Spyglass is the initiator; async state machines are shown as their method name.
    /// </summary>
    static InitiatorDto InitiatorOf(StackTrace st)
    {
        var frames = new List<string>();
        string? user = null;
        foreach (var raw in st.ToString().Split('\n'))
        {
            var m = FrameLine.Match(raw);
            if (!m.Success) continue;
            var name = AsyncFrame.Replace(m.Groups[1].Value, "$1.$2");   // Foo+<LoadAsync>d__5.MoveNext → Foo.LoadAsync
            if (name.StartsWith("Spyglass.Agent", StringComparison.Ordinal) || name.Contains("ExecutionContext.Run")) continue;
            var framework = name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft", StringComparison.Ordinal);
            if (!framework && user is null) user = name;
            if (frames.Count < 20) frames.Add(name);
        }
        return new InitiatorDto(user, frames);
    }

    static readonly Regex FrameLine = new(@"^\s*at ([^(]+)\(", RegexOptions.Compiled);
    static readonly Regex AsyncFrame = new(@"^(.*)\+<([^>]+)>d__\d+\.MoveNext$", RegexOptions.Compiled);

    static bool IsText(string? mime, byte[] bytes)
    {
        if (mime is not null && (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase) || mime.Contains("json") || mime.Contains("xml")
                                 || mime.Contains("javascript") || mime.Contains("x-www-form-urlencoded")))
            return true;
        if (mime is not null && (mime.StartsWith("image/") || mime.StartsWith("audio/") || mime.StartsWith("video/") || mime.Contains("octet-stream")))
            return false;
        var span = bytes.AsSpan(0, Math.Min(bytes.Length, 1024));
        if (span.IndexOf((byte)0) >= 0) return false;
        try { _ = new UTF8Encoding(false, true).GetString(span); return true; } catch { return false; }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "DiagnosticListener payloads are anonymous types from System.Net.Http; their public properties are preserved by the framework for diagnostics.")]
    static T? Get<T>(object? payload, string name) =>
        payload?.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(payload) is T v ? v : default;
}
