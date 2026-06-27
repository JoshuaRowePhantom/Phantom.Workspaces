using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Tools;

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
    private readonly Dictionary<EntityId, CancellationTokenSource> runningCancellations = new();
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

        // The persisted host pause/stop-all state gates every run on this host.
        if (await this.IsHostPausedAsync(hostEntityId, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        var relationships = await this.DiscoverToolRelationshipsForHostAsync(hostEntityId, cancellationToken).ConfigureAwait(false);

        var ranCount = 0;
        foreach (var relationship in relationships)
        {
            // A relationship that is individually paused is skipped even when its schedule is due.
            if (relationship.Paused)
            {
                continue;
            }

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
                if (!await this.IsDueAsync(relationship, now, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // Mark the start before launching so the next evaluation sees the new last-started.
                await this.UpdateLastStartedAsync(relationship.RelationshipId, now, cancellationToken).ConfigureAwait(false);

                if (await this.RunToolAsync(relationship, hostEntityId, hostNameComponents, cancellationToken).ConfigureAwait(false))
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

    /// <summary>
    /// Requests cancellation of every scheduled tool execution currently running on this host.
    /// New runs remain blocked while the persisted <c>scheduled-tools-paused</c> flag is set.
    /// </summary>
    public void StopAllRunningExecutions()
    {
        CancellationTokenSource[] sources;
        lock (this.runningLock)
        {
            sources = this.runningCancellations.Values.ToArray();
        }

        foreach (var source in sources)
        {
            source.Cancel();
        }
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
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        foreach (var scheduleEntityId in relationship.ScheduleEntityIds)
        {
            var scheduleData = (await this.ReadEntitySnapshotAsync(scheduleEntityId, cancellationToken).ConfigureAwait(false))?.Data;
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

            if (ScheduleEvaluator.IsDue(schedule, relationship.LastStarted, now))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<bool> IsHostPausedAsync(EntityId hostEntityId, CancellationToken cancellationToken)
    {
        var data = (await this.ReadEntitySnapshotAsync(hostEntityId, cancellationToken).ConfigureAwait(false))?.Data;
        return data is { } hostData
            && hostData.TryGetProperty("scheduled-tools-paused", out var pausedElement)
            && pausedElement.ValueKind == JsonValueKind.True;
    }

    private async Task UpdateLastStartedAsync(EntityId relationshipId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var snapshot = await this.ReadEntitySnapshotAsync(relationshipId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.Data is not { } data)
        {
            return;
        }

        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node["last-started"] = now.ToString("o", CultureInfo.InvariantCulture);
        var updated = JsonSerializer.SerializeToElement(node);

        await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "scheduled-tools: mark last-started" } },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = relationshipId,
                        ConcurrencyTag = snapshot.ConcurrencyTag,
                        Data = updated,
                        EntityChangeMode = EntityChangeMode.Replace,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> RunToolAsync(
        ToolRelationship relationship,
        EntityId hostEntityId,
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken)
    {
        var toolEntity = await this.ReadEntitySnapshotAsync(relationship.ToolEntityId, cancellationToken).ConfigureAwait(false);
        if (toolEntity?.Data is not { } toolData || !TryReadToolType(toolData, out var toolType))
        {
            return false;
        }

        if (!this.registry.TryGetTool(toolType, out var tool))
        {
            return false;
        }

        var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var executionContext = await this.CreateExecutionContextAsync(relationship, hostEntityId, executionCancellation.Token).ConfigureAwait(false);
        if (executionContext is null)
        {
            executionCancellation.Dispose();
            return false;
        }

        var handle = await this.resultWriter.StartAsync(hostNameComponents, toolType, cancellationToken).ConfigureAwait(false);
        this.AddRunningExecution(relationship.RelationshipId, toolType, hostNameComponents, executionCancellation);
        try
        {
            var result = await tool.ExecuteAsync(executionContext).ConfigureAwait(false);

            await this.resultWriter.CompleteAsync(handle, success: result.IsSuccess, content: result.ResultContent ?? result.ErrorMessage, cancellationToken).ConfigureAwait(false);
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

    private void AddRunningExecution(
        EntityId relationshipId,
        string toolType,
        IReadOnlyList<string> hostNameComponents,
        CancellationTokenSource executionCancellation)
    {
        lock (this.runningLock)
        {
            this.runningExecutions[relationshipId] = new RunningScheduledTool(
                relationshipId,
                toolType,
                hostNameComponents,
                this.timeProvider.GetUtcNow());
            this.runningCancellations[relationshipId] = executionCancellation;
        }

        this.RunningExecutionsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void RemoveRunningExecution(EntityId relationshipId)
    {
        bool removed;
        CancellationTokenSource? executionCancellation;
        lock (this.runningLock)
        {
            removed = this.runningExecutions.Remove(relationshipId);
            this.runningCancellations.Remove(relationshipId, out executionCancellation);
        }

        executionCancellation?.Dispose();

        if (removed)
        {
            this.RunningExecutionsChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private async Task<WorkspaceToolExecutionContext?> CreateExecutionContextAsync(
        ToolRelationship relationship,
        EntityId hostEntityId,
        CancellationToken cancellationToken)
    {
        var currentProfileEntity = await this.ReadEntitySnapshotAsync(hostEntityId, cancellationToken).ConfigureAwait(false);
        var toolRelationshipEntity = await this.ReadEntitySnapshotAsync(relationship.RelationshipId, cancellationToken).ConfigureAwait(false);
        var toolEntity = await this.ReadEntitySnapshotAsync(relationship.ToolEntityId, cancellationToken).ConfigureAwait(false);
        var scheduleEntity = await this.ReadEntitySnapshotAsync(relationship.ScheduleEntityIds[0], cancellationToken).ConfigureAwait(false);
        if (currentProfileEntity is null || toolRelationshipEntity is null || toolEntity is null || scheduleEntity is null)
        {
            return null;
        }

        var currentUserEntity = await this.ReadReferencedEntityAsync(currentProfileEntity, "user-reference", cancellationToken).ConfigureAwait(false)
            ?? CreatePlaceholderEntitySnapshot();
        var currentComputerEntity = await this.ReadReferencedEntityAsync(currentProfileEntity, "computer-reference", cancellationToken).ConfigureAwait(false)
            ?? CreatePlaceholderEntitySnapshot();
        var participants = await this.ReadEntitySnapshotsAsync(relationship.TargetEntityIds, cancellationToken).ConfigureAwait(false);
        if (!participants.Any(participant => participant.EntityId == currentProfileEntity.EntityId))
        {
            participants = [.. participants, currentProfileEntity];
        }

        return new WorkspaceToolExecutionContext
        {
            DataAccessLayer = this.dataAccessLayer,
            CancellationToken = cancellationToken,
            CurrentComputerEntity = currentComputerEntity,
            CurrentUserEntity = currentUserEntity,
            CurrentComputerUserProfileEntity = currentProfileEntity,
            ToolRelationship = toolRelationshipEntity,
            Participants = participants.ToArray(),
            Tool = toolEntity,
            Schedule = scheduleEntity,
        };
    }

    private async Task<EntitySnapshot?> ReadReferencedEntityAsync(
        EntitySnapshot sourceEntity,
        string propertyName,
        CancellationToken cancellationToken)
    {
        if (sourceEntity.Data is not { } data
            || !TryReadEntityName(data, propertyName, out var entityName))
        {
            return null;
        }

        var getResult = await this.dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityName = entityName }],
            },
            cancellationToken).ConfigureAwait(false);

        return getResult.Batches.SelectMany(batch => batch.Entities).FirstOrDefault();
    }

    private async Task<IReadOnlyList<EntitySnapshot>> ReadEntitySnapshotsAsync(
        IReadOnlyList<EntityId> entityIds,
        CancellationToken cancellationToken)
    {
        var snapshots = new List<EntitySnapshot>(entityIds.Count);
        foreach (var entityId in entityIds.Distinct())
        {
            var snapshot = await this.ReadEntitySnapshotAsync(entityId, cancellationToken).ConfigureAwait(false);
            if (snapshot is not null)
            {
                snapshots.Add(snapshot);
            }
        }

        return snapshots;
    }

    private async Task<EntitySnapshot?> ReadEntitySnapshotAsync(EntityId entityId, CancellationToken cancellationToken)
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
            .FirstOrDefault(snapshot => snapshot.EntityId == entityId);
    }

    private static bool TryReadToolType(JsonElement toolEntity, out string toolType)
    {
        toolType = string.Empty;
        if (toolEntity.TryGetProperty("tool-type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String)
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

        var paused = data.TryGetProperty("paused", out var pausedElement)
            && pausedElement.ValueKind == JsonValueKind.True;
        DateTimeOffset? lastStarted = null;
        if (data.TryGetProperty("last-started", out var lastStartedElement)
            && lastStartedElement.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(lastStartedElement.GetString(), out var parsedLastStarted))
        {
            lastStarted = parsedLastStarted;
        }

        relationship = new ToolRelationship(relationshipId, toolEntityId, scheduleEntityIds, targetEntityIds, paused, lastStarted);
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

    private static bool TryReadEntityName(JsonElement parent, string propertyName, out EntityName entityName)
    {
        entityName = default!;
        if (!parent.TryGetProperty(propertyName, out var element) || element.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        var components = element.EnumerateArray()
            .Where(component => component.ValueKind == JsonValueKind.String)
            .Select(component => component.GetString())
            .Where(component => !string.IsNullOrWhiteSpace(component))
            .Cast<string>()
            .ToArray();
        if (components.Length == 0)
        {
            return false;
        }

        entityName = new EntityName(components);
        return true;
    }

    private static EntitySnapshot CreatePlaceholderEntitySnapshot()
    {
        using var placeholderDocument = JsonDocument.Parse(
            """
            {
              "entity-id": "00000000-0000-0000-0000-000000000000",
              "entity-types": ["entity"],
              "names": [["placeholder"]]
            }
            """);
        return new EntitySnapshot
        {
            EntityId = new EntityId(Guid.Empty),
            ModifiedTime = new Timestamp(DateTimeOffset.UnixEpoch, "0"),
            Data = placeholderDocument.RootElement.Clone(),
            Relationships = [],
        };
    }

    private sealed record ToolRelationship(
        EntityId RelationshipId,
        EntityId ToolEntityId,
        IReadOnlyList<EntityId> ScheduleEntityIds,
        IReadOnlyList<EntityId> TargetEntityIds,
        bool Paused,
        DateTimeOffset? LastStarted);
}

/// <summary>A scheduled tool that is currently running on a host.</summary>
public sealed record RunningScheduledTool(
    EntityId RelationshipId,
    string ToolType,
    IReadOnlyList<string> HostNameComponents,
    DateTimeOffset StartedAt);
