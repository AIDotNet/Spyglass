using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Spyglass.VisualStudio.Core.Language;

/// <summary>Where the language server gets its answers: the Spyglass hub (<see cref="HubClient"/>), or a fake in tests.</summary>
public interface IXamlLanguageBackend
{
    /// <summary>Raised when the backend becomes able to answer (the hub connected): open documents are prepared again.</summary>
    event Action? Connected;
    void XamlPrepare(string path);
    Task<XamlCompletionResult?> XamlCompletionAsync(string path, string text, int offset, CancellationToken ct = default);
    Task<XamlHoverResult?> XamlHoverAsync(string path, string text, int offset, CancellationToken ct = default);
    Task<string?> XamlDocumentationAsync(string path, string docId, CancellationToken ct = default);
}

/// <summary>
/// A Language Server Protocol server for .axaml (design/xaml-language/README.md), run inside the Visual Studio extension and
/// handed to Visual Studio's LSP client through an in-memory pipe (LanguageServerProvider). It keeps the open documents'
/// text (full sync) and forwards completion, completion resolve and hover to the hub's xaml/* requests, converting between
/// LSP positions (line/UTF-16 character) and the hub's offsets. Snippets and markdown are used only when the client says it
/// supports them.
/// </summary>
public sealed class AxamlLanguageServer
{
    readonly IXamlLanguageBackend _backend;
    readonly Action<string> _log;
    readonly ConcurrentDictionary<string, string> _documents = new(StringComparer.OrdinalIgnoreCase);   // uri → text
    readonly ConcurrentDictionary<string, CancellationTokenSource> _requests = new();
    readonly SemaphoreSlim _write = new(1, 1);
    Stream _output = Stream.Null;
    bool _snippets, _markdownDocs, _markdownHover;

    /// <summary>Characters after which the client asks for completion by itself (as in the VS Code extension).</summary>
    public static readonly string[] TriggerCharacters = ["<", " ", ":", ".", "{", "\"", "'", "=", ",", "/"];

    public AxamlLanguageServer(IXamlLanguageBackend backend, Action<string>? log = null)
    {
        _backend = backend;
        _log = log ?? (_ => { });
    }

    /// <summary>Serves one client until it sends exit, closes the stream, or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(Stream input, Stream output, CancellationToken ct = default)
    {
        _output = output;
        void PrepareAll() { foreach (var uri in _documents.Keys) if (PathOf(uri) is { } p) _backend.XamlPrepare(p); }
        _backend.Connected += PrepareAll;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var message = await LspFraming.ReadAsync(input, ct).ConfigureAwait(false);
                if (message is null) return;   // the client closed the pipe
                if (message["method"]?.GetValue<string>() is not { } method) continue;   // a response: we send no requests
                var id = message["id"]?.DeepClone();
                var @params = message["params"];
                if (id is null)
                {
                    if (method == "exit") return;
                    Notification(method, @params);
                    continue;
                }
                // Requests run concurrently; document notifications above are applied in order before later requests start.
                _ = AnswerAsync(id, method, @params, ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (EndOfStreamException) { }
        catch (IOException) { }
        finally { _backend.Connected -= PrepareAll; }
    }

    void Notification(string method, JsonNode? p)
    {
        switch (method)
        {
            case "textDocument/didOpen":
            {
                var uri = p!["textDocument"]!["uri"]!.GetValue<string>();
                _documents[uri] = p["textDocument"]!["text"]?.GetValue<string>() ?? "";
                if (PathOf(uri) is { } path) _backend.XamlPrepare(path);
                break;
            }
            case "textDocument/didChange":
            {
                var uri = p!["textDocument"]!["uri"]!.GetValue<string>();
                // Full sync (we announce TextDocumentSyncKind.Full): the last change holds the whole text.
                if (p["contentChanges"] is JsonArray { Count: > 0 } changes && changes[^1]!["text"]?.GetValue<string>() is { } text)
                    _documents[uri] = text;
                break;
            }
            case "textDocument/didClose":
                _documents.TryRemove(p!["textDocument"]!["uri"]!.GetValue<string>(), out _);
                break;
            case "$/cancelRequest":
                if (p?["id"]?.ToJsonString() is { } key && _requests.TryGetValue(key, out var cts)) cts.Cancel();
                break;
        }
    }

    async Task AnswerAsync(JsonNode id, string method, JsonNode? p, CancellationToken ct)
    {
        var key = id.ToJsonString();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _requests[key] = cts;
        JsonNode? result = null;
        JsonObject? error = null;
        try
        {
            result = method switch
            {
                "initialize" => Initialize(p),
                "shutdown" => null,
                "textDocument/completion" => await CompletionAsync(p!, cts.Token).ConfigureAwait(false),
                "completionItem/resolve" => await ResolveAsync(p!, cts.Token).ConfigureAwait(false),
                "textDocument/hover" => await HoverAsync(p!, cts.Token).ConfigureAwait(false),
                _ => throw new MethodNotFoundException(method),
            };
        }
        catch (MethodNotFoundException) { error = new JsonObject { ["code"] = -32601, ["message"] = $"Method not found: {method}" }; }
        catch (OperationCanceledException) { error = new JsonObject { ["code"] = -32800, ["message"] = "Request cancelled" }; }
        catch (Exception ex)
        {
            _log($"{method} failed: {ex.Message}");
            error = new JsonObject { ["code"] = -32603, ["message"] = ex.Message };
        }
        finally { _requests.TryRemove(key, out _); }

        var response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id };
        if (error is not null) response["error"] = error;
        else response["result"] = result;
        await SendAsync(response).ConfigureAwait(false);
    }

