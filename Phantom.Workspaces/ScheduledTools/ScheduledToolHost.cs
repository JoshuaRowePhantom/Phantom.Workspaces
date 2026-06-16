using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.ScheduledTools;

/// <summary>
/// Discovers the <c>tool-relationship</c> entities whose <c>target</c> includes the running host and
/// runs the tools whose bound <c>schedule</c>s are due (see <c>docs/design/scheduled-tools.md</c>).
/// A tool-relationship that is currently running is not started again; the next run begins at the
/// next evaluation once the current run has completed.
/// </summary>
public sealed class ScheduledToolHost
{
    private readonly IDataAccessLayer dataAccessLayer;
    private readonly ScheduledToolRegistry registry;
    private readonly ToolExecutionResultWriter resultWriter;
    private readonly TimeProvider timeProvider;
    private readonly HashSet<EntityId> runningRelationships = new();
    private readonly Dictionary<EntityId, RunningScheduledTool> runningExecutions = new();
    private readonly object runningLock = new();

    public ScheduledToolHost(
        IDataAccessLayer dataAccessLayer,
        ScheduledToolRegistry registry,
        ToolExecutionResultWriter? resultWriter = null,
        TimeProvider? timeProvider = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.resultWriter = resultWriter ?? new ToolExecutionResultWriter(dataAccessLayer, this.timeProvider);
    }

    /// <summary>Raised whenever the set of currently-running scheduled tools changes.</summary>
    public event EventHandler? RunningExecutionsChanged;

    /// <summary>A snapshot of the scheduled tools currently running on this host.</summary>
    public IReadOnlyList<RunningScheduledTool> GetRunningExecutions()
    {
        lock (this.runningLock)
        {
            return this.runningExecutions.Values.ToArray();
        }
    }

    /// <summary>
    /// Evaluates all tool-relationships targeting the host and runs the tools whose schedules are
    /// due. Returns the number of tools that ran.
    /// </summary>
    public async Task<int> RunDueToolsAsync(
        EntityId hostEntityId,
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostNameComponents);

        var now = this.timeProvider.GetUtcNow();
        var relationships = await this.DiscoverToolRelationshipsForHostAsync(hostEntityId, cancellationToken).ConfigureAwait(false);

        var ranCount = 0;
        foreach (var relationship in relationships)
        {
            // Do not start a relationship that is already running.
            lock (this.runningLock)
            {
                if (!this.runningRelationships.Add(relationship.RelationshipId))
                {
                    continue;
                }
            }

            try
            {
                if (!await this.IsDueAsync(relationship, hostNameComponents, now, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                if (await this.RunToolAsync(relationship, hostNameComponents, cancellationToken).ConfigureAwait(false))
                {
                    ranCount++;
                }
            }
            finally
            {
                lock (this.runningLock)
                {
                    this.runningRelationships.Remove(relationship.RelationshipId);
                }
            }
        }

        return ranCount;
    }

    private async Task<IReadOnlyList<ToolRelationship>> DiscoverToolRelationshipsForHostAsync(
        EntityId hostEntityId,
        CancellationToken cancellationToken)
    {
        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("tool-relationships"),
                        Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["tool-relationship"]) },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var relationships = new List<ToolRelationship>();
        foreach (var snapshot in queryResult.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not { } data
                || !TryParseToolRelationship(snapshot.EntityId, data, out var relationship))
            {
                continue;
            }

