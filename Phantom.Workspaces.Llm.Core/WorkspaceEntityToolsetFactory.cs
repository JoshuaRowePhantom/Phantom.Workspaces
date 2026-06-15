using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Serialization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Phantom.Workspaces.Llm;

public sealed class WorkspaceEntityContextProvider : AIContextProvider
{
    private static readonly EntityName WorkspaceEntityToolInstructionsEntityName =
        new("documentation", "entity-workspace-agent-tool-instructions");

    private readonly string stateKey = $"workspace-entity:{Guid.NewGuid():n}";
    private readonly IDataAccessLayer dataAccessLayer;

    public WorkspaceEntityContextProvider(IDataAccessLayer dataAccessLayer)
        : base(null, null, null)
    {
        this.dataAccessLayer = dataAccessLayer;
    }

    public override IReadOnlyList<string> StateKeys => [this.stateKey];

    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken)
    {
        _ = context;
        var instructions = await this.GetWorkspaceEntityToolInstructionsAsync(cancellationToken);
        return new AIContext
        {
            Instructions = instructions,
            Tools =
            [
                new WorkspacesEntityGetTool(this.dataAccessLayer),
                new WorkspacesEntityUpdateTool(this.dataAccessLayer),
                new WorkspacesEntityGenerateGuidTool(),
            ],
        };
    }

    private async Task<string?> GetWorkspaceEntityToolInstructionsAsync(CancellationToken cancellationToken)
    {
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityName = WorkspaceEntityToolInstructionsEntityName,
                    },
                ],
            },
            cancellationToken);

        var instructionsEntity = getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
        return TryReadDefaultMarkdownText(instructionsEntity?.Data);
    }

    private static string? TryReadDefaultMarkdownText(JsonElement? entityData)
    {
        if (entityData is not JsonElement entityDataElement
            || NoteEntityDocument.Deserialize(entityDataElement) is not NoteEntityDocument noteEntityDocument)
        {
            return null;
        }

        return noteEntityDocument.GetPreferredMarkdownText();
    }

    private sealed class WorkspacesEntityGetTool : AIFunction
    {
        private static readonly JsonElement InputSchema = WorkspaceEntityToolSchemas.Denormalize(
            "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace-entities-data-access-layer.json#/$defs/get-request");

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspacesEntityGetTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspaces_entity_get";

        public override string Description =>
            "Execute a workspace GetRequest. Supports entity-id, entity-name, entity-type-names, relationships, timestamps, and property filtering.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!TryParseGetRequest(arguments, out var getRequest, out var parseError))
            {
                return parseError;
            }

            var getResult = await this.dataAccessLayer.GetAsync(getRequest, cancellationToken);
            var requestedProperties = ResolveRequestedProperties(getRequest);
            return SerializeToJsonElement(
                new
                {
                    batches = getResult.Batches.Select(batch => new
                    {
                        timestamp = batch.Timestamp is null
                            ? null
                            : new
                            {
                                datetime = batch.Timestamp.Value.DateTime,
                                changeId = batch.Timestamp.Value.ChangeId,
                            },
                        entities = batch.Entities.Select(entity => ToSerializableEntity(entity, requestedProperties)),
                    }),
                });
        }
    }

    private sealed class WorkspacesEntityUpdateTool : AIFunction
    {
        private static readonly JsonElement InputSchema = WorkspaceEntityToolSchemas.Denormalize(
            "https://schemas.workspaces.phantom.to/workspaces/data/core/workspace-entities-data-access-layer.json#/$defs/update-request");

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspacesEntityUpdateTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspaces_entity_update";

        public override string Description =>
            "Execute a workspace UpdateRequest. Use this single tool for add, replace, and delete changes.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!TryParseUpdateRequest(arguments, out var updateRequest, out var parseError))
            {
                return parseError;
            }

            var updateResult = await this.dataAccessLayer.UpdateAsync(updateRequest, cancellationToken);
            return SerializeToJsonElement(ToSerializableUpdateResult(updateResult));
        }
    }

    private sealed class WorkspacesEntityGenerateGuidTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """).RootElement.Clone();

        public override string Name => "workspaces_entity_generate_guid";

        public override string Description =>
            "Generate a GUID for explicit entity-id assignment when multiple entities must be linked in one update.";

        public override JsonElement JsonSchema => InputSchema;

        protected override ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            _ = arguments;
            _ = cancellationToken;
            var entityId = Guid.NewGuid().ToString("D");
            return ValueTask.FromResult<object?>(SerializeToJsonElement(new { entityId }));
        }
    }

    private static object ToSerializableUpdateResult(UpdateResult updateResult)
    {
        return new
        {
            entityResults = updateResult.EntityResults.Select(static result => new
            {
                updateState = result.UpdateState.ToString(),
                requestedEntityId = result.RequestedEntityId.Value,
                resultingEntityId = result.ResultingEntityId.Value,
                concurrencyTag = result.ConcurrencyTag?.Value,
                concurrencyMatchState = result.ConcurrencyMatchState.ToString(),
                currentEntity = ToSerializableEntity(result.CurrentEntity, properties: null),
                errors = result.Errors.Select(static error => new
                {
                    message = error.Message,
                    relatedEntityId = error.RelatedEntityId?.Value,
                }),
            }),
        };
    }

    private static object? ToSerializableEntity(
        EntitySnapshot? entity,
        IReadOnlyCollection<string>? properties)
    {
        if (entity is null)
        {
            return null;
        }

        return new
        {
            entityId = entity.EntityId.Value,
            concurrencyTag = entity.ConcurrencyTag?.Value,
            modifiedTime = entity.ModifiedTime.DateTime,
            changeId = entity.ModifiedTime.ChangeId,
            data = FilterData(entity.Data, properties),
            relationships = entity.Relationships.Select(relationship => ToSerializableEntity(relationship, properties)).ToArray(),
        };
    }

    private static IReadOnlyCollection<string>? ResolveRequestedProperties(GetRequest getRequest)
    {
        var requestProperties = getRequest.Properties?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        if (requestProperties is { Length: > 0 })
        {
            return requestProperties;
        }

        if (getRequest.Entities.Count == 1)
        {
            return getRequest.Entities.First().Properties?.Where(static value => !string.IsNullOrWhiteSpace(value)).ToArray();
        }

        return null;
    }

    private static JsonElement? FilterData(JsonElement? data, IReadOnlyCollection<string>? properties)
    {
        if (data is not JsonElement dataElement || properties is null || properties.Count == 0)
        {
            return data;
        }

        var filteredData = new JsonObject();
        foreach (var propertyPath in properties)
        {
            if (string.IsNullOrWhiteSpace(propertyPath))
            {
                continue;
            }

            var pathComponents = propertyPath
                .Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (pathComponents.Length == 0 || !TryReadPathValue(dataElement, pathComponents, out var pathValue))
            {
                continue;
            }

            SetPathValue(filteredData, pathComponents, pathValue);
        }

        using var document = JsonDocument.Parse(filteredData.ToJsonString());
        return document.RootElement.Clone();
    }

    private static bool TryReadPathValue(
        JsonElement source,
        IReadOnlyList<string> pathComponents,
        out JsonElement pathValue)
    {
        pathValue = source;
        foreach (var pathComponent in pathComponents)
        {
            if (pathValue.ValueKind != JsonValueKind.Object
                || !pathValue.TryGetProperty(pathComponent, out var nextValue))
            {
                pathValue = default;
                return false;
            }

            pathValue = nextValue;
        }

        return true;
    }

    private static void SetPathValue(
        JsonObject destination,
        IReadOnlyList<string> pathComponents,
        JsonElement value)
    {
        JsonObject currentObject = destination;
        for (var pathIndex = 0; pathIndex < pathComponents.Count - 1; pathIndex++)
        {
            var pathComponent = pathComponents[pathIndex];
            if (currentObject[pathComponent] is not JsonObject childObject)
            {
                childObject = new JsonObject();
                currentObject[pathComponent] = childObject;
            }

            currentObject = childObject;
        }

        currentObject[pathComponents[^1]] = JsonNode.Parse(value.GetRawText());
    }

    private static bool TryParseGetRequest(
        IReadOnlyDictionary<string, object?> arguments,
        out GetRequest getRequest,
        out string error)
    {
        getRequest = default!;
        error = "workspaces_entity_get requires a valid GetRequest payload.";
        if (!TryParseArgumentsAsJson(arguments, out var argumentsJson))
        {
            return false;
        }

        if (argumentsJson.ValueKind != JsonValueKind.Object
            || !argumentsJson.TryGetProperty("get-entity", out var getEntityElement)
            || getEntityElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var entities = new List<GetEntityRequest>();
        foreach (var entityElement in getEntityElement.EnumerateArray())
        {
            if (entityElement.ValueKind != JsonValueKind.Object
                || !TryParseGetEntityRequest(entityElement, out var getEntityRequest))
            {
                return false;
            }

            entities.Add(getEntityRequest);
        }

        if (entities.Count == 0)
        {
            return false;
        }

        getRequest = new GetRequest
        {
            Entities = entities,
            RelationshipsToReturn = TryParseRelationshipRequests(argumentsJson, "relationships-to-return"),
            Timestamps = TryParseTimestamps(argumentsJson),
            Properties = TryParseStringCollection(argumentsJson, "properties"),
        };
        return true;
    }

    private static bool TryParseUpdateRequest(
        IReadOnlyDictionary<string, object?> arguments,
        out UpdateRequest updateRequest,
        out string error)
    {
        updateRequest = default!;
        error = "workspaces_entity_update requires a valid UpdateRequest payload.";
        if (!TryParseArgumentsAsJson(arguments, out var argumentsJson))
        {
            return false;
        }

        if (argumentsJson.ValueKind != JsonValueKind.Object
            || !argumentsJson.TryGetProperty("update-metadata", out var updateMetadataElement)
            || updateMetadataElement.ValueKind != JsonValueKind.Object
            || !updateMetadataElement.TryGetProperty("comment", out var commentElement)
            || commentElement.ValueKind != JsonValueKind.Object
            || !commentElement.TryGetProperty("text", out var commentTextElement)
            || commentTextElement.ValueKind != JsonValueKind.String
            || !argumentsJson.TryGetProperty("changes", out var changesElement)
            || changesElement.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var changes = new List<EntityChange>();
        foreach (var changeElement in changesElement.EnumerateArray())
        {
            if (changeElement.ValueKind != JsonValueKind.Object
                || !TryParseEntityChange(changeElement, out var entityChange))
            {
                return false;
            }

            changes.Add(entityChange);
        }

        if (changes.Count == 0)
        {
            return false;
        }

        updateRequest = new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata
            {
                Comment = new Markdown
                {
                    Text = commentTextElement.GetString()!,
                },
            },
            Changes = changes,
        };
        return true;
    }

    private static bool TryParseGetEntityRequest(JsonElement entityElement, out GetEntityRequest request)
    {
        request = new GetEntityRequest
        {
            EntityId = TryParseEntityId(entityElement, "entity-id"),
            EntityName = TryParseEntityName(entityElement, "entity-name"),
            EnumerateChildren = TryParseEnumerateChildren(entityElement, "enumerate-children") ?? EnumerateChildrenAction.EnumerateSelf,
            EntityTypeNames = TryParseEntityTypeNames(entityElement, "entity-type-names"),
            Properties = TryParseStringCollection(entityElement, "properties"),
            RelationshipsToReturn = TryParseRelationshipRequests(entityElement, "relationships-to-return"),
        };

        return request.EntityId is not null
            || request.EntityName is not null
            || request.EntityTypeNames is not null;
    }

    private static bool TryParseEntityChange(JsonElement changeElement, out EntityChange entityChange)
    {
        entityChange = default;
        if (!TryParseEntityChangeMode(changeElement, out var entityChangeMode))
        {
            return false;
        }

        entityChange = new EntityChange
        {
            EntityId = TryParseEntityId(changeElement, "entity-id"),
            ConcurrencyTag = TryParseConcurrencyTag(changeElement, "concurrency-tag"),
            Data = changeElement.TryGetProperty("data", out var dataElement)
                && dataElement.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
                ? dataElement.Clone()
                : null,
            EntityChangeMode = entityChangeMode,
        };

        return true;
    }

    private static EntityId? TryParseEntityId(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue)
            || propertyValue.ValueKind != JsonValueKind.String
            || !Guid.TryParse(propertyValue.GetString(), out var entityGuid))
        {
            return null;
        }

        return new EntityId(entityGuid);
    }

    private static EntityName? TryParseEntityName(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.TryReadEntityName();
    }

    private static EntityTypeNameSet? TryParseEntityTypeNames(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue))
        {
            return null;
        }

        return propertyValue.TryReadEntityTypeNames();
    }

    private static ConcurrencyTag? TryParseConcurrencyTag(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue)
            || propertyValue.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(propertyValue.GetString()))
        {
            return null;
        }

        return new ConcurrencyTag(propertyValue.GetString()!);
    }

    private static EnumerateChildrenAction? TryParseEnumerateChildren(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue)
            || propertyValue.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return propertyValue.GetString() switch
        {
            "self" => EnumerateChildrenAction.EnumerateSelf,
            "children" => EnumerateChildrenAction.EnumerateChildren,
            "all-children" => EnumerateChildrenAction.EnumerateAllChildren,
            _ => null,
        };
    }

    private static bool TryParseEntityChangeMode(JsonElement jsonElement, out EntityChangeMode entityChangeMode)
    {
        entityChangeMode = default;
        if (!jsonElement.TryGetProperty("entity-change-mode", out var entityChangeModeElement)
            || entityChangeModeElement.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var entityChangeModeText = entityChangeModeElement.GetString();
        entityChangeMode = entityChangeModeText switch
        {
            "replace" => EntityChangeMode.Replace,
            "json-patch" => EntityChangeMode.JsonPatch,
            _ => default,
        };

        return entityChangeModeText switch
        {
            "replace" or "json-patch" => true,
            _ => false,
        };
    }

    private static IReadOnlyCollection<GetRelationshipRequest>? TryParseRelationshipRequests(
        JsonElement jsonElement,
        string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue)
            || propertyValue.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var relationships = new List<GetRelationshipRequest>();
        foreach (var relationshipElement in propertyValue.EnumerateArray())
        {
            if (relationshipElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            relationships.Add(new GetRelationshipRequest
            {
                RelationshipTypeNames = relationshipElement.TryGetProperty("relationship-type-names", out var relationshipTypeNamesElement)
                    ? relationshipTypeNamesElement.TryReadRelationshipTypeNames()
                    : null,
                RelationshipRoleNames = relationshipElement.TryGetProperty("relationship-role-names", out var relationshipRoleNamesElement)
                    ? relationshipRoleNamesElement.TryReadRoleNames()
                    : null,
            });
        }

        return relationships;
    }

    private static IReadOnlyCollection<Timestamp?>? TryParseTimestamps(JsonElement jsonElement)
    {
        if (!jsonElement.TryGetProperty("timestamps", out var timestampsElement)
            || timestampsElement.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var timestamps = new List<Timestamp?>();
        foreach (var timestampElement in timestampsElement.EnumerateArray())
        {
            if (timestampElement.ValueKind == JsonValueKind.Null)
            {
                timestamps.Add(null);
                continue;
            }

            if (timestampElement.ValueKind != JsonValueKind.Object
                || !timestampElement.TryGetProperty("datetime", out var datetimeElement)
                || datetimeElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(datetimeElement.GetString(), out var dateTimeOffset)
                || !timestampElement.TryGetProperty("changeId", out var changeIdElement)
                || changeIdElement.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            timestamps.Add(new Timestamp(dateTimeOffset, changeIdElement.GetString()!));
        }

        return timestamps;
    }

    private static IReadOnlyCollection<string>? TryParseStringCollection(JsonElement jsonElement, string propertyName)
    {
        if (!jsonElement.TryGetProperty(propertyName, out var propertyValue)
            || propertyValue.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var values = propertyValue
            .EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.String)
            .Select(static item => item.GetString())
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
        return values.Length == 0 ? [] : values;
    }

    private static bool TryParseArgumentsAsJson(
        IReadOnlyDictionary<string, object?> arguments,
        out JsonElement argumentsJson)
    {
        argumentsJson = default;
        try
        {
            var serializedArguments = JsonSerializer.Serialize(arguments);
            using var document = JsonDocument.Parse(serializedArguments);
            argumentsJson = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static JsonElement SerializeToJsonElement(object value)
    {
        return JsonSerializer.SerializeToElement(value);
    }
}
