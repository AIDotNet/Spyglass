using System.Text.RegularExpressions;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.Core.Network;

/// <summary>Chrome's resource-type buttons, classified by response MIME type (design/network/02-chrome-parity.md §2.3).</summary>
public enum ResourceType { FetchXhr, Doc, Css, Js, Font, Img, Media, WS, Wasm, Other }

public static class NetworkClassifier
{
    public static ResourceType TypeOf(NetworkRequestDto r)
    {
        if (r.IsUpgrade) return ResourceType.WS;
        var m = (r.MimeType ?? "").ToLowerInvariant();
        if (m == "text/html") return ResourceType.Doc;
        if (m == "text/css") return ResourceType.Css;
        if (m.Contains("javascript") || m.Contains("ecmascript")) return ResourceType.Js;
        if (m.StartsWith("font/") || m.StartsWith("application/font")) return ResourceType.Font;
        if (m.StartsWith("image/")) return ResourceType.Img;
        if (m.StartsWith("audio/") || m.StartsWith("video/")) return ResourceType.Media;
        if (m == "application/wasm") return ResourceType.Wasm;
        if (m.Length == 0 || m.StartsWith("text/") || m.Contains("json") || m.Contains("xml") || m.Contains("form") || m.Contains("grpc"))
            return ResourceType.FetchXhr;
        return ResourceType.Other;
    }

    /// <summary>Type column text, like Chrome's (fetch, png, websocket…).</summary>
    public static string TypeLabel(NetworkRequestDto r) => TypeOf(r) switch
    {
        ResourceType.WS => "websocket",
        ResourceType.Img => (r.MimeType ?? "image/").Split('/').Last().Replace("svg+xml", "svg"),
        ResourceType.Doc => "document",
        ResourceType.Css => "stylesheet",
        ResourceType.Js => "script",
        ResourceType.Font => "font",
        ResourceType.Media => "media",
        ResourceType.Wasm => "wasm",
        ResourceType.FetchXhr => "fetch",
        _ => "other",
    };
}

/// <summary>
/// Chrome's Network filter box (design/network/02-chrome-parity.md §2): plain text matches URL/host;
/// <c>-</c> negates; <c>/regex/</c>; space-separated terms are ANDed; property filters
/// domain:, url:, method:, status-code:, mime-type:, larger-than:, scheme:, has-response-header:, resource-type:,
/// is:running, set-cookie-name:/value:/domain:, cookie-name:/value:.
/// </summary>
public sealed class NetworkFilter
{
    readonly List<Func<NetworkRequestDto, bool>> _terms = new();

    public NetworkFilter(string? text)
    {
        foreach (Match m in Regex.Matches(text ?? "", @"(-?)(/(?:\\/|[^/])+/|\S+)"))
        {
            var negate = m.Groups[1].Value == "-";
            var term = Compile(m.Groups[2].Value);
            _terms.Add(negate ? r => !term(r) : term);
        }
    }

    public bool IsEmpty => _terms.Count == 0;
    public bool Matches(NetworkRequestDto r) => _terms.All(t => t(r));

    static Func<NetworkRequestDto, bool> Compile(string term)
    {
        if (term.Length > 2 && term.StartsWith('/') && term.EndsWith('/'))
        {
            try
            {
                var rx = new Regex(term[1..^1], RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(100));
                return r => rx.IsMatch(r.Url);
            }
            catch (ArgumentException) { return _ => false; }
        }
        var colon = term.IndexOf(':');
        if (colon > 0 && colon < term.Length - 1 && !term.Contains("://"))
        {
            var key = term[..colon].ToLowerInvariant();
            var value = term[(colon + 1)..];
            switch (key)
            {
                case "domain": return r => Wildcard(value).IsMatch(Host(r));
                case "url": return r => r.Url.Contains(value, StringComparison.OrdinalIgnoreCase);
                case "method": return r => r.Method.Equals(value, StringComparison.OrdinalIgnoreCase);
                case "status-code": return r => r.Status?.ToString() == value;
                case "mime-type": return r => (r.MimeType ?? "").Equals(value, StringComparison.OrdinalIgnoreCase);
                case "scheme": return r => Uri.TryCreate(r.Url, UriKind.Absolute, out var u) && u.Scheme.Equals(value, StringComparison.OrdinalIgnoreCase);
                case "larger-than": return r => (r.ResponseSize ?? 0) > ParseSize(value);
                case "has-response-header": return r => r.ResponseHeaders.Any(h => h.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                case "resource-type": return r => NetworkClassifier.TypeLabel(r).Equals(value, StringComparison.OrdinalIgnoreCase)
                                                  || NetworkClassifier.TypeOf(r).ToString().Equals(value, StringComparison.OrdinalIgnoreCase);
                case "is": return r => value == "running" && r.IsUpgrade && r.State == NetworkState.Finished;
                case "set-cookie-name": return r => SetCookies(r).Any(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                case "set-cookie-value": return r => SetCookies(r).Any(c => c.Value == value);
                case "set-cookie-domain": return r => SetCookies(r).Any(c => c.Domain?.Equals(value, StringComparison.OrdinalIgnoreCase) == true);
                case "cookie-name": return r => RequestCookies(r).Any(c => c.Name.Equals(value, StringComparison.OrdinalIgnoreCase));
                case "cookie-value": return r => RequestCookies(r).Any(c => c.Value == value);
            }
        }
        return r => r.Url.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    static string Host(NetworkRequestDto r) => Uri.TryCreate(r.Url, UriKind.Absolute, out var u) ? u.Host : "";

    static Regex Wildcard(string pattern) => new("^" + Regex.Escape(pattern).Replace("\\*", ".*") + "$", RegexOptions.IgnoreCase);

    /// <summary>Chrome: 1000 = 1k (decimal), as the docs state.</summary>
    static long ParseSize(string v)
    {
        v = v.Trim().ToLowerInvariant();
        var mult = v.EndsWith('k') ? 1000L : v.EndsWith('m') ? 1000_000L : 1L;
        return double.TryParse(v.TrimEnd('k', 'm'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? (long)(n * mult) : long.MaxValue;
    }

    public sealed record Cookie(string Name, string Value, string? Domain, string? Path, string? Expires, bool HttpOnly, bool Secure, string? SameSite);

    public static IEnumerable<Cookie> SetCookies(NetworkRequestDto r) =>
        r.ResponseHeaders.Where(h => h.Name.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split('\n')).Select(ParseSetCookie).OfType<Cookie>();

    public static IEnumerable<Cookie> RequestCookies(NetworkRequestDto r) =>
        r.RequestHeaders.Where(h => h.Name.Equals("Cookie", StringComparison.OrdinalIgnoreCase))
            .SelectMany(h => h.Value.Split(';')).Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2).Select(p => new Cookie(p[0].Trim(), p[1].Trim(), null, null, null, false, false, null));

    static Cookie? ParseSetCookie(string header)
    {
        var parts = header.Split(';').Select(p => p.Trim()).ToList();
        var nv = parts[0].Split('=', 2);
        if (nv.Length != 2) return null;
        string? Attr(string name) => parts.Skip(1).Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0].Equals(name, StringComparison.OrdinalIgnoreCase)) is { Length: 2 } a ? a[1] : null;
        bool Flag(string name) => parts.Skip(1).Any(p => p.Equals(name, StringComparison.OrdinalIgnoreCase));
        return new Cookie(nv[0], nv[1], Attr("Domain"), Attr("Path"), Attr("Expires") ?? (Attr("Max-Age") is { } ma ? $"Max-Age={ma}" : null),
            Flag("HttpOnly"), Flag("Secure"), Attr("SameSite"));
    }
}
