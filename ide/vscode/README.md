# Spyglass for Avalonia (VS Code)

XAML hot reload for running [Avalonia](https://avaloniaui.net) apps and a side-by-side design-time preview, free and open source.

- **Edit `.axaml` and the running app updates as you type** (300 ms after you stop), without saving and without restarting.
  The view keeps its DataContext, typed text, selection, scroll position and focus.
- **Mistakes never break the app**: a XAML error shows up as a squiggle at the right line and column; the app keeps
  the last good UI until you fix it.
- Works with any number of running apps; each gets the files it compiled.
- "Open in editor" in the Spyglass window jumps to the element's XAML line here.

## Avalonia XAML language

- **Syntax highlighting** for `.axaml`: markup extensions, property elements (`Grid.RowDefinitions`), attached properties
  (`Grid.Row`), `x:` directives and `using:` namespaces get their own colors on top of the XML grammar, in any theme.
- **IntelliSense** from your project's own references, so it knows Avalonia *and* the component libraries you use (and your
  own controls and view models once the project is built): elements, properties, events, attached properties, enum and color
  values, markup extensions, `{Binding}` paths from `x:DataType`, resource keys, event handlers from the code-behind, `Setter`
  properties of a style. Picking an element of a library you have not declared yet adds its `xmlns:` to the root. Hover shows
  the documentation. Needs Spyglass 0.2.2+ (`spyglass serve`, started automatically) and a restored project.
- Works next to Avalonia's own extension (same `axaml` language id).

## Design-time preview

Open an `.axaml` file and press **Ctrl+Alt+Shift+P** (**Cmd+Alt+Shift+P** on macOS), click the preview button in the
editor title bar, or use the editor's right-click menu / **Spyglass: Open Preview to the Side**. The preview shows beside
the editor without running your app. Running the command again focuses the existing preview.

- **Live**: it updates from the **unsaved** buffer 300 ms after you stop typing, follows the active `.axaml`, and also
  refreshes when another `.axaml` of the project (App.axaml, styles, dictionaries) is edited or saved. The toolbar shows
  *Updating…*, *Updated · N ms* or *Error*. It restarts its process by itself when you rebuild, keeping file and zoom. It runs from a private copy of the build output in the temp folder (`spyglass-preview`), so it does not lock `bin/` and you can build while it is open.
- **Errors** appear as a squiggle at the right line and column (source `Spyglass Preview`) and in an error bar under the
  preview; click the bar to jump to the error. The last good picture stays.
- **Zoom**: Fit, 25–400 %, Ctrl+wheel. **Theme**: Default / Light / Dark (adds `RequestedThemeVariant` to a `Window` root;
  a `UserControl` root is previewed unchanged).
- Mouse, wheel and keyboard input are forwarded, so the preview is interactive.
- Uses the previewer host that ships in your project's own Avalonia package, so the version always matches. The project
  must be an application project that has been built once (the preview offers a **Build project** button).

Limits: only `Design.DataContext` data is available, as in the official previewer (use hot reload, below, for real data).
The previewer host loads styles, resources and other XAML from the *compiled* assembly, so an edit to App.axaml or a
resource dictionary changes other files' preview only after a rebuild (which restarts the preview automatically).
Without a `Background` of its own, a `Window` is drawn without its theme's background; with a Light/Dark theme selected
the preview adds `Background="{DynamicResource SystemRegionBrush}"` so the variant is visible.

| Setting | Default | |
|---|---|---|
| `spyglass.preview.enabled` | `true` | Enable the preview |
| `spyglass.preview.autoOpen` | `false` | Open the preview to the side when an `.axaml` file is opened |
| `spyglass.preview.delay` | `300` | ms without typing before the preview updates; `0` = only on save |
| `spyglass.preview.zoom` | `fit` | `fit`, `25%` … `400%` |
| `spyglass.preview.theme` | `default` | `default`, `light`, `dark` |

Commands: **Open Preview to the Side**, **Restart Preview**, **Build Project for Preview**.

## Setup (hot reload)

1. In the app: `AppBuilder…UseSpyglass()` (package `TKCode.Spyglass.Agent`) and, for Debug builds, the `TKCode.Spyglass.HotReload` package.
2. Install the Spyglass tool: `dotnet tool install -g TKCode.Spyglass` (the extension starts `spyglass serve` in the background
   when nothing is running; the Spyglass window works too).
3. Run the app (F5, `dotnet run`, or **Spyglass: Run Project with dotnet watch** to also apply C# edits).

The status bar shows the connected apps (⚡), a XAML error (red), or that Spyglass is not running.

## Settings

| Setting | Default | |
|---|---|---|
| `spyglass.hotReload.enabled` | `true` | Send unsaved `.axaml` edits to running apps |
| `spyglass.hotReload.delay` | `300` | ms without typing before an edit is sent; `0` = only on save |
| `spyglass.port` | `0` | Hub port; `0` = `SPYGLASS_PORT` or 29500 |
| `spyglass.command` | `spyglass` | Command used to start `spyglass serve` (an executable or `Spyglass.dll`) |
| `spyglass.autoStart` | `true` | Start `spyglass serve` when no Spyglass is running |

## How it works

The extension connects to the Spyglass hub (`ws://127.0.0.1:29500/ide`, loopback only, with a per-hub token read from
`~/.spyglass/hub-PORT.json`). It sends the buffers of `.axaml` files that a connected app compiled; the app's Spyglass
agent recompiles them with Avalonia's runtime XAML loader and re-populates the live controls in place.
Design: `design/hot-reload/` and `design/preview/` in the Spyglass repository.

The preview talks the Avalonia remote-preview protocol (BSON over TCP, `src/preview/`) to `Avalonia.Designer.HostApp`,
which it starts with `dotnet exec` using the properties `dotnet msbuild -getProperty` reports for the project.

## Develop

```bash
npm install
npm test            # unit tests (HubClient against a fake hub)
npm run test:e2e    # real spyglass serve + sample app + VS Code, incl. the preview (build the .NET solution first)
npm run package     # .vsix
```
