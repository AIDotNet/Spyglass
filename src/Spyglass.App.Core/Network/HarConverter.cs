using System.Globalization;
using System.Text;
using System.Text.Json;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

/// <param name="IncludeSensitive">Chrome's "Export HAR (with sensitive data)". Default is sanitized.</param>
/// <param name="ExtraSensitiveHeaders">User-configured sensitive headers, stripped from sanitized output in addition to the built-in set.</param>
public sealed record HarOptions(bool IncludeSensitive = false, IReadOnlyCollection<string>? ExtraSensitiveHeaders = null);

public sealed record HarImport(IReadOnlyList<NetworkRequestDto> Requests, IReadOnlyDictionary<(long Id, string Part), NetworkBodyResult> Bodies);

/// <summary>
/// HAR 1.2 export/import (design/network/02-chrome-parity.md §10). Pure: no UI, no agent calls.
/// Unknown timings are -1 per the spec. Spyglass specifics live in underscore fields (<c>_initiator</c>, <c>_id</c>, <c>_error</c>…),
/// which the HAR spec reserves for custom data.
/// </summary>
public static class HarConverter
{
    /// <summary>Chrome's sanitized HAR drops these (plus the headers the agent marked sensitive).</summary>
    public static readonly string[] SensitiveHeaderNames = ["Cookie", "Set-Cookie", "Authorization", "Proxy-Authorization"];

    static readonly JsonWriterOptions Writer = new() { Indented = true };

    public static bool IsStripped(HeaderDto h, HarOptions o) =>
        !o.IncludeSensitive && (h.IsSensitive
            || SensitiveHeaderNames.Contains(h.Name, StringComparer.OrdinalIgnoreCase)
            || o.ExtraSensitiveHeaders?.Contains(h.Name, StringComparer.OrdinalIgnoreCase) == true);

    // ---- export ----

