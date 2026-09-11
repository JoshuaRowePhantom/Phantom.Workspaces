using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentSessionClientTests
{
    [Fact]
    public void Constructor_NullTransport_ThrowsArgumentNullException()
        => Assert.Throws<ArgumentNullException>(() => new RemoteAgentSessionClient(null!));

    [Fact]
    public void LastAppliedCursor_NoFrames_IsNull()
        => Assert.Null(new RemoteAgentSessionClient(new TestTransport()).LastAppliedCursor);

    [Fact]
    public async Task Constructor_ProcessScopedTransport_DoesNotDisposeBorrowedTransport()
    {
        var transport = new TestTransport();
        await new RemoteAgentSessionClient(transport).DisposeAsync();
        Assert.False(transport.Disposed);
    }

    [Fact]
    public void ClientRequestTypes_RequiredInitProperties_AreMarkedRequired()
    {
        AssertRequired<AgentSessionStatusRequest>(nameof(AgentSessionStatusRequest.Transport), nameof(AgentSessionStatusRequest.OpenRequest));
        AssertRequired<TerminateAgentSessionRequest>(nameof(TerminateAgentSessionRequest.Reason), nameof(TerminateAgentSessionRequest.CommandId));
        AssertRequired<OpenAgentSubagentRequest>(nameof(OpenAgentSubagentRequest.AgentId), nameof(OpenAgentSubagentRequest.CommandId));
        AssertRequired<RespondToAgentModalRequest>(nameof(RespondToAgentModalRequest.ModalId), nameof(RespondToAgentModalRequest.Response), nameof(RespondToAgentModalRequest.CommandId));
        AssertRequired<SetAgentToolEnabledRequest>(nameof(SetAgentToolEnabledRequest.ToolId), nameof(SetAgentToolEnabledRequest.Enabled), nameof(SetAgentToolEnabledRequest.CommandId));
        AssertRequired<SetAgentSessionRetentionRequest>(nameof(SetAgentSessionRetentionRequest.ContinueInBackground), nameof(SetAgentSessionRetentionRequest.CommandId));
    }

    [Fact]
    public void ClientRequestTypes_NamedInitializers_PreserveStatusAndCommandPayloads()
    {
        var id = Guid.NewGuid();
        var status = new AgentSessionStatusRequest { Transport = new TestTransport(), OpenRequest = AgentSessionProtocolCodecTests.Open() };
        var terminate = new TerminateAgentSessionRequest { Reason = "done", CommandId = id };
        var subagent = new OpenAgentSubagentRequest { AgentId = "agent", CommandId = id };
        var response = JsonDocument.Parse("""{"answer":"yes"}""").RootElement.Clone();
        var modal = new RespondToAgentModalRequest { ModalId = "modal", Response = response, CommandId = id };
        var tool = new SetAgentToolEnabledRequest { ToolId = "tool", Enabled = true, CommandId = id };
        var retention = new SetAgentSessionRetentionRequest { ContinueInBackground = true, CommandId = id };
        Assert.Equal("session", status.OpenRequest.AgentSessionId);
        Assert.Equal(("done", id), (terminate.Reason, terminate.CommandId));
        Assert.Equal(("agent", id), (subagent.AgentId, subagent.CommandId));
        Assert.Equal(("modal", "yes", id), (modal.ModalId, modal.Response.GetProperty("answer").GetString(), modal.CommandId));
        Assert.Equal(("tool", true, id), (tool.ToolId, tool.Enabled, tool.CommandId));
        Assert.Equal((true, id), (retention.ContinueInBackground, retention.CommandId));
    }

    [Theory]
    [InlineData(AgentSessionRemoteStatus.Running)]
    [InlineData(AgentSessionRemoteStatus.NotRunning)]
    [InlineData(AgentSessionRemoteStatus.Unavailable)]
    public async Task GetStatusAsync_AuthorizedRunningOrStopped_ReturnsAuthoritativeStatusOnly(
        AgentSessionRemoteStatus status)
    {
        var transport = new TestTransport();
        await transport.ServerSendAsync(Frame(1, new SessionStatusEvent { Status = status }));
        var result = await RemoteAgentSessionClient.GetStatusAsync(new AgentSessionStatusRequest
        {
            Transport = transport,
            OpenRequest = AgentSessionProtocolCodecTests.Open() with
            {
                OpenIntent = AgentSessionOpenIntent.Status,
            },
        });
        Assert.Equal(status, result);
        Assert.False(transport.Disposed);
    }

    [Fact]
    public async Task ConnectAsync_SnapshotThenDelta_UpdatesCursorAndRaisesFramesInOrder()
    {
        var transport = new TestTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var observed = new List<long>();
        var allObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, frame) =>
        {
            Assert.Equal(frame.Sequence, client.LastAppliedCursor!.Value.Sequence);
            observed.Add(frame.Sequence);
            if (observed.Count == 2) allObserved.TrySetResult();
        };
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.ServerSendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await connecting;
        await transport.ServerSendAsync(Frame(2, new BusyChangedEvent { IsBusy = true }));
        await allObserved.Task;
        Assert.Equal([1L, 2L], observed);
        Assert.Equal(2, client.LastAppliedCursor!.Value.Sequence);
    }

    [Fact]
    public async Task ConnectAsync_SecondCall_ThrowsInvalidOperationException()
    {
        var transport = new TestTransport();
        await using var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.ServerSendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await connecting;
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ConnectAsync(AgentSessionProtocolCodecTests.Open()));
    }

    [Fact]
    public async Task ConnectAsync_SequenceGap_ClosesWithProtocolException()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        await transport.ServerSendAsync(Frame(3, new BusyChangedEvent { IsBusy = true }));
        await disconnected.Task;
        Assert.Equal(1, client.LastAppliedCursor!.Value.Sequence);
    }

    [Fact]
    public async Task CreateQueueAsync_Connected_SerializesCommandAndAwaitsResult()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var commandId = Guid.NewGuid();
        var operation = client.CreateQueueAsync(new CreateAgentInputQueueRequest
        {
            Configuration = new AgentInputQueueConfiguration
            {
                Name = "later", Immediacy = AgentInputQueueImmediacy.Queue, Priority = 4,
            },
            CommandId = commandId, ExpectedRevision = 1,
        });
        var commandJson = await transport.ServerReadAsync();
        var command = Assert.IsType<CreateQueueCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(commandJson));
        Assert.Equal(commandId, command.CommandId);
        var result = new AgentInputQueueCommandResult
        {
            CommandId = commandId, Status = AgentInputQueueCommandStatus.Applied,
            QueueId = "later-id", Revision = 2,
        };
        await transport.ServerSendAsync(Frame(2, new CommandCompletedEvent
        {
            CommandId = commandId,
            Result = JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options),
        }, command.CorrelationId));
        Assert.Equal("later-id", (await operation).QueueId);
    }

    [Fact]
    public async Task RemoteAgentSessionException_WireError_ExposesOnlySafeFields()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = client.InterruptAsync(Guid.NewGuid());
        var command = AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync());
        var correlation = command.CorrelationId;
        await transport.ServerSendAsync(Frame(2, new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = "runtime-changed", Operation = "interrupt", IsRetryable = true,
                Message = "The runtime changed.", CorrelationId = correlation,
            },
        }, correlation));
        var error = await Assert.ThrowsAsync<RemoteAgentSessionException>(() => operation);
        Assert.Equal("runtime-changed", error.Code);
        Assert.Equal("interrupt", error.Operation);
        Assert.True(error.IsRetryable);
        Assert.Equal(correlation, error.CorrelationId);
        Assert.Equal("The runtime changed.", error.Message);
    }

    [Fact]
    public async Task TerminateAsync_Connected_SerializesReasonAndAwaitsTerminal()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var commandId = Guid.NewGuid();
        var operation = client.TerminateAsync(new TerminateAgentSessionRequest
        {
            Reason = "user-requested", CommandId = commandId,
        });
        var command = Assert.IsType<TerminateSessionCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        await transport.ServerSendAsync(Frame(2, new CommandCompletedEvent
        {
            CommandId = commandId,
        }, command.CorrelationId));
        Assert.False(operation.IsCompleted);
        await transport.ServerSendAsync(Frame(3, new SessionTerminalEvent
        {
            Reason = "user-requested",
            CompletionState = JsonDocument.Parse("""{"state":"completed"}""").RootElement.Clone(),
        }, Guid.NewGuid()));
        await operation;
    }

    [Fact]
    public async Task TerminalFrame_PendingNonTerminateCommand_FailsWithoutHanging()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var operation = client.SetContinueInBackgroundAsync(new SetAgentSessionRetentionRequest
        {
            ContinueInBackground = true,
            CommandId = Guid.NewGuid(),
        });
        _ = Assert.IsType<SetContinueInBackgroundCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));

        await transport.ServerSendAsync(Frame(2, new SessionTerminalEvent
        {
            Reason = "runtime-stopped",
            CompletionState = JsonDocument.Parse("""{"state":"completed"}""").RootElement.Clone(),
        }, Guid.NewGuid()));

        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => operation);
    }

    [Fact]
    public async Task DetachAsync_RepeatedCall_IsIdempotent()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        await client.DetachAsync();
        await client.DetachAsync();
        Assert.IsType<DetachCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        Assert.False(transport.ClientWrites.Reader.TryRead(out _));
    }

    private static async Task<RemoteAgentSessionClient> ConnectAsync(TestTransport transport)
    {
        var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open());
        await transport.ServerSendAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot(),
        }));
        await connecting;
        return client;
    }

    private static JsonElement Frame(long sequence, AgentSessionServerEvent value, Guid? correlation = null)
        => AgentSessionProtocolCodec.SerializeFrame(AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            AgentSessionProtocolCodecTests.Epoch(), sequence, correlation ?? Guid.NewGuid(), value));

    private sealed class TestTransport : ITransport
    {
        private readonly TestMessageChannel channel = new();
        public Channel<JsonElement> ClientWrites => this.channel.ClientWrites;
        public JsonElement LastOpen { get; private set; }
        public bool ChannelDisposed => this.channel.Disposed;
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            Assert.Equal("attach-agent-session", request.GetProperty("type").GetString());
            this.LastOpen = request.Clone();
            return Task.FromResult<IMessageChannel>(this.channel);
        }
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public bool Disposed { get; private set; }
        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
        public ValueTask ServerSendAsync(JsonElement value) => this.channel.ServerWrites.Writer.WriteAsync(value);
        public ValueTask<JsonElement> ServerReadAsync() => this.channel.ClientWrites.Reader.ReadAsync();
        public void CompleteServer() => this.channel.ServerWrites.Writer.TryComplete();
    }

    private sealed class TestMessageChannel : IMessageChannel
    {
        public bool Disposed { get; private set; }
        public Channel<JsonElement> ClientWrites { get; } = Channel.CreateUnbounded<JsonElement>();
        public Channel<JsonElement> ServerWrites { get; } = Channel.CreateUnbounded<JsonElement>();
        public ChannelWriter<JsonElement> Writer => this.ClientWrites.Writer;
        public ChannelReader<JsonElement> Reader => this.ServerWrites.Reader;
        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            this.ClientWrites.Writer.TryComplete();
            this.ServerWrites.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
