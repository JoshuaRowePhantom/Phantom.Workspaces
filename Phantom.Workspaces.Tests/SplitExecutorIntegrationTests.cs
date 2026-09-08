using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Gui.Shared.Utilities;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Interfaces;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Transport;
using IRunningAgentChatFactory = Phantom.Workspaces.Llm.IRunningAgentChatFactory;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// End-to-end coverage for the default split-executor Copilot manifest (issue #1441,
/// per-component-executor-binding). Resolves the manifest's executor resources against a launch-time
/// user-computer-profile selection, authors an <c>agent-session</c> entity with
/// <see cref="AgentSessionEntityFactory.CreateEntityData"/>, and reads the executor bindings back with
/// <see cref="AgentSessionExecutorBindings"/> to prove the model's <c>worker</c> executor routes remote
/// while the session (and every unbound workspace/OAuth tool) stays local.
/// </summary>
public sealed class SplitExecutorIntegrationTests
{
    private const string WorkerProfileUuid = "a1b2c3d4-e5f6-7788-99aa-bbccddeeff00";
    private const string ResourceName = "Phantom.Workspaces.Tests.copilot-split-executor.json";

    private static string LoadManifestJson()
    {
        var assembly = typeof(SplitExecutorIntegrationTests).Assembly;
        using var stream = assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource not found: {ResourceName}");
        using var reader = new StreamReader(stream);
        using var document = JsonDocument.Parse(reader.ReadToEnd());
        return document.RootElement.GetProperty("manifest").GetRawText();
    }

    private static (JsonElement EntityData, ExecutorBindings Bindings) AuthorSession()
    {
        var manifestJson = LoadManifestJson();

        // The default manifest is a real, schema-valid manifest.
        Assert.NotNull(AgentManifestLoader.LoadManifestFromJson(manifestJson));

        var resources = ExecutorResource.ParseManifestResources(manifestJson);
        var selections = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["worker-profile"] = ExecutorParameterSelection.ForUserComputerProfile(WorkerProfileUuid),
        };

        var bindings = ExecutorBindings.Build(resources, selections, trustProfile: null);

        var entityData = AgentSessionEntityFactory.CreateEntityData(
            new CreateAgentSessionEntityDataRequest
            {
                AgentDefinitionEntityId = new EntityId(),
                AgentDisplayName = "GitHub Copilot (split executor)",
                AgentSessionId = "11111111-1111-4111-8111-111111111111",
                AgentSessionNames = [new EntityName("agent-sessions", "split-executor-test")],
                CurrentTime = DateTimeOffset.UnixEpoch,
                ComputerName = "test-host",
                HostProfileEntityId = new EntityId(WorkerProfileUuid),
                SessionExecutor = ExecutorBindings.LocalDescriptor(),
                ExecutorComponentBindings = bindings.ToPersistableMap(),
                ParameterSelections = selections,
            });

