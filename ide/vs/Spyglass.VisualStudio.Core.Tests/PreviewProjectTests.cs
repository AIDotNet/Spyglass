using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class PreviewProjectTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-preview-" + Guid.NewGuid().ToString("N"));

    public PreviewProjectTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    string Touch(string name) { var p = Path.Combine(_dir, name); Directory.CreateDirectory(Path.GetDirectoryName(p)!); File.WriteAllText(p, ""); return p; }

    static string Json(string tool, string target, string rc, string deps) =>
        System.Text.Json.JsonSerializer.Serialize(new
        {
            Properties = new Dictionary<string, string>
            {
                ["AvaloniaPreviewerNetCoreToolPath"] = tool, ["TargetPath"] = target,
                ["ProjectRuntimeConfigFilePath"] = rc, ["ProjectDepsFilePath"] = deps,
            },
        });

    [Fact]
    public void Parse_reads_the_four_properties_and_normalizes_the_tool_path()
    {
        var tool = Touch("pkg/tools/net8.0/designer/HostApp.dll");
        var messy = Path.Combine(_dir, "pkg", "buildTransitive") + "//../tools/net8.0/designer/HostApp.dll";
        var r = PreviewProjectResolver.Parse(Path.Combine(_dir, "App.csproj"),
            Json(messy, Touch("bin/App.dll"), Touch("bin/App.runtimeconfig.json"), Touch("bin/App.deps.json")));
        Assert.NotNull(r.Project);
        Assert.Equal(Path.GetFullPath(tool), r.Project!.ToolPath);
        Assert.Equal(_dir, r.Project.ProjectDirectory);
        Assert.Equal("Views/Main.axaml", r.Project.RelativeXamlPath(Path.Combine(_dir, "Views", "Main.axaml")));
    }

    [Fact]
    public void Parse_without_a_previewer_says_the_project_is_not_avalonia()
    {
        var r = PreviewProjectResolver.Parse("x.csproj", Json("", "", "", ""));
        Assert.Null(r.Project);
        Assert.Contains("Avalonia", r.Error);
        Assert.False(r.CanBuild);
    }

    [Fact]
    public void Parse_of_a_library_says_it_is_not_executable()
    {
        var tool = Touch("t/HostApp.dll");
        var r = PreviewProjectResolver.Parse("x.csproj", Json(tool, Touch("bin/Lib.dll"), "", ""));
        Assert.Null(r.Project);
        Assert.Contains("executable", r.Error);
    }

    [Fact]
    public void Parse_of_an_unbuilt_project_offers_to_build()
    {
        var tool = Touch("t/HostApp.dll");
        var r = PreviewProjectResolver.Parse("x.csproj", Json(tool, Path.Combine(_dir, "bin", "App.dll"), Path.Combine(_dir, "bin", "App.runtimeconfig.json"), Path.Combine(_dir, "bin", "App.deps.json")));
        Assert.Null(r.Project);
        Assert.True(r.CanBuild);
        Assert.Contains("built", r.Error);
    }

    [Fact]
    public void Parse_survives_garbage()
    {
        var r = PreviewProjectResolver.Parse("x.csproj", "not json");
        Assert.Null(r.Project);
        Assert.NotNull(r.Error);
    }

    [Fact]
    public void FindProject_walks_up_to_the_nearest_csproj()
    {
        Touch("App/App.csproj");
        var xaml = Touch("App/Views/Deep/Main.axaml");
        Assert.Equal(Path.Combine(_dir, "App", "App.csproj"), PreviewProjectResolver.FindProject(xaml));
    }

    [Fact]
    public async Task Resolve_reports_a_missing_project_without_running_dotnet()
    {
        var xaml = Touch("loose/Main.axaml");
        // No .csproj anywhere above the temp dir is the normal case; if one exists, the lookup is still deterministic.
        var found = PreviewProjectResolver.FindProject(xaml);
        if (found is not null) return;
        var r = await PreviewProjectResolver.ResolveAsync(xaml, "dotnet", CancellationToken.None);
        Assert.Null(r.Project);
        Assert.Contains("csproj", r.Error);
    }
}
