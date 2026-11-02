using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentSessionClientTests
{
    [Fact]
    public async Task RestartProtocol_ViewerReadyFailure_IsNotAcknowledgedAsSuccess()
    {
        var transport = new TestTransport();
        await using var client = await ConnectAsync(transport);
        var acknowledgement = client.AcknowledgeRestartProjectionAsync(CancellationToken.None);
        var command = Assert.IsType<RestartViewerReadyCommand>(
            AgentSessionProtocolCodec.DeserializeCommand(await transport.ServerReadAsync()));
        await transport.ServerSendAsync(Frame(2, new OperationErrorEvent
        {
            Error = new RemoteAgentOperationError
            {
                Code = "runtime-changed",
                Operation = command.Type,
                IsRetryable = true,
                Message = "The replacement changed before viewer readiness.",
                CorrelationId = command.CorrelationId,
            },
        }, command.CorrelationId));

        var error = await Assert.ThrowsAsync<RemoteAgentSessionException>(() => acknowledgement);

        Assert.Equal("runtime-changed", error.Code);
        Assert.Equal("restart-viewer-ready", error.Operation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartProtocol_MismatchedCommandId_IsRejected(bool status)
    {
        var request = new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = new AgentSessionRuntimeIdentity
            {
                AgentSessionId = "session",
                OwningProfileEntityId = "owner",
                OwnershipGeneration = 3,
                RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            },
        };
        var transport = new RestartReplyTransport(JsonSerializer.SerializeToElement(
            new AgentSessionOwnerRestartResult
            {
                CommandId = Guid.NewGuid(),
                State = AgentSessionOwnerRestartState.Succeeded,
                ReplacementRuntime = request.ExpectedRuntime,
            }, AgentSessionProtocolCodec.Options));
        await using var client = new RemoteAgentSessionClient(transport);
        var error = status
            ? await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => client.GetRestartStatusAsync(request, CancellationToken.None))
            : await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => client.RestartAsync(request, CancellationToken.None));
        Assert.Contains("mismatched command id", error.Message);
        Assert.Equal(status ? "agent-session-restart-status" : "restart-agent-session",
            transport.Open.GetProperty("type").GetString());
    }

    [Fact]
    public async Task RestartProtocol_NullStatus_IsNotSuccess()
    {
        await using var client = new RemoteAgentSessionClient(new RestartReplyTransport(JsonSerializer.SerializeToElement<object?>(null)));
        Assert.Null(await client.GetRestartStatusAsync(new AgentSessionOwnerRestartRequest
        {
            CommandId = Guid.NewGuid(),
            ExpectedRuntime = new AgentSessionRuntimeIdentity
            {
                AgentSessionId = "session",
                OwningProfileEntityId = "owner",
                OwnershipGeneration = 3,
                RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            },
        }, CancellationToken.None));
    }

    [Fact]
    public async Task RestartProtocol_HandoffThenWrongReplacementSnapshot_RejectsEpochWithoutApplyingIt()
    {
        var transport = new ReconnectTransport();
        await using var client = await ConnectAsync(transport);
        var previous = client.LastAppliedCursor;
        var published = new RuntimeEpoch { Value = Guid.NewGuid() };
        var handoff = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, frame) =>
        {
            if (frame.Type == "session-restarting")
                handoff.TrySetResult();
        };
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        await transport.SendAsync(0, Frame(2, new SessionRestartingEvent { ReplacementRuntimeEpoch = published }));
        await handoff.Task;
        transport.Complete(0);
        await disconnected.Task;
        var reconnect = client.ReconnectAsync();
        var wrong = AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.CreateFrame(
            new RuntimeEpoch { Value = Guid.NewGuid() }, 1, Guid.NewGuid(),
            new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() });
        await transport.SendAsync(1, AgentSessionProtocolCodec.SerializeFrame(wrong));

        var error = await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => reconnect);

        Assert.Contains("published replacement epoch", error.Message);
        Assert.Equal(previous!.Value.Epoch, client.LastAppliedCursor!.Value.Epoch);
    }

    private sealed class RestartReplyTransport(JsonElement reply) : ITransport
    {
        internal JsonElement Open { get; private set; }
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            this.Open = request.Clone();
            var channel = new TestMessageChannel();
            channel.ServerWrites.Writer.TryWrite(reply);
            return Task.FromResult<IMessageChannel>(channel);
        }
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
