// A private copy of the app's build output for the previewer to run from. No VS Code dependency.
// On Windows a running process locks the assemblies it loaded, so a previewer running from bin/ makes the next build fail with
// MSB3021/MSB3027 and can never see the rebuild. One copy per preview panel; a restart copies only what changed. Other platforms'
// runtimes/ folders are left out: they are most of a typical Avalonia output and the previewer never loads them.
import { randomUUID } from "crypto";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { PreviewProject } from "./project";

const ATTEMPTS = 10;
const RETRY_MS = 300;
const ORPHAN_AGE_MS = 7 * 24 * 60 * 60 * 1000;
const ARCHITECTURES = new Set(["x64", "x86", "arm64", "arm", "armel", "s390x", "ppc64le", "loongarch64", "riscv64"]);
const RETRIABLE = new Set(["EBUSY", "EPERM", "EACCES", "ENOENT", "ENOTEMPTY"]);

const delay = (ms: number) => new Promise((resolve) => setTimeout(resolve, ms));

/** Whether a runtimes/<rid> folder can be used on this OS family ("win", "osx", "linux") and architecture. */
export function isRuntimeFor(rid: string, osFamily: string, architecture: string): boolean {
  const family = rid.split(/[-.]/)[0];
  if (!family.startsWith(osFamily) && !(osFamily !== "win" && family === "unix")) return false;
  const last = rid.split("-").at(-1)!;
  return !ARCHITECTURES.has(last) || last === architecture;
}

function isRuntimeForThisProcess(rid: string): boolean {
  const family = process.platform === "win32" ? "win" : process.platform === "darwin" ? "osx" : "linux";
  return isRuntimeFor(rid, family, process.arch === "ia32" ? "x86" : process.arch);
}

export class PreviewShadowCopy {
  private readonly directory: string;
  private queue: Promise<unknown> = Promise.resolve();   // a delete and a sync of the same folder must not overlap
  private purged = false;

  constructor(private readonly root = path.join(os.tmpdir(), "spyglass-preview")) {
    this.directory = path.join(root, randomUUID().replace(/-/g, ""));
  }

  /**
   * Brings the copy up to date and returns the project with its paths in the copy. The build may still be writing (and a stopped
   * previewer may still hold its files), so locked files are retried for a few seconds.
   */
  sync(project: PreviewProject): Promise<PreviewProject> {
    return this.enqueue(async () => {
      const source = path.dirname(path.resolve(project.targetPath));
      await this.purgeOrphans();
      for (let attempt = 1; ; attempt++) {
        try {
          await fs.promises.access(source);   // no output at all: fail at once, nothing to wait for
          const expected = new Set<string>();
          await copyTree(source, this.directory, true, false, expected);
          await removeOthers(this.directory, expected);
          break;
        } catch (e) {
          const code = (e as NodeJS.ErrnoException).code ?? "";
          if (attempt >= ATTEMPTS || !RETRIABLE.has(code) || !fs.existsSync(source)) throw e;
          await delay(RETRY_MS);
        }
      }
      return {
        ...project,
        targetPath: this.inCopy(source, project.targetPath),
        runtimeConfig: this.inCopy(source, project.runtimeConfig),
        depsFile: this.inCopy(source, project.depsFile),
      };
    });
  }

  /** Removes the copy. Best effort: a previewer that is still exiting may hold files, and a later copy removes the orphan. */
  delete(): Promise<void> {
    return this.enqueue(async () => {
      for (let attempt = 1; attempt <= ATTEMPTS && fs.existsSync(this.directory); attempt++) {
        try { await fs.promises.rm(this.directory, { recursive: true, force: true }); return; }
        catch { await delay(RETRY_MS); }
      }
    });
  }

  private enqueue<T>(work: () => Promise<T>): Promise<T> {
    const run = this.queue.then(work, work);
    this.queue = run.catch(() => undefined);
    return run;
  }

  private inCopy(source: string, file: string): string {
    const relative = path.relative(source, path.resolve(file));
    return relative.startsWith("..") || path.isAbsolute(relative) ? file : path.join(this.directory, relative);
  }

  /** Removes copies left by a crash, once per panel. A week old: a copy in use by a running preview is not touched. */
  private async purgeOrphans(): Promise<void> {
    if (this.purged) return;
    this.purged = true;
    try {
      for (const name of await fs.promises.readdir(this.root)) {
        const dir = path.join(this.root, name);
        if (Date.now() - (await fs.promises.stat(dir)).mtimeMs > ORPHAN_AGE_MS)
          await fs.promises.rm(dir, { recursive: true, force: true }).catch(() => undefined);
      }
    } catch { /* no copies yet, or one is in use */ }
  }
}

async function copyTree(from: string, to: string, isRoot: boolean, inRuntimes: boolean, expected: Set<string>): Promise<void> {
  await fs.promises.mkdir(to, { recursive: true });
  for (const entry of await fs.promises.readdir(from)) {
    const source = path.join(from, entry);
    const target = path.join(to, entry);
    const info = await fs.promises.stat(source);   // follows links
    if (info.isDirectory()) {
      if (inRuntimes && !isRuntimeForThisProcess(entry)) continue;
      await copyTree(source, target, false, isRoot && entry.toLowerCase() === "runtimes", expected);
      continue;
    }
    expected.add(target);
    const existing = await fs.promises.stat(target).catch(() => undefined);
    if (existing && existing.size === info.size && Math.abs(existing.mtimeMs - info.mtimeMs) < 2) continue;
    await fs.promises.copyFile(source, target);
    await fs.promises.utimes(target, info.atime, info.mtime);
  }
}

/** Removes files gone from the output (another project, a removed reference). */
async function removeOthers(dir: string, expected: Set<string>): Promise<void> {
  for (const entry of await fs.promises.readdir(dir, { withFileTypes: true })) {
    const file = path.join(dir, entry.name);
    if (entry.isDirectory()) await removeOthers(file, expected);
    else if (!expected.has(file)) await fs.promises.rm(file, { force: true });
  }
}
