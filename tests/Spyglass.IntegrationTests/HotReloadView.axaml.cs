using Avalonia.Controls;
using Avalonia.Interactivity;

namespace Spyglass.IntegrationTests;

/// <summary>Hot reload target (HotReloadTests). Keep HotReloadView.axaml in sync with HotReloadTests.Original.</summary>
public partial class HotReloadView : UserControl
{
    public int Clicks;
    public int GoClicks;

    public HotReloadView()
    {
        InitializeComponent();
        Go.Click += (_, _) => GoClicks++;   // code-behind wiring a named control: hot reload must carry it over (CodeBehindState)
    }

    void OnAnyClick(object? sender, RoutedEventArgs e) => Clicks++;
}

public sealed class HotReloadViewModel
{
    // Plain properties without change notification: HotReloadCodeTests uses that to stand in for a changed getter.
    public string Title { get; set; } = "Hello";
    public string Subtitle { get; set; } = "World";
}
