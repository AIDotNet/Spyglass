# Spyglass for Avalonia (Visual Studio)

XAML hot reload for running [Avalonia](https://avaloniaui.net) apps from Visual Studio, free and open source. The
same behaviour as the VS Code extension (`ide/vscode`), built on the out-of-process **VisualStudio.Extensibility** SDK.

- **Edit `.axaml` and the running app updates as you type** (300 ms after you stop), without saving or restarting.
- **Mistakes never break the app**: a XAML error appears in the Error List and as a squiggle at its line and column;
  the app keeps the last good UI until you fix it.
- "Open in editor" in the Spyglass window opens the file at the element's line here.

Requires **Visual Studio 2022 17.14 or later** (Visual Studio 2026 included), Windows x64 or Arm64.

## Setup

1. In the app: `AppBuilder…UseSpyglass()` (package `Spyglass.Agent`) and, for Debug builds, the `Spyglass.HotReload` package.
2. Install the Spyglass tool: `dotnet tool install -g Spyglass` (the extension starts `spyglass serve` in the background
   when nothing is running; the Spyglass window works too).
3. Run the app (F5, `dotnet run`, or **Extensions › Spyglass › Run Project with dotnet watch** to also apply C# edits).

**Extensions › Spyglass › Show Spyglass** (also under View › Other Windows) shows the connection, the apps that can
hot-reload, the latest reload result and the log; the log also goes to Output › Spyglass. The extension loads (and
connects) when you open the first `.axaml`/`.xaml` file or run a Spyglass command.

## Settings (Tools › Options › Spyglass)

| Setting | Default | |
|---|---|---|
| Send unsaved .axaml edits | on | Off closes the files on the hub; apps go back to the files on disk |
| Delay | `300` | ms without typing before an edit is sent; `0` = only on save |
| Port | `0` | Hub port; `0` = `SPYGLASS_PORT` or 29500 |
| Command | `spyglass` | Starts `spyglass serve` (an executable, or a `Spyglass.dll` run with `dotnet`) |
| Auto start | on | Start `spyglass serve` when no Spyglass is running |

Port, command and auto start apply the next time Visual Studio starts.

## How it works

The extension connects to the Spyglass hub (`ws://127.0.0.1:29500/ide`, loopback only, with a per-hub token read from
`~/.spyglass/hub-PORT.json`). It sends the buffers of `.axaml` files that a connected app compiled; the app's Spyglass
agent recompiles them with Avalonia's runtime XAML loader and re-populates the live controls in place.
Design: `design/hot-reload/` in the Spyglass repository.

| Project | |
|---|---|
| `Spyglass.VisualStudio.Core` | net8.0, no Visual Studio dependency: JSON-RPC over `ClientWebSocket`, `HubClient` (token, auto-start, reconnect 1 s → 10 s), `XamlBufferSync` (debounce, didOpen/didChange/didClose), status text |
| `Spyglass.VisualStudio` | The extension: `ITextViewOpenClosedListener`/`ITextViewChangedListener` for `**/*.axaml`/`**/*.xaml`, `DiagnosticsReporter` (Error List), `OpenDocumentAsync` for `editor/reveal`, settings, commands, a Remote UI tool window |
| `Spyglass.VisualStudio.Core.Tests` | xUnit: the client against a fake hub, and against the real `spyglass serve` + sample app |

## Develop

Any OS (.NET 10 SDK):

```bash
dotnet build Spyglass.VisualStudio.slnx      # on macOS/Linux the extension compiles, but no .vsix is made
dotnet test --project Spyglass.VisualStudio.Core.Tests
```

The real-hub tests use the repository's Debug build (`src/Spyglass.App` and `samples/Spyglass.Sample`, built with
`dotnet build` at the repo root) and ports 29541–29542; they are skipped when those binaries are missing.

Windows, Visual Studio 2022 17.14+ with the **Visual Studio extension development** workload:

- Open `Spyglass.VisualStudio.slnx`, set `Spyglass.VisualStudio` as the startup project, press **F5**: an experimental
  instance (`/rootSuffix Exp`) starts with the extension installed. Open an Avalonia solution there.
- `dotnet build Spyglass.VisualStudio/Spyglass.VisualStudio.csproj -c Release` (or `msbuild`) writes
  `Spyglass.VisualStudio/bin/Release/net8.0-windows8.0/Spyglass.VisualStudio.vsix`; double-click it to install.
