using System.Text.Json;
using AgentSchema;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Interfaces;

namespace Phantom.Workspaces.Services;

public interface IAgentDefinitionResolver
{
    Task<ResolvedAgentDefinition?> ResolveAsync(AgentDefinitionResolveRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentDefinitionResolveRequest
{
    public AgentDefinition? AgentDefinition { get; init; }
    public AgentManifest? AgentManifest { get; init; }
    public JsonElement? AgentSessionEntity { get; init; }
    public IToolResourceFactory? ToolResourceFactory { get; init; }
    public IReadOnlyDictionary<string, string>? Parameters { get; init; }
}

public sealed record ResolvedAgentDefinition(AgentDefinition Definition, EntityName? AgentDefinitionReference = null);

public sealed class AgentDefinitionResolver : IAgentDefinitionResolver
{
    private readonly IDataAccessLayer dataAccessLayer;

    public AgentDefinitionResolver(IDataAccessLayer dataAccessLayer)
    {
        this.dataAccessLayer = dataAccessLayer;
    }

    public async Task<ResolvedAgentDefinition?> ResolveAsync(
        AgentDefinitionResolveRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.AgentDefinition is not null)
        {
            return new ResolvedAgentDefinition(request.AgentDefinition);
        }

        if (request.AgentManifest is not null)
        {
            return new ResolvedAgentDefinition(await ProjectManifestAsync(request.AgentManifest, request, cancellationToken));
        }

        if (request.AgentSessionEntity is not JsonElement entityData)
        {
            return null;
        }

        if (entityData.TryGetProperty("definition", out var inlineDefinition))
        {
            return new ResolvedAgentDefinition(AgentDefinition.FromJson(inlineDefinition.GetRawText()));
        }

        if (entityData.TryGetProperty("manifest", out var inlineManifest))
        {
            var manifest = AgentManifestLoader.LoadManifestFromJson(inlineManifest.GetRawText());
            return new ResolvedAgentDefinition(await ProjectManifestAsync(manifest, request, cancellationToken));
        }

        if (entityData.TryGetProperty("agent-definition-reference", out var referenceElement))
        {
            var reference = referenceElement.TryReadEntityName()
                ?? throw new InvalidOperationException("Agent session agent-definition-reference is not a valid entity name.");
            var referencedEntity = await GetReferencedEntityAsync(reference, cancellationToken);
            if (referencedEntity?.Data is not JsonElement referencedData)
            {
                throw new InvalidOperationException($"Agent definition reference '{string.Join("/", reference.Components)}' could not be found.");
            }

            return await ResolveReferencedEntityDataAsync(referencedData, request, reference, cancellationToken);
        }

        if (TryReadDefinitionEntityId(entityData, out var definitionEntityId))
        {
            var referencedEntity = await GetReferencedEntityAsync(definitionEntityId, cancellationToken);
            if (referencedEntity?.Data is not JsonElement referencedData)
            {
                throw new InvalidOperationException($"Agent definition entity '{definitionEntityId.Value}' could not be found.");
            }

            return await ResolveReferencedEntityDataAsync(referencedData, request, null, cancellationToken);
        }

        return null;
    }

    private async Task<ResolvedAgentDefinition> ResolveReferencedEntityDataAsync(
        JsonElement entityData,
        AgentDefinitionResolveRequest request,
        EntityName? reference,
        CancellationToken cancellationToken)
    {
        if (entityData.TryGetProperty("definition", out var definitionElement))
        {
            return new ResolvedAgentDefinition(AgentDefinition.FromJson(definitionElement.GetRawText()), reference);
        }

        if (entityData.TryGetProperty("manifest", out var manifestElement))
        {
            var manifest = AgentManifestLoader.LoadManifestFromJson(manifestElement.GetRawText());
            return new ResolvedAgentDefinition(await ProjectManifestAsync(manifest, request, cancellationToken), reference);
        }

        throw new InvalidOperationException("Referenced agent definition entity does not contain a definition or manifest.");
    }

    private async Task<EntitySnapshot?> GetReferencedEntityAsync(EntityName entityName, CancellationToken cancellationToken)
    {
        var result = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityName = entityName }],
            },
            cancellationToken);
        return result.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault(static entity => entity.Data is not null);
    }

    private async Task<EntitySnapshot?> GetReferencedEntityAsync(EntityId entityId, CancellationToken cancellationToken)
    {
        var result = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            cancellationToken);
        return result.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault(static entity => entity.Data is not null);
    }

    private static bool TryReadDefinitionEntityId(JsonElement entityData, out EntityId entityId)
    {
        entityId = default;
        if ((!entityData.TryGetProperty("agent-source-entity-id", out var idElement)
                && !entityData.TryGetProperty("agent-definition-entity-id", out idElement))
            || idElement.ValueKind != JsonValueKind.String
            || !Guid.TryParse(idElement.GetString(), out var guid))
        {
            return false;
        }

        entityId = new EntityId(guid);
        return true;
    }

    private static Task<AgentDefinition> ProjectManifestAsync(
        AgentManifest manifest,
        AgentDefinitionResolveRequest request,
        CancellationToken cancellationToken)
    {
        return AgentFactory.CreateAgentDefinitionAsync(
            new CreateAgentDefinitionRequest
            {
                AgentManifest = manifest,
                Parameters = request.Parameters,
                ToolResourceFactory = request.ToolResourceFactory,
            },
            cancellationToken);
    }
}
