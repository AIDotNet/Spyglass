using System.Text.Json.Serialization;

namespace Spyglass.Protocol.Messages;

// design/network/README.md §6 and 02-chrome-parity.md. Capture runs only between network/enable and network/disable.

public sealed record NetworkEnableParams(
    bool CaptureBodies = true,
    int BodyLimit = 1024 * 1024,
    bool CaptureInitiator = true,
    IReadOnlyList<string>? SensitiveHeaders = null);

public enum NetworkState
{
    [JsonStringEnumMemberName("pending")] Pending,
    [JsonStringEnumMemberName("finished")] Finished,
    [JsonStringEnumMemberName("failed")] Failed,
}

/// <summary>A header as captured. <see cref="IsSensitive"/> marks credentials (masked by clients, stripped from sanitized HAR).</summary>
public sealed record HeaderDto(string Name, string Value, bool IsSensitive = false);

public sealed record InitiatorDto(string? Frame, IReadOnlyList<string> Stack);

/// <summary>Chrome's Timing phases in milliseconds; -1 when not applicable (e.g. no DNS on a reused connection).</summary>
public sealed record NetworkTimingDto(
    double Queueing = -1, double Dns = -1, double Connect = -1, double Ssl = -1,
    double Send = -1, double Wait = -1, double Download = -1, double Total = -1);

/// <summary>Full snapshot of one request; every network/* notification carries these and clients upsert by Id.</summary>
public sealed record NetworkRequestDto
{
    public required long Id { get; init; }
    public required string Method { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset StartTime { get; init; }
    public NetworkState State { get; init; }
    public IReadOnlyList<HeaderDto> RequestHeaders { get; init; } = [];
    /// <summary>Headers the HTTP handler adds after capture (Host, Cookie from CookieContainer…) are missing: show Chrome's "Provisional headers" notice.</summary>
    public bool ProvisionalHeaders { get; init; } = true;
    public long? RequestBodySize { get; init; }
    public int? Status { get; init; }
    public string? StatusText { get; init; }
    public string? HttpVersion { get; init; }
    public IReadOnlyList<HeaderDto> ResponseHeaders { get; init; } = [];
    public string? MimeType { get; init; }
    /// <summary>Decoded body bytes (HttpClient decompresses before we see the body, so the transferred size is unknown).</summary>
    public long? ResponseSize { get; init; }
    public bool ResponseTruncated { get; init; }
    public bool BodiesEvicted { get; init; }
    public bool IsUpgrade { get; init; }
    public long? RedirectedFromId { get; init; }
    public string? RemoteAddress { get; init; }
    public long? ConnectionId { get; init; }
    /// <summary>Chrome-style error, e.g. "net::ERR_CONNECTION_REFUSED", plus the .NET message in <see cref="ErrorDetail"/>.</summary>
    public string? Error { get; init; }
    public string? ErrorDetail { get; init; }
    public bool Canceled { get; init; }
    public InitiatorDto? Initiator { get; init; }
    public NetworkTimingDto? Timing { get; init; }
    public DateTimeOffset? EndTime { get; init; }
}

public sealed record NetworkRequestsParams(IReadOnlyList<NetworkRequestDto> Requests);

public sealed record NetworkGetBodyParams(long Id, string Part = "response");
public sealed record NetworkBodyResult(string? MimeType, string Encoding, string Content, long TotalBytes, bool Truncated);

public sealed record NetworkSearchParams(string Query, bool MatchCase = false, bool Regex = false, int Limit = 200);
public sealed record NetworkSearchMatch(long Id, string Part, string Preview);
public sealed record NetworkSearchResult(IReadOnlyList<NetworkSearchMatch> Matches);

public sealed record NetworkStatusResult(bool Enabled, int Captured);
