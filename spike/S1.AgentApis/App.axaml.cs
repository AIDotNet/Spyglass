using Avalonia;
using Avalonia.Markup.Xaml;

namespace S1.AgentApis;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);
}

public partial class TestWindow : Avalonia.Controls.Window
{
    public TestWindow() => InitializeComponent();
}

public class TestViewModel
{
    public string Title => "Hello Spyglass";
    public UserVm User { get; } = new();
}

public class UserVm
{
    public string FullName => "Ada Lovelace";
}
