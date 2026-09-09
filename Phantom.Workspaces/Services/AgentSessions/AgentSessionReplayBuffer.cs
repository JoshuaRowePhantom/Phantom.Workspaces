using Phantom.Workspaces.Llm.Remote;
using System.Text;
using ProtocolReplayCursor = Phantom.Workspaces.Llm.Remote.ReplayCursor;

namespace Phantom.Workspaces.Services.AgentSessions;

internal readonly record struct ReplayReadResult
{
    public required bool IsCovered { get; init; }
    public required IReadOnlyList<AgentSessionServerFrame> Frames { get; init; }
}

internal sealed class AgentSessionReplayBuffer
{
    private const int MaximumEvents = 4096;
    private const long MaximumBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan MaximumAge = TimeSpan.FromMinutes(15);
    private readonly object gate = new();
    private readonly LinkedList<Entry> entries = [];
    private readonly RuntimeEpoch epoch;
    private readonly TimeProvider timeProvider;
    private long highWaterMark;
    private long retainedBytes;

    internal AgentSessionReplayBuffer(RuntimeEpoch epoch, TimeProvider? timeProvider = null)
    {
        this.epoch = epoch;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal long HighWaterMark
    {
        get { lock (this.gate) return this.highWaterMark; }
    }

    internal AgentSessionServerFrame Append(Guid correlationId, AgentSessionServerEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);
        lock (this.gate)
        {
            var frame = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
                this.epoch, ++this.highWaterMark, correlationId, value);
            var bytes = Encoding.UTF8.GetByteCount(AgentSessionProtocolCodec.SerializeFrame(frame).GetRawText());
            this.entries.AddLast(new Entry(frame, this.timeProvider.GetUtcNow(), bytes));
            this.retainedBytes += bytes;
            this.Trim();
            return frame;
        }
    }

    internal ReplayReadResult ReadAfter(ProtocolReplayCursor cursor)
    {
        lock (this.gate)
        {
            this.Trim();
            var first = this.entries.First?.Value.Frame.Sequence ?? this.highWaterMark + 1;
            var covered = cursor.Epoch == this.epoch
                && cursor.Sequence <= this.highWaterMark
                && cursor.Sequence >= first - 1;
            return new ReplayReadResult
            {
                IsCovered = covered,
                Frames = covered
                    ? this.entries.Where(entry => entry.Frame.Sequence > cursor.Sequence).Select(entry => entry.Frame).ToArray()
                    : [],
            };
        }
    }

    private void Trim()
    {
        var cutoff = this.timeProvider.GetUtcNow() - MaximumAge;
        while (this.entries.First is { } first
               && (this.entries.Count > MaximumEvents || this.retainedBytes > MaximumBytes || first.Value.CreatedAt < cutoff))
        {
            this.retainedBytes -= first.Value.SerializedBytes;
            this.entries.RemoveFirst();
        }
    }

    private sealed record Entry(AgentSessionServerFrame Frame, DateTimeOffset CreatedAt, long SerializedBytes);
}
