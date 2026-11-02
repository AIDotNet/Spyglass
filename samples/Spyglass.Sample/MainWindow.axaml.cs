using Avalonia.Controls;

namespace Spyglass.Sample;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // SPYGLASS_SAMPLE_IDLE=1: no animation, so the app is truly idle (to see how Spyglass shows a quiet app).
        if (Environment.GetEnvironmentVariable("SPYGLASS_SAMPLE_IDLE") == "1") Busy.IsIndeterminate = false;
    }
}