    sealed class MethodNotFoundException(string method) : Exception(method);

    async Task SendAsync(JsonObject message)
    {
        await _write.WaitAsync().ConfigureAwait(false);
        try { await LspFraming.WriteAsync(_output, message).ConfigureAwait(false); }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        finally { _write.Release(); }
    }

    // ---------- initialize ----------

    JsonNode Initialize(JsonNode? p)
    {
        var td = p?["capabilities"]?["textDocument"];
        _snippets = td?["completion"]?["completionItem"]?["snippetSupport"]?.GetValue<bool>() == true;
        _markdownDocs = Formats(td?["completion"]?["completionItem"]?["documentationFormat"]).Contains("markdown");
        _markdownHover = Formats(td?["hover"]?["contentFormat"]).Contains("markdown");
        return new JsonObject
        {
            ["capabilities"] = new JsonObject
            {
                ["textDocumentSync"] = new JsonObject { ["openClose"] = true, ["change"] = 1 },
                ["completionProvider"] = new JsonObject
                {
                    ["triggerCharacters"] = new JsonArray(TriggerCharacters.Select(c => (JsonNode)JsonValue.Create(c)!).ToArray()),
                    ["resolveProvider"] = true,
                },
                ["hoverProvider"] = true,
            },
            ["serverInfo"] = new JsonObject { ["name"] = "Spyglass Avalonia XAML" },
        };

        static List<string> Formats(JsonNode? n) => n is JsonArray a ? a.Select(x => x?.GetValue<string>() ?? "").ToList() : [];
    }

    // ---------- completion ----------

    async Task<JsonNode?> CompletionAsync(JsonNode p, CancellationToken ct)
    {
        var uri = p["textDocument"]!["uri"]!.GetValue<string>();
        if (PathOf(uri) is not { } path || !_documents.TryGetValue(uri, out var text)) return null;
        var lines = new LineIndex(text);
        var offset = lines.Offset(p["position"]!["line"]!.GetValue<int>(), p["position"]!["character"]!.GetValue<int>());
        var r = await _backend.XamlCompletionAsync(path, text, offset, ct).ConfigureAwait(false);
        if (r is null) return new JsonObject { ["isIncomplete"] = true, ["items"] = new JsonArray() };   // hub not (yet) there: ask again

        var from = Math.Clamp(r.From, 0, offset);
        var range = Range(lines, from, offset);
        var items = new JsonArray();
        foreach (var i in r.Items)
        {
            var insert = i.InsertText ?? i.Label;
            var snippet = i.Snippet && _snippets;
            if (i.Snippet && !_snippets) insert = StripTabStops(insert);
            var item = new JsonObject
            {
                ["label"] = i.Label,
                ["kind"] = Kind(i.Kind),
                ["insertTextFormat"] = snippet ? 2 : 1,
                ["filterText"] = i.Label,
                ["textEdit"] = new JsonObject { ["range"] = range.DeepClone(), ["newText"] = insert },
            };
            if (i.Detail is not null) item["detail"] = i.Detail;
            if (i.SortText is not null) item["sortText"] = i.SortText + i.Label;
            if (i.Deprecated) item["tags"] = new JsonArray(1);
            if (i.AdditionalEdit is { } e)
                item["additionalTextEdits"] = new JsonArray(new JsonObject { ["range"] = Range(lines, e.Offset, e.Offset + e.Length), ["newText"] = e.Text });
            if (i.DocId is not null) item["data"] = new JsonObject { ["path"] = path, ["docId"] = i.DocId };
            items.Add(item);
        }
        return new JsonObject { ["isIncomplete"] = r.Incomplete, ["items"] = items };
    }

    async Task<JsonNode?> ResolveAsync(JsonNode item, CancellationToken ct)
    {
        var resolved = item.DeepClone();
        if (item["data"]?["path"]?.GetValue<string>() is { } path && item["data"]?["docId"]?.GetValue<string>() is { } docId
            && await _backend.XamlDocumentationAsync(path, docId, ct).ConfigureAwait(false) is { Length: > 0 } md)
            resolved["documentation"] = Markup(md, _markdownDocs);
        return resolved;
    }

    // ---------- hover ----------

    async Task<JsonNode?> HoverAsync(JsonNode p, CancellationToken ct)
    {
        var uri = p["textDocument"]!["uri"]!.GetValue<string>();
        if (PathOf(uri) is not { } path || !_documents.TryGetValue(uri, out var text)) return null;
        var lines = new LineIndex(text);
        var offset = lines.Offset(p["position"]!["line"]!.GetValue<int>(), p["position"]!["character"]!.GetValue<int>());
        var r = await _backend.XamlHoverAsync(path, text, offset, ct).ConfigureAwait(false);
        if (r?.Markdown is not { Length: > 0 } md) return null;
        return new JsonObject { ["contents"] = Markup(md, _markdownHover), ["range"] = Range(lines, r.From, r.To) };
    }

