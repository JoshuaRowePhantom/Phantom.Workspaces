using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentChatLifecyclePersistenceTests
{
    private sealed class HeldScheduler : TaskScheduler
    {
        private readonly List<Task> held = [];
        public bool Hold { get; set; }
        protected override IEnumerable<Task> GetScheduledTasks() => this.held;
        protected override bool TryExecuteTaskInline(Task task, bool taskWasPreviouslyQueued) => false;
        protected override void QueueTask(Task task)
        {
            if (this.Hold) this.held.Add(task);
            else this.TryExecuteTask(task);
        }
        public void ReleaseInReverseOrder()
        {
            this.Hold = false;
            foreach (var task in this.held.AsEnumerable().Reverse().ToArray()) this.TryExecuteTask(task);
            this.held.Clear();
        }
    }

    private static AgentDefinition Definition() => AgentDefinitionLoader.LoadAgentFromJson("""
        {"kind":"prompt","name":"hosted","model":{"id":"github-copilot-subagent","provider":"github-copilot-subagent"},"tools":[]}
        """);

    [Theory]
    [InlineData("unknown", "cancelled", AgentChatCompletionState.Failed)]
    [InlineData("cancelled", "running", AgentChatCompletionState.Running)]
    public async Task AgentChat_QueuedObsoleteProjection_CannotOverwriteCurrentEpoch(string obsolete, string current, AgentChatCompletionState expected)
    {
        var scheduler = new HeldScheduler();
        var store = new InMemoryAgentPersistenceStore();
        await using var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = Definition(), ConfiguredStore = store, ClientOverride = new CopilotSubAgentChatClient(),
            ForegroundScheduler = scheduler,
        });
        await chat.RestoreCompleted;
        scheduler.Hold = true;
        chat.ApplySubAgentLifecycleState(new(chat.AgentSessionId, CopilotSubAgentLifecycleLayer.Session, obsolete));
        chat.ApplySubAgentLifecycleState(new(chat.AgentSessionId, CopilotSubAgentLifecycleLayer.Session, current));
        scheduler.ReleaseInReverseOrder();
        await chat.WaitForLifecyclePersistenceAsync();
        Assert.Equal(expected, chat.CompletionState);
        Assert.Equal(current, (await store.RestoreAsync(new() { AgentSessionId = chat.AgentSessionId }))!.Value.SubAgentLifecycleState);
    }

    [Theory]
    [InlineData("cancelled", AgentChatCompletionState.Failed)]
    [InlineData("failed", AgentChatCompletionState.Failed)]
    [InlineData("idle", AgentChatCompletionState.Succeeded)]
    [InlineData("unknown", AgentChatCompletionState.Unknown)]
    [InlineData("running", AgentChatCompletionState.Unknown)]
    [InlineData("completed", AgentChatCompletionState.Succeeded)]
    public async Task AgentChat_NormalizedLifecycle_PersistsAndReloadsWithoutSuccessCoercion(string state, AgentChatCompletionState expected)
    {
        var store = new InMemoryAgentPersistenceStore();
        string id;
        await using (var chat = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentDefinition = Definition(), ConfiguredStore = store, ClientOverride = new CopilotSubAgentChatClient(),
        }))
        {
            id = chat.AgentSessionId;
            var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            chat.CompletionStateChanged += (_, _) => applied.TrySetResult();
            chat.ApplySubAgentLifecycleState(new(id, CopilotSubAgentLifecycleLayer.Session, state));
            await applied.Task;
            await chat.WaitForLifecyclePersistenceAsync();
            var persisted = await store.RestoreAsync(new() { AgentSessionId = id });
            Assert.Equal(state, persisted!.Value.SubAgentLifecycleState);
        }
        await using var reloaded = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = id, AgentDefinition = Definition(), ConfiguredStore = store,
            ClientOverride = new CopilotSubAgentChatClient(),
        });
        Assert.Equal(expected, reloaded.CompletionState);
        Assert.Equal(id, reloaded.AgentSessionId);
    }

    [Theory]
    [InlineData("cancelled", AgentChatCompletionState.Failed)]
    [InlineData("idle", AgentChatCompletionState.Succeeded)]
    [InlineData("unknown", AgentChatCompletionState.Unknown)]
    [InlineData("running", AgentChatCompletionState.Unknown)]
    public async Task AgentChat_LazyChildRestore_PreservesPersistedProjectionAndStateOnlyUpdates(string state, AgentChatCompletionState expected)
    {
        var store = new InMemoryAgentPersistenceStore();
        var childId = CopilotSubAgentIdentity.Create("child", "call").Value;
        await store.StoreAsync(new()
        {
            Agent = new() { AgentSessionId = childId, SubAgentLifecycleState = state },
        });
        await store.AddSubAgentLinkAsync("parent", childId);
        var factory = new SubAgentTestFakes.FakeRunningAgentChatFactory();
        await using var parent = await AgentChat.CreateAsync(new InternalCreateAgentChatRequest
        {
            AgentSessionId = "parent", AgentDefinition = Definition(), ConfiguredStore = store,
            ClientOverride = new CopilotSubAgentChatClient(),
            AgentServices = new() { RunningAgentChatFactory = factory },
        });
        await parent.RestoreCompleted;
        var child = Assert.IsType<SubAgent>(Assert.Single(parent.SubAgents));
        Assert.Null(child.AgentChat);
        Assert.Equal(expected, ((IRunningSubAgent)child).CompletionState);
        Assert.Empty(factory.CreateCalls);
        child.SetRestoredLifecycleState("cancelled");
        await parent.WaitForLifecyclePersistenceAsync();
        Assert.Equal("cancelled", (await store.RestoreAsync(new() { AgentSessionId = childId }))!.Value.SubAgentLifecycleState);
        Assert.Same(child, Assert.Single(parent.SubAgents));
    }
}
