using System.Text.Json;
using System.Threading.Channels;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentSessionClientTests
{
    [Fact]
    public async Task RestartObservation_NoOperation_DoesNotInventRetryAuthority()
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var observing = client.ObserveRestartAsync(_ => throw new InvalidOperationException(), CancellationToken.None);
        await transport.Observation.ServerWrites.Writer.WriteAsync(JsonSerializer.SerializeToElement<object?>(null));
        Assert.Null(await observing);
        Assert.Null(client.RestartCommandState.Pending);
        Assert.True(transport.Observation.Disposed);
    }

    [Theory]
    [InlineData(AgentSessionOwnerRestartState.Stopped)]
    [InlineData(AgentSessionOwnerRestartState.Committed)]
    public async Task RestartObservation_DurableOperation_WaitsForSuccessWithoutPolling(AgentSessionOwnerRestartState initialState)
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var states = new List<AgentSessionOwnerRestartState>();
        var observing = client.ObserveRestartAsync(result =>
        {
            states.Add(result.State);
            applied.TrySetResult();
            return Task.CompletedTask;
        }, CancellationToken.None);
        var stopped = ObservationResult(client, initialState);
        await transport.SendAsync(stopped);
        await applied.Task;
        Assert.False(observing.IsCompleted);
        Assert.Equal(stopped.CommandId, client.RestartCommandState.Pending!.CommandId);
        Assert.Equal(stopped, client.RestartCommandState.Result);
        var success = stopped with { State = AgentSessionOwnerRestartState.Succeeded, Message = null };
        await transport.SendAsync(success);
        Assert.Equal(AgentSessionOwnerRestartState.Succeeded, await observing);
        Assert.Equal([initialState, AgentSessionOwnerRestartState.Succeeded], states);
        Assert.True(client.HasRestartHandoff);
        Assert.True(transport.Observation.Disposed);
    }

    [Fact]
    public async Task RestartObservation_Blocked_IsNotRetryableAndClosesObservation()
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var observing = client.ObserveRestartAsync(_ => Task.CompletedTask, CancellationToken.None);
        await transport.SendAsync(ObservationResult(client, AgentSessionOwnerRestartState.Blocked));
        Assert.Equal(AgentSessionOwnerRestartState.Blocked, await observing);
        Assert.Equal(AgentSessionOwnerRestartState.Blocked, client.RestartCommandState.Result!.State);
        Assert.False(client.HasRestartHandoff);
        Assert.True(transport.Observation.Disposed);
    }

    [Theory]
    [InlineData("command")]
    [InlineData("session")]
    [InlineData("owner")]
    [InlineData("generation")]
    [InlineData("missing-runtime")]
    [InlineData("empty-epoch")]
    [InlineData("unchanged-epoch")]
    [InlineData("state")]
    public async Task RestartObservation_MismatchedAuthority_IsRejected(string mismatch)
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var result = ObservationResult(client, AgentSessionOwnerRestartState.Stopped);
        result = mismatch switch
        {
            "command" => result with { CommandId = Guid.Empty },
            "session" => result with { ReplacementRuntime = result.ReplacementRuntime! with { AgentSessionId = "other" } },
            "owner" => result with { ReplacementRuntime = result.ReplacementRuntime! with { OwningProfileEntityId = "other" } },
            "generation" => result with { ReplacementRuntime = result.ReplacementRuntime! with { OwnershipGeneration = 999 } },
            "empty-epoch" => result with { ReplacementRuntime = result.ReplacementRuntime! with { RuntimeEpoch = default } },
            "unchanged-epoch" => result with { ReplacementRuntime = result.ReplacementRuntime! with { RuntimeEpoch = AgentSessionProtocolCodecTests.Epoch() } },
            "state" => result with { State = AgentSessionOwnerRestartState.TargetChanged },
            _ => result with { ReplacementRuntime = null },
        };
        var observing = client.ObserveRestartAsync(_ => throw new InvalidOperationException(), CancellationToken.None);
        await transport.SendAsync(result);
        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => observing);
        Assert.Null(client.RestartCommandState.Pending);
        Assert.True(transport.Observation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartObservation_ChannelClosedOrCancelled_ReleasesResources(bool cancel)
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        using var cancellation = new CancellationTokenSource();
        var observing = client.ObserveRestartAsync(_ => Task.CompletedTask, cancellation.Token);
        if (cancel)
        {
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observing);
        }
        else
        {
            transport.Observation.ServerWrites.Writer.TryComplete();
            await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => observing);
        }
        Assert.True(transport.Observation.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartObservation_ProtocolErrorFrame_IsNotDeserializedAsAResult(bool terminal)
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var observing = client.ObserveRestartAsync(_ => Task.CompletedTask, CancellationToken.None);
        await transport.Observation.ServerWrites.Writer.WriteAsync(Frame(1, terminal
            ? new SessionTerminalEvent
            {
                Reason = "not-found",
                CompletionState = JsonSerializer.SerializeToElement(new
                {
                    code = "not-found", operation = "attach", retryable = false, message = "Unavailable.",
                }),
            }
            : new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        if (terminal)
            await Assert.ThrowsAsync<RemoteAgentSessionException>(() => observing);
        else
            await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => observing);
        Assert.True(transport.Observation.Disposed);
    }

    [Fact]
    public async Task RestartObservation_MalformedResult_IsRejected()
    {
        var (client, transport) = await ConnectObservationClientAsync();
        await using var lifetime = client;
        var observing = client.ObserveRestartAsync(_ => Task.CompletedTask, CancellationToken.None);
        await transport.Observation.ServerWrites.Writer.WriteAsync(JsonSerializer.SerializeToElement("invalid"));
        await Assert.ThrowsAsync<RemoteAgentProtocolException>(() => observing);
        Assert.Null(client.RestartCommandState.Pending);
        Assert.True(transport.Observation.Disposed);
    }

    [Fact]
    public async Task RestartObservation_BeforeOpen_IsRejected()
    {
        await using var client = new RemoteAgentSessionClient(new ObservationTransport());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ObserveRestartAsync(_ => Task.CompletedTask, CancellationToken.None));
    }

    private static AgentSessionOwnerRestartResult ObservationResult(RemoteAgentSessionClient client, AgentSessionOwnerRestartState state)
    {
        var open = AgentSessionProtocolCodecTests.Open();
        return new AgentSessionOwnerRestartResult
        {
            CommandId = Guid.NewGuid(), State = state, Message = "Retry this operation.",
            ReplacementRuntime = new AgentSessionRuntimeIdentity
            {
                AgentSessionId = open.AgentSessionId,
                OwningProfileEntityId = open.ExpectedOwningProfileEntityId,
                OwnershipGeneration = open.ExpectedOwnershipGeneration,
                RuntimeEpoch = new RuntimeEpoch { Value = Guid.NewGuid() },
            },
        };
    }

    private static async Task<(RemoteAgentSessionClient, ObservationTransport)> ConnectObservationClientAsync()
    {
        var transport = new ObservationTransport();
        var client = new RemoteAgentSessionClient(transport);
        var connecting = client.ConnectAsync(AgentSessionProtocolCodecTests.Open(), CancellationToken.None);
        await transport.Attachment.ServerWrites.Writer.WriteAsync(Frame(1,
            new SessionSnapshotEvent { Snapshot = AgentSessionProtocolCodecTests.Snapshot() }));
        await connecting;
        return (client, transport);
    }

    private sealed class ObservationTransport : ITransport
    {
        internal TestMessageChannel Attachment { get; } = new();
        internal TestMessageChannel Observation { get; } = new();
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            if (request.GetProperty("type").GetString() == "attach-agent-session")
                return Task.FromResult<IMessageChannel>(this.Attachment);
            Assert.Equal("observe-agent-session-restart", request.GetProperty("type").GetString());
            Assert.Equal(AgentSessionProtocolCodecTests.Epoch(),
                request.GetProperty("expected-runtime").Deserialize<AgentSessionRuntimeIdentity>(AgentSessionProtocolCodec.Options)!.RuntimeEpoch);
            return Task.FromResult<IMessageChannel>(this.Observation);
        }
        internal ValueTask SendAsync(AgentSessionOwnerRestartResult result)
            => this.Observation.ServerWrites.Writer.WriteAsync(JsonSerializer.SerializeToElement(result, AgentSessionProtocolCodec.Options));
        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
