using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Spyglass.Protocol;

namespace Spyglass.App.Core.Lan;

/// <summary>
/// LAN policy of the hub (design/protocol §6). When <see cref="AllowLan"/> is false only loopback peers get anything.
/// When true, Kestrel listens on all interfaces, but <see cref="Install"/> keeps every path except /agent loopback-only
/// (403 for the rest, MCP included), and non-loopback agents must present the pairing code in session/hello.
/// </summary>
public sealed class LanGate
{
    static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(15);
    static readonly TimeSpan FlushGrace = TimeSpan.FromSeconds(2);
    readonly object _lock = new();
    readonly HashSet<Admission> _remote = [];

    public LanGate(bool allowLan = false, PairingGuard? guard = null)
    {
        AllowLan = allowLan;
        Guard = guard ?? new PairingGuard();
        Guard.CodeChanged += DropRemoteSessions;
    }

    public bool AllowLan { get; }
    public PairingGuard Guard { get; }

    /// <summary>Loopback, including IPv4-mapped IPv6 (::ffff:127.0.0.1). An unknown address is not loopback (fail closed).</summary>
    public static bool IsLoopback(IPAddress? address) =>
        address is not null && IPAddress.IsLoopback(address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address);

    /// <summary>Whether a request from <paramref name="remote"/> for <paramref name="path"/> may proceed.</summary>
    public bool IsAllowed(IPAddress? remote, PathString path) =>
        IsLoopback(remote) || (AllowLan && path.StartsWithSegments(SpyglassProtocol.AgentPath, StringComparison.OrdinalIgnoreCase));

    /// <summary>First middleware: 403 for anything a non-loopback peer may not reach.</summary>
    public void Install(WebApplication app) => app.Use(async (ctx, next) =>
    {
        if (IsAllowed(ctx.Connection.RemoteIpAddress, ctx.Request.Path)) await next().ConfigureAwait(false);
        else ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
    });

    /// <summary>Decides about an incoming /agent connection. Dispose when the connection ends.</summary>
    public Admission Admit(IPAddress? remote, CancellationToken requestAborted)
    {
        if (IsLoopback(remote)) return new Admission(this, null, requestAborted, null);
        if (!AllowLan || remote is null) return new Admission(this, null, requestAborted, StatusCodes.Status403Forbidden);
        if (Guard.IsBlocked(remote)) return new Admission(this, null, requestAborted, StatusCodes.Status429TooManyRequests);
        var a = new Admission(this, remote, requestAborted, null);
        lock (_lock) _remote.Add(a);
        return a;
    }

    void DropRemoteSessions()
    {
        Admission[] all;
        lock (_lock) all = [.. _remote];
        foreach (var a in all) a.Close();
    }

    public sealed class Admission : IDisposable
    {
        readonly LanGate _gate;
        readonly IPAddress? _remote;
        readonly CancellationTokenSource _cts;

        internal Admission(LanGate gate, IPAddress? remote, CancellationToken aborted, int? refusal)
        {
            _gate = gate; _remote = remote; Refusal = refusal;
            _cts = CancellationTokenSource.CreateLinkedTokenSource(aborted);
            if (remote is not null) _cts.CancelAfter(HelloTimeout);   // an unauthenticated peer may not idle
        }

        /// <summary>HTTP status to refuse the upgrade with, or null to accept the WebSocket.</summary>
        public int? Refusal { get; }

        /// <summary>Cancelled when the peer must be dropped (code regenerated, authentication failed or timed out).</summary>
        public CancellationToken Token => _cts.Token;

        /// <summary>Called from session/hello. Loopback peers always pass; others need the code (else -32011 and the connection is closed).</summary>
        public void Authorize(string? presented)
        {
            if (_remote is null) return;
            var result = _gate.Guard.Check(_remote, presented);
            if (result == PairingResult.Accepted) { _cts.CancelAfter(Timeout.InfiniteTimeSpan); return; }
            // Let the error response flush (a loaded machine needs more than a few hundred ms), then drop the connection.
            // Nothing else is possible meanwhile: the peer has no session and the hello timeout still applies.
            _ = Task.Delay(FlushGrace).ContinueWith(_ => Close(), TaskScheduler.Default);
            throw new SpyglassProtocolException(ErrorCodes.Unauthorized, result == PairingResult.Blocked
                ? "Too many wrong pairing codes from this address; try again later."
                : "Pairing code missing or wrong. Use the code shown in Spyglass (o.PairingCode or SPYGLASS_PAIRING_CODE).");
        }

        internal void Close() { try { _cts.Cancel(); } catch (ObjectDisposedException) { } }

        public void Dispose()
        {
            lock (_gate._lock) _gate._remote.Remove(this);
            _cts.Dispose();
        }
    }
}
