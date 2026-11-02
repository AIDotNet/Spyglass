using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Spyglass.VisualStudio.Core.Preview;

namespace Spyglass.VisualStudio;

/// <summary>
/// Runs a WPF window on its own STA thread inside the extension host process and owns it to the Visual Studio main window,
/// so it floats above VS and minimizes with it. (There is no way to embed it in a VS tool window from out of process.)
/// </summary>
internal sealed class PreviewWindowHost
{
    Thread? _thread;
    Dispatcher? _dispatcher;
    PreviewWindow? _window;

    public bool IsOpen => _window is not null;

    /// <summary>Creates the window with <paramref name="create"/> on the UI thread and shows it. Returns when it is up.</summary>
    public Task<PreviewWindow> OpenAsync(Func<PreviewWindow> create, Action closed)
    {
        var tcs = new TaskCompletionSource<PreviewWindow>(TaskCreationOptions.RunContinuationsAsynchronously);
        _thread = new Thread(() =>
        {
            try
            {
                var window = create();
                _window = window;
                _dispatcher = window.Dispatcher;
                var owner = VisualStudioWindow.TryFind();
                PreviewPlacement? put = null;     // where we last put the window; null once the user moved it
                Rect lastBounds = default;
                window.SourceInitialized += (_, _) =>
                {
                    if (owner is { } o)
                    {
                        new WindowInteropHelper(window) { Owner = o.Handle };
                        put = VisualStudioWindow.PlaceBeside(window, o.Bounds);
                        lastBounds = o.Bounds;
                    }
                };
                // Follow Visual Studio while the window is still where we put it: moved, resized, maximized or restored.
                var follow = new DispatcherTimer(DispatcherPriority.Background, window.Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
                follow.Tick += (_, _) =>
                {
                    if (put is not { } placed || VisualStudioWindow.TryFind() is not { } now) return;
                    if (now.Bounds == lastBounds || now.Bounds.Left <= -30000 || now.Bounds.Width < 200) return;   // unchanged, or minimized
                    lastBounds = now.Bounds;
                    if (window.WindowState != WindowState.Normal) return;
                    if (!PreviewPlacement.StillDocked(placed, window.Left, window.Top, window.Width, window.Height)) { put = null; return; }
                    put = VisualStudioWindow.PlaceBeside(window, now.Bounds);
                };
                follow.Start();
                window.Closed += (_, _) =>
                {
                    follow.Stop();
                    _window = null;
                    window.Dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); // returns an operation we do not need
                    closed();
                };
                window.Show();
                tcs.TrySetResult(window);
                Dispatcher.Run();
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        })
        { IsBackground = true, Name = "Spyglass Preview window" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        return tcs.Task;
    }

    public void Activate()
    {
        if (_window is { } w) Post(w.Dispatcher, () => { if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal; w.Activate(); });
    }

    public void Close()
    {
        if (_window is { } w) Post(w.Dispatcher, w.Close);
    }

    // The preview window has its own dispatcher thread; Visual Studio's JoinableTaskFactory rules (VSTHRD001/110) are about
    // VS's main thread and do not apply to it.
#pragma warning disable VSTHRD001, VSTHRD110
    /// <summary>Runs <paramref name="action"/> on the preview window's thread without waiting for it.</summary>
    public static void Post(Dispatcher dispatcher, Action action) => dispatcher.BeginInvoke(action);

    /// <summary>The same at render priority, for pictures.</summary>
    public static void PostRender(Dispatcher dispatcher, Action action) => dispatcher.BeginInvoke(DispatcherPriority.Render, action);
#pragma warning restore VSTHRD001, VSTHRD110
}

/// <summary>Finds the Visual Studio main window that launched this extension host (parent process) via Win32.</summary>
internal static class VisualStudioWindow
{
    public readonly record struct Info(IntPtr Handle, Rect Bounds);

    [StructLayout(LayoutKind.Sequential)] struct Rect32 { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out Rect32 rect);
    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, ref ProcessBasicInformation info, int size, out int returned);

    public static Info? TryFind()
    {
        try
        {
            using var self = Process.GetCurrentProcess();
            var pbi = new ProcessBasicInformation();
            if (NtQueryInformationProcess(self.Handle, 0, ref pbi, Marshal.SizeOf<ProcessBasicInformation>(), out _) != 0) return null;
            using var parent = Process.GetProcessById((int)pbi.InheritedFromUniqueProcessId);
            var hwnd = parent.MainWindowHandle;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
            return new Info(hwnd, new Rect(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top));
        }
        catch { return null; }
    }

    /// <summary>Puts the window on the right third of the Visual Studio window (device pixels → DIPs) and returns where it went.</summary>
    public static PreviewPlacement PlaceBeside(Window window, Rect vs)
    {
        var dpi = VisualTreeHelperDpi(window);
        var p = PreviewPlacement.Beside(vs.Left / dpi, vs.Top / dpi, vs.Width / dpi, vs.Height / dpi);
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Width = p.Width;
        window.Height = p.Height;
        window.Left = p.Left;
        window.Top = p.Top;
        return p;
    }

    static double VisualTreeHelperDpi(Window w)
    {
        try { return System.Windows.Media.VisualTreeHelper.GetDpi(w).DpiScaleX; } catch { return 1; }
    }
}
