using Avalonia.Controls;
using Avalonia.Interactivity;
namespace S6.HotReload;
public partial class CardView : UserControl
{
    public int Saves;
    public CardView() => InitializeComponent();
    void OnSave(object? sender, RoutedEventArgs e) => Saves++;
    public int RootFocusCount;
    void OnRootClick(object? sender, RoutedEventArgs e) => RootFocusCount++;
}
public class CardViewModel { public string Title { get; set; } = "Hello"; public string Subtitle { get; set; } = "Sub"; }
