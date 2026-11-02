using System.Collections.ObjectModel;

namespace S2.ArcCompact;

public sealed class Node(string type, string? name, string? classes, int depth)
{
    public string Type { get; } = type;
    public string NameText { get; } = name is null ? "" : "#" + name;
    public string ClassText { get; } = classes is null ? "" : "." + classes;
    public int Depth { get; } = depth;
    public ObservableCollection<Node> Children { get; } = new();
    public override string ToString() => Type + NameText;
}

public sealed record PropRow(string Name, string Value, string Source);

public sealed class MainViewModel
{
    public ObservableCollection<Node> Roots { get; } = new();
    public List<PropRow> Properties { get; } = new();
    public int TotalNodes { get; private set; }
    public int MaxDepth { get; private set; }
    public Node Last { get; private set; } = null!;
    public string Status => $"● Connected  SampleApp · PID 12345 · Avalonia 12.1.4 · Agent 0.1.0 (Advanced)   ·   {TotalNodes:N0} elements";

    static readonly string[] Containers = ["Grid", "StackPanel", "Border", "DockPanel", "ScrollViewer", "ContentPresenter", "ItemsPresenter", "Panel"];
    static readonly string[] Leaves = ["TextBlock", "Button", "TextBox", "Image", "CheckBox", "Path", "Rectangle", "AccessText"];
    static readonly string[] Classes = ["card", "primary", "muted", "toolbar", "title", "row"];

    public static MainViewModel Create(int target)
    {
        var vm = new MainViewModel();
        var rnd = new Random(42);
        var root = new Node("MainWindow", "Shell", null, 0);
        vm.Roots.Add(root);
        vm.TotalNodes = 1;
        var queue = new Queue<Node>();
        queue.Enqueue(root);
        // Breadth-first fill, branching 2–6, mixing containers and leaves, like a real visual tree.
        while (vm.TotalNodes < target && queue.Count > 0)
        {
            var parent = queue.Dequeue();
            var n = rnd.Next(2, 7);
            for (var i = 0; i < n && vm.TotalNodes < target; i++)
            {
                var isContainer = parent.Depth < 14 && rnd.NextDouble() < 0.55;
                var type = isContainer ? Containers[rnd.Next(Containers.Length)] : Leaves[rnd.Next(Leaves.Length)];
                var name = rnd.NextDouble() < 0.2 ? $"{type}{vm.TotalNodes}" : null;
                var cls = rnd.NextDouble() < 0.25 ? Classes[rnd.Next(Classes.Length)] : null;
                var child = new Node(type, name, cls, parent.Depth + 1);
                parent.Children.Add(child);
                vm.TotalNodes++;
                vm.MaxDepth = Math.Max(vm.MaxDepth, child.Depth);
                vm.Last = child;
                if (isContainer) queue.Enqueue(child);
            }
        }
        string[][] props =
        [
            ["Content", "\"Save\"", "Local"], ["IsEnabled", "True", "Local"], ["Background", "#0562EF", "Style"],
            ["Padding", "12,6,12,6", "Style"], ["CornerRadius", "6", "Theme"], ["FontSize", "14", "Inherited"],
            ["Foreground", "#FFFFFF", "Inherited"], ["Opacity", "1", "Default"], ["Width", "120", "Local"],
            ["Height", "32", "Local"], ["Margin", "0", "Default"], ["HorizontalAlignment", "Left", "Theme"],
        ];
        foreach (var p in props) vm.Properties.Add(new PropRow(p[0], p[1], p[2]));
        return vm;
    }

    public IEnumerable<Node> Flatten()
    {
        var stack = new Stack<Node>(Roots.Reverse());
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }
}
