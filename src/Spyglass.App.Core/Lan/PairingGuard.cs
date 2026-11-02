using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Spyglass.App.Core.Lan;

public enum PairingResult { Accepted, WrongCode, Blocked }

/// <summary>
/// The pairing code for LAN connections (design/protocol §6): a 6-digit code from a cryptographic RNG that lives only in
/// memory (a new one per run, replaceable with <see cref="Regenerate"/>), compared in constant time, plus a per-address
/// failure throttle: <see cref="MaxFailures"/> wrong codes within <see cref="FailureWindow"/> block that address for
/// <see cref="BlockDuration"/>. Thread-safe. IPv6 addresses are throttled per /64 (a host owns a whole /64).
/// </summary>
public sealed class PairingGuard
{
    public const int CodeLength = 6;
    public const int MaxFailures = 5;
    public static readonly TimeSpan FailureWindow = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan BlockDuration = TimeSpan.FromMinutes(5);
    const int MaxTrackedAddresses = 4096;

    sealed class Entry { public readonly List<DateTimeOffset> Failures = []; public DateTimeOffset BlockedUntil; }

    readonly object _lock = new();
    readonly Dictionary<string, Entry> _entries = new();
    readonly Func<DateTimeOffset> _now;
    string _code;

    public PairingGuard(string? code = null, Func<DateTimeOffset>? clock = null)
    {
        _now = clock ?? (() => DateTimeOffset.UtcNow);
        _code = code ?? NewCode();
    }

    public string Code { get { lock (_lock) return _code; } }

    /// <summary>Raised (on the calling thread) after <see cref="Regenerate"/>.</summary>
    public event Action? CodeChanged;

    /// <summary>Six random decimal digits, uniformly distributed (leading zeros kept).</summary>
    public static string NewCode() => RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");

    public string Regenerate()
    {
        string code;
        lock (_lock)
        {
            do code = NewCode(); while (code == _code);
            _code = code;
        }
        CodeChanged?.Invoke();
        return code;
    }

    /// <summary>Constant-time comparison (independent of where the strings differ and of their lengths).</summary>
    public static bool CodesEqual(string? a, string? b)
    {
        var equal = CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(a ?? "")), SHA256.HashData(Encoding.UTF8.GetBytes(b ?? "")));
        return equal & a is not null & b is not null;
    }

    public bool IsBlocked(IPAddress address)
    {
        lock (_lock) return _entries.TryGetValue(Key(address), out var e) && e.BlockedUntil > _now();
    }

    /// <summary>
    /// Checks a presented code for a non-loopback peer. A blocked address is refused without looking at the code,
    /// so the block cannot be used as a guessing oracle. A wrong or missing code counts as a failure.
    /// </summary>
    public PairingResult Check(IPAddress address, string? presented)
    {
        var key = Key(address);
        lock (_lock)
        {
            var now = _now();
            if (_entries.TryGetValue(key, out var existing) && existing.BlockedUntil > now) return PairingResult.Blocked;
            if (CodesEqual(presented, _code))
            {
                _entries.Remove(key);
                return PairingResult.Accepted;
            }
            if (!_entries.TryGetValue(key, out var e))
            {
                if (_entries.Count >= MaxTrackedAddresses) Prune(now);
                _entries[key] = e = new Entry();
            }
            e.Failures.RemoveAll(t => now - t > FailureWindow);
            e.Failures.Add(now);
            if (e.Failures.Count >= MaxFailures)
            {
                e.BlockedUntil = now + BlockDuration;
                e.Failures.Clear();
            }
            return PairingResult.WrongCode;
        }
    }

    void Prune(DateTimeOffset now)
    {
        foreach (var k in _entries.Where(p => p.Value.BlockedUntil <= now && p.Value.Failures.All(t => now - t > FailureWindow)).Select(p => p.Key).ToList())
            _entries.Remove(k);
        if (_entries.Count >= MaxTrackedAddresses) _entries.Clear();   // flooded by distinct addresses: forget rather than grow
    }

    static string Key(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return address.ToString();
        return Convert.ToHexString(address.GetAddressBytes().AsSpan(0, 8));
    }
}
