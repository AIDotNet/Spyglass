using System.Reflection;
using Avalonia.Controls;
namespace S7.App;
public partial class CardView : UserControl
{
    public CardView() => InitializeComponent();
}

// --- What Spyglass.HotReload.Generators would emit in Debug (AvaloniaNameGeneratorBehavior=OnlyProperties) ---
partial class CardView
{
    public void InitializeComponent(bool loadXaml = true)
    {
        // The Load(this) call keeps XamlIl happy (AVLN3000) and is the Release-identical fallback.
        if (loadXaml && !SpyglassXaml.Populate(this)) Avalonia.Markup.Xaml.AvaloniaXamlLoader.Load(this);
    }
}
static class SpyglassXaml
{
    public static bool Populate(object o)
    {
        var m = o.GetType().GetMethod("!XamlIlPopulateTrampoline", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
        if (m == null) return false;
        m.Invoke(null, new[] { o });
        return true;
    }
}
