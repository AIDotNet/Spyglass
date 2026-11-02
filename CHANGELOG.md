# Changelog

All notable changes to Spyglass. Versions follow the git tags; the NuGet packages, the IDE extensions and the GitHub release
are published from the tag by `.github/workflows/release.yml`.

## 0.1.3

### Added
- **Elements › Screenshot**: save a PNG (2x) of the selected element from a button next to *Open*, or from the command palette
  ("Screenshot the selected element"). A zero-size or hidden element explains why it cannot be captured.
- **Getting started** section in the README (own Avalonia 12 app, Avalonia 11.2+, no code changes with `spyglass run`,
  remote apps with a pairing code, MCP, troubleshooting) and a Simplified Chinese README (`README.zh-CN.md`).

### Changed
- Scroll bars are always visible (macOS overlay scroll bars hid themselves and were easy to miss in long lists).

## 0.1.2

### Changed
- NuGet packages are published as `TKCode.Spyglass.*` (`TKCode.Spyglass` tool, `TKCode.Spyglass.Agent`,
  `TKCode.Spyglass.Agent.Avalonia11`, `TKCode.Spyglass.HotReload`); README feature screenshots.

## 0.1.1

### Added
- **Command palette** (Cmd/Ctrl+K or the search button): pages, actions (pick an element, reload/switch the tree,
  record/clear the network log), theme and language, and every named element of the connected app. Cmd/Ctrl+1…9 jump to a page.
- **Interface language**: English, 简体中文, 繁體中文 (Settings › Appearance), switched live; `System` follows the OS.
- **Editable client shortcuts** (Settings › Keyboard shortcuts) with conflict warnings and per-row / global reset.
- **Other devices** (off by default): agents on other machines can connect with a 6-digit pairing code. Only `/agent` is
  reachable from the network (MCP and everything else stay local, enforced with 403); wrong codes are throttled per address.
  The connection is plain `ws://`, so use trusted networks only.
- **Attach without code changes**: `spyglass run <command>` / `DOTNET_STARTUP_HOOKS` with the Spyglass startup hook
  (Avalonia 11.2+ and 12; `SPYGLASS_ATTACH=advanced` for the Advanced agent on Avalonia 12.1.4).
- **Avalonia 11.2+ agent** (`TKCode.Spyglass.Agent.Avalonia11`): the same `UseSpyglass()`, without XAML source locations.
- **XAML and code hot reload**, IDE endpoint, Visual Studio / Rider / VS Code extensions, `spyglass serve` (headless hub).
- **Settings page**: theme, accent colour, motion, port, Elements and Network defaults, editor used by *Open in editor*
  (VS Code, Rider, Visual Studio, system, custom command), events buffer size.
- *Open* buttons on Elements and Bindings jump to the XAML location in your editor.

### Changed
- Settings now live in `~/.spyglass/` (`SPYGLASS_HOME` moves it); settings from the old OS app-data folder are copied once.
- The Performance page explains an idle app (Avalonia renders nothing when nothing changes) instead of showing zeros.

### Fixed
- LAN: an unauthorized agent now reliably receives its error before the connection is dropped (grace 300 ms → 2 s).

## 0.1.0

First public version: out-of-process debugger for Avalonia 12 apps (Elements, Events, Network, Resources, Bindings, Logs,
Performance) built on TKCode.Arc, with a free MCP server for AI coding agents.
