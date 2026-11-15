using System.Collections.ObjectModel;
using System.Collections.Specialized;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Core.Tests.Infrastructure;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

internal static class CopilotSdkRetirementPersistenceScenario
{
    private const string Definition = """
        {"kind":"prompt","name":"retirement-root","model":{"id":"echo","provider":"echo","apiType":"Echo"},"tools":[]}
        """;

    internal static async Task RunAsync(Func<IAgentPersistenceStore> freshStore)
    {
        var productId = Guid.NewGuid().ToString();
        var nativeId = Guid.NewGuid().ToString();
        var session = new FakeCopilotSession { SessionId = nativeId };
        var client = new CopilotSdkChatClient("gpt-5", "Copilot", null, null);
        client.SetCopilotClientFactoryForTest(new FakeCopilotClientFactory(new FakeCopilotClient(session)));
        var scheduler = new ConcurrentExclusiveSchedulerPair();
        var factory = new RetirementFactory(scheduler.ExclusiveScheduler);
        await using var parent = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
            ConfiguredStore = freshStore(),
            ClientOverride = client,
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        var timestamp = DateTimeOffset.UtcNow;
        session.EnqueueEvent(new ToolExecutionStartEvent { Data = new() { ToolCallId = "child-call", ToolName = "task" } });
        session.EnqueueEvent(new SubagentStartedEvent
        {
            Id = Guid.NewGuid(), AgentId = "child", Timestamp = timestamp,
            Data = new() { ToolCallId = "child-call", AgentName = "task", AgentDisplayName = "Task", AgentDescription = "synthetic child" },
        });
        session.EnqueueEvent(new AssistantMessageDeltaEvent { Data = new() { MessageId = "registered", DeltaContent = "registered child" } });
        var registered = WhenRunningTextAsync(parent, "registered child");
        var settled = WhenSettledAsync(parent, "durable while retiring");
        parent.EnqueueUserMessage("first request");
        await factory.Created.Task;
        await registered;
        try
        {
            session.Emit(new SubagentCompletedEvent
            {
                Id = Guid.NewGuid(), AgentId = "child", Timestamp = timestamp.AddSeconds(1),
                Data = new() { ToolCallId = "child-call", AgentName = "task", AgentDisplayName = "Task" },
            });
            await factory.Retiring.Task;
            session.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = "child-call", Success = true, Result = new() { Content = "child done" } } });
            EmitTaskFinal(session, "following-call", "durable while retiring");
            await settled;
            Assert.False(factory.Release.Task.IsCompleted);
            var durable = await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId });
            AssertTranscript(durable, "durable while retiring", 2);
            Assert.Equal(nativeId, (await freshStore().RestoreAsync(new() { AgentSessionId = productId }))!.Value.CopilotSdkSessionId);
            Assert.Empty(await freshStore().ReadMessagesAsync(new() { AgentSessionId = nativeId }));
            var continuation = WhenSettledAsync(parent, "notification final");
            EmitTaskFinal(session, "notification-call", "notification final");
            await continuation;
            Assert.False(factory.Release.Task.IsCompleted);
            Assert.Equal(1, session.SendAsyncCallCount);
            AssertTranscript(await freshStore().ReadMessagesAsync(new() { AgentSessionId = productId }), "notification final", 3);
        }
        finally
        {
            factory.Release.TrySetResult();
        }
        await parent.DisposeAsync();
        await using var restored = await AgentChat.CreateAsync(new()
        {
            AgentSessionId = productId,
            AgentDefinition = null,
            ConfiguredStore = freshStore(),
            ClientOverride = new CopilotSubAgentChatClient(),
            ForegroundScheduler = scheduler.ExclusiveScheduler,
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionCallContent>().Count());
        Assert.Equal(3, restored.History.SelectMany(item => item.Contents).OfType<FunctionResultContent>().Count());
        Assert.Contains(restored.History, item => item.Contents.OfType<TextContent>().Any(text => text.Text == "durable while retiring"));
        Assert.Equal(1, factory.Releases);
        if (factory.Child is { } child) await child.DisposeAsync();
    }

    private static void AssertTranscript(ChatMessage[] messages, string final, int calls)
    {
        var contents = messages.SelectMany(message => message.Contents).ToArray();
        var functions = contents.OfType<FunctionCallContent>().ToArray();
        var results = contents.OfType<FunctionResultContent>().ToArray();
        Assert.Equal(calls, functions.Length);
        Assert.Equal(calls, results.Length);
        Assert.Equal(calls, functions.Select(call => call.CallId).Distinct().Count());
        Assert.Equal(functions.Select(call => call.CallId), results.Select(result => result.CallId));
        Assert.Single(contents.OfType<TextContent>(), text => text.Text == final);
        Assert.Equal(final, messages[^1].Text);
    }

    private static void EmitTaskFinal(FakeCopilotSession session, string call, string text)
    {
        session.Emit(new ToolExecutionStartEvent { Data = new() { ToolCallId = call, ToolName = "task" } });
        session.Emit(new ToolExecutionCompleteEvent { Data = new() { ToolCallId = call, Success = true, Result = new() { Content = "completed result" } } });
        session.Emit(new AssistantMessageEvent { Data = new() { MessageId = call + "-final", Content = text } });
        session.Emit(new SessionIdleEvent { Data = new() });
    }

    private static Task WhenSettledAsync(AgentChat chat, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            if (chat.RunningItems.Count != 0 || !chat.History.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == text))) return;
            ((INotifyCollectionChanged)chat.History).CollectionChanged -= Changed;
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
            completion.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.History).CollectionChanged += Changed;
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        return completion.Task;
    }

    private static Task WhenRunningTextAsync(AgentChat chat, string text)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observed = new HashSet<AgentChatRunningItem>();
        void Changed(object? sender, NotifyCollectionChangedEventArgs args)
        {
            foreach (var run in chat.RunningItems)
                if (observed.Add(run)) run.Items.CollectionChanged += Changed;
            if (!chat.RunningItems.Any(run => run.Items.Any(item => item.Contents.OfType<TextContent>().Any(content => content.Text == text)))) return;
            ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged -= Changed;
            foreach (var run in observed) run.Items.CollectionChanged -= Changed;
            completion.TrySetResult();
        }
        ((INotifyCollectionChanged)chat.RunningItems).CollectionChanged += Changed;
        Changed(null, new(NotifyCollectionChangedAction.Reset));
        return completion.Task;
    }

    private sealed class RetirementFactory(TaskScheduler scheduler) : IRunningAgentChatFactory
    {
        public ObservableCollection<RunningAgentChat> RunningSessions { get; } = [];
        public TaskCompletionSource Created { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Retiring { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public AgentChat? Child { get; private set; }
        public RunningAgentChatLease? Lease { get; private set; }
        public int Releases { get; private set; }
        public async Task<RunningAgentChatLease> CreateAsync(AgentDefinition definition, AgentSessionId sessionId,
            AgentServices? services, string? displayNameOverride, string? descriptionOverride, string? nameOverride, CancellationToken ct)
        {
            this.Child = await AgentChat.CreateAsync(new()
            {
                AgentSessionId = sessionId.Value,
                AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(Definition),
                ConfiguredStore = new InMemoryAgentPersistenceStore(),
                ClientOverride = new CopilotSubAgentChatClient(),
                ForegroundScheduler = scheduler,
            });
            this.Lease = new(sessionId, this.Child, async () =>
            {
                this.Releases++;
                this.Retiring.TrySetResult();
                await this.Release.Task;
            });
            this.Created.TrySetResult();
            return this.Lease;
        }
        public Task<RunningAgentChatLease> GetAsync(AgentSessionId id, bool registerAsRunningAgent, CancellationToken ct) =>
            Task.FromResult(this.Lease!);
        public Task<RunningAgentChatLease> GetOrCreateAsync(AgentSessionId id, AgentDefinition? definition, AgentServices? services,
            string? displayNameOverride, string? descriptionOverride, bool registerAsRunningAgent, CancellationToken ct) =>
            Task.FromResult(this.Lease!);
    }
}
