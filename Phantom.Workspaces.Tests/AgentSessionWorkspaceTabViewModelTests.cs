using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Agents.AI;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Agent.Gui.ViewModels;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Testing.Gui;
using Phantom.Workspaces.Utilities;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class AgentSessionWorkspaceTabViewModelTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task SetReady_TransitionsToReadyWhenHistoryPopulated_WithoutWaitingForMcpInit()
    {
        // #1430: opening an agent session must render the chat view (tab Ready) as soon as the chat
        // object and its history exist, WITHOUT waiting for background MCP/tool initialization. The
        // gated toolset stands in for a slow MCP server so its initialization is deterministically
        // still in flight when the tab is asked to become Ready.
        var invoked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new GatedToolsetContextProvider(invoked, release.Task);
        var client = new DeterministicTestChatClient();
        var services = new AgentServices
        {
            ChatClientOverride = client,
            ToolsetFactory = ToolsetFactory.CreateNamedToolsetFactory(
                kind: "scripted_kind",
                createToolsetAsync: (_, _) => Task.FromResult<AIContextProvider?>(provider)),
        };

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = CreateAgentDefinitionWithGatedTool(),
            AgentServices = services,
        });
        await using var chatScope = chat;

        // Creation returned while the toolset/MCP initialization is still gated.
        await invoked.Task;
        Assert.False(chat.Initialization.IsCompleted);

        var loggerFactory = new ObservableLoggerFactory();
        var agentViewModel = new AgentViewModel(chat, "test-agent", "", loggerFactory, TaskScheduler.Default);
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-ready-1430",
            Title = "Agent",
        };

        tab.SetReady(agentViewModel, loggerFactory);

        // The tab is Ready even though background MCP initialization has NOT completed.
        Assert.Equal(AgentTabState.Ready, tab.State);
        Assert.False(chat.Initialization.IsCompleted);

        // Cleanup: let the gated initialization finish, then dispose.
        release.TrySetResult();
        await chat.Initialization;
        await tab.DisposeAsync();
        loggerFactory.Dispose();
    }

    private static AgentSchema.AgentDefinition CreateAgentDefinitionWithGatedTool()
        => AgentDefinitionLoader.LoadAgentFromJson("""
        {
          "kind": "prompt",
          "name": "test-agent",
          "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
          "tools": [ { "kind": "scripted_kind", "description": "Gated toolset" } ]
        }
        """);

    // Gates its first ProvideAIContextAsync invocation on a caller-controlled release task so a test
    // can hold background tool initialization in flight while asserting the tab still becomes Ready
    // (issue #1430).
    private sealed class GatedToolsetContextProvider : AIContextProvider
    {
        private readonly string stateKey = $"gated-toolset:{Guid.NewGuid():n}";
        private readonly TaskCompletionSource invoked;
        private readonly Task release;
        private int invocationCount;

        public GatedToolsetContextProvider(TaskCompletionSource invoked, Task release)
            : base(null, null, null)
        {
            this.invoked = invoked;
            this.release = release;
        }

        public override IReadOnlyList<string> StateKeys => [this.stateKey];

        protected override async ValueTask<AIContext> ProvideAIContextAsync(
            InvokingContext context,
            CancellationToken cancellationToken)
        {
            _ = context;
            if (Interlocked.Increment(ref this.invocationCount) == 1)
            {
                this.invoked.TrySetResult();
                await this.release.WaitAsync(cancellationToken);
            }

            return new AIContext { Tools = [] };
        }
    }

    [Fact]
    public async Task AgentSessionWorkspaceTabViewModel_DisposeAsyncDuringLoading_LeaseAcquiredLaterIsStillReleased()
    {
        // #1340: the tab is disposed while still in its loading state (no agent yet), so
        // DisposeAsync takes the else-branch and disposes leaseDisposables. A racing background
        // initialization (OpenAgentSessionShortcutHandler.InitializeTabInBackgroundAsync) can then
        // still acquire a lease and hand it to SetLease AFTER disposal. The AsyncDisposableCollection
        // post-dispose Add contract must dispose that late lease immediately so the
        // RunningAgentChatLease is never leaked.
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = "agent-race",
            Title = "Agent Race",
        };

        // Dispose while loading (agent is null): the lease acquired below arrives post-dispose.
        await tab.DisposeAsync();

        var released = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var lease = new RunningAgentChatLease(
            new AgentSessionId("session-race-1340"),
            null!,
            () =>
            {
                released.TrySetResult();
                return ValueTask.CompletedTask;
            });

        // The racing background init acquires the lease after DisposeAsync.
        tab.SetLease(lease);

        // The post-dispose Add must dispose the lease immediately (asynchronously).
        await released.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.True(released.Task.IsCompletedSuccessfully);
    }
}
