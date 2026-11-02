// Unit tests for HubClient against a fake hub (node --test). The real hub is covered by IdeEndpointTests (C#) and the
// VS Code end-to-end test (test/e2e).
import assert from "node:assert/strict";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { test } from "node:test";
const opts = { timeout: 15000 };
import { WebSocketServer } from "ws";
import { HubClient, HotReloadStatus, XamlDiagnostics, pathKey } from "../src/hubClient";

async function fakeHub() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), "spyglass-home-"));
  const wss = new WebSocketServer({ port: 0, path: "/ide" });
  await new Promise((r) => wss.once("listening", r));
  const port = (wss.address() as any).port as number;
  fs.writeFileSync(path.join(home, `hub-${port}.json`), JSON.stringify({ port, token: "secret" }));
  const received: any[] = [];
  let socket: any;
  wss.on("connection", (ws) => {
    socket = ws;
    ws.on("message", (raw) => {
      const m = JSON.parse(raw.toString());
      received.push(m);
      if (m.method === "ide/hello") {
        if (m.params.token !== "secret") ws.send(JSON.stringify({ jsonrpc: "2.0", id: m.id, error: { code: -32011, message: "Wrong token" } }));
        else ws.send(JSON.stringify({ jsonrpc: "2.0", id: m.id, result: { hubVersion: "0.1", port, capabilities: ["hotReload"] } }));
      }
    });
  });
  const push = (method: string, params: unknown, id?: number) => socket.send(JSON.stringify({ jsonrpc: "2.0", method, params, id }));
  const close = () => { for (const c of wss.clients) c.terminate(); wss.close(); };
  return { home, port, wss, received, push, close, get socket() { return socket; } };
}

const until = async (cond: () => boolean, ms = 3000) => {
  const end = Date.now() + ms;
  while (!cond()) {
    if (Date.now() > end) throw new Error("timeout");
    await new Promise((r) => setTimeout(r, 20));
  }
};

const newClient = (port: number, home: string, logs: string[] = []) => new HubClient({
  port, home, ide: "test", version: "1", workspaceFolders: () => ["/ws"], command: "spyglass", autoStart: false, log: (l) => logs.push(l),
});

test("hello carries the token from the hub file and workspace folders", opts, async () => {
  const hub = await fakeHub();
  const c = newClient(hub.port, hub.home);
  c.start();
  await until(() => c.isConnected);
  const hello = hub.received.find((m) => m.method === "ide/hello");
  assert.equal(hello.params.token, "secret");
  assert.deepEqual(hello.params.workspaceFolders, ["/ws"]);
  c.stop();
  hub.close();
});

test("status decides which files are reloadable; buffers are sent as notifications", opts, async () => {
  const hub = await fakeHub();
  const c = newClient(hub.port, hub.home);
  c.start();
  await until(() => c.isConnected);
  const file = path.resolve("/proj/Views/Main.axaml");
  assert.equal(c.canReload(file), false);
  const status: HotReloadStatus = { apps: [{ id: "a1", name: "App", pid: 1, hotReload: true, available: true, documents: 1 }], files: [file], lastOk: true };
  hub.push("hotReload/status", status);
  await until(() => c.canReload(file));
  if (process.platform !== "linux") assert.ok(c.canReload(file.toUpperCase()), "case-insensitive like the hub");
  c.didOpen(file, 1, "<a/>");
  c.didChange(file, 2, "<b/>");
  c.didClose(file);
  await until(() => hub.received.some((m) => m.method === "xaml/didClose"));
  const methods = hub.received.filter((m) => m.method?.startsWith("xaml/")).map((m) => [m.method, m.params.version, m.params.text]);
  assert.deepEqual(methods, [["xaml/didOpen", 1, "<a/>"], ["xaml/didChange", 2, "<b/>"], ["xaml/didClose", 0, undefined]]);
  assert.ok(hub.received.filter((m) => m.method === "xaml/didChange").every((m) => m.id === undefined), "notifications have no id");
  c.stop();
  hub.close();
});

test("diagnostics are emitted and editor/reveal is answered", opts, async () => {
  const hub = await fakeHub();
  const c = newClient(hub.port, hub.home);
  let diag: XamlDiagnostics | undefined;
  c.on("diagnostics", (d) => (diag = d));
  c.revealHandler = async (r) => r.line === 7;
  c.start();
  await until(() => c.isConnected);
  hub.push("xaml/diagnostics", { path: "/p/A.axaml", version: 3, items: [{ severity: "error", message: "boom", line: 2, column: 5 }], summary: "✗" });
  await until(() => diag !== undefined);
  assert.equal(diag!.items[0].line, 2);
  hub.push("editor/reveal", { path: "/p/A.axaml", line: 7, column: 1 }, 99);
  await until(() => hub.received.some((m) => m.id === 99));
  assert.deepEqual(hub.received.find((m) => m.id === 99).result, { ok: true });
  c.stop();
  hub.close();
});

test("wrong token is logged and retried, not fatal", opts, async () => {
  const hub = await fakeHub();
  fs.writeFileSync(path.join(hub.home, `hub-${hub.port}.json`), JSON.stringify({ port: hub.port, token: "stale" }));
  const logs: string[] = [];
  const c = newClient(hub.port, hub.home, logs);
  c.start();
  await until(() => logs.some((l) => l.includes("Wrong token")));
  assert.equal(c.isConnected, false);
  fs.writeFileSync(path.join(hub.home, `hub-${hub.port}.json`), JSON.stringify({ port: hub.port, token: "secret" }));
  await until(() => c.isConnected, 5000);
  c.stop();
  hub.close();
});

test("losing the hub clears reloadable files and reconnects", opts, async () => {
  const hub = await fakeHub();
  const c = newClient(hub.port, hub.home);
  c.start();
  await until(() => c.isConnected);
  const file = path.resolve("/proj/X.axaml");
  hub.push("hotReload/status", { apps: [], files: [file], lastOk: true });
  await until(() => c.canReload(file));
  hub.socket.close();
  await until(() => !c.isConnected && !c.canReload(file));
  await until(() => c.isConnected, 5000);
  c.stop();
  hub.close();
});

test("pathKey normalises", () => {
  assert.equal(pathKey("/a/b/../c.axaml"), process.platform === "linux" ? "/a/c.axaml" : "/a/c.axaml".toLowerCase());
});
