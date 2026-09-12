using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionReplayBufferTests
{
    [Fact]
    public void ReplayBuffer_Over4096Events_DropsOldest()
    {
        var epoch = Epoch();
        var replay = new AgentSessionReplayBuffer(epoch);
        for (var index = 0; index < 4097; index++)
            replay.Append(Guid.NewGuid(), new BusyChangedEvent { IsBusy = index % 2 == 0 });

        Assert.False(replay.ReadAfter(new ReplayCursor { Epoch = epoch, Sequence = 0 }).IsCovered);
        Assert.True(replay.ReadAfter(new ReplayCursor { Epoch = epoch, Sequence = 1 }).IsCovered);
    }

    [Fact]
    public void ReplayBuffer_Over8MiB_DropsOldest()
    {
        var epoch = Epoch();
        var replay = new AgentSessionReplayBuffer(epoch);
        var payload = JsonSerializer.SerializeToElement(new string('x', 5 * 1024 * 1024));
        replay.Append(Guid.NewGuid(), new HistoryAppendedEvent { Item = payload });
        replay.Append(Guid.NewGuid(), new HistoryAppendedEvent { Item = payload });

        Assert.False(replay.ReadAfter(new ReplayCursor { Epoch = epoch, Sequence = 0 }).IsCovered);
    }

    [Fact]
    public void ReplayBuffer_Over15Minutes_ForcesSnapshotFallback()
    {
        var epoch = Epoch();
        var time = new FakeTimeProvider();
        var replay = new AgentSessionReplayBuffer(epoch, time);
        replay.Append(Guid.NewGuid(), new BusyChangedEvent { IsBusy = true });
        time.Advance(TimeSpan.FromMinutes(16));

        Assert.False(replay.ReadAfter(new ReplayCursor { Epoch = epoch, Sequence = 0 }).IsCovered);
    }

    private static RuntimeEpoch Epoch() => new() { Value = Guid.NewGuid() };
}
