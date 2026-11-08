using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class PreviewShadowCopyTests : IAsyncDisposable
{
    readonly string _temp = Path.Combine(Path.GetTempPath(), "spyglass-shadow-test-" + Guid.NewGuid().ToString("N"));
    readonly string _bin;
    readonly PreviewShadowCopy _shadow;
    readonly PreviewProject _project;

    public PreviewShadowCopyTests()
    {
        _bin = Path.Combine(_temp, "app", "bin");
        _shadow = new PreviewShadowCopy(Path.Combine(_temp, "copies"));
        _project = new PreviewProject(Path.Combine(_temp, "app", "App.csproj"), Path.Combine(_temp, "nuget", "HostApp.dll"),
            Write("App.dll"), Write("App.runtimeconfig.json"), Write("App.deps.json"));
    }

    string Write(string relative, string text = "x")
    {
        var path = Path.Combine(_bin, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    public async ValueTask DisposeAsync()
    {
        await _shadow.DeleteAsync();
        try { Directory.Delete(_temp, true); } catch (IOException) { }
    }

    [Fact]
    public async Task Paths_move_to_the_copy_except_those_outside_the_output()
    {
        var copy = await _shadow.SyncAsync(_project);

        Assert.NotEqual(_project.TargetPath, copy.TargetPath);
        Assert.True(File.Exists(copy.TargetPath));
        Assert.True(File.Exists(copy.RuntimeConfigPath));
        Assert.True(File.Exists(copy.DepsPath));
        Assert.Equal(_project.ToolPath, copy.ToolPath);
        Assert.Equal(_project.ProjectPath, copy.ProjectPath);
        Assert.Equal("Views/Main.axaml", copy.RelativeXamlPath(Path.Combine(_project.ProjectDirectory, "Views", "Main.axaml")));
    }

    [Fact]
    public async Task Subfolders_are_copied_and_only_this_platforms_runtimes()
    {
        var rid = (OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux") + "-"
            + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        Write("zh-Hans/App.resources.dll");
        Write($"runtimes/{rid}/native/lib.bin");
        Write("runtimes/other-os-x64/native/lib.bin");
        // Not the top-level runtimes folder: not filtered.
        Write("content/runtimes/other-os-x64/data.txt");

        var root = Path.GetDirectoryName((await _shadow.SyncAsync(_project)).TargetPath)!;

        Assert.True(File.Exists(Path.Combine(root, "zh-Hans", "App.resources.dll")));
        Assert.True(File.Exists(Path.Combine(root, "runtimes", rid, "native", "lib.bin")));
        Assert.False(Directory.Exists(Path.Combine(root, "runtimes", "other-os-x64")));
        Assert.True(File.Exists(Path.Combine(root, "content", "runtimes", "other-os-x64", "data.txt")));
    }

    [Fact]
    public async Task A_second_sync_copies_only_changes_and_removes_what_is_gone()
    {
        var unchanged = Write("Lib.dll", "lib");
        var removed = Write("Old.dll");
        var copy = await _shadow.SyncAsync(_project);
        var root = Path.GetDirectoryName(copy.TargetPath)!;

        // A marker of the same size and time as the source: it survives only if the file is not copied again.
        var copied = Path.Combine(root, "Lib.dll");
        File.WriteAllText(copied, "LIB");
        File.SetLastWriteTimeUtc(copied, File.GetLastWriteTimeUtc(unchanged));
        File.WriteAllText(_project.TargetPath, "rebuilt");
        File.Delete(removed);

        await _shadow.SyncAsync(_project);

        Assert.Equal("LIB", File.ReadAllText(copied));
        Assert.Equal("rebuilt", File.ReadAllText(copy.TargetPath));
        Assert.False(File.Exists(Path.Combine(root, "Old.dll")));
    }

    [Fact]
    public async Task A_missing_output_fails_at_once_and_delete_removes_the_copy()
    {
        var missing = _project with { TargetPath = Path.Combine(_temp, "nowhere", "App.dll") };
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAsync<DirectoryNotFoundException>(() => _shadow.SyncAsync(missing));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(2), "not retried");

        var root = Path.GetDirectoryName((await _shadow.SyncAsync(_project)).TargetPath)!;
        await _shadow.DeleteAsync();
        Assert.False(Directory.Exists(root));
    }

    [Theory]
    [InlineData("win-x64", "win", "X64", true)]
    [InlineData("win", "win", "X64", true)]
    [InlineData("win10-x64", "win", "X64", true)]
    [InlineData("win-arm64", "win", "X64", false)]
    [InlineData("win-arm64", "win", "Arm64", true)]
    [InlineData("linux-x64", "win", "X64", false)]
    [InlineData("osx", "win", "X64", false)]
    [InlineData("unix", "win", "X64", false)]
    [InlineData("unix", "linux", "X64", true)]
    [InlineData("linux-musl-x64", "linux", "X64", true)]
    [InlineData("osx.10.14-x64", "osx", "Arm64", false)]
    [InlineData("osx-arm64", "osx", "Arm64", true)]
    public void Runtime_folders_match_the_os_and_architecture(string rid, string os, string architecture, bool expected) =>
        Assert.Equal(expected, PreviewShadowCopy.IsRuntimeFor(rid, os, architecture));
}
