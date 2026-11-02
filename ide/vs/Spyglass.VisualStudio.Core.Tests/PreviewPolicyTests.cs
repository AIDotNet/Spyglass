using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class PreviewPolicyTests : IDisposable
{
    readonly string _dir = Path.Combine(Path.GetTempPath(), "spyglass-policy-" + Guid.NewGuid().ToString("N"));

    public PreviewPolicyTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    // ---------- auto open ----------

    [Theory]
    [InlineData("/p/Views/Main.axaml", true, true, false, true)]
    [InlineData("/p/Views/MAIN.AXAML", true, true, false, true)]
    [InlineData("/p/Views/Main.axaml", false, true, false, false)]   // setting off (the default)
    [InlineData("/p/Views/Main.axaml", true, false, false, false)]   // preview disabled
    [InlineData("/p/Views/Main.axaml", true, true, true, false)]     // already open: never a second window
    [InlineData("/p/Views/Main.xaml", true, true, false, false)]     // .xaml may be WPF
    [InlineData("/p/Program.cs", true, true, false, false)]
    public void Auto_open_needs_the_setting_an_axaml_and_no_window(string path, bool setting, bool enabled, bool open, bool expected) =>
        Assert.Equal(expected, PreviewPolicy.ShouldAutoOpen(path, setting, enabled, open));

    // ---------- which files refresh the preview ----------

    [Theory]
    [InlineData("/p/app", "/p/app/App.axaml", true)]
    [InlineData("/p/app", "/p/app/Styles/Theme.axaml", true)]
    [InlineData("/p/app", "/p/app/Resources/Brushes.xaml", true)]
    [InlineData("/p/app", "/P/APP/views/Main.axaml", true)]                  // case-insensitive
    [InlineData("/p/app/", "/p/app/Main.axaml", true)]                       // trailing separator
    [InlineData("/p/app", "/p/app2/Main.axaml", false)]                      // sibling with the same prefix
    [InlineData("/p/app", "/p/other/Main.axaml", false)]
    [InlineData("/p/app", "/p/app/Program.cs", false)]                       // not XAML
    [InlineData("/p/app", "/p/app/obj/Debug/net10.0/Generated.axaml", false)]   // build output
    [InlineData("/p/app", "/p/app/bin/Debug/Copied.axaml", false)]
    [InlineData("/p/app", "/p/app/src/binary/Main.axaml", true)]             // "binary" is not "bin"
    [InlineData(null, "/p/app/Main.axaml", false)]
    [InlineData("", "/p/app/Main.axaml", false)]
    public void Project_xaml_is_any_xaml_inside_the_project_but_build_output(string? dir, string path, bool expected) =>
        Assert.Equal(expected, PreviewPolicy.IsProjectXaml(dir, path));

    [Fact]
    public void Project_xaml_accepts_windows_paths()
    {
        if (!OperatingSystem.IsWindows()) return;   // separators are the host OS's
        Assert.True(PreviewPolicy.IsProjectXaml(@"C:\src\app", @"C:\src\app\Styles\Theme.axaml"));
        Assert.False(PreviewPolicy.IsProjectXaml(@"C:\src\app", @"C:\src\app\obj\x.axaml"));
    }

    [Theory]
    [InlineData("<Window xmlns=\"https://github.com/avaloniaui\"/>", true)]
    [InlineData("<UserControl xmlns=\"https://github.com/avaloniaui\"/>", true)]
    [InlineData("<local:MyView xmlns:local=\"clr-namespace:X\"/>", true)]
    [InlineData("<Application xmlns=\"https://github.com/avaloniaui\"/>", false)]
    [InlineData("<?xml version=\"1.0\"?>\n<!-- c -->\n<Application xmlns=\"https://github.com/avaloniaui\"/>", false)]
    [InlineData("<ResourceDictionary xmlns=\"https://github.com/avaloniaui\"/>", false)]
    [InlineData("<Styles xmlns=\"https://github.com/avaloniaui\"/>", false)]
    [InlineData("<x:Application xmlns:x=\"y\"/>", false)]
    [InlineData("", true)]                 // unreadable: let the previewer say why
    [InlineData("not xml at all", true)]
    public void Only_views_are_shown_by_themselves(string xaml, bool previewable) => Assert.Equal(previewable, PreviewPolicy.IsPreviewable(xaml));

    // ---------- window placement ----------

    [Fact]
    public void Placement_is_the_right_third_of_the_ide_window()
    {
        var p = PreviewPlacement.Beside(0, 0, 1800, 1000);
        Assert.Equal(600, p.Width);
        Assert.Equal(920, p.Height);
        Assert.Equal(1800 - 8 - 600, p.Left);
        Assert.Equal(40, p.Top);
        var small = PreviewPlacement.Beside(100, 50, 600, 300);
        Assert.Equal(320, small.Width);    // never narrower than 320
        Assert.Equal(240, small.Height);
    }

    [Fact]
    public void Window_follows_until_the_user_moves_it()
    {
        var put = PreviewPlacement.Beside(0, 0, 1800, 1000);
        Assert.True(PreviewPlacement.StillDocked(put, put.Left + 1, put.Top - 2, put.Width, put.Height + 1));   // rounding noise
        Assert.False(PreviewPlacement.StillDocked(put, put.Left - 40, put.Top, put.Width, put.Height));         // dragged
        Assert.False(PreviewPlacement.StillDocked(put, put.Left, put.Top, put.Width + 100, put.Height));        // resized
    }

    // ---------- the controller uses the policy ----------

    string MakeProject()
    {
        File.WriteAllText(Path.Combine(_dir, "App.csproj"), "<Project/>");
        Directory.CreateDirectory(Path.Combine(_dir, "Styles"));
        return Path.Combine(_dir, "Views", "Main.axaml");
    }

    static async Task WaitFor(Func<bool> condition, string what, int ms = 8000)
    {
        for (var waited = 0; waited < ms; waited += 25)
        {
            if (condition()) return;
            await Task.Delay(25);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }

    [Fact]
    public async Task Editing_or_saving_another_xaml_of_the_project_refreshes_the_preview_but_other_files_do_not()
    {
        var main = MakeProject();
        var calls = 0;
        await using var c = new PreviewController(new PreviewControllerOptions
        {
            DelayMs = 300,
            ResolveProject = (_, _) => { Interlocked.Increment(ref calls); return Task.FromResult(PreviewProjectResult.Fail("n/a")); },
        });
        c.Show(main, () => "<Window/>");
        await WaitFor(() => Volatile.Read(ref calls) == 1, "the first update");

        c.TextChanged(Path.Combine(_dir, "App.axaml"));              // App.axaml edited (unsaved): debounced
        await WaitFor(() => Volatile.Read(ref calls) == 2, "a refresh after App.axaml was edited");

        c.Saved(Path.Combine(_dir, "Styles", "Theme.axaml"));         // a resource dictionary saved: at once
        await WaitFor(() => Volatile.Read(ref calls) == 3, "a refresh after a resource dictionary was saved");

        c.TextChanged(Path.Combine(_dir, "obj", "Gen.axaml"));        // build output
        c.TextChanged(Path.Combine(Path.GetTempPath(), "elsewhere", "X.axaml"));   // another project
        c.Saved(Path.Combine(_dir, "Program.cs"));
        await Task.Delay(900);
        Assert.Equal(3, Volatile.Read(ref calls));

        for (var i = 0; i < 4; i++) { c.TextChanged(Path.Combine(_dir, "App.axaml")); await Task.Delay(20); }   // typing burst
        await WaitFor(() => Volatile.Read(ref calls) >= 4, "a refresh after the burst");
        await Task.Delay(900);
        Assert.Equal(4, Volatile.Read(ref calls));   // one refresh for the whole burst
    }
}
