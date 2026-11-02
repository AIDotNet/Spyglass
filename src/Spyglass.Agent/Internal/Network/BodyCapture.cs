using System.Net;

namespace Spyglass.Agent.Internal.Network;

/// <summary>Bounded copy of a body (spike S4): bytes beyond the limit are counted, not stored.</summary>
internal sealed class BodyCapture(int limit)
{
    readonly MemoryStream _buffer = new();
    readonly object _lock = new();
    public long TotalBytes { get; private set; }
    public bool Completed { get; private set; }
    public bool Truncated => TotalBytes > limit;
    public long StoredBytes { get { lock (_lock) return _buffer.Length; } }
    public event Action? OnCompleted;

    public void Append(ReadOnlySpan<byte> data)
    {
        lock (_lock)
        {
            TotalBytes += data.Length;
            var room = limit - (int)_buffer.Length;
            if (room > 0) _buffer.Write(data[..Math.Min(room, data.Length)]);
        }
    }

    public void Complete()
    {
        if (Completed) return;
        Completed = true;
        OnCompleted?.Invoke();
    }

    public byte[] Bytes { get { lock (_lock) return _buffer.ToArray(); } }
    public void Release() { lock (_lock) { _buffer.SetLength(0); _buffer.Capacity = 0; } }
}

/// <summary>Copies bytes into a <see cref="BodyCapture"/> as they flow, without buffering or delaying (spike S4, N4).</summary>
internal sealed class TeeContent : HttpContent
{
    readonly HttpContent _inner;
    readonly BodyCapture _capture;

    public TeeContent(HttpContent inner, BodyCapture capture)
    {
        _inner = inner;
        _capture = capture;
        foreach (var h in inner.Headers) Headers.TryAddWithoutValidation(h.Key, h.Value);
    }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        var src = await _inner.ReadAsStreamAsync().ConfigureAwait(false);
        using var tee = new TeeStream(src, _capture);
        await tee.CopyToAsync(stream).ConfigureAwait(false);
    }

    protected override async Task<Stream> CreateContentReadStreamAsync() =>
        new TeeStream(await _inner.ReadAsStreamAsync().ConfigureAwait(false), _capture);

    protected override bool TryComputeLength(out long length)
    {
        length = _inner.Headers.ContentLength ?? -1;
        return length >= 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _capture.Complete();   // e.g. redirect hops whose body is never read
        }
        base.Dispose(disposing);
    }
}

internal sealed class TeeStream(Stream inner, BodyCapture capture) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override int Read(byte[] buffer, int offset, int count) => Track(buffer.AsSpan(offset, count), inner.Read(buffer, offset, count));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var n = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        return Track(buffer.Span, n);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
        ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    int Track(Span<byte> buffer, int n)
    {
        if (n > 0) capture.Append(buffer[..n]);
        else capture.Complete();
        return n;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { capture.Complete(); inner.Dispose(); }
        base.Dispose(disposing);
    }
}
