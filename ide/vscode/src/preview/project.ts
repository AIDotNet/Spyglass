// Finds the project of a .axaml file and the MSBuild properties the Avalonia previewer host needs
// (design/preview/README.md §2). No VS Code dependency.
import { execFile } from "child_process";
import * as fs from "fs";
import * as path from "path";

export interface PreviewProject {
  projectFile: string;
  projectDir: string;
  hostApp: string;
  targetPath: string;
  runtimeConfig: string;
  depsFile: string;
}

export class PreviewProjectError extends Error {
  constructor(message: string, readonly kind: "no-project" | "not-avalonia" | "not-built" | "msbuild") { super(message); }
}

const PROPS = ["AvaloniaPreviewerNetCoreToolPath", "TargetPath", "ProjectRuntimeConfigFilePath", "ProjectDepsFilePath"];

/** Nearest .csproj in the file's directory or an ancestor (stops at the filesystem root). */
export function findProjectFile(xamlPath: string): string | undefined {
  let dir = path.dirname(xamlPath);
  for (;;) {
    try {
      const hit = fs.readdirSync(dir).filter((f) => f.toLowerCase().endsWith(".csproj")).sort()[0];
      if (hit) return path.join(dir, hit);
    } catch { /* unreadable directory: keep walking up */ }
    const up = path.dirname(dir);
    if (up === dir) return undefined;
    dir = up;
  }
}

/** Parses `dotnet msbuild -getProperty:…` JSON output, checking that the build artefacts exist. */
export function parseMsbuildProperties(projectFile: string, stdout: string, exists: (p: string) => boolean = fs.existsSync): PreviewProject {
  let props: Record<string, string>;
  try {
    const start = stdout.indexOf("{");
    props = JSON.parse(stdout.slice(start)).Properties ?? {};
  } catch {
    throw new PreviewProjectError(`Could not read MSBuild output for ${path.basename(projectFile)}.`, "msbuild");
  }
  const host = props.AvaloniaPreviewerNetCoreToolPath?.trim();
  if (!host) throw new PreviewProjectError(`${path.basename(projectFile)} is not an Avalonia project (no previewer host; restore the project first).`, "not-avalonia");
  const targetPath = props.TargetPath?.trim();
  const runtimeConfig = props.ProjectRuntimeConfigFilePath?.trim();
  const depsFile = props.ProjectDepsFilePath?.trim();
  if (!targetPath || !runtimeConfig || !depsFile)
    throw new PreviewProjectError(`${path.basename(projectFile)} is not an executable project; the preview needs an application project.`, "not-avalonia");
  const hostApp = path.normalize(host);
  if (!exists(hostApp)) throw new PreviewProjectError(`The Avalonia previewer host was not found at ${hostApp}. Restore the project.`, "not-avalonia");
  if (![targetPath, runtimeConfig, depsFile].every(exists))
    throw new PreviewProjectError(`${path.basename(targetPath)} has not been built yet.`, "not-built");
  return { projectFile, projectDir: path.dirname(projectFile), hostApp, targetPath, runtimeConfig, depsFile };
}

export function resolveProject(xamlPath: string, dotnet = "dotnet"): Promise<PreviewProject> {
  const projectFile = findProjectFile(xamlPath);
  if (!projectFile) return Promise.reject(new PreviewProjectError("This file is not inside a .NET project.", "no-project"));
  return new Promise((resolve, reject) => {
    execFile(dotnet, ["msbuild", projectFile, ...PROPS.map((p) => `-getProperty:${p}`)], { maxBuffer: 4 << 20, timeout: 60000 }, (err, stdout, stderr) => {
      if (err && !stdout.includes("{")) { reject(new PreviewProjectError(`dotnet msbuild failed: ${(stderr || err.message).trim().split("\n")[0]}`, "msbuild")); return; }
      try { resolve(parseMsbuildProperties(projectFile, stdout)); } catch (e) { reject(e); }
    });
  });
}

/** Path of the .axaml relative to the project directory, with forward slashes (UpdateXamlMessage.XamlFileProjectPath). */
export function projectRelativePath(project: PreviewProject, xamlPath: string): string {
  return path.relative(project.projectDir, xamlPath).split(path.sep).join("/");
}
