// VS Code side of the design-time preview (design/preview/README.md §4): one webview panel beside the editor that
// follows the active .axaml, feeds the unsaved buffer to the Avalonia previewer host (debounced), shows errors as
// diagnostics + a clickable error bar, forwards pointer/keyboard input, and offers zoom and theme switching.
import { firstLine } from "./message";
import { execFile } from "child_process";
import { randomBytes } from "crypto";
import * as fs from "fs";
import * as path from "path";
import * as vscode from "vscode";
import { logicalKeyFor, physicalKeyFor } from "./input";
import { findProjectFile, PreviewProject, PreviewProjectError, projectRelativePath, resolveProject } from "./project";
import { InputModifier, MouseButton } from "./protocol";
import { PreviewFrame, PreviewSession, XamlResult } from "./session";
import { previewHtml } from "./webview";
import { applyTheme, dpiFor, FIT, mapBack, parseTheme, parseZoom, PreviewTheme, PreviewZoom, viewportFor, XamlEdit, zoomIn, zoomOut, zoomScale, zoomToString } from "./zoom";

export const isXaml = (d: vscode.TextDocument): boolean => d.uri.scheme === "file" && /\.(axaml|xaml)$/i.test(d.fileName);

export type PreviewState = "starting" | "updating" | "ok" | "error" | "exited";

export interface PreviewDebugState {
  open: boolean;
  file?: string;
  running: boolean;
  frames: number;
  results: number;
  starts: number;
  state: PreviewState;
  statusText: string;
  zoom: string;
  theme: PreviewTheme;
  scale: number;
  viewport: { width: number; height: number };
  lastError?: string;
  lastLine?: number;
  lastColumn?: number;
  lastSentXaml?: string;
}

export class PreviewController implements vscode.Disposable {
  private panel?: vscode.WebviewPanel;
  private session?: PreviewSession;
  private project?: PreviewProject;
  private doc?: vscode.TextDocument;
  private pane = { width: 800, height: 600, dpr: 1 };
  private lastFrameLogical?: { w: number; h: number };
  private scale = 1;
  private timer?: NodeJS.Timeout;
  private watched?: string;
  private rebuildTimer?: NodeJS.Timeout;
  private generation = 0;
  private frames = 0;
  private results = 0;
  private starts = 0;
  private lastResult?: XamlResult;
  private lastEdit?: XamlEdit;
  private lastSent?: string;
  private pushAt = 0;
  private state: PreviewState = "starting";
  private statusText = "Starting…";
  /** Files the preview was auto-opened for while they stay visible, so closing the panel is not undone by focusing the editor again. */
  private readonly autoOpened = new Set<string>();
  private readonly diagnostics = vscode.languages.createDiagnosticCollection("spyglass-preview");
  private readonly disposables: vscode.Disposable[] = [this.diagnostics];

  constructor(private readonly log: (line: string) => void) {
    const schedule = () => {
      const ms = this.delay();
      if (ms <= 0) return;   // only on save
      clearTimeout(this.timer);
      this.timer = setTimeout(() => this.push(), ms);
    };
    this.disposables.push(
      vscode.workspace.onDidChangeTextDocument((e) => {
        if (!this.panel || !this.project || e.contentChanges.length === 0 || !isXaml(e.document)) return;
        // The current file, or any other .axaml of the same project (App.axaml, styles, dictionaries): refresh the preview.
        if (e.document === this.doc || this.inProject(e.document)) schedule();
      }),
      vscode.workspace.onDidSaveTextDocument((d) => {
        if (this.panel && this.project && isXaml(d) && (d === this.doc || this.inProject(d))) this.push();
      }),
      vscode.window.onDidChangeActiveTextEditor((e) => {
        if (!e || !isXaml(e.document)) return;
        if (!this.panel) this.maybeAutoOpen(e.document);
        else if (e.document !== this.doc) void this.follow(e.document);
      }),
      vscode.window.onDidChangeVisibleTextEditors((eds) => {
        const visible = new Set(eds.map((e) => e.document.fileName));
        for (const f of [...this.autoOpened]) if (!visible.has(f)) this.autoOpened.delete(f);
      }),
      vscode.languages.onDidChangeDiagnostics((e) => { if (this.doc && e.uris.some((u) => u.toString() === this.doc!.uri.toString())) this.syncDiagnostic(); }),
      vscode.workspace.onDidCloseTextDocument((d) => { this.diagnostics.delete(d.uri); this.autoOpened.delete(d.fileName); }),
      vscode.workspace.onDidChangeConfiguration((e) => {
        if (!this.panel) return;
        if (e.affectsConfiguration("spyglass.preview.zoom")) { this.applyZoomChange(); }
        if (e.affectsConfiguration("spyglass.preview.theme")) { this.sendToolbar(); this.push(); }
      }),
    );
  }

