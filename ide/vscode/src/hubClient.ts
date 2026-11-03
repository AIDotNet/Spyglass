// Connection to the Spyglass hub's /ide endpoint (design/hot-reload/README.md §7.1, §8). No VS Code dependency, so it
// can be tested with plain Node. Finds the hub's token in ~/.spyglass/hub-PORT.json, starts `spyglass serve` when
// nothing listens and autoStart is on, reconnects with backoff, and exposes the hub's pushes as events.
import { spawn } from "child_process";
import { EventEmitter } from "events";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import WebSocket from "ws";
import { RpcError, RpcPeer } from "./rpc";

export interface XamlDiagnostic {
  severity: "error" | "warning" | "info";
  message: string;
  line?: number;
  column?: number;
  code?: string;
}

export interface XamlDiagnostics {
  path: string;
  version: number;
  items: XamlDiagnostic[];
  summary: string;
}

export interface AppStatus {
  id: string;
  name: string;
  pid: number;
  hotReload: boolean;
  available: boolean;
  reason?: string;
  documents: number;
}

export interface HotReloadStatus {
  apps: AppStatus[];
  files: string[];
  last?: string;
  lastOk: boolean;
}

export interface RevealRequest {
  path: string;
  line: number;
  column: number;
}

// ----- Avalonia XAML IntelliSense (xaml/completion, xaml/hover, xaml/documentation; capability "xamlLanguage") -----

export interface XamlTextEdit { offset: number; length: number; text: string; }

export interface XamlCompletionItem {
  label: string;
  /** element, property, attachedProperty, event, value, markupExtension, namespace, directive, type, resource, name, closingTag, owner, method */
  kind: string;
  detail?: string;
  insertText?: string;
  snippet?: boolean;
  docId?: string;
  deprecated?: boolean;
  retrigger?: boolean;
  sortText?: string;
  additionalEdit?: XamlTextEdit;
}

/** Items replace text[from..offset]. incomplete: the project's types are still loading. */
export interface XamlCompletionResult { from: number; items: XamlCompletionItem[]; incomplete?: boolean; message?: string; }

export interface XamlHoverResult { markdown?: string; from: number; to: number; }

export type ConnectionState = "disconnected" | "starting" | "connecting" | "connected";

export interface HubClientOptions {
  port?: number;
  /** ~/.spyglass unless SPYGLASS_HOME is set. */
  home?: string;
  ide: string;
  version: string;
  workspaceFolders: () => string[];
  /** `spyglass`, a path to the executable, or a path to Spyglass.dll (run with `dotnet`). */
  command: string;
  autoStart: boolean;
  log: (line: string) => void;
}

export const defaultPort = (): number => {
  const p = Number(process.env.SPYGLASS_PORT);
  return Number.isInteger(p) && p > 0 && p < 65536 ? p : 29500;
};

export const defaultHome = (): string => process.env.SPYGLASS_HOME || path.join(os.homedir(), ".spyglass");

/** Paths as the hub compares them: case-insensitive on Windows and macOS. */
export const pathKey = (p: string): string => (process.platform === "linux" ? path.resolve(p) : path.resolve(p).toLowerCase());

export class HubClient extends EventEmitter {
  private peer?: RpcPeer;
  private stopped = false;
  private retryMs = 1000;
  private retryTimer?: NodeJS.Timeout;
  private lastServeStart = 0;
  private files = new Set<string>();
  private capabilities = new Set<string>();
  state: ConnectionState = "disconnected";
  status?: HotReloadStatus;
  /** Called for editor/reveal; return true when the file was shown. */
  revealHandler?: (r: RevealRequest) => Promise<boolean>;

  constructor(private readonly options: HubClientOptions) {
    super();
  }

  get port(): number {
    return this.options.port && this.options.port > 0 ? this.options.port : defaultPort();
  }

  get tokenFile(): string {
    return path.join(this.options.home ?? defaultHome(), `hub-${this.port}.json`);
  }

  get isConnected(): boolean {
    return this.state === "connected" && !!this.peer?.isOpen;
  }

  /** True when some connected app can hot-reload this file. */
  canReload(file: string): boolean {
    return this.files.has(pathKey(file));
  }

  start(): void {
    this.stopped = false;
    void this.connectLoop();
  }

  stop(): void {
    this.stopped = true;
    clearTimeout(this.retryTimer);
    this.peer?.close();
    this.setState("disconnected");
  }

  /** Drops the connection and connects again now. */
  reconnect(): void {
    clearTimeout(this.retryTimer);
    this.retryMs = 1000;
    if (this.peer?.isOpen) this.peer.close();
    else void this.connectLoop();
  }

  didOpen(file: string, version: number, text: string): void {
    this.peer?.notify("xaml/didOpen", { path: file, version, text });
  }

  didChange(file: string, version: number, text: string): void {
    this.peer?.notify("xaml/didChange", { path: file, version, text });
  }

  didClose(file: string): void {
    this.peer?.notify("xaml/didClose", { path: file, version: 0 });
  }

  /** The hub serves Avalonia XAML IntelliSense (0.2.2+). */
  get hasXamlLanguage(): boolean {
    return this.isConnected && this.capabilities.has("xamlLanguage");
  }

  /** An .axaml was opened: let the hub load its project's types before the first completion. */
  xamlPrepare(file: string): void {
    if (this.hasXamlLanguage) this.peer?.notify("xaml/prepare", { path: file });
  }

