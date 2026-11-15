using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Xunit.Abstractions;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class StreamingAggregationPipelineTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ActualAgentChat_SeparatelyAppliedTokens_DoNotReprocessStablePrefix()
    {
        var native = new FakeCopilotSession { SessionId = Guid.NewGuid().ToString() };
        var sdk = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        sdk.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(native)));
        var sent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        native.Sending = _ => sent.TrySetResult();
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var store = new InMemoryAgentPersistenceStore();
        await using var chat = await AgentChat.CreateAsync(new()
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson("""
                {"kind":"prompt","name":"incremental","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
                """),
            ConfiguredStore = store, ClientOverride = sdk, ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = new SubAgentTestFakes.FakeRunningAgentChatFactory() },
        });
        chat.EnqueueUserMessage("request");
        await sent.Task;
        var prefixReady = CopilotSdkRetirementPersistenceScenario.WhenRunningTextAsync(chat, "stable-49");
        for (var index = 0; index < 50; index++)
            native.Emit(new AssistantMessageEvent { Data = new() { MessageId = $"prefix-{index}", Content = $"stable-{index}" } });
        await prefixReady;
        var first = chat.RunningItems.Single().Items[0];
        var before = chat.ProjectionAggregationWork!;
        for (var index = 0; index < 100; index++)
        {
            var applied = CopilotSdkRetirementPersistenceScenario.WhenRunningTextAsync(chat, new string('x', index + 1));
            native.Emit(new AssistantMessageDeltaEvent { Data = new() { MessageId = "growing", DeltaContent = "x" } });
            await applied;
            Assert.Same(first, chat.RunningItems.Single().Items[0]);
        }
        var after = chat.ProjectionAggregationWork!;
        Assert.Equal(100, after.ContentVisits - before.ContentVisits);
        Assert.Equal(100, after.AggregatedCharacters - before.AggregatedCharacters);
        var finished = CopilotSdkRetirementPersistenceScenario.WhenSettledAsync(chat, new string('x', 100));
        native.Emit(new SessionIdleEvent { Data = new() });
        await finished;
        var persistence = chat.PersistenceAggregationWork!;
        Assert.Equal(150, persistence.ContentVisits);
        Assert.Equal(50 + 100, chat.ProjectionAggregationWork!.ContentVisits);
        Assert.Equal(Enumerable.Range(0, 50).Sum(index => $"stable-{index}".Length) + 100, persistence.AggregatedCharacters);
        Assert.Equal(persistence.AggregatedCharacters, persistence.MaterializedCharacters);
        Assert.True(after.MaterializedCharacters > after.AggregatedCharacters);
        Assert.True(after.SnapshotReferences >= 50 * 100);
        var messages = await store.ReadMessagesAsync(new() { AgentSessionId = chat.AgentSessionId });
        Assert.Equal(new string('x', 100), messages[^1].Text);
        Assert.Equal("stable-0", first.Contents.OfType<TextContent>().Single().Text);
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Persistence = persistence,
            Projection = chat.ProjectionAggregationWork,
            Contract = "Aggregation visits new material once; immutable rendered strings/item arrays are separately counted output copies.",
        }));
    }
}