  private cfg = () => vscode.workspace.getConfiguration("spyglass.preview");
  private delay = () => this.cfg().get<number>("delay", 300);
  private zoom = (): PreviewZoom => parseZoom(this.cfg().get<string>("zoom", "fit"));
  private theme = (): PreviewTheme => parseTheme(this.cfg().get<string>("theme", "default"));
  private inProject(d: vscode.TextDocument): boolean {
    const dir = this.project!.projectDir + path.sep;
    return d.fileName.startsWith(dir);
  }

  get debugState(): PreviewDebugState {
    return { open: !!this.panel, file: this.doc?.fileName, running: !!this.session?.isRunning, frames: this.frames, results: this.results,
      starts: this.starts, state: this.state, statusText: this.statusText, zoom: zoomToString(this.zoom()), theme: this.theme(), scale: this.scale,
      viewport: viewportFor(this.zoom(), this.pane.width, this.pane.height),
      lastError: this.lastResult?.error, lastLine: this.lastResult?.line, lastColumn: this.lastResult?.column, lastSentXaml: this.lastSent };
  }

  /** Called when an .axaml opens or becomes active (and once at activation): opens the preview when autoOpen is on. */
  maybeAutoOpen(d: vscode.TextDocument | undefined): void {
    if (!d || !isXaml(d) || this.panel || this.autoOpened.has(d.fileName)) return;
    if (!this.cfg().get<boolean>("autoOpen", false) || !this.cfg().get<boolean>("enabled", true)) return;
    // onDidOpenTextDocument also fires for files opened in the background; only the active editor opens the preview.
    if (vscode.window.activeTextEditor?.document !== d) return;
    this.autoOpened.add(d.fileName);
    void this.open(true);
  }

  /** Command "Spyglass: Open Preview to the Side": opens the preview, or focuses the existing one (never a second panel). */
  async open(preserveFocus = false): Promise<void> {
    if (!this.cfg().get<boolean>("enabled", true)) { void vscode.window.showInformationMessage("Spyglass preview is disabled (spyglass.preview.enabled)."); return; }
    const active = vscode.window.activeTextEditor?.document;
    const doc = (active && isXaml(active) ? active : undefined) ?? this.doc ?? vscode.window.visibleTextEditors.map((e) => e.document).find(isXaml);
    if (!doc) { void vscode.window.showInformationMessage("Open an .axaml file to preview it."); return; }
    if (this.panel) this.panel.reveal(undefined, preserveFocus);
    else this.createPanel(preserveFocus);
    if (doc !== this.doc || !this.session) await this.follow(doc);
  }

  private createPanel(preserveFocus: boolean): void {
    const panel = vscode.window.createWebviewPanel("spyglass.preview", "Avalonia Preview", { viewColumn: vscode.ViewColumn.Beside, preserveFocus },
      { enableScripts: true, retainContextWhenHidden: true });
    this.panel = panel;
    panel.webview.html = previewHtml(randomBytes(16).toString("hex"), panel.webview.cspSource);
    panel.webview.onDidReceiveMessage((m) => this.onWebviewMessage(m), undefined, this.disposables);
    panel.onDidDispose(() => {
      this.panel = undefined;
      this.stopSession();
      this.unwatch();
      clearTimeout(this.timer);
      this.diagnostics.clear(); this.shownDiag = undefined; this.pendingDiag = undefined;
      this.project = undefined;
      this.doc = undefined;
    });
  }

