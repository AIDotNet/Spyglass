using System.Text;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

/// <summary>Chrome's Copy submenu formats (design/network/02-chrome-parity.md §7) plus "Copy as C#".</summary>
public static class NetworkCopy
{
    /// <summary>Headers HttpClient manages itself; omitted from generated code, as Chrome omits pseudo-headers.</summary>
    static readonly HashSet<string> Skip = new(StringComparer.OrdinalIgnoreCase) { "Content-Length", "Host", "traceparent", "tracestate" };

    static IEnumerable<HeaderDto> Headers(NetworkRequestDto r) => r.RequestHeaders.Where(h => !Skip.Contains(h.Name));

    public static string Curl(NetworkRequestDto r, string? body)
    {
        var sb = new StringBuilder($"curl {Sh(r.Url)}");
        if (r.Method != "GET" && !(r.Method == "POST" && body is not null)) sb.Append($" \\\n  -X {r.Method}");
        foreach (var h in Headers(r)) sb.Append($" \\\n  -H {Sh($"{h.Name}: {h.Value}")}");
        if (body is not null) sb.Append($" \\\n  --data-raw {Sh(body)}");
        return sb.ToString();
    }

    public static string PowerShell(NetworkRequestDto r, string? body)
    {
        var sb = new StringBuilder($"Invoke-WebRequest -UseBasicParsing -Uri {Ps(r.Url)} `\n-Method {Ps(r.Method)}");
        var headers = Headers(r).Where(h => !h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)).ToList();
        if (headers.Count > 0)
            sb.Append(" `\n-Headers @{\n").Append(string.Join("\n", headers.Select(h => $"  {Ps(h.Name)}={Ps(h.Value)}"))).Append("\n}");
        if (r.RequestHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) is { } ct)
            sb.Append($" `\n-ContentType {Ps(ct.Value)}");
        if (body is not null) sb.Append($" `\n-Body {Ps(body)}");
        return sb.ToString();
    }

    public static string Fetch(NetworkRequestDto r, string? body, bool node = false)
    {
        var headers = Headers(r).ToList();
        var sb = new StringBuilder($"fetch({Js(r.Url)}, {{\n");
        sb.Append("  \"headers\": {").Append(string.Join(",", headers.Select(h => $"\n    {Js(h.Name.ToLowerInvariant())}: {Js(h.Value)}")))
          .Append(headers.Count > 0 ? "\n  },\n" : "},\n");
        sb.Append($"  \"body\": {(body is null ? "null" : Js(body))},\n");
        sb.Append($"  \"method\": {Js(r.Method)}");
        if (!node) sb.Append(",\n  \"mode\": \"cors\"");
        return sb.Append("\n});").ToString();
    }

    public static string CSharp(NetworkRequestDto r, string? body)
    {
        var sb = new StringBuilder("using var http = new HttpClient();\n");
        sb.Append($"using var request = new HttpRequestMessage(new HttpMethod({Cs(r.Method)}), {Cs(r.Url)});\n");
        var contentType = r.RequestHeaders.FirstOrDefault(h => h.Name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase));
        foreach (var h in Headers(r).Where(h => h != contentType))
            sb.Append($"request.Headers.TryAddWithoutValidation({Cs(h.Name)}, {Cs(h.Value)});\n");
        if (body is not null)
        {
            var media = contentType?.Value.Split(';')[0].Trim() ?? "text/plain";
            sb.Append($"request.Content = new StringContent({Cs(body)}, System.Text.Encoding.UTF8, {Cs(media)});\n");
        }
        sb.Append("using var response = await http.SendAsync(request);\n");
        sb.Append("var text = await response.Content.ReadAsStringAsync();");
        return sb.ToString();
    }

    static string Sh(string s) => "'" + s.Replace("'", "'\\''") + "'";
    static string Ps(string s) => "\"" + s.Replace("`", "``").Replace("\"", "`\"").Replace("$", "`$") + "\"";
    static string Js(string s) => System.Text.Json.JsonSerializer.Serialize(s);
    static string Cs(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r") + "\"";

    /// <summary>Chrome-style sizes: "523 B", "1.2 kB", "3.4 MB" (decimal units).</summary>
    public static string Size(long? bytes) => bytes switch
    {
        null => "—",
        < 1000 => $"{bytes} B",
        < 1000_000 => $"{bytes / 1000.0:0.#} kB",
        _ => $"{bytes / 1000_000.0:0.#} MB",
    };

    public static string Duration(double ms) => ms < 0 ? "—" : ms < 1000 ? $"{ms:0} ms" : $"{ms / 1000:0.00} s";

    /// <summary>Status column per Chrome: code, (pending), (failed) net::ERR_…, (canceled).</summary>
    public static string Status(NetworkRequestDto r) =>
        r.Canceled ? "(canceled)"
        : r.Error is { } e ? $"(failed) {e}"
        : r.Status is { } s ? s.ToString()
        : "(pending)";

    /// <summary>Name column: last path segment + query, or host for "/".</summary>
    public static string Name(NetworkRequestDto r)
    {
        if (!Uri.TryCreate(r.Url, UriKind.Absolute, out var u)) return r.Url;
        var last = u.Segments.LastOrDefault()?.Trim('/') ?? "";
        return (last.Length == 0 ? u.Host : last) + u.Query;
    }
}
