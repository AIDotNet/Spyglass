using Avalonia;
using Avalonia.Markup.Xaml;
namespace S6.HotReload;
public class App : Application { public override void Initialize() => AvaloniaXamlLoader.Load(this); }
