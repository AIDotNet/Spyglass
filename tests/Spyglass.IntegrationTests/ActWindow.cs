using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;

namespace Spyglass.IntegrationTests;

/// <summary>A second window with one control per kind of action (built in code so TestWindow and its snapshot tests stay untouched).</summary>
public sealed class ActWindow : Window
{
    public int ActClicks;
    public int SaveShortcutCount;
    public int KeysSeen;
    public readonly TextBlock Status = new() { Name = "ActStatus", Text = "idle" };
    public readonly TextBlock Broken = new() { Name = "ActBroken", Text = "ok" };
    public readonly StackPanel BrokenHost = new() { Name = "ActBrokenHost" };
    public readonly CheckBox Check = new() { Name = "ActCheck", Content = "Agree" };
    public readonly ComboBox Combo = new() { Name = "ActCombo", ItemsSource = new[] { "Alpha", "Beta", "Gamma" }, SelectedIndex = 0 };
    public readonly ListBox List = new() { Name = "ActList", ItemsSource = new[] { "One", "Two", "Three" }, Height = 90 };
    public readonly ToggleButton Toggle = new() { Name = "ActToggle", Content = "Power" };
    public readonly Expander Expand = new() { Name = "ActExpander", Header = "More", Content = new TextBlock { Text = "details" } };
    public readonly TextBox Input = new() { Name = "ActText", PlaceholderText = "type here" };
    public readonly Button Click = new() { Name = "ActButton", Content = "Press me" };
    public readonly Button Locked = new() { Name = "ActLocked", Content = "Locked" };
    public readonly Button Delay = new() { Name = "ActDelay", Content = "Start" };
    public readonly TextBlock Later = new() { Name = "ActLater", Text = "Done", IsVisible = false };
    public readonly Button Break = new() { Name = "ActBreak", Content = "Break binding" };

    sealed class NeverCommand : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => false;
        public void Execute(object? parameter) { }
    }

    public ActWindow()
    {
        Title = "Act";
        Width = 400; Height = 600;
        Click.Click += (_, _) => { ActClicks++; Status.Text = "clicked " + ActClicks; };
        Locked.Command = new NeverCommand();
        Delay.Click += (_, _) => DispatcherTimer.RunOnce(() => Later.IsVisible = true, TimeSpan.FromMilliseconds(300));
        Broken.Bind(TextBlock.TextProperty, new Binding("Missing"));
        BrokenHost.Children.Add(Broken);
        Break.Click += (_, _) => BrokenHost.DataContext = new object();
        KeyBindings.Add(new KeyBinding { Gesture = new KeyGesture(Key.S, KeyModifiers.Control), Command = new Relay(() => SaveShortcutCount++) });
        KeyDown += (_, _) => KeysSeen++;
        Content = new StackPanel
        {
            Spacing = 4,
            Children = { Status, Click, Locked, Input, Check, Toggle, Combo, List, Expand, Delay, Later, Break, BrokenHost },
        };
    }

    sealed class Relay(Action a) : ICommand
    {
        public event EventHandler? CanExecuteChanged { add { } remove { } }
        public bool CanExecute(object? parameter) => true;
        public void Execute(object? parameter) => a();
    }
}
