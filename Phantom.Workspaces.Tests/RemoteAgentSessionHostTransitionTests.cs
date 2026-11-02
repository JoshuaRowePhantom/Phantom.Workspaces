using System.Text.Json;
using Moq;
using Phantom.Workspaces.Llm.Remote;
using Phantom.Workspaces.Services.AgentSessions;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

public sealed partial class RemoteAgentSessionHostTests
{
    [Fact]
    public async Task ModalResponse_PendingCompletion_AdmitsOtherViewerAndDeduplicatesRetry()
    {
        await using var fixture = new HostFixture();
        await using var first = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach), TestContext.Current.CancellationToken);
        await using var secondChannel = new DuplexChannel();
        await using var second = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach, secondChannel, "second"),
            TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var attempts = 0;
        fixture.Chat.Setup(value => value.RespondToModalAsync(
                "modal", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                if (Interlocked.Increment(ref attempts) != 1)
                    throw new InvalidOperationException("The response was already accepted.");
                entered.TrySetResult();
                return release.Task;
            });
        var command = new ModalResponseCommand
        {
            CommandId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            RuntimeEpoch = fixture.Runtime.Epoch,
            ModalId = "modal",
            Response = JsonSerializer.SerializeToElement(new { approved = true }),
        };
        var pending = fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, first, command,
            TestContext.Current.CancellationToken).AsTask();
        try
        {
            await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
            var duplicate = fixture.Host.DispatchCommandAsync(
                Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, second,
                command with { CorrelationId = Guid.NewGuid() },
                TestContext.Current.CancellationToken).AsTask();
            await fixture.Host.DispatchCommandAsync(
                Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, second,
                command with { CommandId = Guid.NewGuid(), CorrelationId = Guid.NewGuid() },
                TestContext.Current.CancellationToken);
            Assert.Equal(2, attempts);
            Assert.False(pending.IsCompleted);
            Assert.False(duplicate.IsCompleted);
            release.TrySetResult();
            await Task.WhenAll(pending, duplicate);
            fixture.Chat.Verify(value => value.RespondToModalAsync(
                "modal", It.IsAny<JsonElement>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
        }
        finally
        {
            release.TrySetResult();
            await pending;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Command_TakeoverFencesRuntime_RejectsBeforeCleanupIncludingQueuedAdmission(bool queueBeforeFence)
    {
        await using var fixture = new HostFixture();
        await using var attachment = await fixture.Host.OpenAsync(
            fixture.Request(AgentSessionOpenIntent.Attach), TestContext.Current.CancellationToken);
        await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken);
        var authorizing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var authorize = new TaskCompletionSource<AgentSessionAuthorizationDecision>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Authorizer.Setup(value => value.AuthorizeAsync(
                It.IsAny<TransportPeerIdentity>(),
                It.Is<AgentSessionAuthorizationRequest>(request =>
                    request.Operation == AgentSessionAuthorizationOperation.Takeover),
                It.IsAny<CancellationToken>()))
            .Returns(() =>
            {
                authorizing.TrySetResult();
                return new ValueTask<AgentSessionAuthorizationDecision>(authorize.Task);
            });
        var disposing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.Chat.Setup(value => value.DisposeAsync()).Returns(() =>
        {
            disposing.TrySetResult();
            return new ValueTask(release.Task);
        });
        // A failed ownership CAS keeps the fixture's original generation; no replacement can start.
        fixture.Factory.Setup(value => value.TryTakeOverAsync(
                It.IsAny<AgentSessionTakeoverRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);
        var takeover = fixture.Host.TakeOverAsync(Peer(), Takeover(), TestContext.Current.CancellationToken);
        Task? command = null;
        try
        {
            await authorizing.Task.WaitAsync(TestContext.Current.CancellationToken);
            if (queueBeforeFence)
            {
                command = Dispatch();
                Assert.False(command.IsCompleted);
            }
            authorize.TrySetResult(new AgentSessionAuthorizationDecision { IsAllowed = true });
            await disposing.Task.WaitAsync(TestContext.Current.CancellationToken);
            command ??= Dispatch();
            await command.WaitAsync(TestContext.Current.CancellationToken);
            Assert.True(fixture.Runtime.IsFenced);
            Assert.False(takeover.IsCompleted);
            fixture.Queues.Verify(value => value.CreateQueueAsync(
                It.IsAny<Phantom.Workspaces.Llm.CreateAgentInputQueueRequest>(),
                It.IsAny<CancellationToken>()), Times.Never);
            var error = Assert.IsType<OperationErrorEvent>(
                AgentSessionProtocolCodec.AgentSessionProtocolEventCodec.Deserialize(
                    AgentSessionProtocolCodec.DeserializeFrame(
                        await fixture.Channel.Output.ReadAsync(TestContext.Current.CancellationToken))));
            Assert.Equal("unauthorized", error.Error.Code);
        }
        finally
        {
            authorize.TrySetResult(new AgentSessionAuthorizationDecision { IsAllowed = true });
            release.TrySetResult();
            await Assert.ThrowsAsync<AgentSessionTakeoverBlockedException>(() => takeover);
            if (command is not null)
                await command;
        }

        Task Dispatch() => fixture.Host.DispatchCommandAsync(
            Peer(), Open(AgentSessionOpenIntent.Attach), fixture.Runtime, attachment,
            Command(fixture.Runtime.Epoch), TestContext.Current.CancellationToken).AsTask();
    }
}
