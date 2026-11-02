using Avalonia;
using Avalonia.Headless;
using Avalonia.Input;
using Spyglass.Agent;
using Spyglass.Agent.Internal;
using Spyglass.Protocol.Messages;
using Xunit;

namespace Spyglass.IntegrationTests;

/// <summary>Open / inspect hotkeys in the debugged app (design/ux/03 §1).</summary>
[Collection("app")]
public class HotkeyTests(AppFixture app)
{
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    Task Press(Key key) => app.OnUi(() =>
    {
        app.Window.Activate();
        app.Window.KeyPress(key, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        app.Window.KeyRelease(key, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.None, null);
        return 0;
    });

    [Fact]
    public async Task Open_hotkey_brings_the_client_to_the_front_and_the_app_never_sees_it()
    {
        var activated = new TaskCompletionSource<ActivateParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        void On(ActivateParams p) => activated.TrySetResult(p);
        app.Session.ActivateRequested += On;
        try
        {
            var keysBefore = await app.OnUi(() => app.Window.KeysSeenByApp);
            await Press(Key.I);
            var p = await activated.Task.WaitAsync(Timeout);
            Assert.Equal("hotkey", p.Reason);
            Assert.Null(p.NodeId);
            Assert.Equal(keysBefore, await app.OnUi(() => app.Window.KeysSeenByApp));
        }
        finally { app.Session.ActivateRequested -= On; }
    }

    [Fact]
    public async Task Inspect_hotkey_starts_picking_and_activates_with_the_picked_element()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var activated = new TaskCompletionSource<ActivateParams>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnStarted() => started.TrySetResult();
        void OnActivate(ActivateParams p) { if (p.Reason == "pick") activated.TrySetResult(p); }
        app.Session.PickStarted += OnStarted;
        app.Session.ActivateRequested += OnActivate;
        try
        {
            await Press(Key.C);
            await started.Task.WaitAsync(Timeout);

            await app.OnUi(() =>
            {
                var b = app.Window.CancelButton;
                var pt = b.TranslatePoint(new Point(b.Bounds.Width / 2, b.Bounds.Height / 2), app.Window)!.Value;
                app.Window.MouseMove(pt);
                app.Window.MouseDown(pt, MouseButton.Left);
                app.Window.MouseUp(pt, MouseButton.Left);
                return 0;
            });
            var a = await activated.Task.WaitAsync(Timeout);
            var cancel = Assert.Single((await app.Session.SearchAsync(new SearchParams("#CancelButton"))).Matches);
            // The click lands on the AccessText generated for Content="Cancel"; picking must still select the Button.
            Assert.True(cancel.Id == a.NodeId, $"picked {(await app.Session.DescribeAsync(a.NodeId!.Value)).Selector}, expected {cancel.Node.Name}");
        }
        finally
        {
            app.Session.PickStarted -= OnStarted;
            app.Session.ActivateRequested -= OnActivate;
        }
    }

    [Fact]
    public void Default_gestures_follow_chrome_devtools()
    {
        var o = new SpyglassAgentOptions();
        if (OperatingSystem.IsMacOS())
        {
            Assert.Equal(new KeyGesture(Key.I, KeyModifiers.Meta | KeyModifiers.Alt), o.OpenGesture);
            Assert.Equal(new KeyGesture(Key.C, KeyModifiers.Meta | KeyModifiers.Shift), o.InspectGesture);
        }
        else
        {
            Assert.Equal(new KeyGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift), o.OpenGesture);
            Assert.Equal(new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift), o.InspectGesture);
        }
        Assert.True(o.LaunchClient);
    }

    [Fact]
    public void Client_path_resolution_order()
    {
        var dir = Directory.CreateTempSubdirectory("spyglass-launch-");
        try
        {
            var onPath = Path.Combine(dir.FullName, OperatingSystem.IsWindows() ? "spyglass.exe" : "spyglass");
            File.WriteAllText(onPath, "");
            var fromEnv = Path.Combine(dir.FullName, "from-env");
            File.WriteAllText(fromEnv, "");
            var explicitPath = Path.Combine(dir.FullName, "explicit");
            File.WriteAllText(explicitPath, "");
            string? Env(string name) => name == "SPYGLASS_CLIENT" ? fromEnv : null;

            Assert.Equal(explicitPath, ClientLauncher.Resolve(explicitPath, Env, dir.FullName));
            Assert.Equal(fromEnv, ClientLauncher.Resolve(null, Env, dir.FullName));
            Assert.Equal(onPath, ClientLauncher.Resolve(null, _ => null, dir.FullName));
            Assert.Equal(onPath, ClientLauncher.Resolve(Path.Combine(dir.FullName, "missing"), _ => null, dir.FullName)); // missing explicit path falls through

            Assert.Equal("open", ClientLauncher.StartInfo("/Applications/Spyglass.app").FileName);
            Assert.Equal("dotnet", ClientLauncher.StartInfo("/x/Spyglass.dll").FileName);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Probe_reports_a_listening_hub()
    {
        Assert.True(ClientLauncher.IsListening("127.0.0.1", app.Hub.Port));
        Assert.True(ClientLauncher.IsLoopback("localhost"));
        Assert.False(ClientLauncher.IsLoopback("192.168.1.5"));
    }
}
