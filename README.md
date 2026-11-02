# Spyglass

Open-source, out-of-process debugging tool for Avalonia UI apps (like browser DevTools), with a free MCP server for AI coding agents. Product design lives in [`design/`](design/README.md).

## Projects

| Path | What |
|---|---|
| `src/Spyglass.Protocol` | Wire contract (JSON-RPC 2.0 over WebSocket), shared by agent and client |
| `src/Spyglass.Agent` | In-app agent: `AppBuilder.UseSpyglass()` (public Avalonia APIs only) |
| `src/Spyglass.Agent.Avalonia11` | The same agent built for Avalonia 11.2+ apps (`Spyglass.Agent.Avalonia11` package; same namespace, no XAML source locations) |
| `src/Spyglass.Agent.Advanced` | Advanced agent features on Avalonia private APIs (`UseSpyglassAdvanced()`, pinned to Avalonia 12.1.4) |
| `src/Spyglass.App.Core` | Hub that agents connect to (`ws://127.0.0.1:29500/agent`) and the typed session API |
| `src/Spyglass.Mcp` | MCP server mounted at `http://127.0.0.1:29500/mcp` |
| `src/Spyglass.App` | The Spyglass desktop client (Avalonia 12.1.4 + TKCode.Arc) |
| `samples/Spyglass.Sample` | A small app to debug |
| `tests/` | Protocol tests and end-to-end tests (headless app + hub + MCP client) |
| `spike/` | Throwaway prototypes behind the design decisions |

## Try it

```bash
dotnet run --project src/Spyglass.App            # 1. start Spyglass (listens on 127.0.0.1:29500)
dotnet run --project samples/Spyglass.Sample     # 2. start the sample app; it connects automatically
claude mcp add --transport http spyglass http://127.0.0.1:29500/mcp   # 3. optional: give your coding agent access
```

## Install as a tool

```bash
dotnet tool install -g Spyglass        # then: `spyglass` (GUI) and `spyglass mcp` (headless MCP server)
```

`spyglass mcp` is for editors that launch an MCP server process (stdio), CI and remote machines. If a Spyglass is already
running on the port it forwards to it, so your coding agent and the GUI share one session:

```bash
claude mcp add spyglass -- spyglass mcp          # stdio; add --read-only / --no-input to restrict what the agent can do
spyglass mcp --http --port 29500                 # or serve MCP over HTTP (CI / remote)
```

## Debug your own app

```csharp
AppBuilder.Configure<App>()
    .UsePlatformDetect()
#if DEBUG
    .UseSpyglass()
#endif
    ;
```

In the running app, **Ctrl+Shift+I** (macOS **Cmd+Option+I**) brings Spyglass to the front, starting it if needed;
**Ctrl+Shift+C** (macOS **Cmd+Shift+C**) picks an element, like Chrome DevTools. To let the app start Spyglass, put the
`spyglass` command on PATH or set `SPYGLASS_CLIENT` (e.g. to `src/Spyglass.App/bin/Debug/net10.0/Spyglass`).

```csharp
.UseSpyglass(o =>
{
    o.OpenGesture = new KeyGesture(Key.F12);   // or null to disable
    o.ClientPath = "/path/to/Spyglass";
})
```

## Settings

Theme, accent colour, animations, port, Network capture options and the editor used by "Open in editor" are in the
**Settings** page. They apply immediately and are stored in `~/Library/Application Support/Spyglass/settings.json`
(`%APPDATA%\Spyglass\settings.json` on Windows; override with `SPYGLASS_SETTINGS`).

## Test

```bash
dotnet test --project tests/Spyglass.Protocol.Tests
dotnet test --project tests/Spyglass.IntegrationTests
```
