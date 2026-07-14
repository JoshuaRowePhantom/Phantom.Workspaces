using System;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Shell;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class StreamMessageChannelStreamTests
{
    [Fact]
    public void Dispose_DoesNotBlockCallingThread()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        var stream = new StreamMessageChannelStream(pair.ClientEnd);

        bool wasOnThreadPoolThread = false;
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        ThreadPool.QueueUserWorkItem(_ =>
        {
            wasOnThreadPoolThread = Thread.CurrentThread.IsThreadPoolThread;
            stream.Dispose();
            disposeCompleted.SetResult();
        });

#pragma warning disable xUnit1031
        var completed = Task.WhenAny(
            disposeCompleted.Task,
            Task.Delay(TimeSpan.FromSeconds(2))).GetAwaiter().GetResult();
#pragma warning restore xUnit1031

        Assert.Same(disposeCompleted.Task, completed);
        Assert.True(wasOnThreadPoolThread);
    }

    [Fact]
    public async Task DisposeAsync_CompletesChannel()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        var stream = new StreamMessageChannelStream(pair.ClientEnd);

        byte[] data = [1, 2, 3, 4, 5];
        await stream.WriteAsync(data);

        await stream.DisposeAsync();

        var frame = await pair.HostEnd.ReceiveAsync(CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(StreamFrameKind.Data, frame.Kind);
        Assert.Equal(data, frame.Payload.ToArray());

        var endFrame = await pair.HostEnd.ReceiveAsync(CancellationToken.None);
        Assert.Null(endFrame);
    }

    [Fact]
    public async Task WriteAsync_SendsFrame_WhenChannelHasCapacity()
    {
        var pair = new InMemoryStreamMessageChannelPair();
        await using var stream = new StreamMessageChannelStream(pair.ClientEnd);

        byte[] data = [10, 20, 30];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var writeTask = stream.WriteAsync(data, cts.Token).AsTask();

        var completed = await Task.WhenAny(
            writeTask,
            Task.Delay(TimeSpan.FromSeconds(2)));

        Assert.Same(writeTask, completed);
        await writeTask;

        var frame = await pair.HostEnd.ReceiveAsync(CancellationToken.None);
        Assert.NotNull(frame);
        Assert.Equal(StreamFrameKind.Data, frame.Kind);
        Assert.Equal(data, frame.Payload.ToArray());
    }
}
