using Avalonia.Controls;
using Avalonia.Data.Core;
using Avalonia.Diagnostics;

namespace S1.AgentApis;

/// <summary>Checks that need [PrivateApi] members (AvaloniaAccessUnstablePrivateApis=true).</summary>
static class PrivateChecks
{
    public static void Run(Action<string, string, Func<string>> check, Button save, TextBlock broken)
    {
        check("A1", "[PrivateApi] GetValueStoreDiagnostic().AppliedFrames", () =>
        {
            var diag = save.GetValueStoreDiagnostic();
            var lines = diag.AppliedFrames.Select(f =>
                $"{f.Type}/{f.Priority} active={f.IsActive} src={Short(f.Source)} values=[{string.Join(", ", f.Values.Select(v => $"{v.Property.Name}={v.Value}"))}]");
            return $"{diag.AppliedFrames.Count} frames\n          " + string.Join("\n          ", lines.Take(8));
        });
        check("A2", "[PrivateApi] binding expression details", () =>
        {
            var expr = Avalonia.Data.BindingOperations.GetBindingExpressionBase(broken, TextBlock.TextProperty) as UntypedBindingExpressionBase;
            return expr == null ? "not an UntypedBindingExpressionBase" :
                $"Description='{expr.Description}' ErrorType={expr.ErrorType} Target={expr.TargetProperty?.Name} Priority={expr.Priority}";
        });
    }

    static string Short(object? o) => o switch
    {
        null => "null",
        Avalonia.Styling.Style s => $"Style({s.Selector})",
        Avalonia.Styling.ControlTheme t => $"ControlTheme({t.TargetType?.Name})",
        _ => o.GetType().Name,
    };
}
