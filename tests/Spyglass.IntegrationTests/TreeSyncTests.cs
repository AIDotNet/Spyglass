using Avalonia.Controls;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Incremental tree updates: only differences are pushed, and only for nodes the client already fetched.</summary>
[Collection("app")]
public class TreeSyncTests(AppFixture app)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    async Task<(int WindowId, int RootPanelId)> FetchTrees()
    {
        var window = Assert.Single((await app.Session.GetRootsAsync()).Roots, r => r.Title == "Main").Node.Id;
        await app.Session.GetTreeAsync(window, TreeKind.Visual);
        await app.Session.GetTreeAsync(window, TreeKind.Logical);
        var root = Assert.Single((await app.Session.SearchAsync(new SearchParams("StackPanel#Root"))).Matches).Id;
        return (window, root);
    }

    static TaskCompletionSource<T> Wait<T>(Action<Action<T>> subscribe)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        subscribe(v => tcs.TrySetResult(v));
        return tcs;
    }

    [Fact]
    public async Task Added_and_removed_controls_are_pushed_as_differences()
    {
        var (_, rootPanel) = await FetchTrees();
        var ops = new List<(TreeKind Kind, TreeOp Op)>();
        var added = new TaskCompletionSource<TreeOpAdd>(TaskCreationOptions.RunContinuationsAsynchronously);
        var removed = new TaskCompletionSource<TreeOpRemove>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(TreeChangedParams p)
        {
            foreach (var op in p.Ops)
            {
                lock (ops) ops.Add((p.Tree, op));
                if (p.Tree == TreeKind.Visual && op is TreeOpAdd a && a.Node.Name == "Late") added.TrySetResult(a);
                if (p.Tree == TreeKind.Visual && op is TreeOpRemove r) removed.TrySetResult(r);
            }
        }
        app.Session.TreeChanged += On;
        Button late = null!;
        try
        {
            await app.OnUi(() =>
            {
                late = new Button { Name = "Late", Content = "Late arrival" };
                ((StackPanel)app.Window.Content!).Children.Add(late);
                return 0;
            });
            var add = await added.Task.WaitAsync(Timeout);
            Assert.Equal(rootPanel, add.ParentId);
            Assert.Equal("Button", add.Node.Type);
            Assert.Equal("Late arrival", add.Node.Text);
            Assert.True(add.Node.Children!.Count > 0);                      // the whole new subtree comes with it (template parts)
            Assert.True(add.Index >= 5);                                     // appended after the existing children

            await app.OnUi(() => { ((StackPanel)app.Window.Content!).Children.Remove(late); return 0; });
            var rem = await removed.Task.WaitAsync(Timeout);
            Assert.Equal(rootPanel, rem.ParentId);
            Assert.Equal(add.Node.Id, rem.Id);

            // Nothing else was pushed for the untouched siblings.
            lock (ops) Assert.DoesNotContain(ops, o => o.Op is TreeOpAdd x && x.Node.Name != "Late" && x.ParentId == rootPanel);
        }
        finally { app.Session.TreeChanged -= On; }
    }

    [Fact]
    public async Task Visibility_and_name_changes_are_pushed_as_updates()
    {
        await FetchTrees();
        var cancel = Assert.Single((await app.Session.SearchAsync(new SearchParams("#CancelButton"))).Matches).Id;
        var hidden = new TaskCompletionSource<TreeOpUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(TreeChangedParams p)
        {
            foreach (var op in p.Ops)
                if (op is TreeOpUpdate u && u.Id == cancel && !u.IsVisible) hidden.TrySetResult(u);
        }
        app.Session.TreeChanged += On;
        try
        {
            await app.OnUi(() => { app.Window.CancelButton.IsVisible = false; return 0; });
            var u = await hidden.Task.WaitAsync(Timeout);
            Assert.Equal("CancelButton", u.Name);
        }
        finally
        {
            app.Session.TreeChanged -= On;
            await app.OnUi(() => { app.Window.CancelButton.IsVisible = true; return 0; });
        }
    }

    [Fact]
    public async Task Nothing_is_pushed_for_parents_the_client_never_fetched()
    {
        // A window opened by this test: no other test (and no tool's "similar element" search) can have fetched its
        // subtree, so the result does not depend on test order. Its root is announced (rootAdded) but never expanded.
        await app.Session.GetRootsAsync();   // like a real client: the agent only announces windows after the roots were requested
        var opened = new TaskCompletionSource<TreeOpRootAdded>(TaskCreationOptions.RunContinuationsAsynchronously);
        var seen = new List<TreeOp>();
        void On(TreeChangedParams p)
        {
            lock (seen) seen.AddRange(p.Ops);
            foreach (var op in p.Ops) if (op is TreeOpRootAdded a && a.Root.Title == "Unfetched") opened.TrySetResult(a);
        }
        app.Session.TreeChanged += On;
        Window win = null!;
        StackPanel panel = null!;
        try
        {
            await app.OnUi(() =>
            {
                panel = new StackPanel();
                win = new Window { Title = "Unfetched", Width = 200, Height = 100, Content = panel };
                win.Show();
                return 0;
            });
            await opened.Task.WaitAsync(Timeout);
            await app.OnUi(() => { panel.Children.Add(new Button { Name = "Deep" }); return 0; });   // parent never expanded by the client
            await Task.Delay(400);
            lock (seen) Assert.DoesNotContain(seen, o => o is TreeOpAdd a && a.Node.Name == "Deep");
        }
        finally
        {
            app.Session.TreeChanged -= On;
            await app.OnUi(() => { win.Close(); return 0; });
        }
    }
}
