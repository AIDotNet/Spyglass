using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Spyglass.Agent;
using Spyglass.Agent.Advanced;

namespace Spyglass.IntegrationTests;

public partial class TestApp : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Entry point for HeadlessUnitTestSession. The hub must be started first (AppFixture.Port).</summary>
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<TestApp>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .UseSpyglassAdvanced(o =>
            {
                o.Port = AppFixture.Port;
                o.ApplicationName = "Spyglass Integration Tests";
                // Deterministic across platforms, and never spawn a client from tests.
                o.OpenGesture = new KeyGesture(Key.I, KeyModifiers.Control | KeyModifiers.Shift);
                o.InspectGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift);
                o.LaunchClient = false;
            });
}

public partial class TestWindow : Window
{
    public int Clicks;
    public int KeysSeenByApp;

    public TestWindow()
    {
        InitializeComponent();
        SaveButton.Click += (_, _) => Clicks++;
        KeyDown += (_, _) => KeysSeenByApp++;
    }
}

public sealed class TestViewModel
{
    public string Title => "Hello Spyglass";
    public UserVm User { get; } = new();
}

public sealed class UserVm
{
    public string FullName => "Ada Lovelace";
}
