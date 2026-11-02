// End-to-end: real `spyglass serve` + the real sample app + VS Code with this extension (design/hot-reload §12, HR2).
// Usage: npm run test:e2e   (needs `dotnet build` of src/Spyglass.App and samples/Spyglass.Sample; VS Code installed)
import { ChildProcess, spawn } from "child_process";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { runTests } from "@vscode/test-electron";

const repo = path.resolve(__dirname, "../../../../..");
const port = 29512;

function vscodeExecutable(): string | undefined {
  if (process.env.VSCODE_EXECUTABLE) return process.env.VSCODE_EXECUTABLE;
  const mac = "/Applications/Visual Studio Code.app/Contents/MacOS/Code";
  return fs.existsSync(mac) ? mac : undefined;   // undefined: test-electron downloads a VS Code
}

async function main() {
  const home = fs.mkdtempSync(path.join(os.tmpdir(), "spyglass-e2e-"));
  const env = { ...process.env, SPYGLASS_PORT: String(port), SPYGLASS_HOME: home };
  const procs: ChildProcess[] = [];
  const start = (dll: string, args: string[] = []) => {
    const p = spawn("dotnet", [dll, ...args], { env, stdio: ["ignore", "inherit", "inherit"] });
    procs.push(p);
    return p;
  };
  try {
    start(path.join(repo, "src/Spyglass.App/bin/Debug/net10.0/Spyglass.dll"), ["serve", "--port", String(port)]);
    const tokenFile = path.join(home, `hub-${port}.json`);
    for (let i = 0; i < 100 && !fs.existsSync(tokenFile); i++) await new Promise((r) => setTimeout(r, 100));
    start(path.join(repo, "samples/Spyglass.Sample/bin/Debug/net10.0/Spyglass.Sample.dll"));

    await runTests({
      vscodeExecutablePath: vscodeExecutable(),
      extensionDevelopmentPath: path.resolve(__dirname, "../../.."),
      extensionTestsPath: path.resolve(__dirname, "suite"),
      extensionTestsEnv: { SPYGLASS_PORT: String(port), SPYGLASS_HOME: home, SPYGLASS_E2E_SHOT: path.join(repo, "ide/vscode/.vscode-test/e2e-app.png") },
      launchArgs: [path.join(repo, "samples/Spyglass.Sample"), "--disable-extensions", "--disable-workspace-trust",
                   "--user-data-dir", fs.mkdtempSync(path.join(os.tmpdir(), "spyglass-vscode-"))],
    });
    console.log("E2E passed");
  } catch (e) {
    console.error("E2E failed:", e);
    process.exitCode = 1;
  } finally {
    for (const p of procs) p.kill();
  }
}

void main();
