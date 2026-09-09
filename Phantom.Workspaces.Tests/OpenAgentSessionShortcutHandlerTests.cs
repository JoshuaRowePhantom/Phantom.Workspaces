using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Agent.Gui;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.ViewModels;
using Xunit;
using AgentViewModel = Phantom.Workspaces.Agent.Gui.ViewModels.AgentViewModel;

namespace Phantom.Workspaces.Tests;

public sealed class OpenAgentSessionShortcutHandlerTests
{
    [AvaloniaFact(Timeout = 30_000)]
    public async Task ComposeSessionAgentViewModel_AlwaysConfiguresSlashCommands()
    {
        // #1429: ComposeSessionAgentViewModel is the single seam every GUI session launch path
        // routes through. Calling it directly must always leave slash commands wired, so no future
        // launch path can bypass slash-command configuration by depending on caller-side wiring.
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel();
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var definitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("bbbb0005-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "bbbb0005-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "slash-seam"]],
              "display-name": { "default": "Echo slash-seam" },
              "definition": {
                "kind": "prompt",
                "name": "slash-seam",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel, definitionEntity, Guid.NewGuid().ToString("n"));
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable());

        var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = AgentDefinitionLoader.LoadAgentFromJson(
                """
                {
                  "kind": "prompt",
                  "name": "slash-seam",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": []
                }
                """),
            ForegroundScheduler = TaskScheduler.Default,
        });

        var loggerFactory = new ObservableLoggerFactory();
        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionEntity!.EntityId.ToString(),
            Title = sessionEntity.DisplayName,
            Entity = sessionEntity,
        };

        AgentViewModel agent = handler.ComposeSessionAgentViewModel(
            new ComposeSessionAgentViewModelOptions
            {
                MainWindowViewModel = viewModel,
                LoggerFactory = loggerFactory,
                AgentChat = chat,
                AgentSessionEntity = sessionEntity,
                Tab = tab,
                ForegroundScheduler = TaskScheduler.Default,
            });

        try
        {
            await MainWindowIntegrationTests.AssertSlashCommandsEnabledAsync(agent);
        }
        finally
        {
            await agent.DisposeAsync();
            await chat.DisposeAsync();
            loggerFactory.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task FirstOpen_UsesPersistedSplitBindings()
    {
        // Regression pin for #1481 — the first-open path (Open shortcut → TryBuildAgentAsync) must
        // route through the shared IAgentSessionRuntimeContextFactory hydrator, reconstructing the
        // persisted executor-bindings before definition resolution / chat creation. No GUI code
        // may parse executor bindings on this path.
        var legacyHostProfileEntityId = new EntityId("bbbb1481-0000-4000-8000-000000000001");
        const string WorkerProfileEntityId = "cccc1481-0000-4000-8000-000000000001";
        var registry = new TransportFactoryRegistry();
        var registryProvider = new TransportFactoryRegistryProvider(registry);
        var innerRuntimeFactory = AgentSessionRuntimeContextFactory.FromProvider(registryProvider);
        var spyRuntimeFactory = new SpyRuntimeContextFactory(innerRuntimeFactory);
        var runningChatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            SynchronizationContextTaskScheduler.FromCurrent());
        var table = new RunningAgentChatTable(runningChatFactory, spyRuntimeFactory);
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var agentDefinitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("dd811481-0000-4000-8000-000000000001"),
            """
            {
              "entity-id": "dd811481-0000-4000-8000-000000000001",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "first-open-split-bindings"]],
              "display-name": { "default": "First-Open Split Bindings" },
              "definition": {
                "kind": "prompt",
                "name": "first-open-split-bindings",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionEntityId = new EntityId("eeee1481-0000-4000-8000-000000000001");
        var sessionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            sessionEntityId,
            $$"""
            {
              "entity-id": "{{sessionEntityId}}",
              "entity-types": ["entity", "agent-session"],
              "names": [["tests", "agent-sessions", "first-open-legacy-split-bindings"]],
              "display-name": { "default": "First-Open Legacy Split Bindings" },
              "agent-source-entity-id": "{{agentDefinitionEntity.EntityId}}",
              "agent-session-id": "{{Guid.NewGuid():n}}",
              "host-profile-entity-id": "{{legacyHostProfileEntityId}}",
              "executor-bindings": {
                "components": {
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "{{WorkerProfileEntityId}}"
                  }
                }
              }
            }
            """);
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table);

        var tab = new AgentSessionWorkspaceTabViewModel
        {
            Id = sessionEntity!.EntityId.ToString(),
            Title = sessionEntity.DisplayName,
            Entity = sessionEntity,
        };
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();

        var result = await Task.Run(() =>
            handler.TryBuildAgentAsync(viewModel, sessionEntity!, tab, foregroundScheduler));

        try
        {
            Assert.NotNull(result);
            Assert.Equal(1, spyRuntimeFactory.CreateCallCount);
            var lastContext = Assert.IsType<AgentSessionRuntimeContext>(spyRuntimeFactory.LastContext);
            Assert.Equal(
                legacyHostProfileEntityId.ToString(),
                lastContext.Intent.ExecutorBindings.SessionExecutor.GetProperty("entity-id").GetString());
            var workerBinding = lastContext.Intent.ExecutorBindings.ResolveComponent("worker");
            Assert.Equal("user-computer-profile", workerBinding.GetProperty("type").GetString());
            Assert.Equal(WorkerProfileEntityId, workerBinding.GetProperty("entity-id").GetString());
            Assert.Same(registry, lastContext.TransportFactoryRegistry);
        }
        finally
        {
            if (result?.lease is { } lease)
            {
                await lease.DisposeAsync();
            }
            if (result?.agent is { } createdAgent)
            {
                await createdAgent.DisposeAsync();
            }
            result?.loggerFactory.Dispose();
        }
    }

    [AvaloniaFact(Timeout = 30_000)]
    public async Task AutoResume_UsesPersistedSplitBindings()
    {
        // Regression pin for #1481 — the auto-resume path must go through the same hydrator, so a
        // persisted split-executor session that auto-resumes reconstructs the same runtime context
        // as first-open.
        const string WorkerProfileEntityId = "cccc1481-0000-4000-8000-000000000002";
        var registry = new TransportFactoryRegistry();
        var registryProvider = new TransportFactoryRegistryProvider(registry);
        var innerRuntimeFactory = AgentSessionRuntimeContextFactory.FromProvider(registryProvider);
        var spyRuntimeFactory = new SpyRuntimeContextFactory(innerRuntimeFactory);
        var runningChatFactory = new AgentChatFactory(
            new InMemoryAgentPersistenceStore(),
            new AgentServices(),
            SynchronizationContextTaskScheduler.FromCurrent());
        var table = new RunningAgentChatTable(runningChatFactory, spyRuntimeFactory);
        var appServices = new ApplicationServices(table, new AgentPersistenceStoreCache());
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(applicationServices: appServices);
        await viewModel.InitializeAsync();

        var entityBroker = MainWindowIntegrationTests.GetEntityBroker(viewModel);
        var agentDefinitionEntity = await MainWindowIntegrationTests.UpsertEntityAndLoadAsync(
            entityBroker,
            new EntityId("dd811481-0000-4000-8000-000000000002"),
            """
            {
              "entity-id": "dd811481-0000-4000-8000-000000000002",
              "entity-types": ["entity", "agent-definition"],
              "names": [["tests", "agent-definitions", "auto-resume-split-bindings"]],
              "display-name": { "default": "Auto-Resume Split Bindings" },
              "definition": {
                "kind": "prompt",
                "name": "auto-resume-split-bindings",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);

        var context = new AgentSessionShortcutContext();
        var sessionExecutor = ExecutorBindings.LocalDescriptor();
        var componentBindings = BuildWorkerComponentBindings(WorkerProfileEntityId);
        var sessionEntity = await context.CreateAgentSessionEntityAsync(
            viewModel,
            agentDefinitionEntity,
            Guid.NewGuid().ToString("n"),
            sessionExecutor: sessionExecutor,
            executorComponentBindings: componentBindings);
        Assert.NotNull(sessionEntity);

        var handler = new OpenAgentSessionShortcutHandler(
            context,
            MainWindowIntegrationTests.CreateLocalTrustedExecutorSelector(),
            table);

        const string resumePrompt = "Resume with persisted split bindings.";
        var foregroundScheduler = SynchronizationContextTaskScheduler.FromCurrent();
        var lease = await Task.Run(() =>
            handler.TryStartAutoResumeAsync(viewModel, sessionEntity!, resumePrompt, foregroundScheduler));

        try
        {
            Assert.NotNull(lease);
            Assert.Equal(1, spyRuntimeFactory.CreateCallCount);
            var lastContext = Assert.IsType<AgentSessionRuntimeContext>(spyRuntimeFactory.LastContext);
            var workerBinding = lastContext.Intent.ExecutorBindings.ResolveComponent("worker");
            Assert.Equal("user-computer-profile", workerBinding.GetProperty("type").GetString());
            Assert.Equal(WorkerProfileEntityId, workerBinding.GetProperty("entity-id").GetString());
            Assert.Same(registry, lastContext.TransportFactoryRegistry);
        }
        finally
        {
            if (lease is not null)
            {
                await lease.DisposeAsync();
            }
        }
    }

    private static JsonElement BuildWorkerComponentBindings(string workerProfileEntityId)
    {
        using var document = JsonDocument.Parse(
            $$"""
            {
              "worker": {
                "type": "user-computer-profile",
                "entity-id": "{{workerProfileEntityId}}"
              }
            }
            """);
        return document.RootElement.Clone();
    }

    private sealed class SpyRuntimeContextFactory : IAgentSessionRuntimeContextFactory
    {
        private readonly IAgentSessionRuntimeContextFactory inner;
        private int createCallCount;

        public SpyRuntimeContextFactory(IAgentSessionRuntimeContextFactory inner)
        {
            this.inner = inner;
        }

        public int CreateCallCount => Volatile.Read(ref this.createCallCount);
        public AgentSessionRuntimeContext? LastContext { get; private set; }

        public AgentSessionRuntimeContext Create(JsonElement agentSessionEntity)
        {
            Interlocked.Increment(ref this.createCallCount);
            var context = this.inner.Create(agentSessionEntity);
            this.LastContext = context;
            return context;
        }
    }
}
