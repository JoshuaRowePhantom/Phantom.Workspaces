using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDefinitionResolverTests
{
    [Fact]
    public async Task ResolveAsync_AgentDefinitionReference_LoadsReferencedManifest()
    {
        var reference = new EntityName("tests", "agent-manifests", "github-copilot");
        using var sessionDoc = JsonDocument.Parse(
            """
            {
              "agent-definition-reference": ["tests", "agent-manifests", "github-copilot"],
              "agent-session-id": "bea98bb4-4129-4815-861f-3927fe511315"
            }
            """);
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(TestContext.Current.CancellationToken);
        await fixture.SeedValidEntityAsync(
            Parse(
                """
                {
                  "entity-id": "10000000-0000-4000-8000-000000000006",
                  "entity-types": ["entity", "agent-manifest"],
                  "names": [["tests", "agent-manifests", "github-copilot"]],
                  "manifest": {
                    "name": "github-copilot",
                    "displayName": "GitHub Copilot",
                    "template": {
                      "kind": "prompt",
                      "name": "github-copilot",
                      "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
                    }
                  }
                }
                """),
            TestContext.Current.CancellationToken);
        var dataAccessLayer = fixture.DataAccessLayer;
        var resolver = new AgentDefinitionResolver(dataAccessLayer);

        var resolved = await resolver.ResolveAsync(new AgentDefinitionResolveRequest
        {
            AgentSessionEntity = sessionDoc.RootElement.Clone(),
        }, TestContext.Current.CancellationToken);

        var promptAgent = Assert.IsType<PromptAgent>(resolved?.Definition);
        Assert.Equal("github-copilot", promptAgent.Name);
        Assert.Equal(reference, resolved.AgentDefinitionReference);
    }

    [Fact]
    public async Task ResolveAsync_AgentDefinitionReferenceMissing_ThrowsSpecificError()
    {
        using var sessionDoc = JsonDocument.Parse(
            """
            {
              "agent-definition-reference": ["missing", "agent"],
              "agent-session-id": "bea98bb4-4129-4815-861f-3927fe511315"
            }
            """);
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(TestContext.Current.CancellationToken);
        var resolver = new AgentDefinitionResolver(fixture.DataAccessLayer);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => resolver.ResolveAsync(new AgentDefinitionResolveRequest
        {
            AgentSessionEntity = sessionDoc.RootElement.Clone(),
        }, TestContext.Current.CancellationToken));

        Assert.Contains("Agent definition reference 'missing/agent' could not be found", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResolveAsync_AgentSourceEntityId_LoadsReferencedDefinition()
    {
        var definitionId = new EntityId("11111111-1111-4111-8111-111111111111");
        using var sessionDoc = JsonDocument.Parse(
            $$"""
            {
              "agent-source-entity-id": "{{definitionId.Value}}",
              "agent-session-id": "bea98bb4-4129-4815-861f-3927fe511315"
            }
            """);
        var fixture = await ValidatingEntitySeedFixture.CreateAsync(TestContext.Current.CancellationToken);
        await fixture.SeedValidEntityAsync(
            Parse(
                $$"""
                {
                  "entity-id": "{{definitionId}}",
                  "entity-types": ["entity", "agent-definition"],
                  "definition": {
                    "kind": "prompt",
                    "name": "source-definition",
                    "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                    "tools": []
                  }
                }
                """),
            TestContext.Current.CancellationToken);
        var dataAccessLayer = fixture.DataAccessLayer;
        var resolver = new AgentDefinitionResolver(dataAccessLayer);

        var resolved = await resolver.ResolveAsync(new AgentDefinitionResolveRequest
        {
            AgentSessionEntity = sessionDoc.RootElement.Clone(),
        }, TestContext.Current.CancellationToken);

        var promptAgent = Assert.IsType<PromptAgent>(resolved?.Definition);
        Assert.Equal("source-definition", promptAgent.Name);
    }

    private static JsonElement Parse(string json)
        => JsonDocument.Parse(json).RootElement.Clone();
}
