using System.ComponentModel;
using System.Text;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using Spyglass.App.Core;
using Spyglass.App.Core.Network;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>Network tools (design/network/README.md §8). Sensitive headers are always masked in MCP output.</summary>
[McpServerToolType]
public sealed class SpyglassNetworkTools(SpyglassHub hub)
{
    const string AppParam = "Connected app (id like 'a1', name, or pid). Optional when only one app is connected.";

    [McpServerTool(Name = "network_capture", Title = "Start/stop HTTP capture", Idempotent = true, OpenWorld = false, Destructive = false)]
    [Description("Starts or stops recording the app's HTTP requests (like opening Chrome DevTools' Network panel). Nothing is intercepted until started. Start it, make the app do something (or use the app yourself), then call network_requests.")]
    public async Task<string> NetworkCapture(
        [Description("'start', 'stop' or 'status'.")] string action = "status",
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        if (!s.Has(Capabilities.Network)) throw new McpException("This agent does not support network capture (update Spyglass.Agent).");
        var status = action.ToLowerInvariant() switch
        {
            "start" => await s.EnableNetworkAsync(),
            "stop" => await s.DisableNetworkAsync(),
            "status" => await s.GetNetworkStatusAsync(),
            _ => throw new McpException("action must be 'start', 'stop' or 'status'."),
        };
        return status.Enabled
            ? $"Recording network activity ({s.Network.Count} request(s) captured so far). Exercise the app, then call network_requests."
            : $"Not recording. {s.Network.Count} request(s) kept from earlier recording.";
    }

    [McpServerTool(Name = "network_requests", Title = "List HTTP requests", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Lists captured HTTP requests: id, method, status (or (failed) net::ERR_…), path, type, size, time, and the code that sent it. Filter by URL text, method, status ('4xx,5xx', '404'), or type; `since` returns only requests after an id.")]
    public string NetworkRequests(
        [Description("Only URLs containing this text.")] string? urlContains = null,
        [Description("HTTP method, e.g. POST.")] string? method = null,
        [Description("Status filter: '4xx,5xx', '404', 'failed'.")] string? status = null,
        [Description("Type: fetch, document, image, script, stylesheet, font, media, websocket, other.")] string? type = null,
        [Description("Only requests with an id greater than this.")] long since = 0,
        [Description("Maximum results. Default 50.")] int limit = 50,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var all = s.Network.Requests;
        var list = all.Where(r => r.Id > since)
            .Where(r => urlContains is null || r.Url.Contains(urlContains, StringComparison.OrdinalIgnoreCase))
            .Where(r => method is null || r.Method.Equals(method, StringComparison.OrdinalIgnoreCase))
            .Where(r => type is null || NetworkClassifier.TypeLabel(r).StartsWith(type, StringComparison.OrdinalIgnoreCase)
                                     || (type.Equals("image", StringComparison.OrdinalIgnoreCase) && NetworkClassifier.TypeOf(r) == ResourceType.Img))
            .Where(r => status is null || StatusMatches(r, status))
            .ToList();
        if (all.Count == 0) return "No requests captured. Call network_capture with action 'start' first, then make the app send requests.";
        if (list.Count == 0) return $"No requests match ({all.Count} captured in total).";

        var shown = list.TakeLast(Math.Clamp(limit, 1, 500)).ToList();
        var sb = new StringBuilder($"{list.Count} request(s){(shown.Count < list.Count ? $", showing last {shown.Count}" : "")} (cursor: {list.Max(r => r.Id)}).\n");
        foreach (var r in shown)
        {
            var path = Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.PathAndQuery : r.Url;
            var warn = r.Error is not null || r.Status >= 400 ? "  ⚠" : "";
            sb.Append($"#{r.Id,-4} {r.Method,-6} {NetworkCopy.Status(r),-8} {Clip(path, 48),-48} {NetworkClassifier.TypeLabel(r),-9} " +
                      $"{NetworkCopy.Size(r.ResponseSize),-8} {NetworkCopy.Duration(r.Timing?.Total ?? -1),-8} {r.Initiator?.Frame ?? ""}{warn}\n");
        }
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "network_request", Title = "HTTP request details", ReadOnly = true, Idempotent = true, OpenWorld = false)]
    [Description("Details of one captured request: URL, status, headers (credentials always masked), request and response bodies (JSON pretty-printed, truncated to maxChars), timing breakdown, remote address and the call stack that sent it.")]
    public async Task<string> NetworkRequest(
        [Description("Request id from network_requests (e.g. 12 or '#12').")] string id,
        [Description("Maximum characters per body. Default 4000.")] int maxChars = 4000,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        if (!long.TryParse(id.TrimStart('#'), out var rid) || s.Network.Get(rid) is not { } r)
            throw new McpException($"No captured request '{id}'. Use network_requests to list ids.");

        var sb = new StringBuilder();
        sb.Append($"#{r.Id} {r.Method} {r.Url}\n");
        sb.Append($"status      {NetworkCopy.Status(r)}{(r.StatusText is { } st && r.Status is not null ? $" {st}" : "")}  {r.HttpVersion}\n");
        if (r.ErrorDetail is { } ed) sb.Append($"error       {ed}\n");
        if (r.RedirectedFromId is { } from) sb.Append($"redirected  from #{from}\n");
        sb.Append($"remote      {r.RemoteAddress ?? "—"}{(r.ConnectionId is { } c ? $"  (connection {c})" : "")}\n");
        if (r.Timing is { } t)
            sb.Append($"timing      total {Ms(t.Total)} = queue {Ms(t.Queueing)} · dns {Ms(t.Dns)} · connect {Ms(t.Connect)} · ssl {Ms(t.Ssl)} · send {Ms(t.Send)} · wait (TTFB) {Ms(t.Wait)} · download {Ms(t.Download)}\n");
        if (r.Initiator is { } ini)
            sb.Append($"initiator   {ini.Frame ?? "(framework)"}\n            {string.Join("\n            ", ini.Stack.Take(6))}\n");
        sb.Append("request headers").Append(r.ProvisionalHeaders ? " (provisional: Host, cookies from CookieContainer and handler-added headers are not shown)" : "").Append(":\n");
        foreach (var h in r.RequestHeaders) sb.Append($"  {h.Name}: {Mask(h)}\n");
        sb.Append("response headers:\n");
        foreach (var h in r.ResponseHeaders) sb.Append($"  {h.Name}: {Mask(h)}\n");
        if (r.RequestBodySize > 0) sb.Append("request body:\n").Append(await Body(s, r.Id, "request", maxChars)).Append('\n');
        if (r.ResponseSize > 0) sb.Append($"response body ({NetworkCopy.Size(r.ResponseSize)}{(r.ResponseTruncated ? ", truncated by the agent" : "")}):\n").Append(await Body(s, r.Id, "response", maxChars));
        return sb.ToString().TrimEnd();
    }

    [McpServerTool(Name = "network_export_har", Title = "Export captured requests as HAR", ReadOnly = true, Idempotent = false, OpenWorld = false)]
    [Description("Writes the captured requests (optionally filtered) to a HAR 1.2 file and returns its path plus a short summary, so the file can be opened in other tools or read selectively. Always sanitized: Cookie, Set-Cookie, Authorization and any configured sensitive headers are removed. Bodies are included when captured.")]
    public async Task<string> NetworkExportHar(
        [Description("Filter with the Network panel's syntax (e.g. 'domain:api.example.com status-code:500 -/\\.png/'). Empty = all.")] string? filter = null,
        [Description("Maximum requests (the most recent ones). Default 200.")] int limit = 200,
        [Description("Include request/response bodies. Default true.")] bool includeBodies = true,
        [Description(AppParam)] string? app = null)
    {
        var s = ElementResolver.App(hub, app);
        var all = s.Network.Requests;
        if (all.Count == 0) return "No requests captured. Call network_capture with action 'start' first, then make the app send requests.";
        var f = new NetworkFilter(filter);
        var list = all.Where(f.Matches).TakeLast(Math.Clamp(limit, 1, 2000)).ToList();
        if (list.Count == 0) return $"No requests match ({all.Count} captured in total).";

        var bodies = includeBodies ? await HarExport.CollectBodiesAsync(s, list) : null;
        var json = HarConverter.Build(list, bodies, new HarOptions(IncludeSensitive: false));
        var dir = Path.Combine(Path.GetTempPath(), "spyglass-har");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"spyglass-{DateTime.Now:yyyyMMdd-HHmmss-fff}.har");
        await File.WriteAllTextAsync(path, json);

        var statuses = string.Join(" ", list.GroupBy(r => r.Error is not null || r.Canceled ? "failed" : r.Status is { } st ? $"{st / 100}xx" : "pending")
            .OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}"));
        return $"HAR written: {path}\n{list.Count} request(s) (#{list[0].Id}–#{list[^1].Id}), {NetworkCopy.Size(new FileInfo(path).Length)}, {bodies?.Count ?? 0} bod{(bodies?.Count == 1 ? "y" : "ies")}; {statuses}.\n" +
               "Sanitized: Cookie, Set-Cookie, Authorization and sensitive headers removed (request/response bodies are NOT redacted).";
    }

