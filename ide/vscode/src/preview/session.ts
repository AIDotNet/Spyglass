// One previewer-host process + its TCP session (design/preview/README.md §2–§4). No VS Code dependency.
import { ChildProcess, spawn } from "child_process";
import { randomUUID } from "crypto";
import { EventEmitter } from "events";
import * as net from "net";
import { encodeMessage, FrameParser, InputModifier, MessageName, MouseButton, PixelFormat } from "./protocol";
import { PreviewProject } from "./project";

export interface PreviewFrame {
  width: number;
  height: number;
  stride: number;
  dpiX: number;
  dpiY: number;
  format: PixelFormat;
  data: Buffer;
}

export interface XamlResult {
  error?: string;
  line?: number;
  column?: number;
  exceptionType?: string;
}

export interface PreviewSessionEvents {
  frame: (f: PreviewFrame) => void;
  result: (r: XamlResult) => void;
  log: (line: string) => void;
  exit: (code: number | null) => void;
  ready: () => void;
}

export class PreviewSession extends EventEmitter {
  private server?: net.Server;
  private socket?: net.Socket;
  private proc?: ChildProcess;
  private parser = new FrameParser();
  private stopped = false;
  private pending: Buffer[] = [];
  private viewport = { width: 800, height: 600, dpi: 96 };
  frames = 0;
  results = 0;

  constructor(readonly project: PreviewProject, private readonly dotnet = "dotnet", private readonly connectTimeoutMs = 60000) { super(); }

  get isRunning(): boolean { return !this.stopped; }

  async start(): Promise<void> {
    this.server = net.createServer((s) => this.accept(s));
    await new Promise<void>((resolve, reject) => { this.server!.once("error", reject); this.server!.listen(0, "127.0.0.1", resolve); });
    const port = (this.server.address() as net.AddressInfo).port;
    const p = this.project;
    const args = ["exec", "--runtimeconfig", p.runtimeConfig, "--depsfile", p.depsFile, p.hostApp,
      "--transport", `tcp-bson://127.0.0.1:${port}/`, "--session-id", randomUUID().replace(/-/g, ""), "--method", "avalonia-remote", p.targetPath];
    this.log(`starting previewer host: dotnet ${args.join(" ")}`);
    const proc = spawn(this.dotnet, args, { cwd: p.projectDir, stdio: ["ignore", "pipe", "pipe"] });
    this.proc = proc;
    const pipe = (s: NodeJS.ReadableStream) => {
      let tail = "";
      s.on("data", (d: Buffer) => { const lines = (tail + d.toString()).split(/\r?\n/); tail = lines.pop() ?? ""; lines.forEach((l) => l && this.log(`[host] ${l}`)); });
    };
    pipe(proc.stdout!); pipe(proc.stderr!);
    proc.on("error", (e) => { this.log(`could not start dotnet: ${e.message}`); this.finish(null); });
    proc.on("exit", (code) => { this.log(`previewer host exited (${code})`); this.finish(code); });
    const timer = setTimeout(() => { if (!this.socket && !this.stopped) { this.log("previewer host did not connect in time"); this.stop(); } }, this.connectTimeoutMs);
    timer.unref();
  }

  private accept(s: net.Socket): void {
    if (this.socket) { s.destroy(); return; }
    this.socket = s;
    s.setNoDelay(true);
    s.on("data", (d) => {
      try { for (const m of this.parser.push(d)) this.onMessage(m.name, m.body, m.guid); }
      catch (e) { this.log(`protocol error: ${(e as Error).message}`); this.stop(); }
    });
    s.on("error", (e) => this.log(`socket error: ${e.message}`));
    s.on("close", () => { if (!this.stopped) this.finish(null); });
    this.sendRaw(encodeMessage("ClientSupportedPixelFormats", { Formats: [PixelFormat.Bgra8888, PixelFormat.Rgba8888] }));
    this.sendViewport();
    for (const b of this.pending) this.sendRaw(b);
    this.pending = [];
    this.emit("ready");
  }