  private post(m: unknown): void { void this.panel?.webview.postMessage(m); }
  private message(text?: string, button?: { label: string; command: string }): void { this.post({ type: "message", text, button }); }

  private setState(state: PreviewState, text: string): void {
    this.state = state;
    this.statusText = text;
    this.post({ type: "status", state, text });
  }

  private sendToolbar(): void {
    const z = this.zoom();
    this.post({ type: "toolbar", zoom: z.fit ? "Fit" : zoomToString(z), fit: z.fit, theme: this.theme() === "default" ? "Default" : this.theme() === "light" ? "Light" : "Dark" });
  }

  private onWebviewMessage(m: any): void {
    const mods: InputModifier[] = m.mods ?? [];
    switch (m.type) {
      case "ready": this.sendToolbar(); this.post({ type: "status", state: this.state, text: this.statusText }); break;
      case "pane": this.pane = { width: m.width, height: m.height, dpr: m.dpr }; this.sendViewport(); break;
      case "zoom": this.changeZoom(m.op); break;
      case "theme": {
        const order: PreviewTheme[] = ["default", "light", "dark"];
        void this.cfg().update("theme", order[(order.indexOf(this.theme()) + 1) % 3], vscode.ConfigurationTarget.Global);
        break;
      }
      case "reveal": void this.revealError(); break;
      case "pointer": this.session?.pointer(m.kind, m.x, m.y, m.button as MouseButton, mods); break;
      case "scroll": this.session?.scroll(m.x, m.y, m.dx, m.dy, mods); break;
      case "key": this.session?.key(m.down, logicalKeyFor(m.key, m.code), physicalKeyFor(m.code), m.key.length === 1 ? m.key : undefined, mods); break;
      case "text": this.session?.text(m.text, mods); break;
      case "command": void vscode.commands.executeCommand(m.command); break;
    }
  }

  // ---------- zoom ----------

  private changeZoom(op: string): void {
    const next = op === "in" ? zoomIn(this.scale) : op === "out" ? zoomOut(this.scale) : op === "actual" ? { fit: false, percent: 100 } : FIT;
    void this.cfg().update("zoom", zoomToString(next), vscode.ConfigurationTarget.Global);
  }

  private applyZoomChange(): void {
    this.sendToolbar();
    this.sendViewport();
    this.updateScale();
  }

  private sendViewport(): void {
    const v = viewportFor(this.zoom(), this.pane.width, this.pane.height);
    this.session?.setViewport(v.width, v.height, dpiFor(this.pane.dpr));
    this.updateScale();
  }

  private updateScale(): void {
    const f = this.lastFrameLogical;
    this.scale = zoomScale(this.zoom(), f?.w ?? 0, f?.h ?? 0, this.pane.width, this.pane.height);
    this.post({ type: "scale", scale: this.scale });
  }

  // ---------- following the document ----------

  /** Previews `doc`: (re)starts the host when the project changed, then sends the buffer. */
  private async follow(doc: vscode.TextDocument): Promise<void> {
    const gen = ++this.generation;
    this.doc = doc;
    if (this.panel) this.panel.title = `Preview: ${vscode.workspace.asRelativePath(doc.uri)}`;
    this.diagnostics.clear(); this.shownDiag = undefined; this.pendingDiag = undefined;
    this.lastResult = undefined;
    this.post({ type: "error", text: undefined });
    let project: PreviewProject;
    try {
      this.message("Resolving project…");
      this.setState("starting", "Resolving project…");
      project = await resolveProject(doc.fileName);
    } catch (e) {
      if (gen !== this.generation) return;
      this.stopSession();
      this.project = undefined;
      const err = e instanceof PreviewProjectError ? e : new PreviewProjectError((e as Error).message, "msbuild");
      this.log(`preview: ${err.message}`);
      this.message(err.message, err.kind === "not-built" ? { label: "Build project", command: "spyglass.preview.build" } : undefined);
      this.setState("error", err.kind === "not-built" ? "Not built" : "Unavailable");
      return;
    }
    if (gen !== this.generation) return;
    const same = this.project && this.project.projectFile === project.projectFile && this.project.targetPath === project.targetPath && this.session?.isRunning;
    this.project = project;
    if (!same) this.startSession(project);
    else this.push();
    this.watch(project.targetPath);
  }

