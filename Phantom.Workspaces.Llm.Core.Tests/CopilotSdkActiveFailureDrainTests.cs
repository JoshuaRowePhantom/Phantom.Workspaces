using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed partial class CopilotSdkChatClientTests
{
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
