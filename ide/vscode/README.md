# Spyglass for Avalonia (VS Code)

XAML hot reload for running [Avalonia](https://avaloniaui.net) apps, free and open source.

- **Edit `.axaml` and the running app updates as you type** (300 ms after you stop), without saving and without restarting.
  The view keeps its DataContext, typed text, selection, scroll position and focus.
- **Mistakes never break the app**: a XAML error shows up as a squiggle at the right line and column; the app keeps
  the last good UI until you fix it.
- Works with any number of running apps; each gets the files it compiled.
- "Open in editor" in the Spyglass window jumps to the element's XAML line here.

## Setup

1. In the app: `AppBuilder…UseSpyglass()` (package `Spyglass.Agent`) and, for Debug builds, the `Spyglass.HotReload` package.
2. Install the Spyglass tool: `dotnet tool install -g Spyglass` (the extension starts `spyglass serve` in the background
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
Design: `design/hot-reload/` in the Spyglass repository.

## Develop

```bash
npm install
npm test            # unit tests (HubClient against a fake hub)
npm run test:e2e    # real spyglass serve + sample app + VS Code (build the .NET solution first)
npm run package     # .vsix
```