  private startSession(project: PreviewProject): void {
    this.stopSession();
    const s = new PreviewSession(project);
    this.session = s;
    this.starts++;
    this.frames = 0;
    this.results = 0;
    this.message("Starting preview…");
    this.setState("starting", "Starting preview…");
    s.on("log", (l) => this.log(`preview: ${l}`));
    s.on("frame", (f: PreviewFrame) => {
      if (this.session !== s) return;
      this.frames++;
      if (this.frames === 1) this.message(undefined);
      this.lastFrameLogical = { w: f.width * 96 / (f.dpiX || 96), h: f.height * 96 / (f.dpiY || 96) };
      this.updateScale();
      this.post({ type: "frame", width: f.width, height: f.height, stride: f.stride, dpiX: f.dpiX, dpiY: f.dpiY, format: f.format, scale: this.scale, data: new Uint8Array(f.data) });
    });
    s.on("result", (r: XamlResult) => { if (this.session === s) this.onResult(r); });
    s.on("exit", (code) => {
      if (this.session !== s) return;
      this.session = undefined;
      if (this.panel) {
        this.message(`The preview process exited${code ? ` (code ${code})` : ""}.`, { label: "Restart preview", command: "spyglass.preview.restart" });
        this.setState("exited", "Preview process exited");
      }
    });
    const v = viewportFor(this.zoom(), this.pane.width, this.pane.height);
    s.setViewport(v.width, v.height, dpiFor(this.pane.dpr));
    s.start().then(() => this.push(), (e) => { this.log(`preview: ${e.message}`); this.message(`Could not start the preview: ${e.message}`); this.setState("error", "Could not start"); });
  }

  private stopSession(): void {
    const s = this.session;
    this.session = undefined;
    s?.stop();
  }

  /** Sends the current editor buffer (with the theme applied) to the host. */
  private push(): void {
    const doc = this.doc;
    if (!doc || doc.isClosed || !this.session || !this.project) return;
    const themed = applyTheme(doc.getText(), this.theme());
    this.lastEdit = themed.edit;
    this.lastSent = themed.xaml;
    this.pushAt = Date.now();
    if (this.state !== "starting" || this.frames > 0) this.setState("updating", "Updating…");
    this.session.updateXaml(themed.xaml, projectRelativePath(this.project, doc.fileName));
  }

  private onResult(r: XamlResult): void {
    this.results++;
    this.lastResult = r.error ? { ...r, ...mapBack(this.lastEdit, r.line, r.column) } : r;
    const doc = this.doc;
    if (!doc) return;
    if (!r.error) {
      this.pendingDiag = undefined;
      this.diagnostics.delete(doc.uri); this.shownDiag = undefined;
      this.post({ type: "error", text: undefined });
      this.setState("ok", `Updated · ${Math.max(0, Date.now() - this.pushAt)} ms`);
      return;
    }
    const { line: l, column: c } = this.lastResult;
    const line = Math.max(0, (l ?? 1) - 1);
    const col = Math.max(0, (c ?? 1) - 1);
    const range = line < doc.lineCount
      ? doc.getWordRangeAtPosition(new vscode.Position(line, col), /[\w:.{}-]+/) ?? doc.lineAt(line).range
      : new vscode.Range(line, col, line, col + 1);
    const short = firstLine(r.error);
    const d = new vscode.Diagnostic(range, short, vscode.DiagnosticSeverity.Error);
    d.source = "Spyglass Preview";
    this.pendingDiag = d;
    this.syncDiagnostic();
    this.post({ type: "error", text: l ? `${short} (line ${l}, column ${c ?? 1})` : short, detail: r.error });
    this.setState("error", "Error");
  }

