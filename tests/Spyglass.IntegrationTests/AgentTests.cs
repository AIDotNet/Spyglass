using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Spyglass.Protocol;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

[Collection("app")]
public class AgentTests(AppFixture app)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    async Task<int> Find(string query)
    {
        var r = await app.Session.SearchAsync(new SearchParams(query));
        return Assert.Single(r.Matches).Id;
    }

    [Fact]
    public void Hello_describes_the_app()
    {
        var s = app.Session;
        Assert.Equal("Spyglass Integration Tests", s.App.Name);
        Assert.Equal("12.1.4", s.App.AvaloniaVersion);
        Assert.Equal(AgentFlavor.Advanced, s.Agent.Flavor);
        Assert.True(s.Has(Capabilities.ValueFrames));
        Assert.True(s.Has(Capabilities.Tree));
        Assert.True(s.Has(Capabilities.BindingErrors));
    }

    [Fact]
    public async Task Roots_and_full_tree()
    {
        var roots = (await app.Session.GetRootsAsync()).Roots;
        var main = Assert.Single(roots, r => r.Title == "Main");
        Assert.Equal(RootKind.Window, main.Kind);

        var tree = await app.Session.GetTreeAsync(main.Node.Id, TreeKind.Visual);
        var all = Flatten(tree).ToList();
        var save = Assert.Single(all, n => n.Name == "SaveButton");
        Assert.Equal("Button", save.Type);
        Assert.Equal(["primary"], save.Classes);
        Assert.Equal("Save", save.Text);
        Assert.False(save.IsTemplatePart);
        Assert.Equal(120, save.Bounds!.W);
        Assert.Contains(all, n => n.IsTemplatePart);              // Fluent templates are in the visual tree
        Assert.False(Assert.Single(all, n => n.Name == "CancelButton").IsEnabled);
        Assert.Equal("Your name", Assert.Single(all, n => n.Name == "NameBox").Placeholder);

        var logical = Flatten(await app.Session.GetTreeAsync(main.Node.Id, TreeKind.Logical)).ToList();
        Assert.True(logical.Count < all.Count);
        Assert.DoesNotContain(logical, n => n.IsTemplatePart);
    }

    [Theory]
    [InlineData("Button#SaveButton", "SaveButton")]
    [InlineData("#SaveButton", "SaveButton")]
    [InlineData("Button.primary", "SaveButton")]
    [InlineData("text=Save", "SaveButton")]
    [InlineData("text=cancel", "CancelButton")]
    [InlineData("StackPanel#Buttons > Button:nth(2)", "CancelButton")]
    [InlineData("Window StackPanel > TextBox", "NameBox")]
    [InlineData("xaml=TestWindow.axaml:13", "SaveButton")]
    [InlineData("Control#Heading", "Heading")]           // base type match
    public async Task Search_queries(string query, string expectedName)
    {
        var r = await app.Session.SearchAsync(new SearchParams(query));
        var m = Assert.Single(r.Matches);
        Assert.Equal(expectedName, m.Node.Name);
        Assert.Equal(m.Id, m.Path[^1]);
    }

    [Fact]
    public async Task Search_without_match_and_bad_selector()
    {
        Assert.Empty((await app.Session.SearchAsync(new SearchParams("#Nope"))).Matches);
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.SearchAsync(new SearchParams("xaml=oops")));
        Assert.Equal(ErrorCodes.InvalidParams, ex.Code);
    }

    [Fact]
    public async Task Describe_gives_selector_source_and_datacontext()
    {
        var d = await app.Session.DescribeAsync(await Find("#SaveButton"));
        Assert.Equal("TestWindow > StackPanel#Root > StackPanel#Buttons > Button#SaveButton.primary", d.Selector);
        Assert.Equal(typeof(TestViewModel).FullName, d.DataContextType);
        Assert.NotNull(d.Source);
        Assert.EndsWith("TestWindow.axaml", d.Source!.Uri);
        Assert.Equal(13, d.Source.Line);
        Assert.NotNull(d.TopLevelId);
    }

    [Fact]
    public async Task Properties_show_value_and_priority()
    {
        var props = await app.Session.GetPropertiesAsync(await Find("#SaveButton"));
        var width = Assert.Single(props, p => p.Name == "Width");
        Assert.Equal("120", width.Value.Text);
        Assert.Equal("LocalValue", width.Priority);
        Assert.True(width.IsSet);
        var bg = Assert.Single(props, p => p.Name == "Background");
        Assert.Equal(ValueKind.Brush, bg.Value.Kind);
        Assert.Equal("StyleTrigger", bg.Priority);
        var fontSize = Assert.Single(props, p => p.Name == "FontSize");
        Assert.Equal("Inherited", fontSize.Priority);
        Assert.False(fontSize.IsSet);
        var text = Assert.Single(await app.Session.GetPropertiesAsync(await Find("#Heading")), p => p.Name == "Text");
        Assert.True(text.HasBinding);
    }

    [Fact]
    public async Task Set_and_reset_property()
    {
        var id = await Find("#CancelButton");
        var set = await app.Session.SetPropertyAsync(id, "Width", "Layoutable", new ValueDto(ValueKind.Number, "200"));
        Assert.True(set.IsModifiedBySpyglass);
        Assert.Equal(200, (await app.Session.GetLayoutAsync(id)).Bounds.W);

        await app.Session.ResetPropertiesAsync(new PropsResetParams(Id: id, Name: "Width", Owner: "Layoutable"));
        var w = Assert.Single(await app.Session.GetPropertiesAsync(id), p => p.Name == "Width");
        Assert.Equal("Unset", w.Priority);
        Assert.False(w.IsModifiedBySpyglass);

        var bad = await Assert.ThrowsAsync<SpyglassProtocolException>(() =>
            app.Session.SetPropertyAsync(id, "Width", "Layoutable", new ValueDto(ValueKind.String, "wide")));
        Assert.Equal(ErrorCodes.ConversionFailed, bad.Code);
    }

    [Fact]
    public async Task Layout_box_model()
    {
        var l = await app.Session.GetLayoutAsync(await Find("#SaveButton"));
        Assert.Equal(new ThicknessData(4, 2, 4, 2), l.Margin);
        Assert.Equal(120, l.Width);
        Assert.Contains("width", l.ActiveConstraints);
        Assert.NotNull(l.Padding);
    }

    [Fact]
    public async Task Pseudo_class_forcing_applies_styles()
    {
        var id = await Find("#SaveButton");
        await app.Session.SetPseudoClassAsync(id, ":pointerover", true);
        Assert.Contains(":pointerover", (await app.Session.GetClassesAsync(id)).PseudoClasses);
        var bg = Assert.Single(await app.Session.GetPropertiesAsync(id), p => p.Name == "Background");
        Assert.Equal("#ff2b7bf5", bg.Value.Text.ToLowerInvariant());
        await app.Session.SetPseudoClassAsync(id, ":pointerover", false);
        Assert.Contains(":pointerover", (await app.Session.GetClassesAsync(id)).KnownPseudoClasses);
    }

    [Fact]
    public async Task Screenshot_is_png()
    {
        var shot = await app.Session.ScreenshotAsync(await Find("#SaveButton"), 2);
        Assert.Equal(240, shot.Width);
        var bytes = Convert.FromBase64String(shot.Base64);
        Assert.Equal([0x89, 0x50, 0x4E, 0x47], bytes[..4]);
    }

    [Fact]
    public async Task Binding_errors_point_at_the_element_and_xaml()
    {
        var errors = await app.Session.GetBindingErrorsAsync();
        var e = Assert.Single(errors, x => x.Path == "User.FullNmae");
        Assert.Equal("TextBlock", e.TargetType);
        Assert.Equal("Text", e.Property);
        Assert.Contains("FullNmae", e.Message);
        Assert.Equal(await Find("#Broken"), e.TargetId);
        Assert.Equal(10, e.Location!.Line);
        Assert.Single(await app.Session.GetBindingErrorsAsync(e.TargetId));
    }

    [Fact]
    public async Task Unknown_node_is_an_actionable_error()
    {
        var ex = await Assert.ThrowsAsync<SpyglassProtocolException>(() => app.Session.DescribeAsync(999_999));
        Assert.Equal(ErrorCodes.NodeNotFound, ex.Code);
    }

    [Fact]
    public async Task Multiple_windows_owned_dialog_and_live_root_changes()
    {
        var mainId = Assert.Single((await app.Session.GetRootsAsync()).Roots, r => r.Title == "Main").Node.Id;
        var added = new TaskCompletionSource<TreeOpRootAdded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<TreeOpRootRemoved>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnChanged(TreeChangedParams p)
        {
            foreach (var op in p.Ops)
            {
                if (op is TreeOpRootAdded a) added.TrySetResult(a);
                if (op is TreeOpRootRemoved r) removed.TrySetResult(r);
            }
        }
        app.Session.TreeChanged += OnChanged;
        try
        {
            var dialog = await app.OnUi(() =>
            {
                var d = new Window { Title = "Settings", Width = 300, Height = 200, Content = new Button { Name = "OkButton", Content = "OK" } };
                d.Show(app.Window);   // owned by the main window, like a dialog
                return d;
            });

            var a = await added.Task.WaitAsync(Timeout);
            Assert.Equal("Settings", a.Root.Title);
            Assert.Equal(mainId, a.Root.OwnerId);

            var roots = (await app.Session.GetRootsAsync()).Roots;
            Assert.Equal(2, roots.Count(r => r.Kind == RootKind.Window));
            // Search spans every window; ids are unique across windows.
            var ok = Assert.Single((await app.Session.SearchAsync(new SearchParams("#OkButton"))).Matches);
            Assert.Equal(a.Root.Node.Id, ok.Path[0]);

            await app.OnUi(() => { dialog.Close(); return 0; });
            var r = await removed.Task.WaitAsync(Timeout);
            Assert.Equal(a.Root.Node.Id, r.Id);
            Assert.Single((await app.Session.GetRootsAsync()).Roots, x => x.Kind == RootKind.Window);
        }
        finally { app.Session.TreeChanged -= OnChanged; }
    }

    [Fact]
    public async Task Pick_selects_user_element_and_swallows_the_click()
    {
        var selected = new TaskCompletionSource<PickSelectedParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnSelected(PickSelectedParams p) => selected.TrySetResult(p);
        app.Session.PickSelected += OnSelected;
        try
        {
            var clicksBefore = await app.OnUi(() => app.Window.Clicks);
            await app.Session.StartPickAsync();
            await app.OnUi(() =>
            {
                var b = app.Window.SaveButton;
                var p = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), app.Window)!.Value;
                app.Window.MouseMove(p);
                app.Window.MouseDown(p, MouseButton.Left);
                app.Window.MouseUp(p, MouseButton.Left);
                return 0;
            });
            var s = await selected.Task.WaitAsync(Timeout);
            Assert.Equal(await Find("#SaveButton"), s.Id);      // not the ContentPresenter inside the template
            Assert.Equal(s.Id, s.Path![^1]);
            Assert.Equal(clicksBefore, await app.OnUi(() => app.Window.Clicks)); // the app never saw the click
        }
        finally { app.Session.PickSelected -= OnSelected; }
    }

    [Fact]
    public async Task Highlight_show_and_hide()
    {
        var id = await Find("#SaveButton");
        await app.Session.HighlightAsync(id, HighlightStyle.Layout, 0);
        var tree = Flatten(await app.Session.GetTreeAsync((await app.Session.GetRootsAsync()).Roots[0].Node.Id)).ToList();
        Assert.DoesNotContain(tree, n => n.Type == "Adorner");  // our adorner never shows up in the tree
        await app.Session.HideHighlightAsync();
    }

    static IEnumerable<NodeDto> Flatten(NodeDto n) => new[] { n }.Concat(n.Children?.SelectMany(Flatten) ?? []);
}

