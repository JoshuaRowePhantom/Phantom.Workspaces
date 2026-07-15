using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Services;

namespace Phantom.Workspaces.Tests;

public sealed class AgentDefinitionResolverTests
{
    [Fact]
    public async Task ResolveAsync_AgentDefinitionReference_LoadsReferencedManifest()
    {
        var reference = new EntityName("defaults", "agent-manifests", "github-copilot");
        using var sessionDoc = JsonDocument.Parse(
            """
            {
              "agent-definition-reference": ["defaults", "agent-manifests", "github-copilot"],
              "agent-session-id": "bea98bb4-4129-4815-861f-3927fe511315"
            }
            """);
        using var manifestDoc = JsonDocument.Parse(
            """
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
        var dataAccessLayer = new FakeDataAccessLayer(
            byName: new Dictionary<EntityName, JsonElement> { [reference] = manifestDoc.RootElement.Clone() });
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
        var resolver = new AgentDefinitionResolver(new FakeDataAccessLayer());

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
        using var definitionDoc = JsonDocument.Parse(
            """
            {
              "definition": {
                "kind": "prompt",
                "name": "source-definition",
                "model": { "id": "echo", "provider": "echo", "apiType": "Echo" },
                "tools": []
              }
            }
            """);
        var dataAccessLayer = new FakeDataAccessLayer(
            byId: new Dictionary<EntityId, JsonElement> { [definitionId] = definitionDoc.RootElement.Clone() });
        var resolver = new AgentDefinitionResolver(dataAccessLayer);

        var resolved = await resolver.ResolveAsync(new AgentDefinitionResolveRequest
        {
            AgentSessionEntity = sessionDoc.RootElement.Clone(),
        }, TestContext.Current.CancellationToken);

        var promptAgent = Assert.IsType<PromptAgent>(resolved?.Definition);
        Assert.Equal("source-definition", promptAgent.Name);
    }

    private sealed class FakeDataAccessLayer : IDataAccessLayer
    {
        private readonly IReadOnlyDictionary<EntityName, JsonElement> byName;
        private readonly IReadOnlyDictionary<EntityId, JsonElement> byId;

        public FakeDataAccessLayer(
            IReadOnlyDictionary<EntityName, JsonElement>? byName = null,
            IReadOnlyDictionary<EntityId, JsonElement>? byId = null)
        {
            this.byName = byName ?? new Dictionary<EntityName, JsonElement>();
            this.byId = byId ?? new Dictionary<EntityId, JsonElement>();
        }

        public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        {
            var entities = new List<EntitySnapshot>();
            foreach (var entityRequest in request.Entities)
            {
                if (entityRequest.EntityName is EntityName name && this.byName.TryGetValue(name, out var nameData))
                {
                    entities.Add(CreateSnapshot(nameData));
                }
                else if (entityRequest.EntityId is EntityId id && this.byId.TryGetValue(id, out var idData))
                {
                    entities.Add(CreateSnapshot(idData, id));
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

        private static EntitySnapshot CreateSnapshot(JsonElement data, EntityId? entityId = null)
            => new()
            {
                EntityId = entityId ?? new EntityId(),
                ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "test"),
                Data = data,
                Relationships = [],
            };
    }
}
