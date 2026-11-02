using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Avalonia.Remote.Protocol;
using Avalonia.Remote.Protocol.Designer;
using Avalonia.Remote.Protocol.Input;
using Avalonia.Remote.Protocol.Viewport;

namespace Spyglass.VisualStudio.Core.Preview;

/// <summary>One rendered frame from the previewer host (BGRA, top-down, <see cref="Stride"/> bytes per row).</summary>
public sealed record PreviewFrame(long SequenceId, int Width, int Height, int Stride, bool IsBgra, double DpiX, double DpiY, byte[] Data);

/// <summary>The host's answer to an UpdateXaml: Error is null on success; Line/Column are 1-based when the XAML is malformed.</summary>
public sealed record PreviewXamlResult(string? Error, string? ExceptionType, int? Line, int? Column);

public sealed class PreviewSessionOptions
{
    public string Dotnet { get; init; } = "dotnet";
    public Action<string>? Log { get; init; }
    public double DpiX { get; init; } = 96;
    public double DpiY { get; init; } = 96;
}

/// <summary>
/// A running `Avalonia.Designer.HostApp` process and the BSON-over-TCP connection to it (design/preview/README.md §2–§3):
/// the IDE listens first, the host connects. Every frame is acknowledged (the host sends the next one only then).
/// </summary>
public sealed class PreviewSession : IAsyncDisposable
{
    readonly PreviewProject _project;
    readonly PreviewSessionOptions _options;
    readonly TaskCompletionSource<IAvaloniaRemoteTransportConnection> _connected = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly SemaphoreSlim _sendGate = new(1, 1);
    IDisposable? _listener;
    IAvaloniaRemoteTransportConnection? _connection;
    Process? _process;
    int _disposed;
    int _exitRaised;

    public PreviewSession(PreviewProject project, PreviewSessionOptions? options = null)
    {
        _project = project;
        _options = options ?? new();
    }

    public PreviewProject Project => _project;

    /// <summary>Raised on a thread-pool thread for each frame.</summary>
    public event Action<PreviewFrame>? Frame;

    public event Action<PreviewXamlResult>? XamlResult;

    /// <summary>The host process ended (crash, kill or dispose); the argument is the exit code when known.</summary>
    public event Action<int?>? Exited;

    public bool HasExited
    {
        get
        {
            if (Volatile.Read(ref _exitRaised) != 0 || _process is null) return true;
            try { return _process.HasExited; } catch (InvalidOperationException) { return true; }
        }
    }

