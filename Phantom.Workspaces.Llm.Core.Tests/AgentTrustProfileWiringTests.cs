using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class AgentTrustProfileWiringTests
{
    private const string LocalOnlyProfileName = "local-only";
    private const string RemoteOnlyProfileName = "remote-only";

    private static DictionaryTrustProfileProvider CreateProvider()
    {
        var entities = new Dictionary<string, TrustProfileEntity>(StringComparer.Ordinal)
        {
            [LocalOnlyProfileName] = new TrustProfileEntity
            {
                Name = LocalOnlyProfileName,
                Definition = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["."] },
            },
            [RemoteOnlyProfileName] = new TrustProfileEntity
            {
                Name = RemoteOnlyProfileName,
                Definition = new TrustProfileDefinition { HostingWorkspacesClientInstances = ["remote-a"] },
            },
        };

        return new DictionaryTrustProfileProvider(entities);
    }

    private static AgentDefinition CreateEchoAgent(string? trustProfileName)
    {
        var trustMetadata = trustProfileName is null
            ? string.Empty
            : $$"""
                ,
                  "metadata": { "trust-profile": "{{trustProfileName}}" }
                """;

        return AgentDefinitionLoader.LoadAgentFromJson(
            $$"""
            {
              "kind": "prompt",
              "name": "echo-agent",
              "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
              "tools": []{{trustMetadata}}
            }
            """);
    }

    [Fact]
    public async Task Resolve_NoTrustProfileMetadata_ReturnsNull()
    {
        var agent = CreateEchoAgent(trustProfileName: null);

        var resolved = await AgentTrustProfileResolver.ResolveAsync(agent, CreateProvider());

        Assert.Null(resolved);
    }

    [Fact]
    public async Task Resolve_ReferencedProfile_ResolvesComposedProfile()
    {
        var agent = CreateEchoAgent(LocalOnlyProfileName);

        var resolved = await AgentTrustProfileResolver.ResolveAsync(agent, CreateProvider());

        Assert.NotNull(resolved);
        Assert.True(resolved!.AllowsLocalExecution());
    }

    [Fact]
    public async Task CreateAgentChat_LocalPermittedProfile_Succeeds()
    {
        var agent = CreateEchoAgent(LocalOnlyProfileName);

        await using var chat = await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
        {
            AgentDefinition = agent,
            TrustProfileProvider = CreateProvider(),
        });

        Assert.NotNull(chat);
    }

    [Fact]
    public async Task CreateAgentChat_RemoteOnlyProfile_ThrowsForLocalExecution()
    {
        var agent = CreateEchoAgent(RemoteOnlyProfileName);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await AgentFactory.CreateAgentChatAsync(new CreateAgentChatRequest
            {
                AgentDefinition = agent,
                TrustProfileProvider = CreateProvider(),
            }));

        Assert.Contains("does not permit local execution", exception.Message, StringComparison.Ordinal);
    }
}
