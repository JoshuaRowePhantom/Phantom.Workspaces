using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Serialization;
using System.Text.Json;

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
                new WorkspaceEntityGetByIdTool(this.dataAccessLayer),
                new WorkspaceEntityGetByNameTool(this.dataAccessLayer),
                new WorkspaceEntityAddTool(this.dataAccessLayer),
                new WorkspaceEntityReplaceTool(this.dataAccessLayer),
                new WorkspaceEntityDeleteTool(this.dataAccessLayer),
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
            || !NoteEntityDocument.TryParse(entityDataElement, out var noteEntityDocument))
        {
            return null;
        }

        return noteEntityDocument.GetPreferredMarkdownText();
    }

    private sealed class WorkspaceEntityGetByIdTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity-id": {
                  "type": "string",
                  "description": "Entity id as GUID."
                }
              },
              "required": [ "entity-id" ],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspaceEntityGetByIdTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspace_entity_get_by_id";

        public override string Description =>
            "Get one entity by id, including current concurrency tag for subsequent updates.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var entityIdValue = ExtractString(arguments, "entity-id");
            if (string.IsNullOrWhiteSpace(entityIdValue) || !Guid.TryParse(entityIdValue, out var entityGuid))
            {
                return new TextContent("workspace_entity_get_by_id requires a valid 'entity-id' GUID.");
            }

            var getResult = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityId = new EntityId(entityGuid),
                        },
                    ],
                },
                cancellationToken);

            var entity = getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
            return new TextContent(SerializeAsJson(
                new
                {
                    entity = ToSerializableEntity(entity),
                }));
        }
    }

    private sealed class WorkspaceEntityGetByNameTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity-name": {
                  "type": "array",
                  "items": { "type": "string" },
                  "description": "Entity name as ordered string components."
                }
              },
              "required": [ "entity-name" ],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspaceEntityGetByNameTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspace_entity_get_by_name";

        public override string Description =>
            "Get one entity by name, including current concurrency tag for subsequent updates.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!TryExtractEntityName(arguments, "entity-name", out var entityName))
            {
                return new TextContent("workspace_entity_get_by_name requires 'entity-name' as a non-empty string array.");
            }

            var getResult = await this.dataAccessLayer.GetAsync(
                new GetRequest
                {
                    Entities =
                    [
                        new GetEntityRequest
                        {
                            EntityName = entityName,
                        },
                    ],
                },
                cancellationToken);

            var entity = getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
            return new TextContent(SerializeAsJson(
                new
                {
                    entity = ToSerializableEntity(entity),
                }));
        }
    }

    private sealed class WorkspaceEntityAddTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity-id": {
                  "type": "string",
                  "description": "Optional entity id as GUID. If omitted, a new GUID is generated."
                },
                "data": {
                  "type": "object",
                  "description": "Entity payload JSON object."
                },
                "comment": {
                  "type": "string",
                  "description": "Change comment."
                }
              },
              "required": [ "data" ],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspaceEntityAddTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspace_entity_add";

        public override string Description =>
            "Add a new entity. Fails if the entity already exists.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            var hasEntityIdArgument = arguments.TryGetValue("entity-id", out var entityIdArgument)
                && entityIdArgument is not null;
            var hasValidEntityId = TryExtractEntityId(arguments, "entity-id", out var parsedEntityId);
            if (hasEntityIdArgument && !hasValidEntityId)
            {
                return new TextContent("workspace_entity_add requires a valid 'entity-id' GUID.");
            }

            var entityId = hasValidEntityId ? parsedEntityId : new EntityId();
            if (!TryExtractJsonElement(arguments, "data", out var dataElement))
            {
                return new TextContent("workspace_entity_add requires 'data' as a JSON object.");
            }

            var existingEntity = await GetEntityByIdAsync(this.dataAccessLayer, entityId, cancellationToken);
            if (existingEntity is not null)
            {
                return new TextContent("workspace_entity_add failed because the entity already exists. Use workspace_entity_replace with the current concurrency tag.");
            }

            var updateResult = await this.dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = BuildUpdateMetadata(arguments),
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = entityId,
                            Data = dataElement,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                    ],
                },
                cancellationToken);

            return new TextContent(SerializeAsJson(new
            {
                entityId = entityId.Value,
                update = ToSerializableUpdateResult(updateResult),
            }));
        }
    }

    private sealed class WorkspaceEntityReplaceTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity-id": {
                  "type": "string",
                  "description": "Entity id as GUID."
                },
                "concurrency-tag": {
                  "type": "string",
                  "description": "Required current concurrency tag from a prior read."
                },
                "data": {
                  "type": "object",
                  "description": "Entity payload JSON object."
                },
                "comment": {
                  "type": "string",
                  "description": "Change comment."
                }
              },
              "required": [ "entity-id", "concurrency-tag", "data" ],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspaceEntityReplaceTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspace_entity_replace";

        public override string Description =>
            "Replace an existing entity. Requires the current concurrency tag from a previous read.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!TryExtractEntityId(arguments, "entity-id", out var entityId))
            {
                return new TextContent("workspace_entity_replace requires a valid 'entity-id' GUID.");
            }

            var concurrencyTag = ExtractString(arguments, "concurrency-tag");
            if (string.IsNullOrWhiteSpace(concurrencyTag))
            {
                return new TextContent("workspace_entity_replace requires a non-empty 'concurrency-tag'.");
            }

            if (!TryExtractJsonElement(arguments, "data", out var dataElement))
            {
                return new TextContent("workspace_entity_replace requires 'data' as a JSON object.");
            }

            var existingEntity = await GetEntityByIdAsync(this.dataAccessLayer, entityId, cancellationToken);
            if (existingEntity is null)
            {
                return new TextContent("workspace_entity_replace failed because the entity does not exist. Use workspace_entity_add first.");
            }

            var updateResult = await this.dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = BuildUpdateMetadata(arguments),
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = entityId,
                            ConcurrencyTag = new ConcurrencyTag(concurrencyTag),
                            Data = dataElement,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                    ],
                },
                cancellationToken);

            return new TextContent(SerializeAsJson(ToSerializableUpdateResult(updateResult)));
        }
    }

    private sealed class WorkspaceEntityDeleteTool : AIFunction
    {
        private static readonly JsonElement InputSchema = JsonDocument.Parse(
            """
            {
              "type": "object",
              "properties": {
                "entity-id": {
                  "type": "string",
                  "description": "Entity id as GUID."
                },
                "concurrency-tag": {
                  "type": "string",
                  "description": "Required current concurrency tag from a prior read."
                },
                "comment": {
                  "type": "string",
                  "description": "Change comment."
                }
              },
              "required": [ "entity-id", "concurrency-tag" ],
              "additionalProperties": false
            }
            """).RootElement.Clone();

        private readonly IDataAccessLayer dataAccessLayer;

        public WorkspaceEntityDeleteTool(IDataAccessLayer dataAccessLayer)
        {
            this.dataAccessLayer = dataAccessLayer;
        }

        public override string Name => "workspace_entity_delete";

        public override string Description =>
            "Delete an existing entity. Requires the current concurrency tag from a previous read.";

        public override JsonElement JsonSchema => InputSchema;

        protected override async ValueTask<object?> InvokeCoreAsync(
            AIFunctionArguments arguments,
            CancellationToken cancellationToken)
        {
            if (!TryExtractEntityId(arguments, "entity-id", out var entityId))
            {
                return new TextContent("workspace_entity_delete requires a valid 'entity-id' GUID.");
            }

            var concurrencyTag = ExtractString(arguments, "concurrency-tag");
            if (string.IsNullOrWhiteSpace(concurrencyTag))
            {
                return new TextContent("workspace_entity_delete requires a non-empty 'concurrency-tag'.");
            }

            var existingEntity = await GetEntityByIdAsync(this.dataAccessLayer, entityId, cancellationToken);
            if (existingEntity is null)
            {
                return new TextContent("workspace_entity_delete failed because the entity does not exist.");
            }

            var updateResult = await this.dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = BuildUpdateMetadata(arguments),
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = entityId,
                            ConcurrencyTag = new ConcurrencyTag(concurrencyTag),
                            Data = null,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                    ],
                },
                cancellationToken);

            return new TextContent(SerializeAsJson(ToSerializableUpdateResult(updateResult)));
        }
    }

    private static async Task<EntitySnapshot?> GetEntityByIdAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        CancellationToken cancellationToken)
    {
        var getResult = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities =
                [
                    new GetEntityRequest
                    {
                        EntityId = entityId,
                    },
                ],
            },
            cancellationToken);

        return getResult.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
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
                currentEntity = ToSerializableEntity(result.CurrentEntity),
                errors = result.Errors.Select(static error => new
                {
                    message = error.Message,
                    relatedEntityId = error.RelatedEntityId?.Value,
                }),
            }),
        };
    }

    private static object? ToSerializableEntity(EntitySnapshot? entity)
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
            data = entity.Data,
            relationships = entity.Relationships.Select(ToSerializableEntity).ToArray(),
        };
    }

    private static UpdateMetadata BuildUpdateMetadata(IReadOnlyDictionary<string, object?> arguments)
    {
        return new UpdateMetadata
        {
            Comment = new Markdown
            {
                Text = ExtractString(arguments, "comment") ?? "Updated by workspace entity toolset.",
            },
        };
    }

    private static bool TryExtractEntityId(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        out EntityId entityId)
    {
        entityId = default;
        var entityIdValue = ExtractString(arguments, key);
        if (string.IsNullOrWhiteSpace(entityIdValue) || !Guid.TryParse(entityIdValue, out var entityGuid))
        {
            return false;
        }

        entityId = new EntityId(entityGuid);
        return true;
    }

    private static bool TryExtractEntityName(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        out EntityName entityName)
    {
        entityName = default;
        if (!arguments.TryGetValue(key, out var value) || value is null)
        {
            return false;
        }

        if (value is JsonElement element)
        {
            var maybeEntityName = element.TryReadEntityName();
            if (maybeEntityName is null)
            {
                return false;
            }

            entityName = maybeEntityName.Value;
            return true;
        }

        if (value is IEnumerable<string> stringValues)
        {
            var components = stringValues.Where(static item => !string.IsNullOrWhiteSpace(item)).ToArray();
            if (components.Length == 0)
            {
                return false;
            }

            entityName = new EntityName(components);
            return true;
        }

        return false;
    }

    private static bool TryExtractJsonElement(
        IReadOnlyDictionary<string, object?> arguments,
        string key,
        out JsonElement value)
    {
        value = default;
        if (!arguments.TryGetValue(key, out var argumentValue) || argumentValue is null)
        {
            return false;
        }

        if (argumentValue is JsonElement jsonElement)
        {
            value = jsonElement.Clone();
            return true;
        }

        if (argumentValue is string jsonString)
        {
            try
            {
                using var jsonDocument = JsonDocument.Parse(jsonString);
                value = jsonDocument.RootElement.Clone();
                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        try
        {
            var serializedArgument = JsonSerializer.Serialize(argumentValue);
            using var jsonDocument = JsonDocument.Parse(serializedArgument);
            value = jsonDocument.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ExtractString(
        IReadOnlyDictionary<string, object?> arguments,
        string key)
    {
        if (!arguments.TryGetValue(key, out var value))
        {
            return null;
        }

        return value switch
        {
            string stringValue => stringValue,
            JsonElement { ValueKind: JsonValueKind.String } jsonElement => jsonElement.GetString(),
            _ => null,
        };
    }

    private static string SerializeAsJson(object value)
    {
        return JsonSerializer.Serialize(value);
    }
}