  private onMessage(name: MessageName | undefined, body: Record<string, any>, guid: string): void {
    switch (name) {
      case "Frame": {
        this.frames++;
        const f: PreviewFrame = { width: body.Width, height: body.Height, stride: body.Stride, dpiX: body.DpiX, dpiY: body.DpiY, format: body.Format, data: body.Data };
        this.emit("frame", f);
        this.sendRaw(encodeMessage("FrameReceived", { SequenceId: body.SequenceId }));   // host sends the next frame only after this
        break;
      }
      case "UpdateXamlResult": {
        this.results++;
        const ex = body.Exception;
        this.emit("result", {
          error: body.Error || undefined,
          line: ex?.LineNumber ?? undefined, column: ex?.LinePosition ?? undefined, exceptionType: ex?.ExceptionType ?? undefined,
        } satisfies XamlResult);
        break;
      }
      case "StartDesignerSession": case "MeasureViewport": case "RequestViewportResize": break;   // the panel owns the viewport size
      default: this.log(`ignored message ${name ?? guid}`);
    }
  }

  updateXaml(xaml: string, projectRelativePath: string): void {
    this.send("UpdateXaml", { Xaml: xaml, AssemblyPath: this.project.targetPath, XamlFileProjectPath: projectRelativePath });
  }

  setViewport(width: number, height: number, dpi = 96): void {
    this.viewport = { width: Math.max(1, width), height: Math.max(1, height), dpi };
    if (this.socket) this.sendViewport();
  }

  private sendViewport(): void {
    const v = this.viewport;
    this.sendRaw(encodeMessage("ClientRenderInfo", { DpiX: v.dpi, DpiY: v.dpi }));
    this.sendRaw(encodeMessage("ClientViewportAllocated", { Width: v.width, Height: v.height, DpiX: v.dpi, DpiY: v.dpi }));
  }

  pointer(kind: "move" | "down" | "up", x: number, y: number, button: MouseButton, mods: InputModifier[]): void {
    if (kind === "move") this.send("PointerMoved", { Modifiers: mods, X: x, Y: y });
    else this.send(kind === "down" ? "PointerPressed" : "PointerReleased", { Modifiers: mods, X: x, Y: y, Button: button });
  }
  scroll(x: number, y: number, dx: number, dy: number, mods: InputModifier[]): void { this.send("Scroll", { Modifiers: mods, X: x, Y: y, DeltaX: dx, DeltaY: dy }); }
  key(isDown: boolean, key: number, physicalKey: number, symbol: string | undefined, mods: InputModifier[]): void {
    this.send("KeyEvent", { Modifiers: mods, IsDown: isDown, Key: key, PhysicalKey: physicalKey, KeySymbol: symbol });
  }
  text(text: string, mods: InputModifier[]): void { this.send("TextInput", { Modifiers: mods, Text: text }); }

  private send(name: MessageName, value: Record<string, unknown>): void {
    const b = encodeMessage(name, value);
    if (this.socket) this.sendRaw(b); else this.pending.push(b);   // queued until the host connects
  }

  private sendRaw(b: Buffer): void {
    if (this.socket && !this.socket.destroyed) this.socket.write(b);
  }

  private log(line: string): void { this.emit("log", line); }

  private finish(code: number | null): void {
    if (this.stopped) return;
    this.stopped = true;
    this.cleanup();
    this.emit("exit", code);
  }

  /** Kills the host. Emits "exit" once. */
  stop(): void {
    const wasStopped = this.stopped;
    this.stopped = true;
    this.cleanup();
    if (!wasStopped) this.emit("exit", null);
  }

  private cleanup(): void {
    this.socket?.destroy();
    this.server?.close();
    if (this.proc && this.proc.exitCode === null) this.proc.kill();
  }
}