    // ---------- helpers ----------

    static JsonObject Markup(string markdown, bool supportsMarkdown) => supportsMarkdown
        ? new JsonObject { ["kind"] = "markdown", ["value"] = markdown }
        : new JsonObject { ["kind"] = "plaintext", ["value"] = PlainText(markdown) };

    /// <summary>Markdown as the hub writes it (code fences, `code`, _emphasis_) without the markup.</summary>
    internal static string PlainText(string markdown) => markdown
        .Replace("```csharp\n", "", StringComparison.Ordinal).Replace("\n```", "", StringComparison.Ordinal).Replace("```", "", StringComparison.Ordinal)
        .Replace("`", "", StringComparison.Ordinal).Replace("\n_", "\n", StringComparison.Ordinal).Replace("_\n", "\n", StringComparison.Ordinal);

    /// <summary>Width="$1" → Width="" for clients without snippet support.</summary>
    internal static string StripTabStops(string snippet) =>
        System.Text.RegularExpressions.Regex.Replace(snippet, @"\$\{?\d+\}?", "");

    static JsonObject Range(LineIndex lines, int start, int end)
    {
        var (sl, sc) = lines.Position(start);
        var (el, ec) = lines.Position(end);
        return new JsonObject
        {
            ["start"] = new JsonObject { ["line"] = sl, ["character"] = sc },
            ["end"] = new JsonObject { ["line"] = el, ["character"] = ec },
        };
    }

    /// <summary>LSP CompletionItemKind for the hub's item kinds.</summary>
    internal static int Kind(string kind) => kind switch
    {
        "method" => 2,
        "markupExtension" => 3,
        "name" => 6,
        "element" or "type" or "closingTag" or "owner" => 7,
        "namespace" => 9,
        "property" or "attachedProperty" => 10,
        "directive" => 14,
        "value" => 20,
        "resource" => 21,
        "event" => 23,
        _ => 1,
    };

    internal static string? PathOf(string uri) =>
        Uri.TryCreate(uri, UriKind.Absolute, out var u) && u.IsFile ? u.LocalPath : null;
}

/// <summary>Offsets ↔ LSP positions (0-based line, UTF-16 character); lines end at \n, \r\n or \r.</summary>
internal sealed class LineIndex
{
    readonly string _text;
    readonly List<int> _starts = [0];

    public LineIndex(string text)
    {
        _text = text;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
            if (text[i] is '\n' or '\r') _starts.Add(i + 1);
        }
    }

    public int Offset(int line, int character)
    {
        if (line < 0) return 0;
        if (line >= _starts.Count) return _text.Length;
        // A character past the end of the line means the end of its text, not into or past the line break.
        var lineEnd = line + 1 < _starts.Count ? _starts[line + 1] : _text.Length;
        if (lineEnd > _starts[line] && _text[lineEnd - 1] == '\n') lineEnd--;
        if (lineEnd > _starts[line] && _text[lineEnd - 1] == '\r') lineEnd--;
        return Math.Min(_starts[line] + Math.Max(0, character), lineEnd);
    }

    public (int Line, int Character) Position(int offset)
    {
        offset = Math.Clamp(offset, 0, _text.Length);
        var line = _starts.BinarySearch(offset);
        if (line < 0) line = ~line - 1;
        return (line, offset - _starts[line]);
    }
}

/// <summary>LSP base protocol: "Content-Length: N\r\n\r\n" + N bytes of UTF-8 JSON.</summary>
public static class LspFraming
{
    public static async Task<JsonObject?> ReadAsync(Stream input, CancellationToken ct)
    {
        var length = -1;
        var header = new StringBuilder();
        var one = new byte[1];
        while (true)
        {
            header.Clear();
            while (true)
            {
                if (await input.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
                if (one[0] == '\n') break;
                if (one[0] != '\r') header.Append((char)one[0]);
            }
            if (header.Length == 0) break;   // the blank line after the headers
            var line = header.ToString();
            var colon = line.IndexOf(':');
            if (colon > 0 && line[..colon].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
                length = int.Parse(line[(colon + 1)..].Trim(), System.Globalization.CultureInfo.InvariantCulture);
        }
        if (length < 0) throw new IOException("LSP message without Content-Length.");
        var body = new byte[length];
        await input.ReadExactlyAsync(body, ct).ConfigureAwait(false);
        return JsonNode.Parse(body) as JsonObject;
    }

    public static async Task WriteAsync(Stream output, JsonObject message)
    {
        var body = Encoding.UTF8.GetBytes(message.ToJsonString());
        var header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        await output.WriteAsync(header).ConfigureAwait(false);
        await output.WriteAsync(body).ConfigureAwait(false);
        await output.FlushAsync().ConfigureAwait(false);
    }
}