    /// <summary>Starts the host and waits until it has connected and the client side of the handshake is sent.</summary>
    public async Task StartAsync(TimeSpan timeout, CancellationToken ct)
    {
        var port = FreePort();
        var transport = new BsonTcpTransport();
        _listener = transport.Listen(IPAddress.Loopback, port, conn =>
        {
            _connection = conn;
            conn.OnMessage += OnMessage;
            conn.OnException += (_, ex) => _options.Log?.Invoke($"preview transport: {ex.Message}");
            _connected.TrySetResult(conn);
        });

        var psi = new ProcessStartInfo(_options.Dotnet)
        {
            WorkingDirectory = _project.ProjectDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("exec");
        psi.ArgumentList.Add("--runtimeconfig"); psi.ArgumentList.Add(_project.RuntimeConfigPath);
        psi.ArgumentList.Add("--depsfile"); psi.ArgumentList.Add(_project.DepsPath);
        psi.ArgumentList.Add(_project.ToolPath);
        psi.ArgumentList.Add("--transport"); psi.ArgumentList.Add($"tcp-bson://127.0.0.1:{port}/");
        psi.ArgumentList.Add("--session-id"); psi.ArgumentList.Add(Guid.NewGuid().ToString("N"));
        psi.ArgumentList.Add("--method"); psi.ArgumentList.Add("avalonia-remote");
        psi.ArgumentList.Add(_project.TargetPath);

        _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _process.OutputDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) _options.Log?.Invoke("previewer: " + e.Data); };
        _process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) _options.Log?.Invoke("previewer: " + e.Data); };
        _process.Exited += (_, _) => RaiseExited();
        _options.Log?.Invoke($"Starting previewer for {Path.GetFileName(_project.TargetPath)} on port {port}");
        _process.Start();
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            var exited = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            Exited += _ => exited.TrySetResult();
            if (_process.HasExited) exited.TrySetResult();
            var first = await Task.WhenAny(_connected.Task, exited.Task, Task.Delay(Timeout.Infinite, timeoutCts.Token)).ConfigureAwait(false);
            if (first != _connected.Task)
            {
                if (first == exited.Task) throw new InvalidOperationException("The previewer process exited before connecting.");
                throw new OperationCanceledException(ct);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new TimeoutException("The previewer did not connect in time.");
        }
        catch
        {
            await DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // Handshake: pixel formats, DPI, viewport (the same order AvaloniaVS and AvaloniaRider use).
        await SendAsync(new ClientSupportedPixelFormatsMessage { Formats = [PixelFormat.Bgra8888, PixelFormat.Rgba8888] }).ConfigureAwait(false);
        await SendAsync(new ClientRenderInfoMessage { DpiX = _options.DpiX, DpiY = _options.DpiY }).ConfigureAwait(false);
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    void RaiseExited()
    {
        if (Interlocked.Exchange(ref _exitRaised, 1) != 0) return;
        int? code = null;
        try { code = _process?.ExitCode; } catch { }
        Exited?.Invoke(code);
    }

    void OnMessage(IAvaloniaRemoteTransportConnection conn, object message)
    {
        switch (message)
        {
            case FrameMessage f:
                var data = f.Data ?? [];
                try { Frame?.Invoke(new PreviewFrame(f.SequenceId, f.Width, f.Height, f.Stride, f.Format == PixelFormat.Bgra8888, f.DpiX, f.DpiY, data)); }
                finally { _ = SendAsync(new FrameReceivedMessage { SequenceId = f.SequenceId }); }
                break;
            case UpdateXamlResultMessage r:
                XamlResult?.Invoke(new PreviewXamlResult(
                    string.IsNullOrEmpty(r.Error) ? null : r.Error,
                    r.Exception?.ExceptionType,
                    r.Exception?.LineNumber is > 0 ? r.Exception.LineNumber : null,
                    r.Exception?.LinePosition is > 0 ? r.Exception.LinePosition : null));
                break;
        }
    }

    async Task SendAsync(object message)
    {
        if (Volatile.Read(ref _disposed) != 0 || _connection is not { } c) return;
        await _sendGate.WaitAsync().ConfigureAwait(false);
        try { await c.Send(message).ConfigureAwait(false); }
        catch (Exception ex) { _options.Log?.Invoke($"preview send failed: {ex.Message}"); }
        finally { _sendGate.Release(); }
    }

    public Task UpdateXamlAsync(string xaml, string xamlPath) =>
        SendAsync(new UpdateXamlMessage { Xaml = xaml, AssemblyPath = _project.TargetPath, XamlFileProjectPath = _project.RelativeXamlPath(xamlPath) });

    /// <summary>Tells the host how big the pane is (device-independent pixels) and its DPI.</summary>
    public Task SetViewportAsync(double width, double height, double dpiX = 0, double dpiY = 0) =>
        SendAsync(new ClientViewportAllocatedMessage
        {
            Width = width, Height = height,
            DpiX = dpiX > 0 ? dpiX : _options.DpiX, DpiY = dpiY > 0 ? dpiY : _options.DpiY,
        });

    public Task PointerMovedAsync(double x, double y, InputModifiers[]? modifiers = null) =>
        SendAsync(new PointerMovedEventMessage { X = x, Y = y, Modifiers = modifiers });

    public Task PointerPressedAsync(double x, double y, MouseButton button, InputModifiers[]? modifiers = null) =>
        SendAsync(new PointerPressedEventMessage { X = x, Y = y, Button = button, Modifiers = modifiers });

    public Task PointerReleasedAsync(double x, double y, MouseButton button, InputModifiers[]? modifiers = null) =>
        SendAsync(new PointerReleasedEventMessage { X = x, Y = y, Button = button, Modifiers = modifiers });

    public Task ScrollAsync(double x, double y, double dx, double dy, InputModifiers[]? modifiers = null) =>
        SendAsync(new ScrollEventMessage { X = x, Y = y, DeltaX = dx, DeltaY = dy, Modifiers = modifiers });

    public Task KeyAsync(bool isDown, Key key, PhysicalKey physicalKey = default, string? symbol = null, InputModifiers[]? modifiers = null) =>
        SendAsync(new KeyEventMessage { IsDown = isDown, Key = key, PhysicalKey = physicalKey, KeySymbol = symbol, Modifiers = modifiers });

    public Task TextInputAsync(string text, InputModifiers[]? modifiers = null) =>
        SendAsync(new TextInputEventMessage { Text = text, Modifiers = modifiers });

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        try { _listener?.Dispose(); } catch { }
        try { _connection?.Dispose(); } catch { }
        if (_process is { } p)
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); } catch { }
            try { await p.WaitForExitAsync(new CancellationTokenSource(2000).Token).ConfigureAwait(false); } catch { }
            RaiseExited();
            p.Dispose();
        }
    }
}
