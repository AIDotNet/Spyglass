using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using Avalonia.Markup.Xaml;

namespace Spyglass.Agent.HotReload;

/// <summary>
/// Compile once, populate many. <see cref="AvaloniaRuntimeXamlLoader"/> has no public way to compile a document without
/// populating, so every Load recompiled the whole document (with its includes) per live instance and per new instance:
/// ~40 ms each for a view with three includes, 10 s for 200 instances. The runtime compiler emits a type with
/// <c>static void __AvaloniaXamlIlPopulate(IServiceProvider, TRoot)</c> into one dynamic assembly; after a Load we find
/// the new type and call that method for the remaining instances, exactly as the loader itself does (LoadOrPopulate:
/// <c>populate(XamlIlRuntimeHelpers.CreateRootServiceProviderV3(parent), root)</c>). Same approach as HotAvalonia.
/// Everything is best effort: a null result means "keep calling Load".
/// </summary>
[UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "Debug-only hot reload.")]
[UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Debug-only; hot reload is unavailable without JIT.")]
internal static class EmittedXaml
{
    const string PopulateMethod = "__AvaloniaXamlIlPopulate";

    static Assembly? _emitted;

    /// <summary>Number of types in the loader's dynamic assembly before a Load; pass it to <see cref="Find"/> afterwards.</summary>
    public static int Mark()
    {
        try { return Types() is { } t ? t.Length : 0; }
        catch { return int.MaxValue; }   // Find then finds nothing: Load per instance, as before
    }

    /// <summary>
    /// The populate delegate of the type emitted since <paramref name="mark"/> for the document whose root is
    /// <paramref name="rootType"/> (group members are classless, so the x:Class identifies the main document).
    /// </summary>
    public static Action<object>? Find(int mark, Type rootType)
    {
        try
        {
            if (Types() is not { } types) return null;
            for (var i = types.Length - 1; i >= mark; i--)
            {
                var m = types[i]?.GetMethod(PopulateMethod, BindingFlags.Static | BindingFlags.Public);
                if (m?.GetParameters() is [{ ParameterType: var sp }, { ParameterType: var root }] && sp == typeof(IServiceProvider) && root == rootType)
                    return Bind(m, root);
            }
        }
        catch (Exception ex) { Debug.WriteLine($"[Spyglass] hot reload: compiled populate not found, falling back to Load: {ex.Message}"); }
        return null;
    }

    static Action<object>? Bind(MethodInfo m, Type root)
    {
        if (RootServiceProvider is not { } create) return null;
        var sp = Expression.Parameter(typeof(IServiceProvider));
        var target = Expression.Parameter(typeof(object));
        var call = Expression.Lambda<Action<IServiceProvider, object>>(
            Expression.Call(m, sp, Expression.Convert(target, root)), sp, target).Compile();
        return o => call(create(), o);
    }

    /// <summary>XamlIlRuntimeHelpers.CreateRootServiceProviderV3(null) (Avalonia 11.1+), else V2(): a fresh NameScope per instance.</summary>
    static readonly Func<IServiceProvider>? RootServiceProvider = CreateRootServiceProvider();

    static Func<IServiceProvider>? CreateRootServiceProvider()
    {
        var helpers = typeof(AvaloniaXamlLoader).Assembly.GetType("Avalonia.Markup.Xaml.XamlIl.Runtime.XamlIlRuntimeHelpers");
        if (helpers?.GetMethod("CreateRootServiceProviderV3", [typeof(IServiceProvider)]) is { } v3)
            return () => (IServiceProvider)v3.Invoke(null, [null])!;
        if (helpers?.GetMethod("CreateRootServiceProviderV2", Type.EmptyTypes) is { } v2)
            return () => (IServiceProvider)v2.Invoke(null, null)!;
        return null;
    }

    /// <summary>
    /// Types of the loader's dynamic assembly in definition order (it holds the XamlIlContext class), or null before the
    /// first compile. A failed compile leaves types that were never created; they are null here, positions stay.
    /// </summary>
    static Type?[]? Types()
    {
        _emitted ??= AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault(a => a.IsDynamic && HasContext(a));
        try { return _emitted?.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types; }

        static bool HasContext(Assembly a)
        {
            try { return a.GetType("XamlIlContext") is not null; }
            catch { return false; }
        }
    }
}