[Collection("app")]
public class AdvancedTests(AppFixture app)
{
    async Task<int> Find(string q) => Assert.Single((await app.Session.SearchAsync(new SearchParams(q))).Matches).Id;

    [Fact]
    public async Task Value_frames_show_winner_overridden_and_inactive_styles_with_xaml_lines()
    {
        var frames = (await app.Session.GetValueFramesAsync(await Find("#Card"))).Frames;
        (ValueFrameDto F, SetterDto S) Bg(string selector)
        {
            var f = Assert.Single(frames, x => x.Source.Selector == selector);
            return (f, Assert.Single(f.Setters, s => s.Property == "Background"));
        }

        var card = Bg("Border.card");
        var highlight = Bg("Border.card.highlight");
        var hover = Bg("Border.card:pointerover");
        Assert.True(highlight.F.IsActive);
        Assert.False(highlight.S.IsOverridden);          // declared later: wins
        Assert.True(card.F.IsActive);
        Assert.True(card.S.IsOverridden);                // struck through in the UI
        Assert.False(Assert.Single(card.F.Setters, s => s.Property == "Padding").IsOverridden);
        Assert.False(hover.F.IsActive);                  // condition not met
        Assert.EndsWith("TestApp.axaml", card.F.Source.Location!.Uri);
        Assert.Equal(12, card.F.Source.Location.Line);   // Style lines are already 1-based in Avalonia (see XamlLines)
        Assert.Contains(frames, f => f.Source.Kind == FrameSourceKind.Local);
    }

