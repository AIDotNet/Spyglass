using System.IO.Pipelines;
using System.Text.Json.Nodes;
using Spyglass.VisualStudio.Core.Language;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>The LSP server Visual Studio talks to, over real LSP framing, with a fake hub behind it.</summary>
public sealed class AxamlLanguageServerTests : IAsyncDisposable
{
    sealed class FakeBackend : IXamlLanguageBackend
    {
        public event Action? Connected;
        public readonly List<string> Prepared = [];
        public (string Path, string Text, int Offset)? LastCompletion;
        public XamlCompletionResult? Completion;
        public XamlHoverResult? Hover;
        public void RaiseConnected() => Connected?.Invoke();
        public void XamlPrepare(string path) { lock (Prepared) Prepared.Add(path); }
        public Task<XamlCompletionResult?> XamlCompletionAsync(string path, string text, int offset, CancellationToken ct = default)
        {
            LastCompletion = (path, text, offset);
            return Task.FromResult(Completion);
        }
        public Task<XamlHoverResult?> XamlHoverAsync(string path, string text, int offset, CancellationToken ct = default) => Task.FromResult(Hover);
        public Task<string?> XamlDocumentationAsync(string path, string docId, CancellationToken ct = default) =>
            Task.FromResult<string?>(docId == "T:Avalonia.Controls.Button" ? "```csharp\nclass Button\n```\nA `Button` control." : null);
    }

    readonly FakeBackend _backend = new();
    readonly Pipe _toServer = new(), _toClient = new();
    readonly Task _server;
    readonly CancellationTokenSource _stop = new();
    int _id;

    static readonly string File = OperatingSystem.IsWindows() ? @"C:\app\Views\Main.axaml" : "/app/Views/Main.axaml";
    static readonly string Uri = new Uri(File).AbsoluteUri;

