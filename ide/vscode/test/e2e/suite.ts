// Runs inside the VS Code extension host (see run.ts).
import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import type { HubClient } from "../../src/hubClient";

const port = Number(process.env.SPYGLASS_PORT);

async function until<T>(what: string, f: () => T | undefined | false | Promise<T | undefined | false>, ms = 20000): Promise<T> {
  const end = Date.now() + ms;
  for (;;) {
    const v = await f();
    if (v) return v as T;
    if (Date.now() > end) throw new Error(`timed out: ${what}`);
    await new Promise((r) => setTimeout(r, 100));
  }
}

/** Calls a Spyglass MCP tool on the hub (stateless HTTP). */
async function mcp(name: string, args: Record<string, unknown>): Promise<any[]> {
  const res = await fetch(`http://127.0.0.1:${port}/mcp`, {
    method: "POST",
    headers: { "Content-Type": "application/json", Accept: "application/json, text/event-stream" },
    body: JSON.stringify({ jsonrpc: "2.0", id: 1, method: "tools/call", params: { name, arguments: args } }),
  });
  const text = await res.text();
  const data = text.split("\n").find((l) => l.startsWith("data: "));
  return JSON.parse(data ? data.slice(6) : text).result.content;
}

const appText = async (query: string) => (await mcp("ui_find", { query })).map((c) => c.text ?? "").join("\n");

