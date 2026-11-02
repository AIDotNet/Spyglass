using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class PreviewControllerTests(SampleProjectFixture sample) : IClassFixture<SampleProjectFixture>
{
    static async Task<T> Within<T>(TaskCompletionSource<T> tcs, string what, int ms = 30000)
    {
        var done = await Task.WhenAny(tcs.Task, Task.Delay(ms));
        Assert.True(done == tcs.Task, $"Timed out waiting for {what}.");
        return await tcs.Task;
    }

    static TaskCompletionSource<T> Tcs<T>() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    [Fact]
    public async Task Edits_are_debounced_into_one_update()
    {
        var calls = 0;
        await using var c = new PreviewController(new PreviewControllerOptions
        {
            DelayMs = 150,
            ResolveProject = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(PreviewProjectResult.Fail("n/a")); },
        });
        c.Show("/a/Main.axaml", () => "<Window/>");
        await Task.Delay(900);
        Assert.Equal(1, calls);   // Show updates at once

        for (var i = 0; i < 5; i++) { c.TextChanged("/a/Main.axaml"); await Task.Delay(30); }
        c.TextChanged("/other/File.axaml");   // not the active document: ignored
        await Task.Delay(1000);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task Missing_project_state_is_reported_with_a_build_offer()
    {
        var status = Tcs<PreviewStatus>();
        await using var c = new PreviewController(new PreviewControllerOptions
        {
            ResolveProject = (_, _) => Task.FromResult(PreviewProjectResult.Fail("The project is not built yet.", canBuild: true)),
        });
        c.StatusChanged += s => status.TrySetResult(s);
        c.Show("/a/Main.axaml", () => "<Window/>");
        var s = await Within(status, "a status");
        Assert.Equal(PreviewStatusKind.Error, s.Kind);
        Assert.True(s.CanBuild);
    }

    [Fact]
    public async Task Shows_the_sample_reports_errors_with_position_and_recovers()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        var text = File.ReadAllText(sample.MainWindow);
        var log = new List<string>();
        await using var c = new PreviewController(new PreviewControllerOptions { DelayMs = 50, Log = l => { lock (log) log.Add(l); } });
        var frame = Tcs<PreviewFrame>();
        var running = Tcs<PreviewStatus>();
        var diag = new System.Collections.Concurrent.ConcurrentQueue<(string, PreviewDiagnostic?)>();
        var bad = Tcs<PreviewDiagnostic>();
        var cleared = Tcs<bool>();
        c.FrameReady += f => frame.TrySetResult(f);
        c.StatusChanged += s => { if (s.Kind == PreviewStatusKind.Running && s.XamlError is null) running.TrySetResult(s); };
        c.DiagnosticsChanged += (p, d) => { if (d is not null) bad.TrySetResult(d); else if (bad.Task.IsCompleted) cleared.TrySetResult(true); };

        c.SetViewport(640, 420);
        var current = text;
        c.Show(sample.MainWindow, () => current);
        await Within(running, "Live status");
        var f = await Within(frame, "a frame");
        Assert.True(f.Width > 0 && f.Height > 0);

        current = "<Window xmlns=\"https://github.com/avaloniaui\">\n  <StackPanel>\n    <Button Content=\"x\">\n  </StackPanel>\n</Window>";
        c.TextChanged(sample.MainWindow);
        var d = await Within(bad, "a diagnostic for broken XAML");
        Assert.Equal(sample.MainWindow, d.Path);
        Assert.True(d.Line >= 1 && d.Column >= 1);
        Assert.Equal(PreviewStatusKind.Running, c.Status.Kind);
        Assert.NotNull(c.Status.XamlError);

        current = text;
        c.TextChanged(sample.MainWindow);
        await Within(cleared, "the diagnostic to clear");
        Assert.Null(c.Status.XamlError);

        await c.CloseAsync();
        Assert.Equal(PreviewStatusKind.Idle, c.Status.Kind);
    }

    [Fact]
    public async Task User_control_renders_at_its_design_size_and_switching_files_updates()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        await using var c = new PreviewController(new PreviewControllerOptions { DelayMs = 50 });
        var sizes = new System.Collections.Concurrent.ConcurrentQueue<(int, int)>();
        var first = Tcs<bool>();
        var second = Tcs<bool>();
        c.FrameReady += f =>
        {
            sizes.Enqueue((f.Width, f.Height));
            if (f.Width == 300 && f.Height == 150) first.TrySetResult(true);
            if (f.Width == 120 && f.Height == 80) second.TrySetResult(true);
        };
        c.SetViewport(320, 200);
        c.Show(sample.MainWindow, () => "<UserControl xmlns=\"https://github.com/avaloniaui\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" d:DesignWidth=\"300\" d:DesignHeight=\"150\"><Border Background=\"Tomato\"/></UserControl>");
        await Within(first, $"a 300x150 frame (saw: {string.Join(", ", sizes)}; now {c.Status})");

        // The user switches to another .axaml of the same project: same host, new XAML.
        var other = Path.Combine(Path.GetDirectoryName(sample.MainWindow)!, "App.axaml");
        c.Show(other, () => "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"120\" Height=\"80\"><Border Background=\"Teal\"/></Window>");
        Assert.Equal(other, c.ActivePath);
        await Within(second, $"a 120x80 frame (saw: {string.Join(", ", sizes)}; now {c.Status})");
    }

    static double Brightness(PreviewFrame f)
    {
        // Mean luminance (0-255) of a BGRA frame.
        double sum = 0;
        long n = 0;
        for (var y = 0; y < f.Height; y++)
            for (var x = 0; x < f.Width; x++)
            {
                var i = y * f.Stride + x * 4;
                sum += 0.114 * f.Data[i] + 0.587 * f.Data[i + 1] + 0.299 * f.Data[i + 2];
                n++;
            }
        return n == 0 ? 0 : sum / n;
    }

    [Fact]
    public async Task Theme_changes_what_the_previewer_draws_and_error_columns_stay_in_editor_coordinates()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        await using var c = new PreviewController(new PreviewControllerOptions { DelayMs = 0 });
        PreviewFrame? last = null;
        c.FrameReady += f => last = f;
        var xaml = "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\"/>";   // no Background of its own

        // The host may send one more frame of the old picture before the result of a new XAML: wait for the result, settle,
        // and only then look at the newest frame.
        async Task<double> Render(Action trigger)
        {
            var result = Tcs<bool>();
            void OnStatus(PreviewStatus s) { if (s.Kind == PreviewStatusKind.Running && s.XamlError is null) result.TrySetResult(true); }
            c.StatusChanged += OnStatus;
            trigger();
            await Within(result, "the render result");
            c.StatusChanged -= OnStatus;
            await Task.Delay(1000);
            return Brightness(last!);
        }

        c.SetViewport(300, 200);
        var light = await Render(() => { c.Theme = PreviewTheme.Light; c.Show(sample.MainWindow, () => xaml); });
        var dark = await Render(() => c.Theme = PreviewTheme.Dark);
        Assert.True(light - dark > 40, $"light {light:0} vs dark {dark:0}: the themes must differ by more than 40 in brightness");

        // A Window that has its own Background keeps it in either theme.
        xaml = "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\" Background=\"Tomato\"/>";
        var tomatoDark = await Render(() => c.TextChanged(sample.MainWindow));
        c.Theme = PreviewTheme.Light;
        var tomatoLight = await Render(() => c.Refresh());
        Assert.True(Math.Abs(tomatoDark - tomatoLight) < 5, $"own Background unchanged by theme ({tomatoDark:0} vs {tomatoLight:0})");

        // A broken root line: the reported column is the editor's, not the one in the text the host saw.
        var bad = Tcs<PreviewDiagnostic>();
        c.DiagnosticsChanged += (_, d) => { if (d is not null) bad.TrySetResult(d); };
        xaml = "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\" Bogus=\"1\">\n</Window>";
        c.TextChanged(sample.MainWindow);
        var d = await Within(bad, "an error for an unknown attribute");
        Assert.Equal(1, d.Line);
        Assert.True(d.Column <= xaml.IndexOf('\n'), $"column {d.Column} is inside the editor's first line");
        Assert.Equal(d.Line, c.Status.ErrorLine);
    }

    [Fact]
    public async Task Status_reports_updating_then_the_time_it_took()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<PreviewStatus>();
        var updated = Tcs<PreviewStatus>();
        await using var c = new PreviewController(new PreviewControllerOptions { DelayMs = 0 });
        c.StatusChanged += s => { seen.Enqueue(s); if (s.Kind == PreviewStatusKind.Running && s.XamlError is null) updated.TrySetResult(s); };
        c.Show(sample.MainWindow, () => "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\"/>");
        var s = await Within(updated, "an Updated status");
        Assert.Matches(@"^Updated · \d+ ms$", s.Message);
        var kinds = seen.Select(x => x.Kind).ToList();
        Assert.Contains(PreviewStatusKind.Starting, kinds);
        Assert.True(kinds.IndexOf(PreviewStatusKind.Updating) is var u && u >= 0 && u < kinds.IndexOf(PreviewStatusKind.Running), "Updating comes before the result");
    }

    /// <summary>The sample's build output with every file symlinked except the app assembly, which is a private copy.</summary>
    static PreviewProject? CopyOfSampleBuild(PreviewProject real, string dir)
    {
        try
        {
            Directory.CreateDirectory(dir);
            var src = Path.GetDirectoryName(real.TargetPath)!;
            foreach (var f in Directory.GetFiles(src))
            {
                var dst = Path.Combine(dir, Path.GetFileName(f));
                if (f == real.TargetPath || f == real.RuntimeConfigPath || f == real.DepsPath) File.Copy(f, dst, true);
                else File.CreateSymbolicLink(dst, f);
            }
            foreach (var d in Directory.GetDirectories(src)) Directory.CreateSymbolicLink(Path.Combine(dir, Path.GetFileName(d)), d);
            return real with
            {
                TargetPath = Path.Combine(dir, Path.GetFileName(real.TargetPath)),
                RuntimeConfigPath = Path.Combine(dir, Path.GetFileName(real.RuntimeConfigPath)),
                DepsPath = Path.Combine(dir, Path.GetFileName(real.DepsPath)),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return null; }
    }

    [Fact]
    public async Task Rebuilding_the_app_restarts_the_previewer_and_keeps_file_theme_and_viewport()
    {
        if (sample.SkipReason is { } skip) Assert.Skip(skip);
        var dir = Path.Combine(Path.GetTempPath(), "spyglass-rebuild-" + Guid.NewGuid().ToString("N"));
        try
        {
            var project = CopyOfSampleBuild(sample.Project!, dir);
            if (project is null) { Assert.Skip("Cannot create symbolic links here."); return; }

            var log = new List<string>();
            var starts = 0;
            var running = Tcs<bool>();
            var restarted = Tcs<bool>();
            await using var c = new PreviewController(new PreviewControllerOptions
            {
                DelayMs = 0,
                RebuildDelay = TimeSpan.FromMilliseconds(300),
                ResolveProject = (_, _) => Task.FromResult(PreviewProjectResult.Ok(project)),
                Log = l =>
                {
                    lock (log) log.Add(l);
                    if (l.StartsWith("Starting previewer", StringComparison.Ordinal) && Interlocked.Increment(ref starts) == 2) restarted.TrySetResult(true);
                },
            });
            PreviewFrame? last = null;
            c.FrameReady += f => last = f;
            c.StatusChanged += s => { if (s.Kind == PreviewStatusKind.Running && s.XamlError is null) running.TrySetResult(true); };

            c.Theme = PreviewTheme.Dark;
            c.SetViewport(400, 300);
            c.Show(sample.MainWindow, () => "<Window xmlns=\"https://github.com/avaloniaui\" Width=\"60\" Height=\"40\"/>");
            await Within(running, "the first render");
            await Task.Delay(600);
            var dark = Brightness(last!);
            Assert.True(dark < 100, $"dark theme drawn before the rebuild (brightness {dark:0})");

            // "Rebuild": the assembly is rewritten.
            running = Tcs<bool>();
            File.Copy(sample.Project!.TargetPath, project.TargetPath, true);
            File.SetLastWriteTimeUtc(project.TargetPath, DateTime.UtcNow.AddMinutes(1));
            await Within(restarted, $"a second previewer start (log: {string.Join(" | ", log)})");
            await Within(running, "a render after the restart");
            await Task.Delay(800);

            Assert.Equal(sample.MainWindow, c.ActivePath);   // the same file is shown again
            Assert.Equal(PreviewTheme.Dark, c.Theme);
            Assert.True(Math.Abs(dark - Brightness(last!)) < 5, "still the dark theme after the restart");
            Assert.Equal(2, starts);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
