// The copy of the build output the previewer runs from. The real build with a running previewer is in preview.host.test.ts.
import * as assert from "assert";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { test } from "node:test";
import { PreviewProject } from "../src/preview/project";
import { isRuntimeFor, PreviewShadowCopy } from "../src/preview/shadow";

function setup() {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), "spyglass-shadow-test-"));
  const bin = path.join(temp, "app", "bin");
  const write = (relative: string, text = "x") => {
    const file = path.join(bin, relative);
    fs.mkdirSync(path.dirname(file), { recursive: true });
    fs.writeFileSync(file, text);
    return file;
  };
  const project: PreviewProject = {
    projectFile: path.join(temp, "app", "App.csproj"),
    projectDir: path.join(temp, "app"),
    hostApp: path.join(temp, "nuget", "HostApp.dll"),
    targetPath: write("App.dll"),
    runtimeConfig: write("App.runtimeconfig.json"),
    depsFile: write("App.deps.json"),
  };
  return { temp, write, project, shadow: new PreviewShadowCopy(path.join(temp, "copies")) };
}

test("paths move to the copy, except those outside the output", async (t) => {
  const { temp, project, shadow } = setup();
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const copy = await shadow.sync(project);
  assert.notEqual(copy.targetPath, project.targetPath);
  for (const f of [copy.targetPath, copy.runtimeConfig, copy.depsFile]) assert.ok(fs.existsSync(f), f);
  assert.equal(copy.hostApp, project.hostApp);
  assert.equal(copy.projectFile, project.projectFile);
  assert.equal(copy.projectDir, project.projectDir);
});

test("subfolders are copied, and only this platform's top-level runtimes", async (t) => {
  const { temp, write, project, shadow } = setup();
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const family = process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : "linux";
  const rid = `${family}-${process.arch}`;
  write("zh-Hans/App.resources.dll");
  write(`runtimes/${rid}/native/lib.bin`);
  write("runtimes/other-os-x64/native/lib.bin");
  write("content/runtimes/other-os-x64/data.txt");

  const root = path.dirname((await shadow.sync(project)).targetPath);

  assert.ok(fs.existsSync(path.join(root, "zh-Hans", "App.resources.dll")));
  assert.ok(fs.existsSync(path.join(root, "runtimes", rid, "native", "lib.bin")));
  assert.ok(!fs.existsSync(path.join(root, "runtimes", "other-os-x64")));
  assert.ok(fs.existsSync(path.join(root, "content", "runtimes", "other-os-x64", "data.txt")));
});

test("a second sync copies only changes and removes what is gone", async (t) => {
  const { temp, write, project, shadow } = setup();
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const unchanged = write("Lib.dll", "lib");
  const removed = write("Old.dll");
  const copy = await shadow.sync(project);
  const root = path.dirname(copy.targetPath);

  // A marker of the same size and time as the source: it survives only if the file is not copied again.
  const copied = path.join(root, "Lib.dll");
  fs.writeFileSync(copied, "LIB");
  const times = fs.statSync(unchanged);
  fs.utimesSync(copied, times.atime, times.mtime);
  fs.writeFileSync(project.targetPath, "rebuilt");
  fs.rmSync(removed);

  await shadow.sync(project);

  assert.equal(fs.readFileSync(copied, "utf8"), "LIB");
  assert.equal(fs.readFileSync(copy.targetPath, "utf8"), "rebuilt");
  assert.ok(!fs.existsSync(path.join(root, "Old.dll")));
});

test("a missing output fails at once, and delete removes the copy", async (t) => {
  const { temp, project, shadow } = setup();
  t.after(() => fs.rmSync(temp, { recursive: true, force: true }));
  const started = Date.now();
  await assert.rejects(shadow.sync({ ...project, targetPath: path.join(temp, "nowhere", "App.dll") }));
  assert.ok(Date.now() - started < 2000, "not retried");

  const root = path.dirname((await shadow.sync(project)).targetPath);
  await shadow.delete();
  assert.ok(!fs.existsSync(root));
});

test("runtime folders match the OS and architecture", () => {
  const cases: [string, string, string, boolean][] = [
    ["win-x64", "win", "x64", true], ["win", "win", "x64", true], ["win10-x64", "win", "x64", true],
    ["win-arm64", "win", "x64", false], ["win-arm64", "win", "arm64", true], ["linux-x64", "win", "x64", false],
    ["osx", "win", "x64", false], ["unix", "win", "x64", false], ["unix", "linux", "x64", true],
    ["linux-musl-x64", "linux", "x64", true], ["osx.10.14-x64", "osx", "arm64", false], ["osx-arm64", "osx", "arm64", true],
  ];
  for (const [rid, family, arch, expected] of cases) assert.equal(isRuntimeFor(rid, family, arch), expected, `${rid} on ${family}-${arch}`);
});
