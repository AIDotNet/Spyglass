using Spyglass.App.Core;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;

namespace Spyglass.Mcp;

/// <summary>
/// <c>spyglass serve</c>: the hub without the GUI (design/hot-reload/README.md §3, D10). Debugged apps connect, XAML hot
/// reload watches their source files, and MCP is served at /mcp. IDE extensions start it when nothing listens on the port.
/// Prints one line per connection and per reload to stderr.
/// </summary>
public static class ServeCli
{
    const string Usage = """
        spyglass serve [--port N] [--quiet] [--no-watch]

          --port N   port debugged apps (and IDE extensions) connect to (default: SPYGLASS_PORT or 29500)
          --quiet    do not print connections and reloads
          --no-watch don't watch .axaml files (apps run under `dotnet watch` get saved XAML from it; editor
                     extensions still push unsaved edits)

        Runs until Ctrl+C. Apps started with UseSpyglass() connect automatically; with the Spyglass.HotReload package,
        saving a .axaml file updates the running app. MCP is available at http://127.0.0.1:N/mcp.
        """;

    public static async Task<int> RunAsync(IReadOnlyList<string> args, CancellationToken ct = default)
    {
        var port = SpyglassProtocol.Port;
        var quiet = false;
        var watch = true;
        for (var i = 0; i < args.Count; i++)
        {
            switch (args[i])
            {
                case "--port" when i + 1 < args.Count && int.TryParse(args[i + 1], out var p) && p is > 0 and < 65536: port = p; i++; break;
                case "--quiet": quiet = true; break;
                case "--no-watch": watch = false; break;
                case "-h" or "--help": await Console.Error.WriteLineAsync(Usage); return 0;
                default: await Console.Error.WriteLineAsync($"Unknown argument '{args[i]}'.\n\n{Usage}"); return 2;
            }
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Ctrl+C, and SIGTERM from IDE extensions / scripts. Without our own SIGTERM handler, Kestrel's console lifetime
        // takes the signal, stops only the web app, and the process stays alive.
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
        using var sigterm = System.Runtime.InteropServices.PosixSignalRegistration.Create(
            System.Runtime.InteropServices.PosixSignal.SIGTERM, c => { c.Cancel = true; stop.Cancel(); });

        await using var hub = new SpyglassHub(new SpyglassHubOptions { Port = port }.WithMcp());
        hub.HotReload.WatchFiles = watch;
        try { await hub.StartAsync(stop.Token); }
        catch (Exception ex)
        {
            await Console.Error.WriteLineAsync($"spyglass serve: cannot listen on 127.0.0.1:{port}: {ex.GetBaseException().Message}. Is Spyglass already running?");
            return 1;
        }

        void Say(string s) { if (!quiet) Console.Error.WriteLine($"{DateTime.Now:HH:mm:ss} {s}"); }
        Say($"spyglass serve: apps connect to ws://127.0.0.1:{hub.Port}/agent · MCP at http://127.0.0.1:{hub.Port}/mcp · Ctrl+C to stop");
        hub.SessionConnected += s =>
        {
            var hr = s.Has(Capabilities.HotReload) ? "hot reload" : "no hot reload (add the Spyglass.HotReload package)";
            Say($"+ {s.App.Name} (pid {s.App.Pid}) connected · {hr}");
            s.HotReloadManifestChanged += m =>
                Say(m.Available ? $"  {s.App.Name}: watching {m.Documents.Count} XAML file(s)" : $"  {s.App.Name}: hot reload unavailable: {m.Reason}");
        };
        hub.SessionDisconnected += s => Say($"- {s.App.Name} (pid {s.App.Pid}) disconnected");
        hub.Ide.ConnectionsChanged += () =>
        {
            var ides = string.Join(", ", hub.Ide.Connections.Select(i => $"{i.Hello.Ide} {i.Hello.Version}"));
            Say($"IDE extensions: {(ides.Length > 0 ? ides : "none")}");
        };
        hub.HotReload.Reloaded += e => Say(Describe(e));

        try { await Task.Delay(Timeout.Infinite, stop.Token); } catch (OperationCanceledException) { }
        return 0;
    }

    /// <summary>One line per reload (shared with the IDE status bar and the GUI).</summary>
    public static string Describe(HotReloadEvent e) => IdeEndpoint.Describe(e);
}
