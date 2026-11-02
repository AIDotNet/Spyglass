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
  const ext = vscode.extensions.getExtension("spyglass.spyglass-avalonia")!;
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
  await vscode.commands.executeCommand("workbench.action.closeAllEditors");
}
