using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Tests;

public sealed partial class RemoteAgentChatTests
{
    [Theory]
    [InlineData("none", true)]
    [InlineData("io", true)]
    [InlineData("unsupported", true)]
    [InlineData("malformed", true)]
    [InlineData("not-found", true)]
    [InlineData("unauthorized", false)]
    [InlineData("runtime-changed", false)]
    public async Task ReconnectAfterLoss_ObservationUnavailable_PreservesOrdinaryReconnectWithoutBypassingAuthority(
        string observation, bool reconnect)
    {
        var time = new ReconnectObservationTimeProvider();
        var transport = new ReconnectObservationTransport(observation);
        var client = new RemoteAgentSessionClient(transport, time);
        await transport.Initial.Incoming.Writer.WriteAsync(Frame(1, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot() with { CanRestartIndependently = true },
        }));
        await using var chat = await RemoteAgentChat.AttachAsync(new RemoteAgentChatAttachOptions
        {
            Client = client, OpenRequest = AgentSessionProtocolCodecTests.Open(),
            ForegroundScheduler = TaskScheduler.Default,
        });
        var disconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reconnected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        client.UnexpectedlyDisconnected += (_, _) => disconnected.TrySetResult();
        chat.RuntimeStateChanged += (_, _) =>
        {
            if (chat.IsConnected && transport.Attachments == 2)
                reconnected.TrySetResult();
        };
        await transport.Replacement.Incoming.Writer.WriteAsync(Frame(2, new SessionSnapshotEvent
        {
            Snapshot = AgentSessionProtocolCodecTests.Snapshot() with { CanRestartIndependently = true },
        }));
        transport.Initial.Incoming.Writer.TryComplete();
        await disconnected.Task;
        if (reconnect)
        {
            await time.ReconnectScheduled.Task;
            time.Advance(TimeSpan.FromMilliseconds(250));
            await reconnected.Task;
            Assert.True(chat.IsConnected);
            Assert.False(chat.IsTerminal);
            Assert.NotNull(client.RestartIdentity);
        }
        else
        {
            // Disconnection is raised before observation/rejection finishes. Await that
            // actual operation, not just the earlier transport-loss notification.
            var reconnecting = (Task)typeof(RemoteAgentChat).GetField("reconnectAfterLoss",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(chat)!;
            await reconnecting;
            Assert.False(time.ReconnectScheduled.Task.IsCompleted);
            Assert.Equal(1, transport.Attachments);
        }
        Assert.Equal(1, transport.Observations);
        Assert.Null(client.RestartCommandState.Pending);
    }

    private sealed class ReconnectObservationTimeProvider : FakeTimeProvider
    {
        internal TaskCompletionSource ReconnectScheduled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = base.CreateTimer(callback, state, dueTime, period);
            if (dueTime == TimeSpan.FromMilliseconds(250))
                this.ReconnectScheduled.TrySetResult();
            return timer;
        }
    }

    private sealed class ReconnectObservationTransport(string observation) : ITransport
    {
        internal TestMessageChannel Initial { get; } = new();
        internal TestMessageChannel Replacement { get; } = new();
        internal int Attachments { get; private set; }
        internal int Observations { get; private set; }

        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
        {
            if (request.GetProperty("type").GetString() == "attach-agent-session")
                return Task.FromResult<IMessageChannel>(++this.Attachments == 1 ? this.Initial : this.Replacement);
            Assert.Equal("observe-agent-session-restart", request.GetProperty("type").GetString());
            this.Observations++;
            if (observation == "io")
                throw new IOException("Observation connection lost.");
            if (observation == "unsupported")
                throw new NotSupportedException("Old peer has no observation endpoint.");
            var channel = new TestMessageChannel();
            channel.Incoming.Writer.TryWrite(observation switch
            {
                "none" => JsonSerializer.SerializeToElement<object?>(null),
                "malformed" => JsonSerializer.SerializeToElement("invalid observation"),
                _ => Frame(1, new SessionTerminalEvent
                {
                    Reason = observation,
                    CompletionState = JsonSerializer.SerializeToElement(new
                    {
                        code = observation, operation = "observe", retryable = false, message = "Unavailable.",
                    }),
                }),
            });
            return Task.FromResult<IMessageChannel>(channel);
        }

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
