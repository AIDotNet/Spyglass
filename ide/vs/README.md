# Spyglass for Avalonia (Visual Studio)

XAML hot reload for running [Avalonia](https://avaloniaui.net) apps from Visual Studio, free and open source. The
same behaviour as the VS Code extension (`ide/vscode`), built on the out-of-process **VisualStudio.Extensibility** SDK.

- **Edit `.axaml` and the running app updates as you type** (300 ms after you stop), without saving or restarting.
- **Mistakes never break the app**: a XAML error appears in the Error List and as a squiggle at its line and column;
  the app keeps the last good UI until you fix it.
- "Open in editor" in the Spyglass window opens the file at the element's line here.

- **Design-time preview** of the active `.axaml`, without running the app, like the official Avalonia extension:
  **Extensions › Spyglass › Open Preview** (also View › Other Windows, the editor's right-click menu in `.axaml`
  files, and **Ctrl+Shift+Alt+P** inside an `.axaml`/`.xaml` editor; running it again focuses the open window). It updates as you type, is interactive (mouse,
  wheel, keyboard, text), zooms (Fit/25–400 %), switches Light/Dark for `Window` roots, and shows XAML errors in the
  Error List ("Spyglass Preview") and in a clickable error bar that jumps to the line and column. Independent of the hub.

- **Avalonia XAML language** for `.axaml`: syntax highlighting (elements, property elements, attributes, attached
  properties, `x:` directives, markup extensions and their arguments, comments, entities) in Visual Studio's markup colors,
  and **IntelliSense** from your project's own references, so it knows Avalonia, the component libraries you use, and your
  own controls and view models once built: elements (a library you have not declared yet gets its `xmlns:` added),
  properties, events, attached properties, enum and color values, markup extensions, `{Binding}` paths from `x:DataType`,
  resource keys, event handlers, `Setter` properties; completion documentation and hover from the packages' XML docs.
  Served by the same engine as VS Code, in `spyglass serve` (Spyglass 0.3+).

Requires **Visual Studio 2022 17.14 or later** (Visual Studio 2026 included), Windows x64 or Arm64.

## Setup

1. In the app: `AppBuilder…UseSpyglass()` (package `TKCode.Spyglass.Agent`) and, for Debug builds, the `TKCode.Spyglass.HotReload` package.
2. Install the Spyglass tool: `dotnet tool install -g TKCode.Spyglass` (the extension starts `spyglass serve` in the background
   when nothing is running; the Spyglass window works too).
3. Run the app (F5, `dotnet run`, or **Extensions › Spyglass › Run Project with dotnet watch** to also apply C# edits).

**Extensions › Spyglass › Show Spyglass** (also under View › Other Windows) shows the connection, the apps that can
hot-reload, the latest reload result and the log; the log also goes to Output › Spyglass. The extension loads (and
connects) when you open the first `.axaml`/`.xaml` file or run a Spyglass command.

### Preview

The preview launches the `Avalonia.Designer.HostApp` that ships in your project's Avalonia package (found with
`dotnet msbuild -getProperty:AvaloniaPreviewerNetCoreToolPath …`), so it needs an **executable Avalonia project that is
built** (the window offers "Build project"). It is restarted when the project is rebuilt or crashes ("Restart"), and
stopped when the window is closed.

**The preview is a floating WPF window, not a docked tool window.** VisualStudio.Extensibility's Remote UI (the only way
an out-of-process extension can add a tool window) cannot show a dynamic bitmap, and reports neither the pane size nor
pointer coordinates. So the extension opens a plain WPF window on its own thread in the extension host process, owned by
the Visual Studio main window (stays above it, minimizes with it) and placed on the right third of it the first time.
Dock it by snapping it with Windows (Win+←/→) or on a second monitor.

- The window size becomes the previewer's viewport (debounced 100 ms) and its DPI follows the monitor scaling, so the
  picture is sharp on HiDPI. A `Window` renders at its own `Width`/`Height`, a `UserControl` at `d:DesignWidth`/
  `d:DesignHeight`; the viewport only bounds the layout (the picture is not stretched to the pane).
