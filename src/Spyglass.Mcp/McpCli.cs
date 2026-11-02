using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Spyglass.App.Core;
using Spyglass.Protocol;

namespace Spyglass.Mcp;

/// <summary>
/// <c>spyglass mcp</c>: the MCP server without the GUI (design/mcp/README.md §3), for editors that launch an MCP
/// server process (stdio), CI and remote machines.
/// <list type="bullet">
/// <item>default: stdio MCP + a hub on 127.0.0.1:port that debugged apps connect to;</item>
/// <item>stdio, but the port is already served by a running Spyglass (GUI or another <c>spyglass mcp</c>): forwards to its
/// <c>/mcp</c> endpoint, so the coding agent and the GUI see the same session;</item>
/// <item><c>--http</c>: no stdio, serves MCP over HTTP at /mcp (CI / remote).</item>
/// </list>
/// stdout carries the MCP protocol: everything human-readable goes to stderr.
/// </summary>
public static class McpCli
{
    const string Usage = """
        spyglass mcp [--port N] [--http] [--read-only] [--no-input]

          --port N      port debugged apps connect to (default: SPYGLASS_PORT or 29500)
          --http        serve MCP over HTTP at http://127.0.0.1:N/mcp instead of stdio
          --read-only   refuse every tool that changes the app (same as SPYGLASS_MCP_READONLY=1)
          --no-input    refuse tools that send input to the app (same as SPYGLASS_MCP_NOINPUT=1)

        Without --http, MCP is spoken on stdin/stdout. If a Spyglass is already running on the port, this
        process forwards to it instead of starting a second hub.
        """;

    public sealed record Options(int Port, bool Http, bool ReadOnly, bool NoInput, bool Help, string? Error);

    public static Options Parse(IReadOnlyList<string> args)
    {
        var port = SpyglassProtocol.Port;
        bool http = false, ro = false, noInput = false, help = false;
        string? error = null;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Count && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536: port = p; i++; break;
                case "--port": error = "--port needs a number between 1 and 65535."; break;
                case "--http": http = true; break;
                case "--read-only": ro = true; break;
                case "--no-input": noInput = true; break;
                case "-h" or "--help": help = true; break;
                default: error = $"Unknown argument '{args[i]}'."; break;
            }
        }
        return new Options(port, http, ro, noInput, help, error);
    }

    /// <summary>Entry point; <paramref name="args"/> are the arguments after "mcp". Returns the process exit code.</summary>
    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var o = Parse(args);
        if (o.Help) { await Console.Error.WriteLineAsync(Usage); return 0; }
        if (o.Error is not null) { await Console.Error.WriteLineAsync(o.Error + "\n\n" + Usage); return 2; }
        if (o.ReadOnly) Environment.SetEnvironmentVariable("SPYGLASS_MCP_READONLY", "1");
        if (o.NoInput) Environment.SetEnvironmentVariable("SPYGLASS_MCP_NOINPUT", "1");

        if (o.Http) return await RunHttpAsync(o.Port, ct);

        // stdio: own hub if the port is free, otherwise forward to whoever serves it.
        var hub = new SpyglassHub(new SpyglassHubOptions { Port = o.Port });
        try
        {
            await hub.StartAsync(ct);
        }
        catch (Exception ex) when (ex is System.IO.IOException or System.Net.Sockets.SocketException or InvalidOperationException)
        {
            await hub.DisposeAsync();
            return await RunProxyAsync(o.Port, ct);
        }

        await Console.Error.WriteLineAsync($"spyglass mcp: MCP on stdio; apps connect to ws://127.0.0.1:{hub.Port}/agent");
        try
        {
            var host = Host.CreateApplicationBuilder();
            StderrLogging(host);
            host.Services.AddSingleton(hub);
            host.Services.AddMcpServer(Configure).WithStdioServerTransport().WithToolsFromAssembly(typeof(SpyglassMcp).Assembly);
            await host.Build().RunAsync(ct);
            return 0;
        }
        finally { await hub.DisposeAsync(); }
    }

    static async Task<int> RunHttpAsync(int port, CancellationToken ct)
    {
        var hub = new SpyglassHub(new SpyglassHubOptions { Port = port }.WithMcp());
        try { await hub.StartAsync(ct); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"spyglass mcp: cannot listen on 127.0.0.1:{port}: {ex.GetBaseException().Message}. Is Spyglass already running?");
            return 1;
        }
        await Console.Error.WriteLineAsync($"spyglass mcp: MCP at http://127.0.0.1:{hub.Port}/mcp · apps connect to ws://127.0.0.1:{hub.Port}/agent · Ctrl+C to stop");
        try { await Task.Delay(Timeout.Infinite, ct); } catch (OperationCanceledException) { }
        await hub.DisposeAsync();
        return 0;
    }

    /// <summary>A Spyglass already listens on the port: speak stdio to the coding agent and forward every call over HTTP.</summary>
    static async Task<int> RunProxyAsync(int port, CancellationToken ct)
    {
        var endpoint = new Uri($"http://127.0.0.1:{port}{SpyglassMcp.Path}");
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(new HttpClientTransport(new HttpClientTransportOptions { Endpoint = endpoint }), cancellationToken: ct);
        }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"spyglass mcp: port {port} is in use but {endpoint} is not a Spyglass MCP endpoint ({ex.GetBaseException().Message}). " +
                                               "Stop whatever uses the port, or pass --port with another one (and set SPYGLASS_PORT for the app).");
            return 1;
        }
        await Console.Error.WriteLineAsync($"spyglass mcp: forwarding to the Spyglass already running on port {port}");
        await using (client)
        {
            var host = Host.CreateApplicationBuilder();
            StderrLogging(host);
            host.Services.AddMcpServer(o =>
                {
                    Configure(o);
                    if (client.ServerInstructions is { } instructions) o.ServerInstructions = instructions;
                })
                .WithStdioServerTransport()
                .WithListToolsHandler(async (_, token) => new ListToolsResult
                {
                    Tools = (await client.ListToolsAsync(cancellationToken: token)).Select(t => t.ProtocolTool).ToList(),
                })
                .WithCallToolHandler(async (context, token) =>
                    await client.CallToolAsync(
                        context.Params!.Name,
                        context.Params.Arguments?.ToDictionary(kv => kv.Key, kv => (object?)kv.Value),
                        cancellationToken: token));
            await host.Build().RunAsync(ct);
        }
        return 0;
    }

    static void Configure(ModelContextProtocol.Server.McpServerOptions o)
    {
        o.ServerInfo = new Implementation { Name = "spyglass", Version = typeof(SpyglassMcp).Assembly.GetName().Version?.ToString(3) ?? "0.1.0" };
        o.ServerInstructions = SpyglassMcp.Instructions;
    }

    static void StderrLogging(HostApplicationBuilder host)
    {
        host.Logging.ClearProviders();
        host.Logging.AddConsole(c => c.LogToStandardErrorThreshold = LogLevel.Trace);   // stdout belongs to the MCP protocol
        host.Logging.SetMinimumLevel(LogLevel.Warning);
    }
}
