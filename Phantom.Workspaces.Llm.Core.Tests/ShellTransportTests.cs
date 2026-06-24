using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Tests;

public sealed class ShellTransportTests
{
    // A generous failsafe token so a regression that deadlocks fails fast instead of hanging the suite.
    // Test correctness never depends on this elapsing: every assertion is reached by deterministic
    // completion of the awaited operation, never by a timed wait.
    private static CancellationToken Failsafe => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    [Fact]
    public void StreamControlMessage_ResizeRoundTrips()
    {
        var message = new StreamControlMessage
        {
            Type = StreamControlMessage.Types.Resize,
            Columns = 120,
            Rows = 40,
        };

        var restored = StreamControlMessage.FromPayload(message.ToPayload());

        Assert.Equal(StreamControlMessage.Types.Resize, restored.Type);
        Assert.Equal(120, restored.Columns);
        Assert.Equal(40, restored.Rows);
        Assert.Null(restored.Signal);
        Assert.Null(restored.ExitCode);
    }

    [Fact]
    public void StreamControlMessage_ExitRoundTrips()
    {
        var restored = StreamControlMessage.FromPayload(
            new StreamControlMessage { Type = StreamControlMessage.Types.Exit, ExitCode = 42 }.ToPayload());

        Assert.Equal(StreamControlMessage.Types.Exit, restored.Type);
        Assert.Equal(42, restored.ExitCode);
        Assert.Null(restored.Columns);
    }

    [Fact]
    public async Task StreamFramedMessageChannel_RoundTripsFramesAndReportsCleanEndOfStream()
    {
        var wire = new MemoryStream();

        await using (var writer = new StreamFramedMessageChannel(wire, ownsStream: false))
        {
            await writer.SendAsync(new StreamFrame(StreamFrameKind.Data, new byte[] { 1, 2, 3 }), Failsafe);
            await writer.SendAsync(new StreamFrame(StreamFrameKind.Control, new byte[] { 9 }), Failsafe);
            await writer.SendAsync(new StreamFrame(StreamFrameKind.Data, ReadOnlyMemory<byte>.Empty), Failsafe);
        }

        wire.Position = 0;
        await using var reader = new StreamFramedMessageChannel(wire, ownsStream: false);

        var first = await reader.ReceiveAsync(Failsafe);
        Assert.NotNull(first);
        Assert.Equal(StreamFrameKind.Data, first!.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, first.Payload.ToArray());

        var second = await reader.ReceiveAsync(Failsafe);
        Assert.NotNull(second);
        Assert.Equal(StreamFrameKind.Control, second!.Kind);
        Assert.Equal(new byte[] { 9 }, second.Payload.ToArray());

        var third = await reader.ReceiveAsync(Failsafe);
        Assert.NotNull(third);
        Assert.Equal(StreamFrameKind.Data, third!.Kind);
        Assert.True(third.Payload.IsEmpty);

        Assert.Null(await reader.ReceiveAsync(Failsafe));
    }

    [Fact]
    public async Task StreamFramedMessageChannel_ThrowsOnTruncatedHeader()
    {
        var wire = new MemoryStream(new byte[] { (byte)StreamFrameKind.Data, 0, 0 });
        await using var reader = new StreamFramedMessageChannel(wire, ownsStream: false);

        await Assert.ThrowsAsync<EndOfStreamException>(async () => await reader.ReceiveAsync(Failsafe));
    }

    [Fact]
    public async Task InMemoryStreamMessageChannelPair_DeliversFramesBothDirections()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var client = pair.ClientEnd;
        await using var host = pair.HostEnd;

        await client.SendAsync(new StreamFrame(StreamFrameKind.Data, new byte[] { 7 }), Failsafe);
        var onHost = await host.ReceiveAsync(Failsafe);
        Assert.Equal(new byte[] { 7 }, onHost!.Payload.ToArray());

        await host.SendAsync(new StreamFrame(StreamFrameKind.Control, new byte[] { 8 }), Failsafe);
        var onClient = await client.ReceiveAsync(Failsafe);
        Assert.Equal(StreamFrameKind.Control, onClient!.Kind);
        Assert.Equal(new byte[] { 8 }, onClient.Payload.ToArray());
    }

    [Fact]
    public async Task InMemoryStreamMessageChannelPair_ReceiveReturnsNullAfterPeerCloses()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        var client = pair.ClientEnd;
        await using var host = pair.HostEnd;

        await client.DisposeAsync();

        Assert.Null(await host.ReceiveAsync(Failsafe));
    }

    [Fact]
    public async Task ShellSession_HostDataReachesStreamReader()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        await host.SendAsync(new StreamFrame(StreamFrameKind.Data, new byte[] { 10, 20, 30 }), Failsafe);

        var buffer = new byte[3];
        await session.Stream.ReadExactlyAsync(buffer, Failsafe);
        Assert.Equal(new byte[] { 10, 20, 30 }, buffer);
    }

    [Fact]
    public async Task ShellSession_StreamWriteSendsDataFrameToHost()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        await session.Stream.WriteAsync(new byte[] { 5, 6 }, Failsafe);

        var frame = await host.ReceiveAsync(Failsafe);
        Assert.Equal(StreamFrameKind.Data, frame!.Kind);
        Assert.Equal(new byte[] { 5, 6 }, frame.Payload.ToArray());
    }

    [Fact]
    public async Task ShellSession_ResizeAndSignalSendControlFrames()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        await session.ResizeAsync(132, 50, Failsafe);
        var resize = StreamControlMessage.FromPayload((await host.ReceiveAsync(Failsafe))!.Payload);
        Assert.Equal(StreamControlMessage.Types.Resize, resize.Type);
        Assert.Equal(132, resize.Columns);
        Assert.Equal(50, resize.Rows);

        await session.SignalAsync("SIGINT", Failsafe);
        var signal = StreamControlMessage.FromPayload((await host.ReceiveAsync(Failsafe))!.Payload);
        Assert.Equal(StreamControlMessage.Types.Signal, signal.Type);
        Assert.Equal("SIGINT", signal.Signal);
    }

    [Fact]
    public async Task ShellSession_ExitControlCompletesWaitForExit()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        var received = new TaskCompletionSource<StreamControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.ControlMessageReceived += (_, message) => received.TrySetResult(message);

        await host.SendAsync(
            new StreamFrame(
                StreamFrameKind.Control,
                new StreamControlMessage { Type = StreamControlMessage.Types.Exit, ExitCode = 3 }.ToPayload()),
            Failsafe);

        Assert.Equal(3, await session.WaitForExitAsync());
        Assert.Equal(StreamControlMessage.Types.Exit, (await received.Task).Type);
    }

    [Fact]
    public async Task ShellSession_ChannelCloseWithoutExitCancelsWaitForExit()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        await host.DisposeAsync();

        await Assert.ThrowsAsync<TaskCanceledException>(async () => await session.WaitForExitAsync());
    }

    [Fact]
    public async Task ShellSession_StreamReportsEndOfStreamWhenChannelCloses()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        var host = pair.HostEnd;
        await using var session = new ShellSession(pair.ClientEnd);

        await host.DisposeAsync();

        var read = await session.Stream.ReadAsync(new byte[8], Failsafe);
        Assert.Equal(0, read);
    }
}
