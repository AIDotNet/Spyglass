// VS Code front end for Spyglass XAML hot reload (design/hot-reload/README.md §8): sends unsaved .axaml buffers to the
// hub (debounced), shows XAML diagnostics and reload status, reveals files for Spyglass's "Open in editor".
import * as path from "path";
import * as vscode from "vscode";
import { HubClient, HotReloadStatus, XamlDiagnostics } from "./hubClient";

const isXaml = (d: vscode.TextDocument): boolean =>
  d.uri.scheme === "file" && /\.(axaml|xaml)$/i.test(d.fileName);

export function activate(context: vscode.ExtensionContext): HubClient {
  const output = vscode.window.createOutputChannel("Spyglass");
  const diagnostics = vscode.languages.createDiagnosticCollection("spyglass");
  const statusBar = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  statusBar.command = "spyglass.showOutput";
  context.subscriptions.push(output, diagnostics, statusBar);

  const cfg = () => vscode.workspace.getConfiguration("spyglass");
  const log = (line: string) => output.appendLine(`[${new Date().toLocaleTimeString()}] ${line}`);

  const client = new HubClient({
    port: cfg().get<number>("port", 0),
    ide: "vscode",
    version: vscode.version,
    workspaceFolders: () => (vscode.workspace.workspaceFolders ?? []).map((f) => f.uri.fsPath),
    command: cfg().get<string>("command", "spyglass"),
    autoStart: cfg().get<boolean>("autoStart", true),
    log,
  });

  // ---------- buffers → hub ----------

  const timers = new Map<string, NodeJS.Timeout>();
  const opened = new Set<string>();   // files announced with didOpen in the current connection
  const enabled = () => cfg().get<boolean>("hotReload.enabled", true);
  const delay = () => cfg().get<number>("hotReload.delay", 300);

  const announce = (d: vscode.TextDocument) => {
    if (!enabled() || !client.isConnected || !isXaml(d) || !client.canReload(d.fileName) || opened.has(d.fileName)) return;
    opened.add(d.fileName);
    client.didOpen(d.fileName, d.version, d.getText());
    if (d.isDirty) client.didChange(d.fileName, d.version, d.getText());   // reconnected with unsaved edits
  };

  const send = (d: vscode.TextDocument) => {
    if (d.isClosed) return;
    announce(d);
    if (opened.has(d.fileName)) client.didChange(d.fileName, d.version, d.getText());
  };

  context.subscriptions.push(
    vscode.workspace.onDidOpenTextDocument(announce),
    vscode.workspace.onDidChangeTextDocument((e) => {
      const d = e.document;
      if (!enabled() || !isXaml(d) || e.contentChanges.length === 0 || !client.canReload(d.fileName)) return;
      const ms = delay();
      if (ms <= 0) return;   // only on save
      clearTimeout(timers.get(d.fileName));
      timers.set(d.fileName, setTimeout(() => { timers.delete(d.fileName); send(d); }, ms));
    }),
    vscode.workspace.onDidSaveTextDocument((d) => {
      if (enabled() && isXaml(d) && client.canReload(d.fileName) && delay() <= 0) send(d);
    }),
    vscode.workspace.onDidCloseTextDocument((d) => {
      clearTimeout(timers.get(d.fileName));
      if (opened.delete(d.fileName)) client.didClose(d.fileName);
    }),
  );

  // ---------- hub → editor ----------

  client.on("connected", () => {
    opened.clear();
    vscode.workspace.textDocuments.forEach(announce);
  });
  client.on("status", (s: HotReloadStatus | undefined) => {
    if (s) vscode.workspace.textDocuments.forEach(announce);   // new apps may make more open files reloadable
    else { opened.clear(); diagnostics.clear(); }
    renderStatus();
  });
  client.on("state", renderStatus);
  client.on("diagnostics", (d: XamlDiagnostics) => {
    const uri = vscode.Uri.file(d.path);
    const doc = vscode.workspace.textDocuments.find((t) => t.uri.fsPath === uri.fsPath);
    // A result for an older version of the buffer than the editor has now is still the best we know; show it.
    diagnostics.set(uri, d.items.map((i) => {
      const line = Math.max(0, (i.line ?? 1) - 1);
      const col = Math.max(0, (i.column ?? 1) - 1);
      const range = doc && line < doc.lineCount
        ? doc.getWordRangeAtPosition(new vscode.Position(line, col), /[\w:.{}-]+/) ?? doc.lineAt(line).range
        : new vscode.Range(line, col, line, col + 1);
      const diag = new vscode.Diagnostic(range, i.message, i.severity === "error" ? vscode.DiagnosticSeverity.Error
        : i.severity === "warning" ? vscode.DiagnosticSeverity.Warning : vscode.DiagnosticSeverity.Information);
      diag.source = "Spyglass hot reload";
      if (i.code) diag.code = i.code;
      return diag;
    }));
    log(d.summary);
    renderStatus(d.summary, d.items.every((i) => i.severity !== "error"));
  });
  client.revealHandler = async (r) => {
    const doc = await vscode.workspace.openTextDocument(r.path);
    const pos = new vscode.Position(Math.max(0, r.line - 1), Math.max(0, r.column - 1));
    await vscode.window.showTextDocument(doc, { selection: new vscode.Range(pos, pos), preview: false });
    return true;
  };

  let lastSummary: string | undefined;
  let lastOk = true;
  function renderStatus(summary?: unknown, ok?: boolean): void {
    if (typeof summary === "string") { lastSummary = summary; lastOk = ok ?? true; }
    const s = client.status;
    if (!enabled()) {
      statusBar.text = "$(circle-slash) Spyglass";
      statusBar.tooltip = "XAML hot reload is off (Spyglass: Toggle XAML Hot Reload)";
    } else if (client.state === "starting" || client.state === "connecting") {
      statusBar.text = "$(sync~spin) Spyglass";
      statusBar.tooltip = client.state === "starting" ? "Starting spyglass serve…" : "Connecting to Spyglass…";
    } else if (!client.isConnected) {
      statusBar.text = "$(debug-disconnect) Spyglass";
      statusBar.tooltip = `Spyglass is not running on port ${client.port}. Click for details.`;
    } else {
      const apps = (s?.apps ?? []).filter((a) => a.hotReload && a.available);
      const icon = !lastOk ? "$(error)" : apps.length > 0 ? "$(zap)" : "$(eye)";
      statusBar.text = `${icon} Spyglass${apps.length ? ": " + apps.map((a) => a.name).join(", ") : ""}`;
      const lines = (s?.apps ?? []).map((a) =>
        `${a.name} (pid ${a.pid}): ${!a.hotReload ? "no hot reload (add Spyglass.HotReload)" : a.available ? `${a.documents} XAML file(s)` : `unavailable: ${a.reason}`}`);
      if (lines.length === 0) lines.push("No app connected. Run an app that calls UseSpyglass().");
      if (lastSummary ?? s?.last) lines.push("", lastSummary ?? s!.last!);
      statusBar.tooltip = lines.join("\n");
    }
    statusBar.backgroundColor = client.isConnected && !lastOk ? new vscode.ThemeColor("statusBarItem.errorBackground") : undefined;
    statusBar.show();
  }

  // ---------- commands ----------

  context.subscriptions.push(
    vscode.commands.registerCommand("spyglass.showOutput", () => output.show(true)),
    vscode.commands.registerCommand("spyglass.reconnect", () => client.reconnect()),
    vscode.commands.registerCommand("spyglass.toggleHotReload", async () => {
      await cfg().update("hotReload.enabled", !enabled(), vscode.ConfigurationTarget.Global);
    }),
    vscode.commands.registerCommand("spyglass.openSpyglass", () => {
      const cmd = cfg().get<string>("command", "spyglass");
      const term = vscode.window.createTerminal({ name: "Spyglass", hideFromUser: true });
      term.sendText(cmd.toLowerCase().endsWith(".dll") ? `dotnet "${cmd}"` : cmd);
    }),
    vscode.commands.registerCommand("spyglass.runWithDotnetWatch", async () => {
      const projects = await vscode.workspace.findFiles("**/*.csproj", "**/{bin,obj,node_modules}/**", 50);
      if (projects.length === 0) { vscode.window.showWarningMessage("No .csproj in this workspace."); return; }
      const pick = projects.length === 1 ? projects[0] : (await vscode.window.showQuickPick(
        projects.map((p) => ({ label: path.basename(p.fsPath), description: vscode.workspace.asRelativePath(p), uri: p })),
        { placeHolder: "Project to run with dotnet watch (C# edits apply without restarting)" }))?.uri;
      if (!pick) return;
      const term = vscode.window.createTerminal({ name: `dotnet watch: ${path.basename(pick.fsPath)}` });
      term.show();
      term.sendText(`dotnet watch run --project "${pick.fsPath}"`);
    }),
    vscode.workspace.onDidChangeConfiguration((e) => {
      if (e.affectsConfiguration("spyglass.hotReload.enabled")) {
        if (enabled()) { opened.clear(); vscode.workspace.textDocuments.forEach(announce); }
        else { for (const f of opened) client.didClose(f); opened.clear(); diagnostics.clear(); }
        renderStatus();
      }
      if (e.affectsConfiguration("spyglass.port") || e.affectsConfiguration("spyglass.command") || e.affectsConfiguration("spyglass.autoStart"))
        vscode.window.showInformationMessage("Reload the window to apply the new Spyglass connection settings.");
    }),
    { dispose: () => client.stop() },
  );

  renderStatus();
  client.start();
  return client;
}

export function deactivate(): void {}