    static async Task<string> Body(AgentSession s, long id, string part, int maxChars)
    {
        try
        {
            var b = await s.GetNetworkBodyAsync(id, part);
            if (b.Encoding != "text") return $"  (binary {b.MimeType ?? ""}, {NetworkCopy.Size(b.TotalBytes)})";
            var text = b.Content;
            if (b.MimeType?.Contains("json") == true || text.TrimStart().StartsWith('{') || text.TrimStart().StartsWith('['))
                try { text = JsonSerializer.Serialize(JsonDocument.Parse(text).RootElement, new JsonSerializerOptions { WriteIndented = true }); } catch (JsonException) { }
            maxChars = Math.Clamp(maxChars, 200, 50_000);
            return text.Length <= maxChars ? text : text[..maxChars] + $"\n… ({text.Length - maxChars} more characters; raise maxChars)";
        }
        catch (SpyglassProtocolException ex) { return $"  ({ex.Message})"; }
    }

    static string Mask(HeaderDto h) => h.IsSensitive ? $"•••••• (length {h.Value.Length}, masked)" : h.Value;
    static string Ms(double v) => v < 0 ? "—" : $"{v:0.#} ms";
    static string Clip(string s, int n) => s.Length <= n ? s : s[..(n - 1)] + "…";

    static bool StatusMatches(NetworkRequestDto r, string filter) =>
        filter.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Any(f =>
            f.Equals("failed", StringComparison.OrdinalIgnoreCase) ? r.Error is not null || r.Canceled
            : f.Length == 3 && f.EndsWith("xx", StringComparison.OrdinalIgnoreCase) ? r.Status / 100 == f[0] - '0'
            : int.TryParse(f, out var code) && r.Status == code);
}