    [Fact]
    public async Task Theme_setters_show_dynamic_resources_with_resolved_values()
    {
        var frames = (await app.Session.GetValueFramesAsync(await Find("#CancelButton"))).Frames;
        var theme = Assert.Single(frames, f => f.Source.Kind == FrameSourceKind.ControlTheme);
        Assert.Equal("Button", theme.Source.TargetType);
        var fg = Assert.Single(theme.Setters, s => s.Property == "Foreground");
        Assert.StartsWith("{DynamicResource ", fg.Value.Text);
        Assert.Contains(" = #", fg.Value.Text);
    }

    [Fact]
    public async Task Binding_details()
    {
        var b = await app.Session.GetBindingDetailsAsync(await Find("#Broken"), "Text");
        Assert.Equal("User.FullNmae", b.Description);
        Assert.Equal("Error", b.ErrorType);
        var ok = await app.Session.GetBindingDetailsAsync(await Find("#Heading"), "Text");
        Assert.Null(ok.ErrorType);
        Assert.Equal("Hello Spyglass", ok.Value.Text);
    }

    [Fact]
    public async Task Binding_errors_are_pushed_and_support_a_cursor()
    {
        var all = await app.Session.GetBindingErrorsAsync();
        var cursor = all.Max(e => e.Seq);
        Assert.Empty(await app.Session.GetBindingErrorsAsync(since: cursor));

        var pushed = new TaskCompletionSource<IReadOnlyList<BindingErrorDto>>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(IReadOnlyList<BindingErrorDto> e) { if (e.Any(x => x.Path == "Nope.Missing")) pushed.TrySetResult(e); }
        app.Session.BindingErrors += On;
        try
        {
            await app.OnUi(() =>
            {
                var tb = new Avalonia.Controls.TextBlock { Name = "LateBroken" };
                tb.Bind(Avalonia.Controls.TextBlock.TextProperty, new Avalonia.Data.Binding("Nope.Missing"));
                ((Avalonia.Controls.StackPanel)app.Window.Content!).Children.Add(tb);
                return 0;
            });
            var e = Assert.Single(await pushed.Task.WaitAsync(TimeSpan.FromSeconds(10)), x => x.Path == "Nope.Missing");
            Assert.True(e.Seq > cursor);
            Assert.Single(await app.Session.GetBindingErrorsAsync(since: cursor), x => x.Path == "Nope.Missing");
        }
        finally
        {
            app.Session.BindingErrors -= On;
            await app.OnUi(() => { var sp = (Avalonia.Controls.StackPanel)app.Window.Content!; sp.Children.Remove(sp.Children.Last()); return 0; });
        }
    }
}

