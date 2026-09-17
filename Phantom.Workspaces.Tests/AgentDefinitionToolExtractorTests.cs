using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Testing;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDefinitionToolExtractorTests
{
    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    [Fact]
    public async Task InlineDefinition_PassesThroughUnchanged()
    {
        var dispatcher = Parse("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "foo",
              "description": "The foo sub-agent",
              "definition": {
                "kind": "prompt",
                "name": "foo-def",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
              }
            }
          ]
        }
        """);
        var resolver = await CreateResolverAsync();

        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("foo", tool.Name);
        Assert.Equal("The foo sub-agent", tool.Description);
        var promptAgent = Assert.IsType<PromptAgent>(tool.Definition);
        Assert.Equal("foo-def", promptAgent.Name);
    }

    [Fact]
    public async Task ManifestReference_ResolvesToReferencedDefinition()
    {
        var manifestEntity = Parse("""
        {
          "entity-id": "10000000-0000-4000-8000-000000000007",
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
        """);
        var dispatcher = Parse("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" },
          "tools": [
            {
              "kind": "agent-definition",
              "name": "bar",
              "description": "The bar sub-agent",
              "manifest-reference": ["tests", "agent-manifests", "github-copilot"]
            }
          ]
        }
        """);
        var resolver = await CreateResolverAsync(manifestEntity);

        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken);

        var tool = Assert.Single(tools);
        Assert.Equal("bar", tool.Name);
        Assert.Equal("The bar sub-agent", tool.Description);
        var promptAgent = Assert.IsType<PromptAgent>(tool.Definition);
        Assert.Equal("github-copilot", promptAgent.Name);
    }

    [Fact]
    public async Task InlineAndReferenceEntries_AreBothResolved_PreservingOrder()
    {
        var manifestEntity = Parse("""
        {
          "entity-id": "10000000-0000-4000-8000-000000000008",
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
        """);
        var dispatcher = Parse("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" },
          "tools": [
            { "kind": "mcp", "name": "some-mcp-tool" },
            {
              "kind": "agent-definition",
              "name": "foo",
              "description": "The foo sub-agent",
              "definition": {
                "kind": "prompt",
                "name": "foo-def",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" }
              }
            },
            {
              "kind": "agent-definition",
              "name": "bar",
              "description": "The bar sub-agent",
              "manifest-reference": ["tests", "agent-manifests", "github-copilot"]
            }
          ]
        }
        """);
        var resolver = await CreateResolverAsync(manifestEntity);

        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(2, tools.Count);
        Assert.Equal("foo", tools[0].Name);
        Assert.Equal("foo-def", Assert.IsType<PromptAgent>(tools[0].Definition).Name);
        Assert.Equal("bar", tools[1].Name);
        Assert.Equal("github-copilot", Assert.IsType<PromptAgent>(tools[1].Definition).Name);
    }

    [Fact]
    public async Task NoToolsArray_ReturnsEmpty()
    {
        var dispatcher = Parse("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" }
        }
        """);
        var resolver = await CreateResolverAsync();

        var tools = await AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
            dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Empty(tools);
    }

    [Fact]
    public async Task EntryWithNeitherDefinitionNorReference_Throws()
    {
        var dispatcher = Parse("""
        {
          "kind": "prompt",
          "name": "dispatcher",
          "model": { "id": "sub-agent-dispatcher", "provider": "sub-agent-dispatcher" },
          "tools": [
            { "kind": "agent-definition", "name": "broken", "description": "Missing both" }
          ]
        }
        """);
        var resolver = await CreateResolverAsync();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
                dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("'broken'", ex.Message, StringComparison.Ordinal);
    }

    private static async Task<AgentDefinitionResolver> CreateResolverAsync(
        JsonElement? entity = null)
    {
        var fixture = await ValidatingEntitySeedFixture.CreateAsync();
        if (entity is { } value)
        {
            await fixture.SeedValidEntityAsync(value);
        }

        return new AgentDefinitionResolver(fixture.DataAccessLayer);
    }
}
