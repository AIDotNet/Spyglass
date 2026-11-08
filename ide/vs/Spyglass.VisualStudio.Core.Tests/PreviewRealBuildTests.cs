using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>
/// A real `dotnet build` of the sample while a previewer is running. They rewrite the sample's bin/, which other tests use, so
/// they run only in their own CI step (vs-extension.yml), with SPYGLASS_REAL_BUILD_REPORT set to a file for the results.
/// </summary>
[Trait("Category", "RealBuild")]
public sealed class PreviewRealBuildTests(SampleProjectFixture sample) : IClassFixture<SampleProjectFixture>
{
    const string Xaml = "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\"/>";
    static readonly string? Report = Environment.GetEnvironmentVariable("SPYGLASS_REAL_BUILD_REPORT");

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    static async Task Within(Task task, string what, int ms = 120000)
    {
        Assert.True(await Task.WhenAny(task, Task.Delay(ms)) == task, $"Timed out waiting for {what}.");
        await task;
    }

    PreviewProject Project()
    {
        if (string.IsNullOrEmpty(Report)) Assert.Skip("Runs only in the CI step that sets SPYGLASS_REAL_BUILD_REPORT.");
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        return sample.Project!;
    }

    /// <summary>Touches a source file so that the build compiles and copies the assembly to bin/ again.</summary>
    async Task<(bool Ok, string Output)> BuildAfterTouch(PreviewProject project)
    {
        File.SetLastWriteTimeUtc(Path.Combine(project.ProjectDirectory, "Program.cs"), DateTime.UtcNow);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        return await PreviewProjectResolver.BuildAsync(project.ProjectPath, "dotnet", cts.Token);
    }

    static void Write(string title, bool ok, string output)
    {
        var errors = output.Split('\n').Where(l => l.Contains(": error ", StringComparison.Ordinal)).Select(l => l.Trim()).Distinct().Take(5);
        File.AppendAllLines(Report!, [$"## {title}", $"dotnet build: {(ok ? "succeeded" : "failed")}", .. errors, ""]);
    }

    /// <summary>The command line the VS Code and Rider extensions use: the previewer runs from bin/ itself.</summary>
    [Fact]
    public async Task A_previewer_running_from_bin_makes_the_build_fail_on_windows()
    {
        var project = Project();
        if (!OperatingSystem.IsWindows()) Assert.Skip("Only Windows locks the assemblies of a running process.");

        var rendered = Tcs<bool>();
        await using var session = new PreviewSession(project);
        session.XamlResult += r => { if (r.Error is null) rendered.TrySetResult(true); else rendered.TrySetException(new InvalidOperationException(r.Error)); };
        await session.StartAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        await session.SetViewportAsync(400, 300, 96, 96);
        await session.UpdateXamlAsync(Xaml, sample.MainWindow);
        await Within(rendered.Task, "the first render");

        var (ok, output) = await BuildAfterTouch(project);

        Write("Previewer running from bin/ (VS Code and Rider command line)", ok, output);
        Assert.False(ok, "The build succeeded, so the lock was not reproduced.");
        Assert.True(output.Contains("MSB3021", StringComparison.Ordinal) || output.Contains("MSB3027", StringComparison.Ordinal), output);
    }

    /// <summary>The Visual Studio extension: the previewer runs from a copy, so the build works and the preview restarts.</summary>
    [Fact]
    public async Task The_controller_lets_the_build_succeed_and_restarts_the_previewer()
    {
        var project = Project();
        var log = new List<string>();
        var starts = 0;
        var restarted = Tcs<bool>();
        var running = Tcs<bool>();
        await using var c = new PreviewController(new PreviewControllerOptions
        {
            DelayMs = 0,
            ResolveProject = (_, _) => Task.FromResult(PreviewProjectResult.Ok(project)),
            Log = l =>
            {
                lock (log) log.Add(l);
                if (l.StartsWith("Starting previewer", StringComparison.Ordinal) && Interlocked.Increment(ref starts) == 2) restarted.TrySetResult(true);
            },
        });
        c.StatusChanged += s => { if (s.Kind == PreviewStatusKind.Running && s.XamlError is null) running.TrySetResult(true); };
        c.SetViewport(400, 300);
        c.Show(sample.MainWindow, () => Xaml);
        await Within(running.Task, "the first render");
        running = Tcs<bool>();

        var (ok, output) = await BuildAfterTouch(project);

        Write("Previewer running from a copy (Visual Studio extension)", ok, output);
        Assert.True(ok, output);
        await Within(restarted.Task, "a second previewer start");
        await Within(running.Task, "a render after the restart");
        File.AppendAllLines(Report!, ["The previewer restarted and rendered after the build.", ""]);
    }
}