[Collection("app")]
public class LayoutExplainTests(AppFixture app)
{
    async Task<LayoutExplainResult> Explain(string selector)
    {
        var m = Assert.Single((await app.Session.SearchAsync(new SearchParams(selector))).Matches);
        return await app.Session.ExplainLayoutAsync(m.Id);
    }

    [Fact]
    public async Task Visible_element_explains_how_it_got_its_size()
    {
        var r = await Explain("#SaveButton");
        Assert.Empty(r.VisibilityIssues);
        Assert.Equal("StackPanel", r.ParentType);
        Assert.NotNull(r.MeasureConstraint);
        Assert.NotNull(r.ArrangeSlot);
        Assert.Contains(r.Notes, n => n.Contains("Width is fixed at 120"));
        Assert.Contains(r.Notes, n => n.Contains("Horizontal StackPanel"));
    }

    [Fact]
    public async Task Hidden_ancestor_is_named_as_the_culprit()
    {
        var r = await Explain("#InHidden");
        var issue = Assert.Single(r.VisibilityIssues, i => i.Code == "not-visible");
        Assert.Equal("StackPanel", issue.NodeType);
        Assert.Contains("IsVisible = false", issue.Message);
        Assert.Contains("hides all of its descendants", issue.Message);
        var hidden = Assert.Single((await app.Session.SearchAsync(new SearchParams("#HiddenPanel"))).Matches);
        Assert.Equal(hidden.Id, issue.NodeId);
    }

    [Fact]
    public async Task Opacity_zero_is_reported()
    {
        var r = await Explain("#Ghost");
        Assert.Contains(r.VisibilityIssues, i => i.Code == "transparent" && i.Message.Contains("Opacity = 0"));
    }

    [Fact]
    public async Task Zero_size_ancestor_and_clipping_are_reported()
    {
        Assert.Contains((await Explain("#InCollapsed")).VisibilityIssues, i => i.Code == "zero-size-ancestor");
        var clipped = (await Explain("#Outside")).VisibilityIssues;
        Assert.Contains(clipped, i => i.Code == "clipped" && i.Message.Contains("ClipToBounds"));
    }

    [Fact]
    public async Task Scrolled_out_of_view_is_reported()
    {
        var r = await Explain("#BelowFold");
        Assert.Contains(r.VisibilityIssues, i => i.Code == "scrolled-out" && i.Message.Contains("scroll"));
    }
}
