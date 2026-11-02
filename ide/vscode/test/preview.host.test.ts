// Real previewer host (Avalonia.Designer.HostApp from the sample's Avalonia package): handshake, frames, XAML errors.
// Skipped when `dotnet`/the sample build is unavailable (build samples/Spyglass.Sample first).
import * as assert from "assert";
import * as fs from "fs";
import * as path from "path";
import { test } from "node:test";
import { findProjectFile, resolveProject, PreviewProject } from "../src/preview/project";
import { PreviewSession, PreviewFrame, XamlResult } from "../src/preview/session";
import { applyTheme, mapBack } from "../src/preview/zoom";

const sampleDir = path.resolve(__dirname, "../../../../samples/Spyglass.Sample");
const xamlFile = path.join(sampleDir, "MainWindow.axaml");

async function project(): Promise<PreviewProject | undefined> {
  try { return await resolveProject(xamlFile); } catch { return undefined; }
}

const once = <T>(s: PreviewSession, ev: string, ms = 60000): Promise<T> =>
  new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new Error(`timed out waiting for ${ev}`)), ms);
    s.once(ev, (v: T) => { clearTimeout(t); resolve(v); });
  });

test("project resolution finds the sample's previewer host", async (t) => {
  const p = await project();
  if (!p) { t.skip("sample project not built / dotnet unavailable"); return; }
  assert.equal(findProjectFile(xamlFile), p.projectFile);
  assert.ok(fs.existsSync(p.hostApp) && p.hostApp.endsWith("Avalonia.Designer.HostApp.dll"));
  assert.ok(p.targetPath.endsWith("Spyglass.Sample.dll"));
});

test("real host: renders a frame, reports a XAML error with line/column, then recovers", async (t) => {
  const p = await project();
  if (!p) { t.skip("sample project not built / dotnet unavailable"); return; }
  const session = new PreviewSession(p);
  const logs: string[] = [];
  session.on("log", (l) => logs.push(l));
  try {
    session.setViewport(640, 480, 96);
    await session.start();
    const xaml = fs.readFileSync(xamlFile, "utf8");

    let frame = once<PreviewFrame>(session, "frame");
    let result = once<XamlResult>(session, "result");
    session.updateXaml(xaml, "MainWindow.axaml");
    const r1 = await result;
    assert.equal(r1.error, undefined, `unexpected error: ${r1.error}`);
    const f = await frame;
    assert.ok(f.width > 0 && f.height > 0, "frame has a size");
    assert.equal(f.data.length, f.stride * f.height, "frame data length = stride * height");
    assert.ok(f.data.some((b) => b !== 0), "frame is not blank");

    // Broken XAML: error with a position, host keeps running.
    result = once<XamlResult>(session, "result");
    session.updateXaml(xaml.replace("</Window>", "</Windo>"), "MainWindow.axaml");
    const r2 = await result;
    assert.ok(r2.error, "error reported");
    assert.ok((r2.line ?? 0) > 0 && (r2.column ?? 0) > 0, `line/column present: ${JSON.stringify(r2)}`);

    // Fixed again.
    result = once<XamlResult>(session, "result");
    frame = once<PreviewFrame>(session, "frame");
    session.updateXaml(xaml, "MainWindow.axaml");
    assert.equal((await result).error, undefined);
    await frame;
  } catch (e) {
    console.error(logs.join("\n"));
    throw e;
  } finally {
    session.stop();
  }
});

test("real host: theme injection changes the picture; an error in a themed root maps back to the editor text", async (t) => {
  const p = await project();
  if (!p) { t.skip("sample project not built / dotnet unavailable"); return; }
  const session = new PreviewSession(p);
  try {
    session.setViewport(400, 300, 96);
    await session.start();
    const xaml = fs.readFileSync(xamlFile, "utf8");
    const mean = (f: PreviewFrame) => { let sum = 0; for (let i = 0; i < f.data.length; i += 4) sum += f.data[i] + f.data[i + 1] + f.data[i + 2]; return sum / (f.data.length / 4 * 3); };
    const render = async (x: string) => {
      const result = once<XamlResult>(session, "result");
      session.updateXaml(x, "MainWindow.axaml");
      const r = await result;
      // The host may send one more frame of the previous window before the new one; the picture of this update is the
      // first frame that differs from what we had, or the next one after the result.
      return { r, f: r.error ? undefined : await once<PreviewFrame>(session, "frame") };
    };
    const light = await render(applyTheme(xaml, "light").xaml);
    const dark = await render(applyTheme(xaml, "dark").xaml);
    assert.equal(light.r.error, undefined);
    assert.equal(dark.r.error, undefined, `themed XAML accepted: ${dark.r.error}`);
    assert.ok(mean(light.f!) > mean(dark.f!) + 40, `light (${mean(light.f!)}) is brighter than dark (${mean(dark.f!)})`);

    // A Window root with an error on its own line: the reported column maps back.
    // Root tag on one line with an unquoted attribute value: an XmlException positioned on that line.
    const oneLine = xaml.replace(/<Window[^>]*>/, (tag) => tag.replace(/\s+/g, " ")).replace("Width=\"560\"", "Width=560");
    const broken = oneLine;
    const themed = applyTheme(broken, "dark");
    const bad = await render(themed.xaml);
    assert.ok(bad.r.error && bad.r.line, `positioned error reported: ${JSON.stringify(bad.r)}`);
    const rawLine = themed.edit!.line;
    const mapped = mapBack(themed.edit, bad.r.line, bad.r.column);
    assert.equal(bad.r.line, rawLine, "the error is on the root tag's line");
    assert.equal(mapped.line, rawLine);
    // The host's column is past the injected attributes; mapped back it must lie inside the editor's text of that line.
    const editorLine = broken.split("\n")[rawLine - 1];
    assert.ok(bad.r.column! > themed.edit!.column + themed.edit!.length, `host column ${bad.r.column} is after the insertion`);
    assert.ok(mapped.column! >= 1 && mapped.column! <= editorLine.length + 1, `mapped column ${mapped.column} within "${editorLine}"`);
  } finally {
    session.stop();
  }
});
