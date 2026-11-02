using Avalonia.Remote.Protocol.Input;
using Spyglass.VisualStudio.Core.Preview;
using Xunit;

namespace Spyglass.VisualStudio.Core.Tests;

public sealed class PreviewViewTests
{
    // ---------- zoom ----------

    [Fact]
    public void Fit_shrinks_to_the_pane_but_never_enlarges()
    {
        var fit = PreviewZoom.FitToPane;
        Assert.Equal(0.5, fit.Scale(800, 400, 400, 300));       // width limits
        Assert.Equal(0.5, fit.Scale(400, 800, 300, 400));       // height limits
        Assert.Equal(1, fit.Scale(100, 50, 400, 300));          // small picture stays 100%
        Assert.Equal(0.1, fit.Scale(100000, 100000, 100, 100)); // floor
        Assert.Equal(1, fit.Scale(0, 0, 400, 300));             // nothing rendered yet
    }

    [Fact]
    public void Fixed_zoom_ignores_the_pane()
    {
        Assert.Equal(2, new PreviewZoom(false, 200).Scale(10, 10, 1, 1));
        Assert.Equal(1, PreviewZoom.Actual.Scale(999, 999, 10, 10));
    }

    [Fact]
    public void Zoom_steps_from_the_current_look()
    {
        Assert.Equal(new PreviewZoom(false, 125), PreviewZoom.In(1.0));
        Assert.Equal(new PreviewZoom(false, 75), PreviewZoom.Out(1.0));
        Assert.Equal(new PreviewZoom(false, 50), PreviewZoom.In(0.33));    // from a Fit scale of 33%
        Assert.Equal(new PreviewZoom(false, 25), PreviewZoom.Out(0.33));
        Assert.Equal(new PreviewZoom(false, 400), PreviewZoom.In(4.0));    // clamped at both ends
        Assert.Equal(new PreviewZoom(false, 25), PreviewZoom.Out(0.25));
    }

    [Theory]
    [InlineData("fit", true, 100)]
    [InlineData("FIT", true, 100)]
    [InlineData("150%", false, 150)]
    [InlineData("100", false, 100)]
    [InlineData("", true, 100)]
    [InlineData("nonsense", true, 100)]
    [InlineData("5%", true, 100)]       // out of range falls back to Fit
    public void Zoom_parses_the_stored_setting(string text, bool fit, double percent)
    {
        var z = PreviewZoom.Parse(text);
        Assert.Equal(fit, z.Fit);
        Assert.Equal(percent, z.Percent);
        Assert.Equal(z, PreviewZoom.Parse(z.ToString()));   // round-trips
    }

    // ---------- viewport / dpi ----------

    [Fact]
    public void Viewport_is_the_pane_undone_by_the_zoom()
    {
        Assert.Equal((800d, 600d), PreviewZoom.FitToPane.Viewport(800, 600));
        Assert.Equal((400d, 300d), new PreviewZoom(false, 200).Viewport(800, 600));
        Assert.Equal((1600d, 1200d), new PreviewZoom(false, 50).Viewport(800, 600));
        Assert.Equal((1d, 1d), PreviewZoom.Actual.Viewport(0, 0));
    }

    [Theory]
    [InlineData(1.0, 96)]
    [InlineData(1.25, 120)]
    [InlineData(1.5, 144)]
    [InlineData(0, 96)]
    [InlineData(100, 384)]   // clamped to 4x
    public void Dpi_follows_the_wpf_scale(double scale, double dpi) => Assert.Equal(dpi, PreviewInput.Dpi(scale));

    // ---------- input ----------

    [Fact]
    public void Pointer_positions_are_unscaled()
    {
        Assert.Equal((50d, 25d), PreviewInput.ToLogical(100, 50, 2));
        Assert.Equal((200d, 100d), PreviewInput.ToLogical(100, 50, 0.5));
        Assert.Equal((10d, 10d), PreviewInput.ToLogical(10, 10, 0));   // bad scale is treated as 1
    }

