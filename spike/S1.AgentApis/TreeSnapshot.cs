using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace S1.AgentApis;

public sealed record NodeDto(int Id, string Type, string? Name, string[]? Classes, int ChildCount, NodeDto[]? Children);

[JsonSerializable(typeof(NodeDto))]
[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
public partial class SnapshotJson : JsonSerializerContext;

static class TreeSnapshot
{
    public static string Measure(int buttons)
    {
        var panel = new StackPanel();
        for (var i = 0; i < buttons; i++)
            panel.Children.Add(new Border { Child = new Button { Name = $"B{i}", Content = new TextBlock { Text = $"Item {i}" } } });
        var w = new Window { Content = new ScrollViewer { Content = panel }, Width = 800, Height = 600 };
        w.Show(); Dispatcher.UIThread.RunJobs(); w.CaptureRenderedFrame(); Dispatcher.UIThread.RunJobs();

        var ids = new System.Runtime.CompilerServices.ConditionalWeakTable<Visual, object>();
        var next = 0;
        NodeDto Snap(Visual v, int depthLeft)
        {
            var id = (int)ids.GetValue(v, _ => ++next);
            var kids = v.GetVisualChildren().ToList();
            return new NodeDto(id, v.GetType().Name, (v as Control)?.Name,
                v is StyledElement se && se.Classes.Count > 0 ? se.Classes.ToArray() : null,
                kids.Count, depthLeft == 0 ? null : kids.Select(k => Snap(k, depthLeft - 1)).ToArray());
        }

        var sw = Stopwatch.StartNew();
        var full = Snap(w, int.MaxValue);
        var walkMs = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        var json = JsonSerializer.SerializeToUtf8Bytes(full, SnapshotJson.Default.NodeDto);
        var serMs = sw.Elapsed.TotalMilliseconds;
        var total = Count(full);
        sw.Restart();
        var lazy = Snap(w, 3);
        var lazyJson = JsonSerializer.SerializeToUtf8Bytes(lazy, SnapshotJson.Default.NodeDto);
        var lazyMs = sw.Elapsed.TotalMilliseconds;
        w.Close();
        return $"{total:N0} visuals: full walk {walkMs:F1} ms + serialize {serMs:F1} ms = {json.Length / 1024.0:F0} KB; lazy depth-3 snapshot {lazyMs:F2} ms = {lazyJson.Length} B";
    }

    static int Count(NodeDto n) => 1 + (n.Children?.Sum(Count) ?? 0);
}