    public AxamlLanguageServerTests()
    {
        var server = new AxamlLanguageServer(_backend);
        _server = server.RunAsync(_toServer.Reader.AsStream(), _toClient.Writer.AsStream(), _stop.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        await _toServer.Writer.CompleteAsync();
        await _server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    Task Notify(string method, JsonObject p) => LspFraming.WriteAsync(_toServer.Writer.AsStream(), new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method, ["params"] = p });

    async Task<JsonNode?> Request(string method, JsonNode? p)
    {
        var id = ++_id;
        await LspFraming.WriteAsync(_toServer.Writer.AsStream(), new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = p });
        while (true)
        {
            var m = await LspFraming.ReadAsync(_toClient.Reader.AsStream(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.NotNull(m);
            if (m!["id"]?.GetValue<int>() != id) continue;
            Assert.Null(m["error"]);
            return m["result"];
        }
    }

    async Task Initialize(bool snippets, bool markdown)
    {
        var caps = new JsonObject
        {
            ["textDocument"] = new JsonObject
            {
                ["completion"] = new JsonObject
                {
                    ["completionItem"] = new JsonObject
                    {
                        ["snippetSupport"] = snippets,
                        ["documentationFormat"] = markdown ? new JsonArray("markdown", "plaintext") : new JsonArray("plaintext"),
                    },
                },
                ["hover"] = new JsonObject { ["contentFormat"] = markdown ? new JsonArray("markdown") : new JsonArray("plaintext") },
            },
        };
        var r = await Request("initialize", new JsonObject { ["capabilities"] = caps });
        var c = r!["capabilities"]!;
        Assert.Equal(1, c["textDocumentSync"]!["change"]!.GetValue<int>());
        Assert.True(c["completionProvider"]!["resolveProvider"]!.GetValue<bool>());
        Assert.Contains("<", c["completionProvider"]!["triggerCharacters"]!.AsArray().Select(x => x!.GetValue<string>()));
        Assert.True(c["hoverProvider"]!.GetValue<bool>());
        await Notify("initialized", []);
    }

    /// <summary>Returns once the server has handled didOpen: notifications get no response, but it prepares the file last.</summary>
    async Task Open(string text)
    {
        int PreparedCount() { lock (_backend.Prepared) return _backend.Prepared.Count; }
        var before = PreparedCount();
        await Notify("textDocument/didOpen", new JsonObject { ["textDocument"] = new JsonObject { ["uri"] = Uri, ["languageId"] = "axaml", ["version"] = 1, ["text"] = text } });
        await Wait.UntilAsync(() => PreparedCount() > before, "the server to handle didOpen");
    }

    static JsonObject At(int line, int character) => new()
    {
        ["textDocument"] = new JsonObject { ["uri"] = Uri },
        ["position"] = new JsonObject { ["line"] = line, ["character"] = character },
    };

    [Fact]
    public async Task Completion_maps_positions_snippets_and_edits()
    {
        await Initialize(snippets: true, markdown: true);
        const string text = "<Window>\r\n  <Bu\r\n</Window>";
        await Open(text);
        Assert.Equal([File], _backend.Prepared);

        var offset = text.IndexOf("<Bu", StringComparison.Ordinal) + 3;
        _backend.Completion = new XamlCompletionResult(offset - 2,
        [
            new("Button", "element", "Avalonia.Controls", DocId: "T:Avalonia.Controls.Button", SortText: "1"),
            new("arc:Card", "element", "TKCode.Arc", AdditionalEdit: new XamlTextEdit(7, 0, " xmlns:arc=\"https://x\"")),
            new("Width", "property", "double", InsertText: "Width=\"$1\"", Snippet: true, Deprecated: true),
        ]);
        var r = await Request("textDocument/completion", At(1, 5));

        Assert.Equal((File, text, offset), _backend.LastCompletion);
        var items = r!["items"]!.AsArray();
        var button = items[0]!;
        Assert.Equal(7, button["kind"]!.GetValue<int>());
        Assert.Equal("1Button", button["sortText"]!.GetValue<string>());
        var range = button["textEdit"]!["range"]!;
        Assert.Equal((1, 3, 1, 5), (range["start"]!["line"]!.GetValue<int>(), range["start"]!["character"]!.GetValue<int>(),
            range["end"]!["line"]!.GetValue<int>(), range["end"]!["character"]!.GetValue<int>()));
        var xmlns = items[1]!["additionalTextEdits"]![0]!;
        Assert.Equal((0, 7), (xmlns["range"]!["start"]!["line"]!.GetValue<int>(), xmlns["range"]!["start"]!["character"]!.GetValue<int>()));
        var width = items[2]!;
        Assert.Equal(("Width=\"$1\"", 2, 10), (width["textEdit"]!["newText"]!.GetValue<string>(), width["insertTextFormat"]!.GetValue<int>(), width["kind"]!.GetValue<int>()));
        Assert.Equal(1, width["tags"]![0]!.GetValue<int>());

        var resolved = await Request("completionItem/resolve", button.DeepClone());
        Assert.Equal("markdown", resolved!["documentation"]!["kind"]!.GetValue<string>());
        Assert.Contains("A `Button` control.", resolved["documentation"]!["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Without_snippet_or_markdown_support_plain_text_is_sent()
    {
        await Initialize(snippets: false, markdown: false);
        await Open("<Button  />");
        _backend.Completion = new XamlCompletionResult(8, [new("Width", "property", InsertText: "Width=\"$1\"", Snippet: true, DocId: "T:Avalonia.Controls.Button")]);
        var item = (await Request("textDocument/completion", At(0, 8)))!["items"]![0]!;
        Assert.Equal(("Width=\"\"", 1), (item["textEdit"]!["newText"]!.GetValue<string>(), item["insertTextFormat"]!.GetValue<int>()));
        var doc = (await Request("completionItem/resolve", item.DeepClone()))!["documentation"]!;
        Assert.Equal("plaintext", doc["kind"]!.GetValue<string>());
        Assert.DoesNotContain("`", doc["value"]!.GetValue<string>());
    }

    [Fact]
    public async Task Changes_are_applied_before_later_requests_and_hover_maps_ranges()
    {
        await Initialize(snippets: true, markdown: true);
        await Open("<A/>");
        await Notify("textDocument/didChange", new JsonObject
        {
            ["textDocument"] = new JsonObject { ["uri"] = Uri, ["version"] = 2 },
            ["contentChanges"] = new JsonArray(new JsonObject { ["text"] = "<Window>\n  <StackPanel Orientation=\"Vertical\" />\n</Window>" }),
        });
        _backend.Hover = new XamlHoverResult("**Orientation**", 23, 34);
        var h = await Request("textDocument/hover", At(1, 15));
        Assert.Equal("**Orientation**", h!["contents"]!["value"]!.GetValue<string>());
        Assert.Equal((1, 14, 1, 25), (h["range"]!["start"]!["line"]!.GetValue<int>(), h["range"]!["start"]!["character"]!.GetValue<int>(),
            h["range"]!["end"]!["line"]!.GetValue<int>(), h["range"]!["end"]!["character"]!.GetValue<int>()));

        // No hub (or an old one): an empty, incomplete list, so the editor asks again.
        _backend.Completion = null;
        var r = await Request("textDocument/completion", At(1, 3));
        Assert.True(r!["isIncomplete"]!.GetValue<bool>());
        Assert.Empty(r["items"]!.AsArray());
    }

    [Fact]
    public async Task Open_documents_are_prepared_again_when_the_hub_connects_and_unknown_requests_fail_cleanly()
    {
        await Initialize(snippets: true, markdown: true);
        await Open("<A/>");
        _backend.RaiseConnected();
        Assert.Equal([File, File], _backend.Prepared);

        var id = ++_id;
        await LspFraming.WriteAsync(_toServer.Writer.AsStream(), new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = "textDocument/definition", ["params"] = At(0, 1) });
        var m = await LspFraming.ReadAsync(_toClient.Reader.AsStream(), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(-32601, m!["error"]!["code"]!.GetValue<int>());

        Assert.Null(await Request("shutdown", null));
        await Notify("exit", []);
        await _server.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void Line_index_handles_crlf_cr_and_out_of_range_positions()
    {
        var lines = new LineIndex("a\r\nbc\rd\ne");
        Assert.Equal(0, lines.Offset(0, 0));
        Assert.Equal(3, lines.Offset(1, 0));
        Assert.Equal(6, lines.Offset(2, 0));
        Assert.Equal(8, lines.Offset(3, 0));
        Assert.Equal(5, lines.Offset(1, 99));       // clamped to the line
        Assert.Equal(9, lines.Offset(9, 0));        // past the end
        Assert.Equal((1, 1), lines.Position(4));
        Assert.Equal((3, 1), lines.Position(9));
    }
}
