using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Issue #1443 (per-component-executor-binding, Commit 6B): binding only the model to a remote
/// executor must produce a split topology — the innermost SDK session is transported while the
/// router (agent-executor) and hosting instance stay local.
/// </summary>
public sealed class AgentChatExecutorBindingTests
{
    [Fact]
    public async Task SplitTopology_ModelRemote_RouterAndToolsLocal()
    {
        using var remoteDescriptor = JsonDocument.Parse(
            $$"""{"type":"user-computer-profile","entity-id":"{{ExecutorRoutingTestHarness.RemoteEntityId}}"}""");
        var bindings = new ExecutorBindings
        {
            Bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model-host"] = remoteDescriptor.RootElement.Clone(),
            },
        };

        // No tool/session executor is bound remotely, so the router and hosting instance follow the
        // default local session executor: everything except the model runs in-process.
        var topology = bindings.ToTopology();
        Assert.Equal(TrustProfile.LocalClientInstance, topology.AgentExecutorClientInstance);
        Assert.Equal(TrustProfile.LocalClientInstance, topology.HostingInstanceClientInstance);

        // The model, bound to the remote executor via model.options.executor, is the only component
        // whose SDK session crosses the wire.
        var (transport, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var _t = transport;
        var registry = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport);
        var client = ExecutorRoutingTestHarness.CreateClient("model-host");
        client.ConfigureExecutorRouting(bindings, registry);

        var remote = await client.ResolveRemoteClientForTestAsync();

        Assert.NotNull(remote);
        Assert.Equal(1, registry.ConnectCount);
    }
}
