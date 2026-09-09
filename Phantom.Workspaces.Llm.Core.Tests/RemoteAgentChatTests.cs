using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Fact]
    public void RemoteAgentChatAttachOptions_RequiredInitProperties_AreMarkedRequired()
    {
        var required = typeof(RemoteAgentChatAttachOptions).GetProperties()
            .Where(p => p.GetCustomAttributes(typeof(System.Runtime.CompilerServices.RequiredMemberAttribute), false).Length != 0)
            .Select(p => p.Name);
        Assert.Equal(
            [nameof(RemoteAgentChatAttachOptions.Client), nameof(RemoteAgentChatAttachOptions.OpenRequest),
             nameof(RemoteAgentChatAttachOptions.ForegroundScheduler)],
            required);
    }

    [Fact]
    public async Task AttachAsync_ValidSnapshot_PublishesInitializedProxy()
    {
        var transport = new TestTransport();
        var client = new RemoteAgentSessionClient(transport);
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client,
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await transport.SendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await using var chat = await attaching;
        Assert.Equal("session", chat.Information.AgentSessionId);
        Assert.Equal(7, chat.Usage.TotalInputTokenCount);
        Assert.Equal(2, chat.InputQueues.Queues.Count);
        Assert.False(chat.IsBusy);
        Assert.Equal(1, chat.ViewerCount);
    }

    [Fact]
    public async Task AttachAsync_CancelledBeforeSnapshot_DisposesClientAndPublishesNothing()
    {
        var transport = new TestTransport();
        var client = new RemoteAgentSessionClient(transport);
        using var cancellation = new CancellationTokenSource();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client,
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        }, cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => attaching);
        Assert.True(transport.ChannelDisposed);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => client.InterruptAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task ProxyEvents_OrderedFrame_AreRaisedAfterStateMutation()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.UsageChanged += (_, _) => observed.TrySetResult(chat.Usage.TotalOutputTokenCount == 9);
            await transport.SendAsync(Frame(2, new UsageChangedEvent
            {
                Usage = new Usage { TotalOutputTokenCount = 9 },
            }));
            Assert.True(await observed.Task);
        }
    }

    [Fact]
    public async Task InputQueues_OrderedDelta_MatchesLocalReadModel()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.InputQueues.Changed += (_, _) => changed.TrySetResult();
            var queue = AgentSessionProtocolCodecTests.Snapshot().InputQueues.Queues.Single(q => q.IsDefault);
            await transport.SendAsync(Frame(2, new QueueChangedEvent
            {
                Revision = 2,
                Queues = [queue with { Name = "Renamed", Revision = 1 }],
                RemovedQueueIds = [],
            }));
            await changed.Task;
            Assert.Equal(2, chat.InputQueues.Snapshot.Revision);
            Assert.Equal("Renamed", chat.InputQueues.DefaultQueue.Snapshot.Name);
        }
    }

    [Fact]
    public async Task ProxyGetters_AfterOrderedStreamingFrames_ReturnMirroredState()
    {
        var (transport, chat) = await AttachAsync();
        await using (chat)
        {
            var item = new AgentChatHistoryItem
            {
                Role = ChatRole.Assistant,
                Contents = [new TextContent("partial")],
            };
            var runningChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((System.Collections.Specialized.INotifyCollectionChanged)chat.RunningItems).CollectionChanged +=
                (_, _) => runningChanged.TrySetResult();
            await transport.SendAsync(Frame(2, new StreamingStartedEvent
            {
                RunId = "run-1",
                Item = JsonSerializer.SerializeToElement(item, AIJsonUtilities.DefaultOptions),
            }));
            await runningChanged.Task;
            Assert.Empty(chat.History);

            var completed = item with { Contents = [new TextContent("complete")] };
            var historyChanged = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged +=
                (_, _) => historyChanged.TrySetResult();
            await transport.SendAsync(Frame(3, new StreamingCompletedEvent
            {
                RunId = "run-1",
                Item = JsonSerializer.SerializeToElement(completed, AIJsonUtilities.DefaultOptions),
            }));
            await historyChanged.Task;
            Assert.Equal(ChatRole.Assistant, chat.History[0].Role);
        }
    }

    [Fact]
    public async Task EnqueueSystemNote_RemoteProxy_AddsLocalDisplayOnlyNote()
    {
        var (_, chat) = await AttachAsync();
        await using (chat)
        {
            var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            ((System.Collections.Specialized.INotifyCollectionChanged)chat.History).CollectionChanged +=
                (_, _) => changed.TrySetResult();
            chat.EnqueueSystemNote("local note");
            await changed.Task;
            Assert.Equal("diagnostic", chat.History[0].Role.Value);
        }
    }

    [Fact]
    public async Task ProxyCommand_AfterDispose_ThrowsObjectDisposedException()
    {
        var (_, chat) = await AttachAsync();
        await chat.DisposeAsync();
        Assert.Throws<ObjectDisposedException>(() => _ = chat.Information);
        Assert.Throws<ObjectDisposedException>(() => chat.Interrupt());
    }

    [Fact]
    public async Task GetService_TransportOrPolicyType_ReturnsNull()
    {
        var (_, chat) = await AttachAsync();
        await using (chat)
        {
            Assert.Null(chat.GetService(typeof(ITransport)));
            Assert.Null(chat.GetService(typeof(RemoteAgentSessionClient)));
        }
    }

    private static async Task<(TestTransport Transport, RemoteAgentChat Chat)> AttachAsync()
    {
        var transport = new TestTransport();
        var attaching = RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = new RemoteAgentSessionClient(transport),
            OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        await transport.SendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        return (transport, await attaching);
    }

    private static JsonElement Frame(long sequence, AgentSessionServerEvent value, Guid? correlation = null)
        => AgentSessionProtocolCodec.SerializeFrame(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            AgentSessionProtocolCodecTests.Epoch(), sequence, correlation ?? Guid.NewGuid(), value));

    private sealed class TestTransport : ITransport
    {
        private readonly TestMessageChannel channel = new();
        public bool ChannelDisposed => this.channel.Disposed;
        public ChannelReader<JsonElement> Outgoing => this.channel.Outgoing.Reader;
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => Task.FromResult<IMessageChannel>(this.channel);
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask SendAsync(JsonElement value) => this.channel.Incoming.Writer.WriteAsync(value);
        public void Disconnect() => this.channel.Incoming.Writer.TryComplete();
    }

    private sealed class TestMessageChannel : IMessageChannel
    {
        public bool Disposed { get; private set; }
        public Channel<JsonElement> Outgoing { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> Incoming { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.Outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.Incoming.Reader;
        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.Outgoing.Writer.TryComplete();
            this.Incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
