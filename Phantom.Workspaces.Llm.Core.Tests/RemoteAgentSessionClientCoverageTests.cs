using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentSessionClientTests
{
    [Fact]
    public async Task GetStatusAsync_UnauthorizedOrMissing_ReturnsUnavailableWithoutMetadata()
    {
        static AgentSessionStatusRequest Request(ITransport transport) => new()
        {
            Transport = transport,
            OpenRequest = AgentSessionProtocolCodecTests.Open() with { OpenIntent = AgentSessionOpenIntent.Status },
        };

        var denial = new TestTransport();
        var deniedCorrelation = Guid.NewGuid();
        await denial.ServerSendAsync(Frame(1, new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = "unauthorized", Operation = "status", IsRetryable = false,
                Message = "Unavailable.", CorrelationId = deniedCorrelation,
            },
        }, deniedCorrelation));
        Assert.Equal(AgentSessionRemoteStatus.Unavailable,
            await RemoteAgentSessionClient.GetStatusAsync(Request(denial)));
        Assert.True(denial.ChannelDisposed);

        var metadataBearingDenial = new TestTransport();
        await metadataBearingDenial.ServerSendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        Assert.Equal(AgentSessionRemoteStatus.Unavailable,
            await RemoteAgentSessionClient.GetStatusAsync(Request(metadataBearingDenial)));
        Assert.True(metadataBearingDenial.ChannelDisposed);

        var missing = new TestTransport();
        missing.CompleteServer();
        Assert.Equal(AgentSessionRemoteStatus.Unavailable,
            await RemoteAgentSessionClient.GetStatusAsync(Request(missing)));
        Assert.True(missing.ChannelDisposed);
    }

    [Fact]
    public async Task ConnectAsync_FirstCall_OpensAttachAgentSessionChannel()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        Assert.Equal("attach-agent-session", transport.LastOpen.GetProperty("type").GetString());
        Assert.Equal("session", transport.LastOpen.GetProperty("agent-session-id").GetString());
    }

    [Fact]
    public async Task ConnectAsync_CancelledBeforeOpen_LeavesClientDisconnected()
    {
        var transport = new TestTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.ConnectAsync(AgentSessionProtocolCodecTests.Open(), cancellation.Token));
        Assert.Null(client.LastAppliedCursor);

        var retry = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.ServerSendAsync(Frame(1, new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await retry;
    }

    [Fact]
    public async Task ConnectAsync_UnknownDiscriminator_ClosesWithProtocolException()
    {
        var transport = new TestTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        var unknown = Frame(1, new BusyChangedEvent { IsBusy = false }).GetRawText()
            .Replace("\"busy-changed\"", "\"unknown\"", StringComparison.Ordinal);
        await transport.ServerSendAsync(JsonDocument.Parse(unknown).RootElement.Clone());
        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => connecting);
        Assert.True(transport.ChannelDisposed);
    }

    [Fact]
    public async Task ReconnectAsync_UnexpectedLoss_ForcesAttachWithTokenAndLastAppliedCursor()
    {
        var transport = new ReconnectTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.SendAsync(0, Frame(1, new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await connecting;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        transport.Complete(0);
        await disconnected.Task;

        var reconnecting = client.ReconnectAsync();
        await transport.SendAsync(1, Frame(2, new BusyChangedEvent { IsBusy = true }));
        await reconnecting;
        var open = transport.Opens[1];
        Assert.Equal("attach", open.GetProperty("open-intent").GetString());
        Assert.Equal(AgentSessionProtocolCodecTests.Open().AttachmentToken, open.GetProperty("attachment-token").GetString());
        Assert.Equal(1, open.GetProperty("replay-cursor").GetProperty("sequence").GetInt64());
        Assert.Equal(2, client.LastAppliedCursor!.Value.Sequence);
    }

    [Fact]
    public async Task ReconnectAsync_ConnectedDetachedTerminalOrExpired_ThrowsInvalidOperationException()
    {
        var connectedTransport = new TestTransport();
        await using (var connected = await ConnectAsync(connectedTransport))
            await Assert.ThrowsAsync<InvalidOperationException>(() => connected.ReconnectAsync());

        var detachedTransport = new TestTransport();
        await using (var detached = await ConnectAsync(detachedTransport))
        {
            await detached.DetachAsync();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => detached.ReconnectAsync());
        }

        var terminalTransport = new ReconnectTransport();
        await using (var terminal = await ConnectAsync(terminalTransport))
        {
            var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            terminal.FrameReceived += (_, frame) =>
            {
                if (frame.Type == "session-terminal") received.TrySetResult();
            };
            await terminalTransport.SendAsync(0, Frame(2, new SessionTerminalEvent
            {
                Reason = "done",
                CompletionState = JsonDocument.Parse("""{"state":"completed"}""").RootElement.Clone(),
            }));
            await received.Task;
            terminalTransport.Complete(0);
            await Assert.ThrowsAsync<InvalidOperationException>(() => terminal.ReconnectAsync());
        }

        var time = new FakeTimeProvider(DateTimeOffset.Parse("2026-09-09T00:00:00Z"));
        var expiredTransport = new ReconnectTransport();
        await using var expired = await ConnectAsync(expiredTransport, time);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        expired.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        expiredTransport.Complete(0);
        await disconnected.Task;
        time.Advance(TimeSpan.FromSeconds(6));
        await Assert.ThrowsAsync<InvalidOperationException>(() => expired.ReconnectAsync());
    }

    [Fact]
    public async Task ReconnectAsync_ConcurrentCallers_JoinSingleFlightAttempt()
    {
        var transport = new ReconnectTransport();
        await using var client = await ConnectAsync(transport);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        transport.Complete(0);
        await disconnected.Task;

        var first = client.ReconnectAsync();
        var second = client.ReconnectAsync();
        Assert.Equal(2, transport.Opens.Count);
        await transport.SendAsync(1, Frame(2, new BusyChangedEvent { IsBusy = true }));
        await Task.WhenAll(first, second);
        Assert.Equal(2, transport.Opens.Count);
    }

    [Fact]
    public async Task ReconnectAsync_CancelledAttempt_AllowsRetryBeforeDeadline()
    {
        var transport = new ReconnectTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.SendAsync(0, Frame(1, new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await connecting;
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        transport.Complete(0);
        await disconnected.Task;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReconnectAsync(cancellation.Token));
        var retry = client.ReconnectAsync();
        await transport.SendAsync(1, Frame(2, new BusyChangedEvent { IsBusy = true }));
        await retry;
    }

    [Fact]
    public async Task DeleteQueueAsync_Connected_SerializesCommandAndAwaitsResult()
        => await AssertQueueCommandAsync(
            (client, id) => client.DeleteQueueAsync(new DeleteAgentInputQueueRequest
            { QueueId = "queue", CommandId = id, ExpectedRevision = 3 }),
            command => Assert.Equal(("queue", 3L), (Assert.IsType<DeleteQueueCommand>(command).QueueId, ((DeleteQueueCommand)command).ExpectedRevision)));

    [Fact]
    public async Task EnqueueAsync_Connected_SerializesMessagesTargetAndRevision()
        => await AssertQueueCommandAsync(
            (client, id) => client.EnqueueAsync(new EnqueueAgentInputRequest
            {
                TargetQueueId = "queue", Messages = [new ChatMessage(ChatRole.User, "hello")],
                CommandId = id, ExpectedRevision = 3,
            }),
            command =>
            {
                var value = Assert.IsType<EnqueueInputCommand>(command);
                Assert.Equal(("queue", 3L), (value.TargetQueueId, value.ExpectedRevision));
                Assert.Equal(JsonValueKind.Array, value.Messages.ValueKind);
            });

    [Fact]
    public async Task EditAsync_Connected_SerializesStableItemIdAndMessages()
        => await AssertQueueCommandAsync(
            (client, id) => client.EditAsync(new EditAgentInputQueueItemRequest
            {
                QueueId = "queue", ItemId = "item", Messages = [new ChatMessage(ChatRole.User, "edited")],
                CommandId = id, ExpectedRevision = 3,
            }),
            command =>
            {
                var value = Assert.IsType<EditQueueItemCommand>(command);
                Assert.Equal(("queue", "item"), (value.QueueId, value.ItemId));
                Assert.Equal(JsonValueKind.Array, value.Messages.ValueKind);
            });

    [Fact]
    public async Task RemoveAsync_Connected_SerializesStableItemId()
        => await AssertQueueCommandAsync(
            (client, id) => client.RemoveAsync(new RemoveAgentInputQueueItemRequest
            { QueueId = "queue", ItemId = "item", CommandId = id, ExpectedRevision = 3 }),
            command => Assert.Equal("item", Assert.IsType<RemoveQueueItemCommand>(command).ItemId));

    [Fact]
    public async Task MoveAsync_Connected_SerializesSourceTargetAndPlacementIds()
        => await AssertQueueCommandAsync(
            (client, id) => client.MoveAsync(new MoveAgentInputQueueItemRequest
            {
                SourceQueueId = "source", ItemId = "item", TargetQueueId = "target",
                BeforeItemId = "before", CommandId = id, ExpectedRevision = 3,
            }),
            command =>
            {
                var value = Assert.IsType<MoveQueueItemCommand>(command);
                Assert.Equal(("source", "item", "target", "before"), (value.SourceQueueId, value.ItemId, value.TargetQueueId, value.BeforeItemId));
            });

    [Fact]
    public async Task ConfigureAsync_Connected_SerializesConfigurationAndRevision()
        => await AssertQueueCommandAsync(
            (client, id) => client.ConfigureAsync(new ConfigureAgentInputQueueRequest
            { QueueId = "queue", Configuration = Configuration(), CommandId = id, ExpectedRevision = 3 }),
            command =>
            {
                var value = Assert.IsType<ConfigureQueueCommand>(command);
                Assert.Equal("renamed", value.Configuration.Name);
                Assert.Equal(3, value.ExpectedRevision);
            });

    [Fact]
    public async Task InterruptAsync_Connected_SerializesInterruptAndAwaitsCorrelation()
        => await AssertNoResultCommandAsync(
            (client, id) => client.InterruptAsync(id),
            command => Assert.IsType<InterruptCommand>(command));

    [Fact]
    public async Task OpenSubagentAsync_Authorized_ReturnsChildDescriptor()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var id = Guid.NewGuid();
        var operation = client.OpenSubagentAsync(new OpenAgentSubagentRequest { AgentId = "child-agent", CommandId = id });
        var command = Assert.IsType<OpenSubagentCommand>(AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        var descriptor = new RemoteSubagentDescriptor
        {
            AgentSessionId = "child", AgentId = "child-agent", OwningProfileEntityId = "profile",
            OwnershipGeneration = 3, RuntimeEpoch = AgentSessionProtocolCodecTests.Epoch(),
        };
        await CompleteAsync(transport, 2, command, descriptor);
        Assert.Equal(descriptor, await operation);
    }

    [Fact]
    public async Task RespondToModalAsync_Connected_SerializesModalIdAndResponse()
        => await AssertNoResultCommandAsync(
            (client, id) => client.RespondToModalAsync(new RespondToAgentModalRequest
            {
                ModalId = "modal", Response = JsonDocument.Parse("""{"approved":true}""").RootElement.Clone(), CommandId = id,
            }),
            command =>
            {
                var value = Assert.IsType<ModalResponseCommand>(command);
                Assert.Equal("modal", value.ModalId);
                Assert.True(value.Response.GetProperty("approved").GetBoolean());
            });

    [Fact]
    public async Task SetToolEnabledAsync_Connected_AwaitsAuthoritativeToolsEvent()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = client.SetToolEnabledAsync(new SetAgentToolEnabledRequest
        { ToolId = "tool", Enabled = true, CommandId = Guid.NewGuid() });
        var command = Assert.IsType<SetToolEnabledCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        await CompleteAsync(transport, 2, command);
        await transport.ServerSendAsync(Frame(3, new ToolsChangedEvent
        {
            Tools = [Tool("other", true), Tool("tool", false)],
        }));
        Assert.False(operation.IsCompleted);
        await transport.ServerSendAsync(Frame(4, new ToolsChangedEvent { Tools = [Tool("tool", true)] }));
        await operation;
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_Connected_AwaitsPersistedAuthoritativeEvent()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = client.SetContinueInBackgroundAsync(new SetAgentSessionRetentionRequest
        { ContinueInBackground = true, CommandId = Guid.NewGuid() });
        var command = Assert.IsType<SetContinueInBackgroundCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        await CompleteAsync(transport, 2, command);
        await transport.ServerSendAsync(Frame(3, new SessionRetentionChangedEvent
        { ContinueInBackground = false, ViewerCount = 1 }));
        Assert.False(operation.IsCompleted);
        await transport.ServerSendAsync(Frame(4, new SessionRetentionChangedEvent
        { ContinueInBackground = true, ViewerCount = 1 }));
        await operation;
    }

    [Fact]
    public async Task SetContinueInBackgroundAsync_Rejected_LeavesProjectionUnchanged()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = client.SetContinueInBackgroundAsync(new SetAgentSessionRetentionRequest
        { ContinueInBackground = true, CommandId = Guid.NewGuid() });
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        await transport.ServerSendAsync(Frame(2, new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = "unauthorized", Operation = command.Type, IsRetryable = false,
                Message = "Denied.", CorrelationId = command.CorrelationId,
            },
        }, command.CorrelationId));
        Assert.Equal("unauthorized", (await Assert.ThrowsAsync<RemoteAgentSessionException>(() => operation)).Code);
    }

    [Fact]
    public async Task CommandMethod_EmptyCommandId_ThrowsArgumentException()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        await Assert.ThrowsAsync<ArgumentException>(() => client.InterruptAsync(Guid.Empty));
    }

    [Fact]
    public async Task CommandMethod_CancelledAfterWrite_DoesNotRetractCommand()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        using var cancellation = new CancellationTokenSource();
        var operation = client.InterruptAsync(Guid.NewGuid(), cancellation.Token);
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal("interrupt", command.Type);
        Assert.False(transport.ClientWrites.Reader.TryRead(out _));
        await CompleteAsync(transport, 2, command);
        var advanced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, frame) =>
        {
            if (frame.Sequence == 3) advanced.TrySetResult();
        };
        await transport.ServerSendAsync(Frame(3, new BusyChangedEvent { IsBusy = true }));
        await advanced.Task;
    }

    [Fact]
    public async Task CommandMethod_NotConnected_ThrowsInvalidOperationException()
    {
        await using var client = new RemoteAgentSessionClient(new TestTransport());
        await Assert.ThrowsAsync<InvalidOperationException>(() => client.InterruptAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task FrameReceived_ValidFrame_CursorAdvancesBeforeSubscriberRuns()
    {
        var transport = new TestTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var observed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, frame) => observed.TrySetResult(client.LastAppliedCursor?.Sequence == frame.Sequence);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.ServerSendAsync(Frame(1, new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await connecting;
        Assert.True(await observed.Task);
    }

    [Fact]
    public async Task DisposeAsync_ActivePump_ClosesChannelWithoutTerminateCommandAndReleasesViewer()
    {
        var transport = new TestTransport();
        var client = await ConnectAsync(transport);
        await client.DisposeAsync();
        Assert.True(transport.ChannelDisposed);
        Assert.False(transport.ClientWrites.Reader.TryRead(out _));
        Assert.False(transport.Disposed);
    }

    private static async Task AssertQueueCommandAsync(
        Func<RemoteAgentSessionClient, Guid, Task<AgentInputQueueCommandResult>> invoke,
        Action<AgentSessionCommand> assertCommand)
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var id = Guid.NewGuid();
        var operation = invoke(client, id);
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        assertCommand(command);
        await CompleteAsync(transport, 2, command, new AgentInputQueueCommandResult
        { CommandId = id, Status = AgentInputQueueCommandStatus.Applied, Revision = 1 });
        Assert.Equal(id, (await operation).CommandId);
    }

    private static async Task AssertNoResultCommandAsync(
        Func<RemoteAgentSessionClient, Guid, Task> invoke,
        Action<AgentSessionCommand> assertCommand)
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var id = Guid.NewGuid();
        var operation = invoke(client, id);
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        assertCommand(command);
        Assert.False(operation.IsCompleted);
        await CompleteAsync(transport, 2, command);
        await operation;
    }

    private static async Task AssertAuthoritativeCommandAsync(
        Func<RemoteAgentSessionClient, Guid, Task> invoke,
        Action<AgentSessionCommand> assertCommand,
        AgentSessionServerEvent authoritativeEvent)
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = invoke(client, Guid.NewGuid());
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        assertCommand(command);
        await CompleteAsync(transport, 2, command);
        Assert.False(operation.IsCompleted);
        await transport.ServerSendAsync(Frame(3, authoritativeEvent));
        await operation;
    }

    private static ValueTask CompleteAsync(TestTransport transport, long sequence, AgentSessionCommand command, object? result = null)
        => transport.ServerSendAsync(Frame(sequence, new CommandCompletedEvent
        {
            CommandId = command.CommandId,
            Result = result is null ? null : JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options),
        }, command.CorrelationId));

    private static AgentInputQueueConfiguration Configuration() => new()
    {
        Name = "renamed", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 2,
    };

    private static JsonElement Tool(string id, bool enabled)
        => JsonSerializer.SerializeToElement(
            new AgentChatToolItem(id, id, id, "", "function", enabled, []),
            AIJsonUtilities.DefaultOptions);

    private static void AssertRequired<T>(params string[] names)
    {
        foreach (var name in names)
            Assert.NotNull(typeof(T).GetProperty(name)!.GetCustomAttribute<System.Runtime.CompilerServices.RequiredMemberAttribute>());
    }

    private static async Task<RemoteAgentSessionClient> ConnectAsync(
        ReconnectTransport transport,
        TimeProvider? timeProvider = null)
    {
        var client = timeProvider is null
            ? new RemoteAgentSessionClient(transport)
            : new RemoteAgentSessionClient(transport, timeProvider);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.SendAsync(0, Frame(1, new SessionSnapshotEvent
        { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await connecting;
        return client;
    }

    private sealed class ReconnectTransport : ITransport
    {
        private readonly List<ReconnectChannel> channels = [];
        public List<JsonElement> Opens { get; } = [];

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var channel = new ReconnectChannel();
            this.channels.Add(channel);
            this.Opens.Add(request.Clone());
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        public ValueTask SendAsync(int index, JsonElement value) => this.channels[index].Incoming.Writer.WriteAsync(value);
        public void Complete(int index) => this.channels[index].Incoming.Writer.TryComplete();
    }

    private sealed class ReconnectChannel : IMessageChannel
    {
        public Channel<JsonElement> Outgoing { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> Incoming { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.Outgoing.Writer;
        public ChannelReader<JsonElement> Reader => this.Incoming.Reader;
        public ValueTask DisposeAsync()
        {
            this.Outgoing.Writer.TryComplete();
            this.Incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
