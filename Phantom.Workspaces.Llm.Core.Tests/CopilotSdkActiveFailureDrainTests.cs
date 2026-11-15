using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed partial class CopilotSdkChatClientTests
{
    [Theory]
    [InlineData("observer")]
    [InlineData("retirement")]
    public Task FaultSealedSingleBatch_CancellationCannotRetireBeforeOuterAcknowledgement(string entry)
    {
        var store = new CopilotSdkRootContinuationTests.SerializedStore();
        return CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(() => store, entry,
            scenario: new(NoIdle: true, Exit: "interrupt-before-drain", SingleBatch: true));
    }

    [Theory]
    [InlineData("draft completed", true, "interrupt", false)]
    [InlineData("replacement", false, "shutdown", false)]
    [InlineData("replacement", true, "shutdown", true)]
    public Task DispatchTailNoIdle_CorrectionAndAcknowledgementSurviveCancellationOrShutdown(
        string final, bool anonymous, string exit, bool flushFailure)
    {
        var store = new CopilotSdkRootContinuationTests.SerializedStore();
        return CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(() => store, "dispatch", true,
            new(final, anonymous, NoIdle: true, FlushFailure: flushFailure, Exit: exit));
    }

    [Theory]
    [InlineData("draft", false, false)]
    [InlineData("draft completed", false, false)]
    [InlineData("replacement", false, false)]
    [InlineData("draft", true, false)]
    [InlineData("draft completed", true, false)]
    [InlineData("replacement", true, false)]
    [InlineData("draft", false, true)]
    [InlineData("draft completed", false, true)]
    [InlineData("replacement", false, true)]
    [InlineData("draft", true, true)]
    [InlineData("draft completed", true, true)]
    [InlineData("replacement", true, true)]
    public Task DispatchTailRecovery_PreservesOriginalMessageCoverageAndIdentity(string final, bool anonymous, bool noIdle)
    {
        var store = new CopilotSdkRootContinuationTests.SerializedStore();
        return CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(() => store, "dispatch", noIdle,
            new(final, anonymous, noIdle));
    }

    [Theory]
    [InlineData("observer", false, false, "none")]
    [InlineData("retirement", false, false, "none")]
    [InlineData("dispatch", false, false, "none")]
    [InlineData("poller", false, false, "none")]
    [InlineData("secondary-dispatch", false, false, "none")]
    [InlineData("observer", true, true, "none")]
    [InlineData("dispatch", true, true, "none")]
    [InlineData("retirement", false, true, "none")]
    [InlineData("observer", false, false, "interrupt")]
    [InlineData("retirement", false, false, "shutdown")]
    [InlineData("dispatch", false, true, "shutdown")]
    public Task FaultSealedNoIdle_AwaitsAcceptedFlushAndProjectionBeforeRetirement(
        string entry, bool flushFailure, bool disposalFailure, string exit)
    {
        var store = new CopilotSdkRootContinuationTests.SerializedStore();
        return CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(() => store, entry, disposalFailure,
            new(NoIdle: true, FlushFailure: flushFailure, Exit: exit));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public async Task UnpreparedSendFailure_WithAcceptedReceipts_DrainsWithoutNativeReplay(bool brokenPipe, bool pending)
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        Exception original = brokenPipe ? new IOException("send failed after receipt") : new InvalidOperationException("send rejected after receipt");
        native.SendHandler = (_, _) =>
        {
            if (native.SendAsyncCallCount > 1)
                return Task.FromException(new InvalidOperationException("Accepted native work was replayed."));
            native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "active", Content = "active accepted" } });
            native.Emit(new SessionIdleEvent { Data = new() });
            if (pending)
            {
                native.Emit(new AssistantMessageEvent { Data = new() { MessageId = "pending", Content = "pending accepted" } });
                native.Emit(new SessionIdleEvent { Data = new() });
            }
            return Task.FromException(original);
        };
        await using var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        sdk.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        sdk.SetSubAgentDependencies(new SubAgentTestFakes.FakeRunningAgentChatFactory(), new SubAgentTestFakes.FakeSubAgentTable());
        var output = new List<ChatResponseUpdate>();
        Assert.Same(original, await Record.ExceptionAsync(async () =>
        {
            await foreach (var update in sdk.GetStreamingResponseAsync([new ChatMessage(ChatRole.User, "one request")]))
                output.Add(update);
        }));
        Assert.Equal(pending ? "active acceptedpending accepted" : "active accepted",
            string.Concat(output.SelectMany(update => update.Contents).OfType<TextContent>().Select(text => text.Text)));
        Assert.Equal(1, native.SendAsyncCallCount);
        Assert.Equal(1, native.DisposeAsyncCallCount);
        Assert.Equal(0, native.SubscriberCount);
        Assert.False(sdk.HasPendingRootContinuation);
    }

    [Theory]
    [InlineData("observer", false)]
    [InlineData("retirement", false)]
    [InlineData("dispatch", false)]
    [InlineData("poller", false)]
    [InlineData("secondary-dispatch", false)]
    [InlineData("observer", true)]
    [InlineData("retirement", true)]
    [InlineData("dispatch", true)]
    [InlineData("poller", true)]
    [InlineData("secondary-dispatch", true)]
    public Task AgentChat_ActiveRootFailure_DrainsFiniteAcceptedOwnershipBeforeRetirement(string entry, bool cleanupFailure)
    {
        var store = new CopilotSdkRootContinuationTests.SerializedStore();
        return CopilotSdkRetirementPersistenceScenario.RunActiveFailureDrainAsync(() => store, entry, cleanupFailure);
    }
}
