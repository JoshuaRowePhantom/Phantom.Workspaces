using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentTrustProfileResolverTests
{
    [Fact]
    public async Task AgentTrustProfileResolver_AbsentTrustProfile_ReturnsNull()
    {
        var definition = CreateAgentDefinition(metadataJson: null);
        var provider = new RecordingTrustProfileProvider();

        var resolved = await AgentTrustProfileResolver.ResolveAsync(definition, provider);

        Assert.Null(resolved);
        Assert.Empty(provider.RequestedProfileNames);
    }

    [Fact]
    public async Task AgentTrustProfileResolver_EntityRef_ResolvesViaProvider()
    {
        var definition = CreateAgentDefinition(
            """
            "trust-profile": { "$ref": { "entity-name": ["trust-profiles", "web-read-only"] } }
            """);
        var provider = new RecordingTrustProfileProvider();

        var resolved = await AgentTrustProfileResolver.ResolveAsync(definition, provider);

        Assert.NotNull(resolved);
        Assert.Equal(["web-read-only"], provider.RequestedProfileNames);
    }

    [Fact]
    public async Task AgentTrustProfileResolver_InlineProfile_ComposesDirectly()
    {
        var definition = CreateAgentDefinition(
            """
            "trust-profile": {
              "hosting-workspaces-client-instances": ["."],
              "network-access-policy": "local-network",
              "default-execution-target": { "type": "user-computer-profile", "entity-id": "11111111-1111-1111-1111-111111111111" }
            }
            """);
        var provider = new RecordingTrustProfileProvider();

        var resolved = await AgentTrustProfileResolver.ResolveAsync(definition, provider);

        Assert.NotNull(resolved);
        Assert.True(resolved!.AllowsLocalExecution());
        Assert.Equal(TrustNetworkAccessPolicy.LocalNetwork, resolved.NetworkAccessPolicy);
        Assert.Equal("user-computer-profile", resolved.DefaultExecutionTarget?.GetProperty("type").GetString());
        Assert.Empty(provider.RequestedProfileNames);
    }

    private static AgentDefinition CreateAgentDefinition(string? metadataJson)
    {
        var metadata = metadataJson is null
            ? string.Empty
            : $$"""
                ,
                  "metadata": { {{metadataJson}} }
                """;

        return AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "trust-test-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []{{metadata}}
            }
            """);
    }

    private sealed class RecordingTrustProfileProvider : ITrustProfileProvider
    {
        public List<string> RequestedProfileNames { get; } = [];

        public ValueTask<TrustProfile> ResolveAsync(string profileName, CancellationToken cancellationToken = default)
        {
            this.RequestedProfileNames.Add(profileName);
            return ValueTask.FromResult(new TrustProfile
            {
                HostingWorkspacesClientInstances = ["."],
            });
        }
    }
}