  /** The error as a squiggle, unless the running app's hot reload already reports the same line (it would show twice in Problems). */
  private pendingDiag?: vscode.Diagnostic;
  /** The diagnostic currently in the collection. Writing to the collection fires onDidChangeDiagnostics again, so only touch it on a real change. */
  private shownDiag?: vscode.Diagnostic;
  private syncDiagnostic(): void {
    const doc = this.doc, d = this.pendingDiag;
    if (!doc) return;
    const dup = !!d && vscode.languages.getDiagnostics(doc.uri).some((o) =>
      o.source !== "Spyglass Preview" && o.severity === vscode.DiagnosticSeverity.Error && o.range.start.line === d.range.start.line);
    if (!d || dup) {
      if (this.shownDiag) { this.diagnostics.delete(doc.uri); this.shownDiag = undefined; }
      return;
    }
    if (this.shownDiag !== d) { this.diagnostics.set(doc.uri, [d]); this.shownDiag = d; }
  }

  /** Click on the error bar: jump to the error's line and column in the editor. */
  private async revealError(): Promise<void> {
    const doc = this.doc, r = this.lastResult;
    if (!doc || !r?.line) return;
    const pos = new vscode.Position(Math.max(0, r.line - 1), Math.max(0, (r.column ?? 1) - 1));
    await vscode.window.showTextDocument(doc, { selection: new vscode.Range(pos, pos), viewColumn: vscode.ViewColumn.One, preserveFocus: false });
  }

  /** Rebuilding the app changes the assembly the host loaded; the host never unloads it, so restart it (file and zoom are kept). */
  private watch(targetPath: string): void {
    if (this.watched === targetPath) return;
    this.unwatch();
    this.watched = targetPath;
    fs.watchFile(targetPath, { interval: 1500 }, (cur, prev) => {
      if (cur.mtimeMs === prev.mtimeMs || cur.mtimeMs === 0) return;
      clearTimeout(this.rebuildTimer);
      this.rebuildTimer = setTimeout(() => { this.log("preview: assembly rebuilt; restarting the previewer host"); this.restart(); }, 800);
    });
  }

  private unwatch(): void {
    if (this.watched) fs.unwatchFile(this.watched);
    this.watched = undefined;
    clearTimeout(this.rebuildTimer);
  }

  /** Command "spyglass.preview.restart". */
  restart(): void {
    if (!this.doc) return;
    this.stopSession();
    this.project = undefined;
    void this.follow(this.doc);
  }

  /** Command "spyglass.preview.build": `dotnet build` the document's project, then restart. */
  async build(): Promise<void> {
    const doc = this.doc;
    if (!doc) return;
    const proj = findProjectFile(doc.fileName);
    if (!proj) return;
    this.message("Building…");
    this.setState("starting", "Building…");
    await vscode.window.withProgress({ location: vscode.ProgressLocation.Notification, title: "Building for Avalonia preview…" }, () =>
      new Promise<void>((resolve) => {
        execFile("dotnet", ["build", proj, "-nologo", "-v:q"], { maxBuffer: 16 << 20 }, (err, stdout, stderr) => {
          this.log(`preview: dotnet build ${err ? "failed" : "succeeded"}\n${stdout}${stderr}`);
          if (err) { this.message("Build failed. See the Spyglass output.", { label: "Build project", command: "spyglass.preview.build" }); this.setState("error", "Build failed"); }
          resolve();
        });
      }));
    if (this.state !== "error") this.restart();
  }

  dispose(): void {
    this.panel?.dispose();
    this.stopSession();
    this.unwatch();
    this.disposables.forEach((d) => d.dispose());
  }
}