- Zoom: Fit (shrinks to the pane, never enlarges), a percentage, − / +, or Ctrl+wheel.
- Theme (Default/Light/Dark) works for `Window` roots (it adds `RequestedThemeVariant` to the root tag, which the
  previewer rejects on other roots).
- Click the picture to give it the keyboard; keys and typed text go to the previewed UI.
- The status line shows `Updating…`, then `Updated · N ms`, or the XAML error (click it to jump to the line and column).
- Editing or saving any other `.axaml` of the project (App.axaml, styles, resource dictionaries) re-renders the active
  view. The previewer reads styles and resources from the *built* assembly, so such changes show after a build; a
  rebuild of the app restarts the previewer by itself and keeps the file, zoom, theme and window size.
  App.axaml, resource dictionaries and styles files are never shown by themselves.
- The window opens on the right third of the Visual Studio window and follows it (move, resize, maximize) until you move
  or resize the preview window yourself. With **Open the preview window automatically** on (`previewAutoOpen`), opening an
  `.axaml` shows it (without taking the keyboard from the editor).

### Avalonia XAML language

The extension registers an `axaml` document type for `.axaml` files with two out-of-process parts:

- **Highlighting**: a classification tagger (`AxamlClassificationTagger.cs`) over `AxamlLexer` (in `Spyglass.VisualStudio.Core`,
  unit-tested). Out-of-process extensions cannot define colors, so XAML constructs use Visual Studio's built-in
  classifications: elements = *markup node*, attributes and property elements = *markup attribute*, values = *markup
  attribute value*, attached-property owners and markup extension names = *type*, `x:` directives = *keyword*.
- **IntelliSense**: a `LanguageServerProvider` (`AxamlLanguageServerProvider.cs`). The LSP server (`AxamlLanguageServer`,
  Core) runs inside the extension and is handed to Visual Studio through an in-memory pipe; it forwards completion,
  completion resolve and hover to the hub's `xaml/*` requests (it starts `spyglass serve` like hot reload does). The first
  completion in a project waits for its types to load (MSBuild; a few seconds) and returns an incomplete list until then.

Both are preview APIs of VisualStudio.Extensibility (`VSEXTPREVIEW_LSP`, `VSEXTPREVIEW_TAGGERS`). If Avalonia's own
"Avalonia for Visual Studio" extension is installed, it opens `.axaml` in Visual Studio's XML editor; then that editor's
coloring and Avalonia's IntelliSense apply instead (hot reload and the preview work either way).

## Settings (Tools › Options › Spyglass)

| Setting | Default | |
|---|---|---|
| Send unsaved .axaml edits | on | Off closes the files on the hub; apps go back to the files on disk |
| Delay | `300` | ms without typing before an edit is sent; `0` = only on save |
| Preview | on | Show the design-time preview (Open Preview) |
| Preview delay | `300` | ms without typing before the preview updates |
| Open the preview automatically | off | Opening an `.axaml` shows the preview window |
| Preview zoom / theme | `fit` / `default` | Remembered by the window's zoom and theme controls (`fit` or `100%`; `default`, `light`, `dark`) |
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
| `Spyglass.VisualStudio.Core` | net8.0, no Visual Studio dependency: JSON-RPC over `ClientWebSocket`, `HubClient` (token, auto-start, reconnect 1 s → 10 s), `XamlBufferSync` (debounce, didOpen/didChange/didClose), status text; `Preview/`: project properties, previewer host session (`Avalonia.Remote.Protocol` BSON), `PreviewController` (debounce, restart on rebuild, theme, diagnostics), zoom/viewport/input mapping |
| `Spyglass.VisualStudio` | The extension: `ITextViewOpenClosedListener`/`ITextViewChangedListener` for `**/*.axaml`/`**/*.xaml`, `DiagnosticsReporter` (Error List), `OpenDocumentAsync` for `editor/reveal`, settings, commands, a Remote UI status tool window, and the WPF preview window (`PreviewWindow`, `PreviewWindowHost`) |
| `Spyglass.VisualStudio.Core.Tests` | xUnit: the client against a fake hub, and against the real `spyglass serve` + sample app; the preview against the real `Avalonia.Designer.HostApp` (frames, error line/column, restart) |

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