    [Theory]
    [InlineData("A", Key.A)]
    [InlineData("Enter", Key.Enter)]
    [InlineData("Return", Key.Return)]
    [InlineData("Back", Key.Back)]
    [InlineData("Tab", Key.Tab)]
    [InlineData("Left", Key.Left)]
    [InlineData("D5", Key.D5)]
    [InlineData("NumPad3", Key.NumPad3)]
    [InlineData("F5", Key.F5)]
    [InlineData("Next", Key.PageDown)]
    [InlineData("Prior", Key.PageUp)]
    [InlineData("Capital", Key.CapsLock)]
    [InlineData("LeftShift", Key.LeftShift)]
    public void Keys_map_by_name(string wpf, Key expected) => Assert.Equal(expected, PreviewInput.MapKey(wpf));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("None")]
    [InlineData("NoSuchKey")]
    [InlineData("123")]   // numeric strings are not key names
    public void Unknown_keys_are_not_forwarded(string? wpf) => Assert.Null(PreviewInput.MapKey(wpf));

    [Fact]
    public void Modifiers_list_what_is_held()
    {
        Assert.Null(PreviewInput.Modifiers(false, false, false));
        Assert.Equal([InputModifiers.Control, InputModifiers.Shift], PreviewInput.Modifiers(true, true, false));
        Assert.Equal([InputModifiers.Alt, InputModifiers.LeftMouseButton], PreviewInput.Modifiers(false, false, true, left: true));
    }

    [Theory]
    [InlineData(120, 1)]
    [InlineData(-240, -2)]
    [InlineData(60, 0.5)]
    public void Wheel_delta_is_in_notches(int delta, double notches) => Assert.Equal(notches, PreviewInput.WheelNotches(delta));

    // ---------- theme injection ----------

    const string Ns = "xmlns=\"https://github.com/avaloniaui\"";

    [Fact]
    public void Theme_is_added_to_a_window_root()
    {
        var xaml = $"<Window {Ns} Width=\"10\">\n  <Border/>\n</Window>";
        var (sent, edit) = PreviewXaml.ApplyTheme(xaml, PreviewTheme.Dark);
        const string inserted = " RequestedThemeVariant=\"Dark\" Background=\"{DynamicResource SystemRegionBrush}\"";
        Assert.StartsWith("<Window" + inserted + " xmlns", sent);
        Assert.Equal(new PreviewXamlEdit(1, 8, inserted.Length), edit);
        Assert.EndsWith("\n  <Border/>\n</Window>", sent);
    }

    [Fact]
    public void Theme_skips_prolog_comments_and_prefixed_roots()
    {
        var xaml = $"<?xml version=\"1.0\"?>\n<!-- <Window> in a comment -->\n<x:Window {Ns} />";
        var (sent, edit) = PreviewXaml.ApplyTheme(xaml, PreviewTheme.Light);
        Assert.Contains("<x:Window RequestedThemeVariant=\"Light\" Background=\"{DynamicResource SystemRegionBrush}\" xmlns", sent);
        Assert.Equal(3, edit.Line);
        Assert.Equal(10, edit.Column);
    }

    [Fact]
    public void Theme_keeps_a_background_the_root_already_has()
    {
        var (sent, edit) = PreviewXaml.ApplyTheme($"<Window {Ns} Background=\"Tomato\" Width=\"10\"/>", PreviewTheme.Dark);
        Assert.StartsWith("<Window RequestedThemeVariant=\"Dark\" xmlns", sent);
        Assert.DoesNotContain("SystemRegionBrush", sent);
        Assert.Equal(" RequestedThemeVariant=\"Dark\"".Length, edit.Length);

        // Not fooled by look-alikes: a BorderBackground attribute or a Window.Background child are not the root's Background.
        var (sent2, _) = PreviewXaml.ApplyTheme($"<Window {Ns} BorderBackground=\"Red\"><Window.Background><SolidColorBrush/></Window.Background></Window>", PreviewTheme.Light);
        Assert.Contains("Background=\"{DynamicResource SystemRegionBrush}\"", sent2);
    }

    [Fact]
    public void Theme_leaves_other_roots_and_existing_settings_alone()
    {
        var uc = $"<UserControl {Ns}/>";
        Assert.Equal((uc, PreviewXamlEdit.None), PreviewXaml.ApplyTheme(uc, PreviewTheme.Dark));
        var set = $"<Window {Ns} RequestedThemeVariant=\"Light\"/>";
        Assert.Equal((set, PreviewXamlEdit.None), PreviewXaml.ApplyTheme(set, PreviewTheme.Dark));
        var win = $"<Window {Ns}/>";
        Assert.Equal((win, PreviewXamlEdit.None), PreviewXaml.ApplyTheme(win, PreviewTheme.Default));
        Assert.Equal(("", PreviewXamlEdit.None), PreviewXaml.ApplyTheme("", PreviewTheme.Dark));
        Assert.Equal(("not xml", PreviewXamlEdit.None), PreviewXaml.ApplyTheme("not xml", PreviewTheme.Dark));
    }

    [Fact]
    public void Error_positions_after_the_insertion_map_back()
    {
        var edit = new PreviewXamlEdit(1, 8, 28);   // any insertion length behaves the same
        Assert.Equal((1, 3), edit.MapBack(1, 3));          // before the insertion: unchanged
        Assert.Equal((1, 8), edit.MapBack(1, 20));         // inside the inserted text: the insertion point
        Assert.Equal((1, 22), edit.MapBack(1, 50));        // after: shifted back
        Assert.Equal((2, 50), edit.MapBack(2, 50));        // other lines: unchanged
        Assert.Equal((1, null), edit.MapBack(1, null));
        Assert.Equal((null, null), edit.MapBack(null, null));
        Assert.Equal((1, 50), PreviewXamlEdit.None.MapBack(1, 50));
    }
}
