using System.Diagnostics;
using System.Text.Json;

namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>What `dotnet msbuild -getProperty` says about the project that owns an .axaml (design/preview/README.md §2).</summary>
public sealed record PreviewProject(string ProjectPath, string ToolPath, string TargetPath, string RuntimeConfigPath, string DepsPath)
{
    public string ProjectDirectory => Path.GetDirectoryName(ProjectPath)!;

    /// <summary>The .axaml path relative to the project, with forward slashes (UpdateXamlMessage.XamlFileProjectPath).</summary>
    public string RelativeXamlPath(string xamlPath) =>
        Path.GetRelativePath(ProjectDirectory, xamlPath).Replace('\\', '/');
}

/// <summary>A project lookup result: either a project or the reason there is none (and whether building would help).</summary>
public sealed record PreviewProjectResult(PreviewProject? Project, string? Error, bool CanBuild)
{
    public static PreviewProjectResult Ok(PreviewProject p) => new(p, null, false);
    public static PreviewProjectResult Fail(string error, bool canBuild = false) => new(null, error, canBuild);
}

public static class PreviewProjectResolver
{
    static readonly string[] Properties =
        ["AvaloniaPreviewerNetCoreToolPath", "TargetPath", "ProjectRuntimeConfigFilePath", "ProjectDepsFilePath"];

    /// <summary>The nearest .csproj in the file's directory or above.</summary>
    public static string? FindProject(string xamlPath)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(xamlPath));
        while (dir is not null)
        {
            try
            {
                var found = Directory.EnumerateFiles(dir, "*.csproj").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault();
                if (found is not null) return found;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>Parses the output of `dotnet msbuild -getProperty:...` and checks the files exist.</summary>
    public static PreviewProjectResult Parse(string projectPath, string json)
    {
        Dictionary<string, string> props = new(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("Properties", out var p))
                foreach (var prop in p.EnumerateObject())
                    props[prop.Name] = prop.Value.ValueKind == JsonValueKind.String ? prop.Value.GetString() ?? "" : "";
        }
        catch (JsonException) { return PreviewProjectResult.Fail("Could not read the project properties (unexpected `dotnet msbuild` output)."); }

        string Get(string n) => props.TryGetValue(n, out var v) ? v.Trim() : "";
        var tool = Get("AvaloniaPreviewerNetCoreToolPath");
        var target = Get("TargetPath");
        var runtimeConfig = Get("ProjectRuntimeConfigFilePath");
        var deps = Get("ProjectDepsFilePath");

        if (tool.Length == 0)
            return PreviewProjectResult.Fail("This project does not reference Avalonia (no previewer found), or its packages are not restored.");
        if (target.Length == 0 || runtimeConfig.Length == 0 || deps.Length == 0)
            return PreviewProjectResult.Fail("This is not an executable project: the preview needs an app with an entry point.");

        tool = Path.GetFullPath(tool);   // MSBuild leaves "buildTransitive//../tools/..." in the path
        if (!File.Exists(tool))
            return PreviewProjectResult.Fail("The Avalonia previewer is missing; restore the project's NuGet packages.", canBuild: true);
        if (!File.Exists(target) || !File.Exists(runtimeConfig) || !File.Exists(deps))
            return PreviewProjectResult.Fail("The project is not built yet. Build it to see the preview.", canBuild: true);
        return PreviewProjectResult.Ok(new PreviewProject(projectPath, tool, target, runtimeConfig, deps));
    }

    public static async Task<PreviewProjectResult> ResolveAsync(string xamlPath, string dotnet, CancellationToken ct)
    {
        var project = FindProject(xamlPath);
        if (project is null) return PreviewProjectResult.Fail("No .csproj found above this file.");

        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(project)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("msbuild");
        psi.ArgumentList.Add(project);
        foreach (var p in Properties) psi.ArgumentList.Add("-getProperty:" + p);

        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var json = await stdout.ConfigureAwait(false);
            if (proc.ExitCode != 0)
            {
                var err = (await stderr.ConfigureAwait(false)).Trim();
                if (err.Length == 0) err = json.Trim();
                return PreviewProjectResult.Fail("`dotnet msbuild` failed: " + (err.Length > 300 ? err[..300] + "…" : err));
            }
            return Parse(project, json);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return PreviewProjectResult.Fail($"Could not run `{dotnet}`: {ex.Message}"); }
    }

    /// <summary>`dotnet build` for the "Build project" button; returns the tail of the output on failure.</summary>
    public static async Task<(bool Ok, string Output)> BuildAsync(string projectPath, string dotnet, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = Path.GetDirectoryName(projectPath)!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("build");
        psi.ArgumentList.Add(projectPath);
        psi.ArgumentList.Add("-nologo");
        psi.ArgumentList.Add("-v:q");
        try
        {
            using var proc = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start");
            var stdout = proc.StandardOutput.ReadToEndAsync(ct);
            var stderr = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct).ConfigureAwait(false);
            var text = (await stdout.ConfigureAwait(false)) + (await stderr.ConfigureAwait(false));
            return (proc.ExitCode == 0, text.Length > 1500 ? text[^1500..] : text);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
