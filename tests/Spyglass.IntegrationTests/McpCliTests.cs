using System.Net;
using System.Net.Sockets;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.Mcp;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>`spyglass mcp` (design/mcp/README.md §3) as a real child process, spoken to over stdio like an editor would.</summary>
[Collection("app")]
public class McpCliTests(AppFixture app)
{
    static string ClientDll
    {
        get
        {
            var root = AppContext.BaseDirectory;
            for (var dir = new DirectoryInfo(root); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "src", "Spyglass.App", "bin", "Debug", "net10.0", "Spyglass.dll");
                if (File.Exists(candidate)) return candidate;
            }
            throw new FileNotFoundException("Spyglass.App is not built (expected src/Spyglass.App/bin/Debug/net10.0/Spyglass.dll).");
        }
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    static Task<McpClient> Launch(int port, params string[] extra) =>
        McpClient.CreateAsync(new StdioClientTransport(new StdioClientTransportOptions
        {
            Name = "spyglass-cli",
            Command = "dotnet",
            Arguments = [ClientDll, "mcp", "--port", port.ToString(), .. extra],
            ShutdownTimeout = TimeSpan.FromSeconds(5),
        }));

    static string Text(CallToolResult r) => string.Join("\n", r.Content.OfType<TextContentBlock>().Select(t => t.Text));

    [Fact]
    public async Task Standalone_stdio_serves_tools_and_owns_a_hub()
    {
        var port = FreePort();
        await using var client = await Launch(port);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "apps_list");
        Assert.Contains(tools, t => t.Name == "ui_snapshot");
        Assert.Equal("spyglass", client.ServerInfo?.Name);

        var text = Text(await client.CallToolAsync("apps_list", new Dictionary<string, object?>()));
        Assert.Contains("No app connected", text);
        Assert.Contains($"127.0.0.1:{port}", text);   // the standalone process listens on the requested port
    }

    [Fact]
    public async Task Stdio_forwards_to_a_Spyglass_that_already_runs_on_the_port()
    {
        // The test hub (with /mcp) plays the GUI. The CLI finds the port taken and forwards to it, so the coding
        // agent sees the very app the "GUI" has connected.
        await using var client = await Launch(app.Hub.Port);
        var tools = await client.ListToolsAsync();
        Assert.Contains(tools, t => t.Name == "element_inspect");

        var text = Text(await client.CallToolAsync("apps_list", new Dictionary<string, object?>()));
        Assert.Contains("Spyglass Integration Tests", text);

        var snapshot = Text(await client.CallToolAsync("ui_snapshot", new Dictionary<string, object?>()));
        Assert.Contains("Button#SaveButton", snapshot);
        // Errors travel through the proxy as tool errors, not as protocol failures.
        var bad = await client.CallToolAsync("element_inspect", new Dictionary<string, object?> { ["element"] = "#DoesNotExist" });
        Assert.True(bad.IsError);
        Assert.Contains("No element matches", Text(bad));
    }

    [Fact]
    public async Task Port_taken_by_something_else_is_a_clear_error()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        try
        {
            var ex = await Assert.ThrowsAnyAsync<Exception>(() => Launch(port));
            Assert.NotNull(ex);   // the process exits with code 1 and a message on stderr, so the MCP handshake fails
        }
        finally { l.Stop(); }
    }

    [Theory]
    [InlineData("--port 12345 --http", 12345, true, false, false, null)]
    [InlineData("--read-only --no-input", 29500, false, true, true, null)]
    [InlineData("--port", 29500, false, false, false, "--port needs")]
    [InlineData("--bogus", 29500, false, false, false, "Unknown argument")]
    public void Argument_parsing(string args, int port, bool http, bool readOnly, bool noInput, string? error)
    {
        Environment.SetEnvironmentVariable("SPYGLASS_PORT", null);
        var o = McpCli.Parse(args.Split(' '));
        Assert.Equal(port, o.Port);
        Assert.Equal(http, o.Http);
        Assert.Equal(readOnly, o.ReadOnly);
        Assert.Equal(noInput, o.NoInput);
        if (error is null) Assert.Null(o.Error); else Assert.Contains(error, o.Error);
    }
}
