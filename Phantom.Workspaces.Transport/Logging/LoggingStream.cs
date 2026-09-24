using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace Phantom.Workspaces.Transport.Logging;

internal sealed class LoggingStream(Stream inner, ILogger logger, string marker) : Stream
{
    private int disposed;

    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => inner.Position = value; }
    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void SetLength(long value) => inner.SetLength(value);
    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);

    public override int Read(byte[] buffer, int offset, int count)
    {
        var started = Stopwatch.GetTimestamp();
        var read = inner.Read(buffer, offset, count);
        this.LogBytes("read", read, started);
        return read;
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        var read = await inner.ReadAsync(buffer, ct).ConfigureAwait(false);
        this.LogBytes("read", read, started);
        return read;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => this.ReadAsync(buffer.AsMemory(offset, count), ct).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
    {
        var started = Stopwatch.GetTimestamp();
        inner.Write(buffer, offset, count);
        this.LogBytes("written", count, started);
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
    {
        var started = Stopwatch.GetTimestamp();
        await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        this.LogBytes("written", buffer.Length, started);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        => this.WriteAsync(buffer.AsMemory(offset, count), ct).AsTask();

    private void LogBytes(string direction, int bytes, long started)
    {
        if (logger.IsEnabled(LogLevel.Debug))
            logger.LogDebug("Transport stream {Direction}; attempt {Attempt}; bytes {Bytes}; outcome complete; elapsed {ElapsedMilliseconds}ms.",
                direction, marker, bytes,
                Math.Clamp((long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, 0, int.MaxValue));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            logger.LogInformation("Transport stream closing; attempt {Attempt}.", marker);
            inner.Dispose();
        }
        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref this.disposed, 1) == 0)
        {
            logger.LogInformation("Transport stream closing; attempt {Attempt}.", marker);
            await inner.DisposeAsync().ConfigureAwait(false);
        }
        await base.DisposeAsync().ConfigureAwait(false);
    }
}