export async function run(): Promise<void> {
  const ext = vscode.extensions.getExtension("AIDotNet.spyglass-avalonia")!;
  const client = (await ext.activate()) as HubClient;
  const file = path.join(vscode.workspace.workspaceFolders![0].uri.fsPath, "MainWindow.axaml");
  const disk = fs.readFileSync(file, "utf8");

  await until("extension connected and file reloadable", () => client.isConnected && client.canReload(file));
  const doc = await vscode.workspace.openTextDocument(file);
  const editor = await vscode.window.showTextDocument(doc);

  // 1. Unsaved edit → the running app shows it.
  const marker = "Added from VS Code, unsaved";
  const anchor = doc.getText().indexOf("<TextBox x:Name=\"NameBox\"");
  assert.ok(anchor > 0, "anchor in MainWindow.axaml");
  await editor.edit((b) => b.insert(doc.positionAt(anchor), `<TextBlock x:Name="FromVsCode" Text="${marker}" Foreground="OrangeRed" />\n    `));
  assert.ok(doc.isDirty);
  await until("app shows the unsaved edit", async () => (await appText(`text=${marker}`)).includes("FromVsCode"));
  const shot = (await mcp("screenshot", {})).find((c) => c.type === "image");
  if (shot && process.env.SPYGLASS_E2E_SHOT) {
    fs.mkdirSync(path.dirname(process.env.SPYGLASS_E2E_SHOT), { recursive: true });
    fs.writeFileSync(process.env.SPYGLASS_E2E_SHOT, Buffer.from(shot.data, "base64"));
  }

  // 2. A mistake → diagnostic in the editor at the right line; the app keeps the last good UI.
  const binding = doc.getText().indexOf("{Binding Title}");
  await editor.edit((b) => b.replace(new vscode.Range(doc.positionAt(binding), doc.positionAt(binding + "{Binding Title}".length)), "{Binding Titel}"));
  const diag = await until("diagnostic for Titel", () =>
    vscode.languages.getDiagnostics(doc.uri).find((d) => d.source === "Spyglass hot reload" && d.message.includes("Titel")));
  assert.strictEqual(diag.range.start.line, doc.positionAt(binding).line);
  assert.ok((await appText(`text=${marker}`)).includes("FromVsCode"), "app untouched by the broken edit");

  // 3. Fixing it clears the diagnostic.
  const bad = doc.getText().indexOf("{Binding Titel}");
  await editor.edit((b) => b.replace(new vscode.Range(doc.positionAt(bad), doc.positionAt(bad + "{Binding Titel}".length)), "{Binding Title}"));
  await until("diagnostics cleared", () => vscode.languages.getDiagnostics(doc.uri).every((d) => d.source !== "Spyglass hot reload"));

  // 4. Nothing was written to disk; reverting the buffer puts the app back.
  assert.strictEqual(fs.readFileSync(file, "utf8"), disk, "file on disk unchanged");
  await vscode.commands.executeCommand("workbench.action.files.revert");
  await until("app back to the saved file", async () => !(await appText(`text=${marker}`)).includes("FromVsCode"));

  // 5. Design-time preview: opens beside the editor, renders, reports an unsaved XAML error as a diagnostic, recovers.
  type PState = { open: boolean; file?: string; running: boolean; frames: number; results: number; starts: number; state: string; statusText: string;
    zoom: string; theme: string; scale: number; viewport: { width: number; height: number }; lastError?: string; lastSentXaml?: string };
  const state = () => vscode.commands.executeCommand<PState>("spyglass.preview._state");
  const previewTabs = () => vscode.window.tabGroups.all.flatMap((g) => g.tabs).filter((t) => (t.input as any)?.viewType?.includes("spyglass.preview"));
  const pcfg = vscode.workspace.getConfiguration("spyglass.preview");
  const setPref = (k: string, v: unknown) => pcfg.update(k, v, vscode.ConfigurationTarget.Global);
  await vscode.window.showTextDocument(doc);
  await vscode.commands.executeCommand("spyglass.openPreview");
  await until("preview rendered a frame", async () => { const st = await state(); return st?.open && st.frames > 0 && st.results > 0 && !st.lastError; }, 90000);
  // 5a. Status text, and running the command again focuses the existing preview instead of creating a second one.
  await until("status shows the update time", async () => /^Updated · \d+ ms$/.test((await state())!.statusText));
  const before = (await state())!;
  await vscode.commands.executeCommand("spyglass.openPreview");
  await vscode.commands.executeCommand("spyglass.openPreview");
  assert.strictEqual(previewTabs().length, 1, "exactly one preview panel");
  assert.strictEqual((await state())!.starts, before.starts, "the host was not restarted");

  // 5b. Theme: injected into the Window root, left out again for "default".
  await setPref("theme", "dark");
  await until("dark theme sent", async () => ((await state())!.lastSentXaml ?? "").includes('RequestedThemeVariant="Dark"'), 15000);
  await until("themed update rendered", async () => { const st = (await state())!; return st.state === "ok" && !st.lastError; }, 30000);
  await setPref("theme", "light");
  await until("light theme sent", async () => ((await state())!.lastSentXaml ?? "").includes('RequestedThemeVariant="Light"'), 15000);
  await setPref("theme", "default");
  await until("theme removed", async () => !((await state())!.lastSentXaml ?? "").includes("RequestedThemeVariant"), 15000);

  // 5c. Zoom: the host viewport shrinks with the zoom (the pane is undone by it).
  const fitViewport = (await state())!.viewport;
  await setPref("zoom", "200%");
  await until("zoom 200%", async () => { const st = (await state())!; return st.zoom === "200%" && st.scale === 2; }, 15000);
  const z2 = (await state())!.viewport;
  assert.strictEqual(z2.width, Math.floor(fitViewport.width / 2));
  assert.strictEqual(z2.height, Math.floor(fitViewport.height / 2));
  await setPref("zoom", "fit");
  await until("zoom fit", async () => (await state())!.zoom === "fit", 15000);

  // 5d. Editing another .axaml of the project (App.axaml) refreshes the current preview.
  const appFile = path.join(path.dirname(file), "App.axaml");
  const appDoc = await vscode.workspace.openTextDocument(appFile);
  const resultsBefore = (await state())!.results;
  const we = new vscode.WorkspaceEdit();   // edited in the background: the preview must not switch to App.axaml
  we.insert(appDoc.uri, new vscode.Position(0, 0), "<!-- preview refresh -->\n");
  await vscode.workspace.applyEdit(we);
  await until("preview refreshed after App.axaml edit", async () => (await state())!.results > resultsBefore, 15000);
  assert.strictEqual((await state())!.file, file, "preview stays on the file it follows");
  const undo = new vscode.WorkspaceEdit();
  undo.delete(appDoc.uri, new vscode.Range(0, 0, 1, 0));
  await vscode.workspace.applyEdit(undo);
  assert.strictEqual(fs.readFileSync(appFile, "utf8"), appDoc.getText(), "App.axaml text restored; disk never touched");

  // 5e. Rebuilding the app restarts the host and keeps the file.
  const startsBefore = (await state())!.starts;
  const dll = path.join(path.dirname(file), "bin/Debug/net10.0/Spyglass.Sample.dll");
  const now = new Date();
  fs.utimesSync(dll, now, now);
  await until("host restarted after rebuild", async () => { const st = (await state())!; return st.starts > startsBefore && st.frames > 0 && st.state === "ok"; }, 60000);
  assert.strictEqual((await state())!.file, file);

  const closing = doc.getText().lastIndexOf("</Window>");
  await editor.edit((b) => b.replace(new vscode.Range(doc.positionAt(closing), doc.positionAt(closing + "</Window>".length)), "</Windo>"));
  const brokenLine = doc.positionAt(closing).line;
  const errorsOnLine = () => vscode.languages.getDiagnostics(doc.uri).filter((d) => d.severity === vscode.DiagnosticSeverity.Error && d.range.start.line === brokenLine);
  await until("preview error reported", async () => errorsOnLine().length > 0 && !!(await state())?.lastError, 30000);
  // The running app's hot reload reports the same line; the preview must not add a second Problems entry for it.
  await new Promise((r) => setTimeout(r, 1500));
  assert.strictEqual(errorsOnLine().length, 1, "one error on the broken line, not one per source: " + errorsOnLine().map((d) => d.source).join(", "));
  await editor.edit((b) => b.replace(new vscode.Range(doc.positionAt(closing), doc.positionAt(closing + "</Windo>".length)), "</Window>"));
  await until("preview diagnostic cleared", async () => vscode.languages.getDiagnostics(doc.uri).every((d) => d.source !== "Spyglass Preview") && !(await state())?.lastError, 30000);
  assert.strictEqual(fs.readFileSync(file, "utf8"), disk, "file on disk unchanged after preview edits");
  await vscode.commands.executeCommand("workbench.action.files.revert");

  // 6. autoOpen: opening an .axaml opens the preview to the side.
  for (const t of previewTabs()) await vscode.window.tabGroups.close(t);
  await until("preview closed", async () => !(await state())!.open, 10000);
  await vscode.commands.executeCommand("workbench.action.closeAllEditors");
  await setPref("autoOpen", true);
  await vscode.window.showTextDocument(await vscode.workspace.openTextDocument(file));
  await until("preview auto-opened", async () => (await state())!.open && (await state())!.frames > 0, 90000);
  await until("one preview tab", () => previewTabs().length === 1, 10000);
  await setPref("autoOpen", false);

  await vscode.commands.executeCommand("workbench.action.closeAllEditors");
  for (const t of previewTabs()) await vscode.window.tabGroups.close(t);
  await until("preview process stopped with its panel", async () => !(await state())?.running, 10000);

  // 7. Avalonia XAML language: .axaml is the axaml language (grammar), IntelliSense answers through VS Code's own API.
  const xdoc = await vscode.workspace.openTextDocument(file);
  await vscode.window.showTextDocument(xdoc);
  assert.strictEqual(xdoc.languageId, "axaml");
  await until("hub offers XAML IntelliSense", () => client.hasXamlLanguage);
  const complete = async (needle: string, delta: number) => {
    const at = xdoc.positionAt(xdoc.getText().indexOf(needle) + delta);
    return (await vscode.commands.executeCommand<vscode.CompletionList>("vscode.executeCompletionItemProvider", xdoc.uri, at, " ")).items
      .map((i) => (typeof i.label === "string" ? i.label : i.label.label));
  };
  const attrs = await until("attribute completion", async () => { const l = await complete('<StackPanel x:Name="Root"', 12); return l.includes("Orientation") && l; }, 90000);
  for (const set of ["x:Name", "Margin", "Spacing"]) assert.ok(!attrs.includes(set), `${set} is already set`);
  for (const ro of ["Bounds", "IsEffectivelyEnabled"]) assert.ok(!attrs.includes(ro), `${ro} is read-only`);
  const paths = await complete("{Binding Title}", "{Binding ".length);
  assert.ok(paths.includes("Title") && paths.includes("User"), "binding paths from x:DataType: " + paths.join(", "));
  const hover = await vscode.commands.executeCommand<vscode.Hover[]>("vscode.executeHoverProvider", xdoc.uri,
    xdoc.positionAt(xdoc.getText().indexOf("Spacing=") + 2));
  assert.ok(hover.some((h) => h.contents.some((c) => (c as vscode.MarkdownString).value?.includes("StackPanel.Spacing"))), "hover shows the property");
  await vscode.commands.executeCommand("workbench.action.closeAllEditors");
}