  xamlCompletion(file: string, text: string, offset: number): Promise<XamlCompletionResult | undefined> {
    return this.xamlRequest<XamlCompletionResult>("xaml/completion", { path: file, text, offset });
  }

  xamlHover(file: string, text: string, offset: number): Promise<XamlHoverResult | undefined> {
    return this.xamlRequest<XamlHoverResult>("xaml/hover", { path: file, text, offset });
  }

  async xamlDocumentation(file: string, docId: string): Promise<string | undefined> {
    return (await this.xamlRequest<{ markdown?: string }>("xaml/documentation", { path: file, docId }))?.markdown;
  }

  private async xamlRequest<T>(method: string, params: unknown): Promise<T | undefined> {
    if (!this.hasXamlLanguage || !this.peer) return undefined;
    try {
      return await this.peer.request<T>(method, params, 5000);
    } catch (e: any) {
      this.options.log(`${method} failed: ${e?.message ?? e}`);
      return undefined;
    }
  }

  private setState(s: ConnectionState): void {
    if (this.state === s) return;
    this.state = s;
    this.emit("state", s);
  }

  private async connectLoop(): Promise<void> {
    if (this.stopped || this.state === "connecting" || this.state === "connected") return;
    try {
      await this.connectOnce();
      this.retryMs = 1000;
    } catch (e: any) {
      this.setState("disconnected");
      if (e instanceof RpcError) this.options.log(`Spyglass refused the connection: ${e.message}`);
      if (this.stopped) return;
      this.retryTimer = setTimeout(() => void this.connectLoop(), this.retryMs);
      this.retryMs = Math.min(this.retryMs * 2, 10000);
    }
  }

  private async connectOnce(): Promise<void> {
    this.setState("connecting");
    let token = this.readToken();
    let socket: WebSocket;
    try {
      if (!token) throw Object.assign(new Error("no hub"), { code: "ENOHUB" });
      socket = await this.open();
    } catch (e: any) {
      // Nothing listens (no token file, or a stale one from a hub that exited): start one if allowed.
      if (!this.options.autoStart || (e.code !== "ECONNREFUSED" && e.code !== "ENOHUB")) throw e;
      if (Date.now() - this.lastServeStart < 30000) throw e;
      this.startServe();
      token = await this.waitForToken(15000);
      socket = await this.open();
    }

    const peer = new RpcPeer(socket);
    peer.onNotification("hotReload/status", (s: HotReloadStatus) => {
      this.status = s;
      this.files = new Set(s.files.map(pathKey));
      this.emit("status", s);
    });
    peer.onNotification("xaml/diagnostics", (d: XamlDiagnostics) => this.emit("diagnostics", d));
    peer.onRequest("editor/reveal", async (r: RevealRequest) => ({ ok: this.revealHandler ? await this.revealHandler(r) : false }));
    socket.on("close", () => {
      if (this.peer === peer) this.peer = undefined;
      this.files.clear();
      this.status = undefined;
      this.setState("disconnected");
      this.emit("status", undefined);
      if (!this.stopped) this.retryTimer = setTimeout(() => void this.connectLoop(), this.retryMs);
    });

    const hello = await peer.request<{ capabilities?: string[] }>("ide/hello", {
      ide: this.options.ide,
      version: this.options.version,
      workspaceFolders: this.options.workspaceFolders(),
      token,
    });
    this.capabilities = new Set(hello?.capabilities ?? []);
    this.peer = peer;
    this.setState("connected");
    this.options.log(`Connected to Spyglass on port ${this.port}.`);
    this.emit("connected");
  }

  private open(): Promise<WebSocket> {
    return new Promise((resolve, reject) => {
      const ws = new WebSocket(`ws://127.0.0.1:${this.port}/ide`);
      ws.once("open", () => resolve(ws));
      ws.once("error", (e) => reject(e));
    });
  }

  private readToken(): string | undefined {
    try {
      const j = JSON.parse(fs.readFileSync(this.tokenFile, "utf8"));
      return typeof j.token === "string" ? j.token : undefined;
    } catch {
      return undefined;
    }
  }

  private async waitForToken(timeoutMs: number): Promise<string> {
    const before = this.readToken();
    const until = Date.now() + timeoutMs;
    while (Date.now() < until) {
      const t = this.readToken();
      if (t && t !== before) return t;
      await new Promise((r) => setTimeout(r, 200));
    }
    const t = this.readToken();
    if (t) return t;
    throw new Error(`spyglass serve did not start (no ${this.tokenFile})`);
  }

  private startServe(): void {
    this.lastServeStart = Date.now();
    this.setState("starting");
    const cmd = this.options.command || "spyglass";
    const [exe, args] = cmd.toLowerCase().endsWith(".dll") ? ["dotnet", [cmd]] : [cmd, [] as string[]];
    const argv = [...args, "serve", "--port", String(this.port), "--quiet"];
    this.options.log(`Starting ${exe} ${argv.join(" ")}`);
    try {
      const child = spawn(exe, argv, { detached: true, stdio: "ignore", windowsHide: true });
      child.on("error", (e: any) =>
        this.options.log(e.code === "ENOENT"
          ? `Cannot start Spyglass: '${exe}' not found. Install it with \`dotnet tool install -g TKCode.Spyglass\`, or set "spyglass.command".`
          : `Cannot start Spyglass: ${e.message}`));
      child.unref();
    } catch (e: any) {
      this.options.log(`Cannot start Spyglass: ${e.message}`);
    }
  }
}
