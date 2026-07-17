using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;

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
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer());

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
        var reference = new EntityName("defaults", "agent-manifests", "github-copilot");
        var manifestEntity = Parse("""
        {
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
              "manifest-reference": ["defaults", "agent-manifests", "github-copilot"]
            }
          ]
        }
        """);
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer(
            byName: new Dictionary<EntityName, JsonElement> { [reference] = manifestEntity }));

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
        var reference = new EntityName("defaults", "agent-manifests", "github-copilot");
        var manifestEntity = Parse("""
        {
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
              "manifest-reference": ["defaults", "agent-manifests", "github-copilot"]
            }
          ]
        }
        """);
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer(
            byName: new Dictionary<EntityName, JsonElement> { [reference] = manifestEntity }));

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
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer());

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
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AgentDefinitionToolExtractor.ExtractAgentDefinitionToolsAsync(
                dispatcher, resolver, cancellationToken: TestContext.Current.CancellationToken));
        Assert.Contains("'broken'", ex.Message, StringComparison.Ordinal);
    }

    private sealed class FakeDataAccessLayer : IDataAccessLayer
    {
        private readonly IReadOnlyDictionary<EntityName, JsonElement> byName;

        public FakeDataAccessLayer(IReadOnlyDictionary<EntityName, JsonElement>? byName = null)
        {
            this.byName = byName ?? new Dictionary<EntityName, JsonElement>();
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            var entities = new List<EntitySnapshot>();
            foreach (var entityRequest in request.Entities)
            {
                if (entityRequest.EntityName is EntityName name && this.byName.TryGetValue(name, out var nameData))
                {
                    entities.Add(new EntitySnapshot
                    {
                        EntityId = new EntityId(),
                        ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "test"),
                        Data = nameData,
                        Relationships = [],
                    });
                }
            }

            return Task.FromResult(new GetResult
            {
                Batches = [new TimestampedEntityBatch { Entities = entities }],
            });
        }

        public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