            if (relationship.TargetEntityIds.Contains(hostEntityId))
            {
                relationships.Add(relationship);
            }
        }

        return relationships;
    }

    private async Task<bool> IsDueAsync(
        ToolRelationship relationship,
        IReadOnlyList<string> hostNameComponents,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var toolName = await this.ResolveToolNameAsync(relationship.ToolEntityId, cancellationToken).ConfigureAwait(false);
        var lastExecution = await this.FindLastExecutionTimeAsync(hostNameComponents, toolName, cancellationToken).ConfigureAwait(false);

        foreach (var scheduleEntityId in relationship.ScheduleEntityIds)
        {
            var scheduleData = await this.ReadEntityDataAsync(scheduleEntityId, cancellationToken).ConfigureAwait(false);
            if (scheduleData is not { } data)
            {
                continue;
            }

            ScheduleDefinition schedule;
            try
            {
                schedule = ScheduleDefinition.FromEntity(data);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (ScheduleEvaluator.IsDue(schedule, lastExecution, now))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> RunToolAsync(
        ToolRelationship relationship,
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken)
    {
        var toolData = await this.ReadEntityDataAsync(relationship.ToolEntityId, cancellationToken).ConfigureAwait(false);
        if (toolData is not { } data || !TryReadToolType(data, out var toolType))
        {
            return false;
        }

        if (!this.registry.TryGetTool(toolType, out var tool))
        {
            return false;
        }

        var handle = await this.resultWriter.StartAsync(hostNameComponents, toolType, cancellationToken).ConfigureAwait(false);
        this.AddRunningExecution(relationship.RelationshipId, toolType, hostNameComponents);
        try
        {
            await tool.RunAsync(
                new ScheduledToolContext
                {
                    ToolEntity = data,
                    TargetEntityIds = relationship.TargetEntityIds,
                    DataAccessLayer = this.dataAccessLayer,
                },
                cancellationToken).ConfigureAwait(false);

            await this.resultWriter.CompleteAsync(handle, success: true, content: null, cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            await this.resultWriter.CompleteAsync(handle, success: false, content: exception.Message, cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.RemoveRunningExecution(relationship.RelationshipId);
        }
    }

    private void AddRunningExecution(EntityId relationshipId, string toolType, IReadOnlyList<string> hostNameComponents)
    {
        lock (this.runningLock)
        {
            this.runningExecutions[relationshipId] = new RunningScheduledTool(
                relationshipId,
                toolType,
                hostNameComponents,
                this.timeProvider.GetUtcNow());
        }

        this.RunningExecutionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveRunningExecution(EntityId relationshipId)
    {
        bool removed;
        lock (this.runningLock)
        {
            removed = this.runningExecutions.Remove(relationshipId);
        }

        if (removed)
        {
            this.RunningExecutionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<string> ResolveToolNameAsync(EntityId toolEntityId, CancellationToken cancellationToken)
    {
        var data = await this.ReadEntityDataAsync(toolEntityId, cancellationToken).ConfigureAwait(false);
        return data is { } toolData && TryReadToolType(toolData, out var toolType) ? toolType : "tool";
    }

    private async Task<DateTimeOffset?> FindLastExecutionTimeAsync(
        IReadOnlyList<string> hostNameComponents,
        string toolName,
        CancellationToken cancellationToken)
    {
        var prefix = hostNameComponents
            .Append(ToolExecutionResultWriter.ToolExecutionsSegment)
            .Append(toolName)
            .ToArray();

        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("tool-execution-results"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet([ToolExecutionResultWriter.ToolExecutionResultEntityType]),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        DateTimeOffset? latest = null;
        foreach (var snapshot in queryResult.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not { } data
                || !HasNamePrefix(data, prefix)
                || !data.TryGetProperty("start-time", out var startTimeElement)
                || startTimeElement.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(startTimeElement.GetString(), out var startTime))
            {
                continue;
            }

            if (latest is null || startTime > latest.Value)
            {
                latest = startTime;
            }
        }

        return latest;
    }

    private async Task<JsonElement?> ReadEntityDataAsync(EntityId entityId, CancellationToken cancellationToken)
    {
        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
                Timestamps = [null],
            },
            cancellationToken).ConfigureAwait(false);

        return getResult.Batches
            .SelectMany(batch => batch.Entities)
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId)
            ?.Data;
    }

    private static bool HasNamePrefix(JsonElement entity, IReadOnlyList<string> prefix)
    {
        if (!entity.TryGetProperty("names", out var names) || names.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var name in names.EnumerateArray())
        {
            if (name.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = name.EnumerateArray().Select(component => component.GetString()).ToArray();
            if (components.Length >= prefix.Count
                && prefix.Select((segment, index) => string.Equals(segment, components[index], StringComparison.Ordinal)).All(matched => matched))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadToolType(JsonElement toolEntity, out string toolType)
    {
        toolType = string.Empty;
        if (toolEntity.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
        {
            toolType = typeElement.GetString() ?? string.Empty;
        }

        return !string.IsNullOrWhiteSpace(toolType);
    }

    private static bool TryParseToolRelationship(EntityId relationshipId, JsonElement data, out ToolRelationship relationship)
    {
        relationship = default!;
        if (!data.TryGetProperty("participants", out var participants) || participants.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!TryReadEntityId(participants, "tool", out var toolEntityId))
        {
            return false;
        }

        var scheduleEntityIds = ReadEntityIdArray(participants, "schedule");
        var targetEntityIds = ReadEntityIdArray(participants, "target");
        if (scheduleEntityIds.Count == 0 || targetEntityIds.Count == 0)
        {
            return false;
        }

        relationship = new ToolRelationship(relationshipId, toolEntityId, scheduleEntityIds, targetEntityIds);
        return true;
    }

    private static bool TryReadEntityId(JsonElement parent, string propertyName, out EntityId entityId)
    {
        entityId = default;
        if (parent.TryGetProperty(propertyName, out var element)
            && element.ValueKind == JsonValueKind.String
            && Guid.TryParse(element.GetString(), out var guid))
        {
            entityId = new EntityId(guid);
            return true;
        }

        return false;
    }

    private static IReadOnlyList<EntityId> ReadEntityIdArray(JsonElement parent, string propertyName)
    {
        var result = new List<EntityId>();
        if (parent.TryGetProperty(propertyName, out var element) && element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && Guid.TryParse(item.GetString(), out var guid))
                {
                    result.Add(new EntityId(guid));
                }
            }
        }

        return result;
    }

    private sealed record ToolRelationship(
        EntityId RelationshipId,
        EntityId ToolEntityId,
        IReadOnlyList<EntityId> ScheduleEntityIds,
        IReadOnlyList<EntityId> TargetEntityIds);
}

/// <summary>A scheduled tool that is currently running on a host.</summary>
public sealed record RunningScheduledTool(
    EntityId RelationshipId,
    string ToolType,
    IReadOnlyList<string> HostNameComponents,
    DateTimeOffset StartedAt);