    public static string Build(IEnumerable<NetworkRequestDto> requests, IReadOnlyDictionary<(long, string), NetworkBodyResult>? bodies = null, HarOptions? options = null)
    {
        options ??= new HarOptions();
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, Writer))
        {
            w.WriteStartObject();
            w.WriteStartObject("log");
            w.WriteString("version", "1.2");
            w.WriteStartObject("creator");
            w.WriteString("name", "Spyglass");
            w.WriteString("version", "0.3");
            w.WriteEndObject();
            w.WriteStartArray("entries");
            foreach (var r in requests) WriteEntry(w, r, bodies, options);
            w.WriteEndArray();
            w.WriteEndObject();
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(ms.ToArray());
    }

    static void WriteEntry(Utf8JsonWriter w, NetworkRequestDto r, IReadOnlyDictionary<(long, string), NetworkBodyResult>? bodies, HarOptions o)
    {
        var reqHeaders = r.RequestHeaders.Where(h => !IsStripped(h, o)).ToList();
        var resHeaders = r.ResponseHeaders.Where(h => !IsStripped(h, o)).ToList();
        NetworkBodyResult? reqBody = null, resBody = null;
        bodies?.TryGetValue((r.Id, "request"), out reqBody);
        bodies?.TryGetValue((r.Id, "response"), out resBody);
        var t = r.Timing;

        w.WriteStartObject();
        w.WriteString("startedDateTime", r.StartTime.ToString("O", CultureInfo.InvariantCulture));
        w.WriteNumber("time", NetworkSort.Total(r) ?? -1);

        w.WriteStartObject("request");
        w.WriteString("method", r.Method);
        w.WriteString("url", r.Url);
        w.WriteString("httpVersion", r.HttpVersion ?? "");
        w.WriteStartArray("cookies");
        if (o.IncludeSensitive) foreach (var c in NetworkFilter.RequestCookies(r)) WriteCookie(w, c);
        w.WriteEndArray();
        WriteHeaders(w, reqHeaders);
        w.WriteStartArray("queryString");
        if (Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Query.Length > 1)
            foreach (var pair in u.Query[1..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                w.WriteStartObject();
                w.WriteString("name", Uri.UnescapeDataString(kv[0].Replace('+', ' ')));
                w.WriteString("value", kv.Length > 1 ? Uri.UnescapeDataString(kv[1].Replace('+', ' ')) : "");
                w.WriteEndObject();
            }
        w.WriteEndArray();
        if (reqBody is not null && reqBody.Encoding == "text")
        {
            w.WriteStartObject("postData");
            w.WriteString("mimeType", reqHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? reqBody.MimeType ?? "");
            w.WriteString("text", reqBody.Content);
            w.WriteEndObject();
        }
        w.WriteNumber("headersSize", -1);
        w.WriteNumber("bodySize", r.RequestBodySize ?? -1);
        w.WriteEndObject();

        w.WriteStartObject("response");
        w.WriteNumber("status", r.Status ?? 0);
        w.WriteString("statusText", r.StatusText ?? "");
        w.WriteString("httpVersion", r.HttpVersion ?? "");
        w.WriteStartArray("cookies");
        if (o.IncludeSensitive) foreach (var c in NetworkFilter.SetCookies(r)) WriteCookie(w, c);
        w.WriteEndArray();
        WriteHeaders(w, resHeaders);
        w.WriteStartObject("content");
        w.WriteNumber("size", r.ResponseSize ?? 0);
        w.WriteString("mimeType", r.MimeType ?? "");
        if (resBody is not null)
        {
            w.WriteString("text", resBody.Content);
            if (resBody.Encoding != "text") w.WriteString("encoding", resBody.Encoding);
        }
        w.WriteEndObject();
        w.WriteString("redirectURL", resHeaders.FirstOrDefault(h => h.Name.Equals("Location", StringComparison.OrdinalIgnoreCase))?.Value ?? "");
        w.WriteNumber("headersSize", -1);
        w.WriteNumber("bodySize", -1);
        w.WriteEndObject();

        w.WriteStartObject("cache");
        w.WriteEndObject();

        w.WriteStartObject("timings");
        w.WriteNumber("blocked", t?.Queueing ?? -1);
        w.WriteNumber("dns", t?.Dns ?? -1);
        w.WriteNumber("connect", t?.Connect ?? -1);
        w.WriteNumber("ssl", t?.Ssl ?? -1);
        w.WriteNumber("send", t?.Send ?? -1);
        w.WriteNumber("wait", t?.Wait ?? -1);
        w.WriteNumber("receive", t?.Download ?? -1);
        w.WriteEndObject();

        if (r.RemoteAddress is { } addr) w.WriteString("serverIPAddress", HostOf(addr));
        if (r.ConnectionId is { } conn) w.WriteString("connection", conn.ToString(CultureInfo.InvariantCulture));

        // Spyglass / Chrome-style custom fields.
        w.WriteNumber("_id", r.Id);
        w.WriteString("_resourceType", NetworkClassifier.TypeOf(r).ToString());
        w.WriteString("_state", r.State.ToString().ToLowerInvariant());
        if (r.RemoteAddress is not null) w.WriteString("_remoteAddress", r.RemoteAddress);
        if (r.Error is not null) w.WriteString("_error", r.Error);
        if (r.ErrorDetail is not null) w.WriteString("_errorDetail", r.ErrorDetail);
        if (r.Canceled) w.WriteBoolean("_canceled", true);
        if (r.ResponseTruncated) w.WriteBoolean("_truncated", true);
        if (r.RedirectedFromId is { } from) w.WriteNumber("_redirectedFromId", from);
        if (r.Initiator is { } ini)
        {
            w.WriteStartObject("_initiator");
            w.WriteString("type", "script");
            if (ini.Frame is not null) w.WriteString("frame", ini.Frame);
            w.WriteStartArray("stack");
            foreach (var f in ini.Stack) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndObject();
    }

    static void WriteHeaders(Utf8JsonWriter w, IEnumerable<HeaderDto> headers)
    {
        w.WriteStartArray("headers");
        foreach (var h in headers)
        {
            w.WriteStartObject();
            w.WriteString("name", h.Name);
            w.WriteString("value", h.Value);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    static void WriteCookie(Utf8JsonWriter w, NetworkFilter.Cookie c)
    {
        w.WriteStartObject();
        w.WriteString("name", c.Name);
        w.WriteString("value", c.Value);
        if (c.Path is not null) w.WriteString("path", c.Path);
        if (c.Domain is not null) w.WriteString("domain", c.Domain);
        if (c.HttpOnly) w.WriteBoolean("httpOnly", true);
        if (c.Secure) w.WriteBoolean("secure", true);
        w.WriteEndObject();
    }

    /// <summary>"127.0.0.1:80" → "127.0.0.1"; "[::1]:80" → "::1".</summary>
    static string HostOf(string addr)
    {
        if (addr.StartsWith('[') && addr.IndexOf(']') is var e and > 0) return addr[1..e];
        var i = addr.LastIndexOf(':');
        return i > 0 && addr.IndexOf(':') == i ? addr[..i] : addr;
    }

    // ---- import ----

    /// <summary>Parses a HAR (Spyglass' own or e.g. Chrome's). Throws <see cref="FormatException"/> when it is not a HAR.</summary>
    public static HarImport Parse(string json)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException ex) { throw new FormatException("Not a valid JSON file: " + ex.Message, ex); }
        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object || !doc.RootElement.TryGetProperty("log", out var log)
                || !log.TryGetProperty("entries", out var entries) || entries.ValueKind != JsonValueKind.Array)
                throw new FormatException("Not a HAR file (missing log.entries).");
            var requests = new List<NetworkRequestDto>();
            var bodies = new Dictionary<(long, string), NetworkBodyResult>();
            var n = 0L;
            foreach (var e in entries.EnumerateArray()) requests.Add(ParseEntry(e, ++n, bodies));
            return new HarImport(requests, bodies);
        }
    }

    static NetworkRequestDto ParseEntry(JsonElement e, long index, Dictionary<(long, string), NetworkBodyResult> bodies)
    {
        var req = Obj(e, "request");
        var res = Obj(e, "response");
        var id = Num(e, "_id") is { } idn ? (long)idn : index;
        var status = (int)(Num(res, "status") ?? 0);
        var error = Str(e, "_error");
        var stateText = Str(e, "_state");
        var state = stateText == "pending" ? NetworkState.Pending : status > 0 && error is null ? NetworkState.Finished : stateText == "finished" ? NetworkState.Finished : NetworkState.Failed;

        var start = DateTimeOffset.TryParse(Str(e, "startedDateTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var st) ? st : DateTimeOffset.UnixEpoch;
        var tm = Obj(e, "timings");
        double T(string name) => Num(tm, name) is { } v && v >= 0 ? v : -1;
        var queue = T("blocked"); var dns = T("dns"); var connect = T("connect"); var ssl = T("ssl");
        var send = T("send"); var wait = T("wait"); var recv = T("receive");
        var time = Num(e, "time") ?? -1;
        if (time < 0) { var sum = new[] { queue, dns, connect, send, wait, recv }.Where(v => v > 0).Sum(); time = sum > 0 ? sum : -1; }
        NetworkTimingDto? timing = time < 0 && new[] { queue, dns, connect, ssl, send, wait, recv }.All(v => v < 0) ? null
            : new NetworkTimingDto(queue, dns, connect, ssl, send, wait, recv, time);

        var reqHeaders = Headers(Arr(req, "headers"));
        var resHeaders = Headers(Arr(res, "headers"));
        var content = Obj(res, "content");
        var mime = Str(content, "mimeType") is { Length: > 0 } m ? m.Split(';')[0].Trim()
            : resHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value.Split(';')[0].Trim();
        if (string.IsNullOrEmpty(mime)) mime = null;

        long? reqSize = Num(req, "bodySize") is { } bs and >= 0 ? (long)bs : null;
        var post = Obj(req, "postData");
        if (Str(post, "text") is { } ptext)
        {
            reqSize ??= Encoding.UTF8.GetByteCount(ptext);
            bodies[(id, "request")] = new NetworkBodyResult(Str(post, "mimeType"), "text", ptext, reqSize.Value, false);
        }
        long? resSize = Num(content, "size") is { } cs && (cs > 0 || status > 0) ? (long)Math.Max(0, cs) : Num(res, "bodySize") is { } rb and >= 0 ? (long)rb : null;
        if (Str(content, "text") is { } ctext)
        {
            var enc = Str(content, "encoding") == "base64" ? "base64" : "text";
            bodies[(id, "response")] = new NetworkBodyResult(mime, enc, ctext, resSize ?? ctext.Length, false);
        }

        var remote = Str(e, "_remoteAddress") ?? Str(e, "serverIPAddress");
        long? conn = long.TryParse(Str(e, "connection"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cn) ? cn : null;
        DateTimeOffset? end = state == NetworkState.Pending || time < 0 ? null : start.AddMilliseconds(time);

        return new NetworkRequestDto
        {
            Id = id,
            Method = Str(req, "method") ?? "GET",
            Url = Str(req, "url") ?? "",
            StartTime = start,
            State = state,
            RequestHeaders = reqHeaders,
            ProvisionalHeaders = false,
            RequestBodySize = reqSize,
            Status = state == NetworkState.Finished || status > 0 ? status : null,
            StatusText = Str(res, "statusText") is { Length: > 0 } s ? s : null,
            HttpVersion = Str(res, "httpVersion") is { Length: > 0 } v ? v : Str(req, "httpVersion") is { Length: > 0 } v2 ? v2 : null,
            ResponseHeaders = resHeaders,
            MimeType = mime,
            ResponseSize = resSize,
            ResponseTruncated = Bool(e, "_truncated"),
            IsUpgrade = status == 101,
            RedirectedFromId = Num(e, "_redirectedFromId") is { } rf ? (long)rf : null,
            RemoteAddress = remote,
            ConnectionId = conn,
            Error = error,
            ErrorDetail = Str(e, "_errorDetail"),
            Canceled = Bool(e, "_canceled"),
            Initiator = ParseInitiator(e),
            Timing = timing,
            EndTime = end,
        };
    }

    static InitiatorDto? ParseInitiator(JsonElement e)
    {
        if (!e.TryGetProperty("_initiator", out var i) || i.ValueKind != JsonValueKind.Object) return null;
        var stack = new List<string>();
        string? frame = Str(i, "frame");
        if (i.TryGetProperty("stack", out var st))
        {
            if (st.ValueKind == JsonValueKind.Array)
                stack.AddRange(st.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!));
            else if (st.ValueKind == JsonValueKind.Object && st.TryGetProperty("callFrames", out var frames) && frames.ValueKind == JsonValueKind.Array)
                stack.AddRange(frames.EnumerateArray().Select(f =>
                    $"{(Str(f, "functionName") is { Length: > 0 } fn ? fn : "(anonymous)")} ({Str(f, "url")}:{Num(f, "lineNumber")})"));
        }
        frame ??= stack.FirstOrDefault() ?? Str(i, "url");
        return frame is null && stack.Count == 0 ? null : new InitiatorDto(frame, stack);
    }

    static List<HeaderDto> Headers(JsonElement arr)
    {
        var list = new List<HeaderDto>();
        if (arr.ValueKind != JsonValueKind.Array) return list;
        foreach (var h in arr.EnumerateArray())
        {
            var name = Str(h, "name");
            if (name is null) continue;
            list.Add(new HeaderDto(name, Str(h, "value") ?? "", SensitiveHeaderNames.Contains(name, StringComparer.OrdinalIgnoreCase)));
        }
        return list;
    }

    static JsonElement Obj(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
    static JsonElement Arr(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;
    static string? Str(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    static double? Num(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
    static bool Bool(JsonElement e, string name) => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>Collects the captured bodies a HAR needs from the agent's cache (evicted/missing bodies are skipped).</summary>
public static class HarExport
{
    public static async Task<Dictionary<(long, string), NetworkBodyResult>> CollectBodiesAsync(
        AgentSession session, IEnumerable<NetworkRequestDto> requests, long maxTotalBytes = 16L * 1024 * 1024, CancellationToken ct = default)
    {
        var result = new Dictionary<(long, string), NetworkBodyResult>();
        long total = 0;
        foreach (var r in requests)
        {
            foreach (var (part, size) in new[] { ("request", r.RequestBodySize ?? 0), ("response", r.State == NetworkState.Pending || r.IsUpgrade ? 0 : r.ResponseSize ?? 0) })
            {
                if (size <= 0 || total + size > maxTotalBytes) continue;
                try
                {
                    var b = await session.GetNetworkBodyAsync(r.Id, part, ct);
                    result[(r.Id, part)] = b;
                    total += b.Content.Length;
                }
                catch (SpyglassProtocolException) { /* evicted or too large */ }
            }
        }
        return result;
    }
}

/// <summary>Chrome's "Copy all …" / "Copy all listed …" families (design/network/02-chrome-parity.md §7).</summary>
public static class NetworkCopyAll
{
    public static readonly string[] Kinds = ["urls", "curl", "powershell", "fetch", "fetch-node", "csharp"];

    public static string Join(string kind, IEnumerable<NetworkRequestDto> requests, Func<NetworkRequestDto, string?> requestBody)
    {
        var parts = requests.Select(r => kind switch
        {
            "urls" => r.Url,
            "curl" => NetworkCopy.Curl(r, requestBody(r)),
            "powershell" => NetworkCopy.PowerShell(r, requestBody(r)),
            "fetch" => NetworkCopy.Fetch(r, requestBody(r)),
            "fetch-node" => NetworkCopy.Fetch(r, requestBody(r), node: true),
            "csharp" => NetworkCopy.CSharp(r, requestBody(r)),
            _ => throw new ArgumentException("unknown format " + kind),
        });
        return string.Join(kind is "curl" or "powershell" ? " ;\n" : "\n", parts);
    }
}
