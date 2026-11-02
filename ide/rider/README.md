# Spyglass for Avalonia (Rider)

XAML hot reload for running [Avalonia](https://avaloniaui.net) apps in JetBrains Rider, free and open source.

- **Edit `.axaml` and the running app updates as you type** (300 ms after you stop), without saving and without restarting.
  The view keeps its DataContext, typed text, selection, scroll position and focus.
- **Mistakes never break the app**: a XAML error is underlined at its line and column (with a gutter icon and an error
  stripe mark); the app keeps the last good UI until you fix it.
- Works with any number of running apps; each gets the files it compiled.
- "Open in editor" in the Spyglass window jumps to the element's XAML line here.
- Coexists with Rider's built-in Avalonia support and AvaloniaRider: no completion, no previewer, no language service.

## Setup

1. In the app: `AppBuilder…UseSpyglass()` (package `Spyglass.Agent`) and, for Debug builds, the `Spyglass.HotReload` package.
2. Install the Spyglass tool: `dotnet tool install -g Spyglass` (the plugin starts `spyglass serve` in the background
   when nothing is running; the Spyglass window works too).
3. Run the app (Run/Debug, `dotnet run`, or **Tools › Spyglass › Run Project with dotnet watch** to also apply C# edits).

The status bar shows the connected apps (⚡), a XAML error (red ✗), or that Spyglass is offline; click it for the
actions. **Tools › Spyglass**: Toggle XAML Hot Reload, Open Spyglass, Run Project with dotnet watch, Reconnect, Show Log.
The plugin connects in projects that contain `.axaml` files (or once one is opened).

## Settings (Settings › Tools › Spyglass)

| Setting | Default | |
|---|---|---|
| Send unsaved `.axaml` edits | on | XAML hot reload on/off |
| Delay | `300` | ms without typing before an edit is sent; `0` = only on save |
| Port | `0` | Hub port; `0` = `SPYGLASS_PORT` or 29500 |
| Command | `spyglass` | Command used to start `spyglass serve` (an executable or `Spyglass.dll`) |
| Start spyglass serve | on | Start `spyglass serve` when no Spyglass is running |

## How it works

One connection per open project to the Spyglass hub (`ws://127.0.0.1:29500/ide`, loopback only, with a per-hub token read
from `~/.spyglass/hub-PORT.json`). The plugin sends the buffers of `.axaml` files that a connected app compiled; the app's
Spyglass agent recompiles them with Avalonia's runtime XAML loader and re-populates the live controls in place. It is a
frontend-only IntelliJ Platform plugin (no ReSharper backend). Design: `design/hot-reload/` in the Spyglass repository.

Code: `dev.spyglass.rider.hub` is the IntelliJ-independent client (JSON-RPC over `java.net.http.WebSocket`, Gson);
`dev.spyglass.rider` is the IDE glue (document listener, markup highlighting, status bar widget, actions, settings).

## Develop

Needs a JDK 17+ to run Gradle (Rider's bundled JBR works:
`export JAVA_HOME=/Applications/Rider.app/Contents/jbr/Contents/Home`); Gradle downloads a JDK 21 to compile with.

```bash
./gradlew buildPlugin                     # build/distributions/spyglass-rider-0.1.0.zip (Settings › Plugins › Install from Disk)
./gradlew test                            # unit tests (HubClient against a fake hub)
SPYGLASS_REPO=$PWD/../.. ./gradlew test   # + real spyglass serve and sample app (build the .NET solution first)
./gradlew runIde                          # a sandbox Rider with the plugin
```

The build downloads Rider (`platformVersion` in `gradle.properties`, about 2 GB); add
`-PplatformLocalPath=/Applications/Rider.app` to build against an installed Rider instead.
