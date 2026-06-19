using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Runs the per-entity classification agent for a single entity. Implementations drive an agent
/// definition (without recording chat history) to create/remove relationships or make other
/// permitted state changes; see <c>docs/design/scheduled-tools.md</c>.
/// </summary>
public interface IEntityClassifierAgentRunner
{
    /// <summary>Drives the classification agent for the entity using its before snapshot.</summary>
    Task RunAsync(EntityClassificationRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Invoked after the agent ran, with the entity's before and after snapshots, so the
    /// classification can reason about what changed. The default does nothing.
    /// </summary>
    Task OnClassifiedAsync(
        EntityId entityId,
        EntitySnapshot beforeSnapshot,
        EntitySnapshot? afterSnapshot,
        CancellationToken cancellationToken)
        => Task.CompletedTask;
}

/// <summary>The inputs to a single entity classification: the assembled prompt and the before snapshot.</summary>
public sealed record EntityClassificationRequest
{
    public required EntityId EntityId { get; init; }

    /// <summary>The classification prompt, assembled in KV-cache-friendly order (see the tool).</summary>
    public required string Prompt { get; init; }

    /// <summary>The entity snapshot captured before the agent ran.</summary>
    public required EntitySnapshot BeforeSnapshot { get; init; }

    /// <summary>The data access layer the agent operates through.</summary>
    public required IDataAccessLayer DataAccessLayer { get; init; }
}

/// <summary>
/// A built-in scheduled tool that classifies entities on a schedule. It pulls batches of
/// recently-changed entities from the classification queue and, for each, assembles a prompt and
/// runs the classification agent once (without chat history), retrieving the entity's before and
/// after snapshots. The queue head advances per batch so a later run resumes where this one stopped.
/// </summary>
/// <remarks>
/// The prompt is assembled in this order to favor LLM KV-cache reuse:
/// 1. the classifier prompt, 2. the set of all entity-type names, 3. the entity's own types,
/// 4. the entity content, 5. the relationships the entity currently has.
/// </remarks>
public sealed class EntityClassifierTool : IWorkspaceTool
{
    /// <summary>The default queue name used for classification.</summary>
    public const string QueueName = "entity-classification";

    /// <summary>The tool-entity property carrying the classifier prompt.</summary>
    public const string ClassifierPromptProperty = "classifier-prompt";

    private readonly IEntityClassifierAgentRunner agentRunner;
    private readonly int batchSize;

    public EntityClassifierTool(IEntityClassifierAgentRunner agentRunner, int batchSize = 50)
    {
        this.agentRunner = agentRunner ?? throw new ArgumentNullException(nameof(agentRunner));
        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        this.batchSize = batchSize;
    }

    public string ToolType => "entity-classifier";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var dataAccessLayer = context.DataAccessLayer;

        var classifierPrompt = ReadString(context.Tool.Data, ClassifierPromptProperty) ?? string.Empty;
        var allEntityTypeNames = await ReadAllEntityTypeNamesAsync(dataAccessLayer, context.CancellationToken).ConfigureAwait(false);
        var interestInstructions = await ReadInterestInstructionsAsync(dataAccessLayer, context.CancellationToken).ConfigureAwait(false);

        Timestamp? token = null;
        var processedEntityIds = new HashSet<EntityId>();
        while (true)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var batch = await dataAccessLayer.ProcessQueueAsync(
                new ProcessQueueRequest { QueueName = QueueName, Token = token, Count = this.batchSize },
                context.CancellationToken).ConfigureAwait(false);

            if (batch.Entities.Count == 0)
            {
                break;
            }

            foreach (var entity in batch.Entities)
            {
                context.CancellationToken.ThrowIfCancellationRequested();

                // Skip tombstoned (deleted) entities, and any entity already processed this run -
                // classifying an entity may modify it, which re-enqueues it; process it only once.
                if (entity.Data is null || !processedEntityIds.Add(entity.EntityId))
                {
                    continue;
                }

                var beforeSnapshot = await ReadSnapshotAsync(dataAccessLayer, entity.EntityId, context.CancellationToken).ConfigureAwait(false)
                    ?? entity;

                var prompt = AssemblePrompt(classifierPrompt, allEntityTypeNames, interestInstructions, beforeSnapshot);

                await this.agentRunner.RunAsync(
                    new EntityClassificationRequest
                    {
                        EntityId = entity.EntityId,
                        Prompt = prompt,
                        BeforeSnapshot = beforeSnapshot,
                        DataAccessLayer = dataAccessLayer,
                    },
                    context.CancellationToken).ConfigureAwait(false);

                // Retrieve the after snapshot so the classification can reason about what changed.
                var afterSnapshot = await ReadSnapshotAsync(dataAccessLayer, entity.EntityId, context.CancellationToken).ConfigureAwait(false);
                await this.agentRunner.OnClassifiedAsync(entity.EntityId, beforeSnapshot, afterSnapshot, context.CancellationToken).ConfigureAwait(false);
            }

            token = batch.Token;
        }

