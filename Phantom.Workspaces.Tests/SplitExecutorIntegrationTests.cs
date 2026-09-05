using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Core.Manifest;

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
            agentDefinitionEntityId: new EntityId(),
            agentDisplayName: "GitHub Copilot (split executor)",
            agentSessionId: "11111111-1111-4111-8111-111111111111",
            agentSessionNames: [new EntityName("agent-sessions", "split-executor-test")],
            currentTime: DateTimeOffset.UnixEpoch,
            computerName: "test-host",
            sessionExecutor: ExecutorBindings.LocalDescriptor(),
            executorComponentBindings: bindings.ToPersistableMap(),
            parameterSelections: selections);

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
}