        return (entityData, bindings);
    }

    [Fact]
    public void DefaultManifest_Session_RecordsExecutorBindings_CopilotWorker_WorkspaceLocal()
    {
        var (entityData, _) = AuthorSession();

        // The session executor is local, so anything unbound (workspace tools, the GitHub web MCP) runs
        // on the local orchestrator.
        var session = AgentSessionExecutorBindings.ReadSessionExecutor(entityData);
        Assert.Equal("local", session.GetProperty("type").GetString());
        Assert.Equal(
            AgentSessionExecutorBindings.LocalClientInstance,
            AgentSessionExecutorBindings.DeriveClientInstance(session));

        // The Copilot chat client's 'worker' executor is bound to the launch-selected remote profile.
        var components = AgentSessionExecutorBindings.ReadComponentBindings(entityData);
        var worker = Assert.Contains("worker", components);
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Equal(WorkerProfileUuid, worker.GetProperty("entity-id").GetString());
        Assert.Equal(WorkerProfileUuid, AgentSessionExecutorBindings.DeriveClientInstance(worker));

        // The typed launch selection round-trips so a resumed session rebuilds the same topology.
        var selections = AgentSessionExecutorBindings.ReadParameterSelections(entityData);
        var selection = Assert.Contains("worker-profile", selections);
        Assert.True(
            ExecutorParameterSelection.TryGetUserComputerProfile(selection, out var selectedEntityId));
        Assert.Equal(WorkerProfileUuid, selectedEntityId);
    }

    [Fact]
    public void DefaultManifest_Topology_RoutesComponentsAccordingly()
    {
        var (_, bindings) = AuthorSession();

        // An unset executor (the session default) and the workspace tools resolve local.
        Assert.Equal("local", bindings.ResolveComponent(null).GetProperty("type").GetString());

        // The model's bound 'worker' executor resolves to the remote profile descriptor.
        var worker = bindings.ResolveComponent("worker");
        Assert.Equal("user-computer-profile", worker.GetProperty("type").GetString());
        Assert.Equal(WorkerProfileUuid, worker.GetProperty("entity-id").GetString());

        // The string-keyed topology keeps GUI-local routing local while the agent-executor / hosting
        // classes follow the (local) session executor.
        var topology = bindings.ToTopology();
        Assert.Equal(".", topology.AgentExecutorClientInstance);
        Assert.Equal(".", topology.HostingInstanceClientInstance);
        Assert.Equal(".", topology.GuiLocalClientInstance);
    }

    [Fact]
    public async Task PersistedSession_RoutesModelAndToolToBoundExecutors()
    {
        // End-to-end coverage for #1481: persist a split-executor session with distinct model and
        // tool component bindings, run the full acquisition boundary
        // (RunningAgentChatTable → AgentSessionRuntimeContextFactory → transport registry), and
        // prove that
        //   (a) the effective ExecutorBindings reaching the running-chat factory resolves each
        //       component to its bound descriptor, and
        //   (b) the shared registry hands each descriptor to the correct fake transport factory
        //       — i.e. model calls route to the model executor and tool calls route to the tool
        //       executor, with no real environment identifiers or network access.
        const string ModelProfileEntityId = "aaaa1481-model-0000-0000-000000000001";
        const string ToolProfileEntityId = "bbbb1481-tool0-0000-0000-000000000002";

        var registry = new TransportFactoryRegistry();
        var modelFactory = new RecordingUserComputerProfileTransportFactory(ModelProfileEntityId);
        var toolFactory = new RecordingUserComputerProfileTransportFactory(ToolProfileEntityId);
        registry.Register(modelFactory);
        registry.Register(toolFactory);

        var runtimeFactory = new AgentSessionRuntimeContextFactory(registry);
        var chatFactory = new CapturingRunningAgentChatFactory();
        var table = new RunningAgentChatTable(chatFactory, runtimeFactory);

        var sessionEntity = JsonDocument.Parse(
            $$"""
            {
              "agent-session-id": "11111111-1111-4111-8111-111111111111",
              "host-profile-entity-id": "22222222-2222-4222-8222-222222222222",
              "executor-bindings": {
                "session": { "type": "local" },
                "components": {
                  "model": {
                    "type": "user-computer-profile",
                    "entity-id": "{{ModelProfileEntityId}}"
                  },
                  "worker": {
                    "type": "user-computer-profile",
                    "entity-id": "{{ToolProfileEntityId}}"
                  }
                }
              }
            }
            """).RootElement.Clone();

        var lease = await table.AcquireAsync(new AcquireAgentChatRequest
        {
            AgentSessionId = new AgentSessionId("11111111-1111-4111-8111-111111111111"),
            AgentSessionEntity = sessionEntity,
            AgentDefinition = AgentDefinition.FromJson(
                """
                {
                  "kind": "prompt",
                  "name": "split-executor-persisted",
                  "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                  "tools": []
                }
                """),
        }, TestContext.Current.CancellationToken);

        try
        {
            // The effective services reaching the running-chat factory carry the reconstructed
            // ExecutorBindings and the shared transport registry.
            var services = chatFactory.LastServices;
            Assert.NotNull(services);
            var effective = Assert.IsType<ExecutorBindings>(services!.ExecutorBindings);
            var model = effective.ResolveComponent("model");
            Assert.Equal("user-computer-profile", model.GetProperty("type").GetString());
            Assert.Equal(ModelProfileEntityId, model.GetProperty("entity-id").GetString());
            var tool = effective.ResolveComponent("worker");
            Assert.Equal("user-computer-profile", tool.GetProperty("type").GetString());
            Assert.Equal(ToolProfileEntityId, tool.GetProperty("entity-id").GetString());
            Assert.Same(registry, services.ExecutorTransportFactoryRegistry);

            // Now prove routing through the registry actually dispatches each descriptor to the
            // matching fake transport — model → model factory, tool → tool factory — so a split
            // model/tool placement is preserved from persistence through to transport selection.
            var modelTransport = await ((ITransportFactoryRegistry)services.ExecutorTransportFactoryRegistry!)
                .ConnectToAsync(model, TestContext.Current.CancellationToken);
            var toolTransport = await ((ITransportFactoryRegistry)services.ExecutorTransportFactoryRegistry!)
                .ConnectToAsync(tool, TestContext.Current.CancellationToken);
            Assert.Same(modelFactory.LastTransport, modelTransport);
            Assert.Same(toolFactory.LastTransport, toolTransport);
            Assert.Equal(1, modelFactory.ConnectCallCount);
            Assert.Equal(1, toolFactory.ConnectCallCount);
        }
        finally
        {
            await lease.DisposeAsync();
        }
    }

    private sealed class RecordingUserComputerProfileTransportFactory : ITransportFactory
    {
        private readonly string acceptedEntityId;
        private int connectCallCount;

        public RecordingUserComputerProfileTransportFactory(string acceptedEntityId)
        {
            this.acceptedEntityId = acceptedEntityId;
        }

        public int ConnectCallCount => Volatile.Read(ref this.connectCallCount);
        public ITransport? LastTransport { get; private set; }

        public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
        {
            if (connectionDescriptor.ValueKind == JsonValueKind.Object
                && connectionDescriptor.TryGetProperty("type", out var type)
                && type.ValueKind == JsonValueKind.String
                && string.Equals(type.GetString(), "user-computer-profile", StringComparison.Ordinal)
                && connectionDescriptor.TryGetProperty("entity-id", out var entityId)
                && entityId.ValueKind == JsonValueKind.String
                && string.Equals(entityId.GetString(), this.acceptedEntityId, StringComparison.Ordinal))
            {
                Interlocked.Increment(ref this.connectCallCount);
                var transport = new FakeTransport();
                this.LastTransport = transport;
                return Task.FromResult<ITransport?>(transport);
            }

            return Task.FromResult<ITransport?>(null);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeTransport : ITransport
    {
        public Task<IMessageChannel> ConnectToMessageChannelAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException("Fake transport does not open channels; the test only verifies routing.");

        public Task<Stream> ConnectToStreamAsync(JsonElement request, CancellationToken ct = default)
            => throw new NotSupportedException("Fake transport does not open streams; the test only verifies routing.");

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CapturingRunningAgentChatFactory : IRunningAgentChatFactory
    {
        public System.Collections.ObjectModel.ObservableCollection<RunningAgentChat> RunningSessions { get; } = new();
        public AgentServices? LastServices { get; private set; }

        public Task<RunningAgentChatLease> GetAsync(AgentSessionId sessionId, bool registerAsRunningAgent = true, CancellationToken ct = default)
            => Task.FromResult(new RunningAgentChatLease(sessionId, null!, () => ValueTask.CompletedTask));

        public Task<RunningAgentChatLease> CreateAsync(
            AgentDefinition definition,
            AgentSessionId sessionId,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            string? nameOverride = null,
            CancellationToken ct = default)
            => GetAsync(sessionId, ct: ct);

        public Task<RunningAgentChatLease> GetOrCreateAsync(
            AgentSessionId sessionId,
            AgentDefinition? definition = null,
            AgentServices? services = null,
            string? displayNameOverride = null,
            string? descriptionOverride = null,
            bool registerAsRunningAgent = true,
            CancellationToken ct = default)
        {
            LastServices = services;
            return GetAsync(sessionId, ct: ct);
        }
    }
}
