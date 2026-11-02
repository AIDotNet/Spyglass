using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

/// <summary>Resolves the sample project once and shares it; the tests are skipped when the sample is not built.</summary>
public sealed class SampleProjectFixture : IAsyncLifetime
{
    public string? RepoRoot { get; } = FindRepoRoot();
    public string MainWindow => Path.Combine(RepoRoot!, "samples", "Spyglass.Sample", "MainWindow.axaml");
    public PreviewProject? Project { get; private set; }
    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (RepoRoot is null || !File.Exists(MainWindow)) { SkipReason = "samples/Spyglass.Sample not found."; return; }
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var r = await PreviewProjectResolver.ResolveAsync(MainWindow, "dotnet", cts.Token);
        if (r.Project is null) SkipReason = "Build samples/Spyglass.Sample (Debug) first: " + r.Error;
        Project = r.Project;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    static string? FindRepoRoot()
    {
        if (Environment.GetEnvironmentVariable("SPYGLASS_REPO") is { Length: > 0 } r) return r;
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "Spyglass.slnx")) && Directory.Exists(Path.Combine(d.FullName, "src"))) return d.FullName;
        return null;
    }
}

/// <summary>The real Avalonia.Designer.HostApp from the sample's Avalonia package: handshake, frames, error positions.</summary>
public sealed class PreviewSessionTests(SampleProjectFixture sample) : IClassFixture<SampleProjectFixture>
{
    static async Task<T> Within<T>(TaskCompletionSource<T> tcs, string what, int ms = 30000)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms));
        Assert.True(done == tcs.Task, $"Timed out waiting for {what}.");
        return await tcs.Task;
    }

    [Fact]
    public async Task Renders_a_frame_and_reports_xaml_errors_with_position()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        var log = new List<string>();
        await using var session = new PreviewSession(sample.Project!, new PreviewSessionOptions { Log = l => { lock (log) log.Add(l); } });

        var frame = new TaskCompletionSource<PreviewFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        var results = new System.Collections.Concurrent.ConcurrentQueue<PreviewXamlResult>();
        var result = new TaskCompletionSource<PreviewXamlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Frame += f => frame.TrySetResult(f);
        session.XamlResult += r => { results.Enqueue(r); result.TrySetResult(r); };

        await session.StartAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        await session.SetViewportAsync(600, 400);
        await session.UpdateXamlAsync(File.ReadAllText(sample.MainWindow), sample.MainWindow);

        var ok = await Within(result, "UpdateXamlResult for the good XAML");
        Assert.True(ok.Error is null, $"Unexpected error: {ok.Error}\n{string.Join("\n", log)}");
        var f = await Within(frame, "the first frame");
        Assert.True(f.Width > 0 && f.Height > 0);
        Assert.Equal(f.Width * 4, f.Stride);
        Assert.Equal(f.Stride * f.Height, f.Data.Length);
        Assert.Contains(f.Data, b => b != 0);   // something was drawn

        // Broken XAML: an error with a line and column; the session survives.
        var bad = new TaskCompletionSource<PreviewXamlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.XamlResult += r => { if (r.Error is not null) bad.TrySetResult(r); };
        await session.UpdateXamlAsync("<Window xmlns=\"https://github.com/avaloniaui\">\n  <StackPanel>\n    <Button Content=\"x\">\n  </StackPanel>\n</Window>", sample.MainWindow);
        var err = await Within(bad, "an error result for broken XAML");
        Assert.NotNull(err.Line);
        Assert.NotNull(err.Column);
        Assert.True(err.Line >= 1 && err.Column >= 1);

        // And recovers.
        var good = new TaskCompletionSource<PreviewXamlResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.XamlResult += r => { if (r.Error is null) good.TrySetResult(r); };
        await session.UpdateXamlAsync(File.ReadAllText(sample.MainWindow), sample.MainWindow);
        await Within(good, "a successful update after the error");
    }

    [Fact]
    public async Task Disposing_kills_the_host_process()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        var session = new PreviewSession(sample.Project!);
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += c => exited.TrySetResult(c);
        await session.StartAsync(TimeSpan.FromSeconds(60), CancellationToken.None);
        Assert.False(session.HasExited);
        await session.DisposeAsync();
        await Within(exited, "the exit event", 10000);
        Assert.True(session.HasExited);
    }
}