        return new WorkspaceToolExecutionResult();
    }

    private static string AssemblePrompt(
        string classifierPrompt,
        IReadOnlyList<string> allEntityTypeNames,
        string interestInstructions,
        EntitySnapshot entity)
    {
        var builder = new StringBuilder();
        builder.AppendLine(classifierPrompt);
        builder.AppendLine();
        builder.AppendLine("# All entity types");
        builder.AppendLine(string.Join(", ", allEntityTypeNames));
        builder.AppendLine();

        // Interest instructions are static across the run (like the entity-type list) and are placed
        // here, before the entity-specific sections, to favor LLM KV-cache reuse.
        if (!string.IsNullOrWhiteSpace(interestInstructions))
        {
            builder.AppendLine(interestInstructions);
            builder.AppendLine();
        }

        builder.AppendLine("# Entity types");
        builder.AppendLine(string.Join(", ", ReadEntityTypes(entity.Data)));
        builder.AppendLine();
        builder.AppendLine("# Entity content");
        builder.AppendLine(ReadEntityText(entity.Data));
        builder.AppendLine();
        builder.AppendLine("# Relationships");
        builder.AppendLine(string.Join(
            ", ",
            entity.Relationships.Select(relationship => relationship.EntityId.Value.ToString())));
        return builder.ToString();
    }

    private static async Task<EntitySnapshot?> ReadSnapshotAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId,
        CancellationToken cancellationToken)
    {
        var result = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(false);

        return result.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId);
    }

    private static async Task<IReadOnlyList<string>> ReadAllEntityTypeNamesAsync(
        IDataAccessLayer dataAccessLayer,
        CancellationToken cancellationToken)
    {
        var result = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("entity-types"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["entity-type"]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var snapshot in result.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not { } data || !data.TryGetProperty("names", out var nameArray) || nameArray.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var name in nameArray.EnumerateArray())
            {
                if (name.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                var components = name.EnumerateArray().Select(component => component.GetString()).ToArray();
                if (components.Length == 2 && string.Equals(components[0], "entity-types", StringComparison.Ordinal) && !string.IsNullOrEmpty(components[1]))
                {
                    names.Add(components[1]!);
                }
            }
        }

        return names.ToArray();
    }

    private static async Task<string> ReadInterestInstructionsAsync(
        IDataAccessLayer dataAccessLayer,
        CancellationToken cancellationToken)
    {
        var result = await dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("interest-types"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["interest-type"]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var interests = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var snapshot in result.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not { } data)
            {
                continue;
            }

            var name = ReadInterestName(data);
            if (name is null)
            {
                continue;
            }

            interests[name] = ReadAppliedDescription(data);
        }

        if (interests.Count == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder();
        builder.AppendLine("# Interests");
        builder.AppendLine(
            "Interests are relationships that attach contextual relevance to an entity and render as "
            + "badges. Apply an interest by creating a relationship of that interest type whose 'target' "
            + "participant is this entity (with 'user'/'view' participants for user/view-scoped "
            + "interests); remove it by deleting that relationship. Whenever you create any relationship "
            + "(including an interest or a workstream 'related' link), include a 'note' property "
            + "explaining why you applied it.");
        builder.AppendLine();
        builder.AppendLine("Available interests:");
        foreach (var (name, description) in interests)
        {
            builder.Append("- ").Append(name);
            if (!string.IsNullOrWhiteSpace(description))
            {
                builder.Append(": ").Append(description);
            }

            builder.AppendLine();
        }

        builder.AppendLine();
        builder.AppendLine(
            "Rules: mark a completed or cancelled task that has not been modified for over a week as "
            + "not-interesting. For a task without an assigned-to interest, choose the user from the "
            + "task's source-system 'assigned-to' field and apply assigned-to. When an entity is clearly "
            + "part of a workstream, associate it with the corresponding task via a 'related' "
            + "relationship.");
        return builder.ToString();
    }

    private static string? ReadInterestName(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object
            || !data.TryGetProperty("names", out var nameArray)
            || nameArray.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var name in nameArray.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = name.EnumerateArray().Select(component => component.GetString()).ToArray();
            if (components.Length == 2
                && string.Equals(components[0], "entity-types", StringComparison.Ordinal)
                && !string.IsNullOrEmpty(components[1]))
            {
                return components[1];
            }
        }

        return null;
    }

    private static string ReadAppliedDescription(JsonElement data)
    {
        if (data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("applied", out var applied)
            && applied.ValueKind == JsonValueKind.Object
            && applied.TryGetProperty("description", out var description))
        {
            return ReadLocalString(description);
        }

        return string.Empty;
    }

    private static string ReadLocalString(JsonElement value)
    {
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Object when value.TryGetProperty("default", out var def) && def.ValueKind == JsonValueKind.String
                => def.GetString() ?? string.Empty,
            _ => string.Empty,
        };
    }

    private static IReadOnlyList<string> ReadEntityTypes(JsonElement? data)
    {
        if (data is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("entity-types", out var types)
            && types.ValueKind == JsonValueKind.Array)
        {
            return types.EnumerateArray()
                .Where(type => type.ValueKind == JsonValueKind.String)
                .Select(type => type.GetString()!)
                .ToArray();
        }

        return [];
    }

    private static string ReadEntityText(JsonElement? data)
    {
        if (data is { ValueKind: JsonValueKind.Object } element
            && element.TryGetProperty("content", out var content)
            && content.ValueKind == JsonValueKind.Object
            && content.TryGetProperty("text", out var text)
            && text.ValueKind == JsonValueKind.String)
        {
            return text.GetString() ?? string.Empty;
        }

        return string.Empty;
    }

    private static string? ReadString(JsonElement? toolEntity, string propertyName)
    {
        if (toolEntity is JsonElement toolEntityValue
            && toolEntityValue.ValueKind == JsonValueKind.Object
            && toolEntityValue.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String)
        {
            return element.GetString();
        }

        return null;
    }
}
