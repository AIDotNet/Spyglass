using Avalonia; using Avalonia.Markup.Xaml;
namespace S7.App;
public class App : Application { public override void Initialize() => AvaloniaXamlLoader.Load(this); }
