using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Llm.Trust;
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
    private readonly IReadOnlyList<ITrustedExecutor> executors;
    private readonly ILogger<ScheduledToolHost> logger;
    private readonly HashSet<EntityId> runningRelationships = new();
    private readonly Dictionary<EntityId, RunningScheduledTool> runningExecutions = new();
    private readonly Dictionary<EntityId, CancellationTokenSource> runningCancellations = new();
    private readonly HashSet<Task> inFlightRuns = new();
    private readonly object runningLock = new();

    public ScheduledToolHost(
        IDataAccessLayer dataAccessLayer,
        ScheduledToolRegistry registry,
        ToolExecutionResultWriter? resultWriter = null,
        TimeProvider? timeProvider = null,
        IReadOnlyList<ITrustedExecutor>? executors = null,
        ILogger<ScheduledToolHost>? logger = null)
    {
        this.dataAccessLayer = dataAccessLayer ?? throw new ArgumentNullException(nameof(dataAccessLayer));
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.timeProvider = timeProvider ?? TimeProvider.System;
        this.resultWriter = resultWriter ?? new ToolExecutionResultWriter(dataAccessLayer, this.timeProvider);
        this.executors = executors ?? [];
        this.logger = logger ?? NullLogger<ScheduledToolHost>.Instance;
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

    /// <summary>Returns true if a tool with the given <paramref name="toolType"/> is registered in this host's registry.</summary>
    public bool TryGetTool(string toolType, out IWorkspaceTool? tool)
        => this.registry.TryGetTool(toolType, out tool!);

    /// <summary>
    /// Evaluates all tool-relationships targeting the host and dispatches the tools whose schedules
    /// are due. Each due tool runs as its own tracked <see cref="Task"/>, so a long-lived (blocking)
    /// tool never starves the others; the method returns the number of tools <em>dispatched</em> this
    /// tick without awaiting their completion. The <c>runningRelationships</c> guard prevents a
    /// relationship whose previous run is still in flight from being dispatched again.
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

        var dispatchedCount = 0;
        foreach (var relationship in relationships)
        {
            // A relationship that is individually paused is skipped even when its schedule is due.
            if (relationship.Paused)
            {
                continue;
            }

            // Do not start a relationship that is already running. The guard is cleared in the run
            // task's continuation (see DispatchRun), so it stays set for the entire lifetime of a
            // still-running tool and re-dispatch on later ticks is correctly suppressed.
            lock (this.runningLock)
            {
                if (!this.runningRelationships.Add(relationship.RelationshipId))
                {
                    continue;
                }
            }

            var dispatched = false;
            try
            {
                if (!await this.IsDueAsync(relationship, now, cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                // Mark the start before launching so the next evaluation sees the new last-started.
                await this.UpdateLastStartedAsync(relationship.RelationshipId, now, cancellationToken).ConfigureAwait(false);

                // Start the run as its own tracked task; DO NOT await it here, so a long-lived tool
                // cannot block this loop or the periodic tick.
                this.DispatchRun(relationship, hostEntityId, hostNameComponents, cancellationToken);
                dispatched = true;
                dispatchedCount++;
            }
            finally
            {
                // If we did not hand ownership to a run task (not due, or the due/last-started work
                // threw), release the guard here. When a run was dispatched, its continuation clears
                // the guard instead.
                if (!dispatched)
                {
                    lock (this.runningLock)
                    {
                        this.runningRelationships.Remove(relationship.RelationshipId);
                    }
                }
            }
        }

        return dispatchedCount;
    }

    // Starts a single tool run on its own task and arranges cleanup once it finishes. The run's
    // running-state (runningExecutions/runningCancellations) is managed inside RunToolAsync; here we
    // only track the task for draining and clear the per-relationship de-dup guard on completion.
    private void DispatchRun(
        ToolRelationship relationship,
        EntityId hostEntityId,
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken)
    {
        lock (this.runningLock)
        {
            var runTask = Task.Run(() => this.RunToolAsync(relationship, hostEntityId, hostNameComponents, cancellationToken));

            Task tracked = null!;
            tracked = runTask.ContinueWith(
                completed =>
                {
                    // Observe any fault so a fire-and-forget run never surfaces as an unobserved
                    // TaskScheduler exception (which would pop a fatal crash dialog). RunToolAsync has
                    // already logged and recorded the failure; a cancelled run has no Exception.
                    if (completed.Exception is { } exception)
                    {
                        this.logger.LogError(exception, "Scheduled tool run faulted; continuing.");
                    }

                    lock (this.runningLock)
                    {
                        this.runningRelationships.Remove(relationship.RelationshipId);
                        this.inFlightRuns.Remove(tracked);
                    }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);

            this.inFlightRuns.Add(tracked);
        }
    }

    /// <summary>
    /// Awaits every scheduled-tool run currently in flight on this host, including their cleanup
    /// continuations. Individual run faults and cancellations are already observed and recorded by the
    /// run tasks, so this never throws. Intended for graceful drain and deterministic tests.
    /// </summary>
    public async Task WaitForRunningExecutionsAsync()
    {
        while (true)
        {
            Task[] tasks;
            lock (this.runningLock)
            {
                tasks = this.inFlightRuns.ToArray();
            }

            if (tasks.Length == 0)
            {
                return;
            }

            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
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

        var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var handle = await this.resultWriter.StartAsync(hostNameComponents, toolType, cancellationToken).ConfigureAwait(false);
        this.AddRunningExecution(relationship.RelationshipId, toolType, hostNameComponents, executionCancellation);
        var hostLabel = string.Join(" / ", hostNameComponents);
        this.logger.LogInformation("Scheduled tool {Tool} starting on host {Host}.", toolType, hostLabel);
        var stopwatch = Stopwatch.StartNew();
        try
        {
            // Route via a registered executor when one can handle the target client instance (e.g.
            // a reverse-tunnel or forward-HTTP executor for a remote machine).
            var targetInstanceId = hostEntityId.ToString();
            foreach (var executor in this.executors)
            {
                if (executor.CanExecute(targetInstanceId))
                {
                    await executor.RunToolAsync(
                        new TrustedToolRequest
                        {
                            ToolTypeName = toolType,
                            ToolEntityId = relationship.ToolEntityId.ToString(),
                            TargetClientInstance = targetInstanceId,
                        },
                        executionCancellation.Token).ConfigureAwait(false);

                    stopwatch.Stop();
                    this.logger.LogInformation(
                        "Scheduled tool {Tool} completed in {Elapsed}. {Summary}",
                        toolType, stopwatch.Elapsed, "routed via remote executor");

                    await this.TryCompleteAsync(handle, success: true, content: null, toolType).ConfigureAwait(false);
                    await this.TryUpdateLastCompletedAsync(relationship.RelationshipId, "succeeded", cancellationToken).ConfigureAwait(false);
                    return true;
                }
            }

            // No executor matched — run the tool locally.
            if (!this.registry.TryGetTool(toolType, out var tool))
            {
                stopwatch.Stop();
                var registryMissMessage = $"tool-type '{toolType}' is not registered on this host";
                this.logger.LogError(
                    "Scheduled tool {Tool} could not run: {Reason}",
                    toolType, registryMissMessage);
                await this.TryCompleteAsync(handle, success: false, content: registryMissMessage, toolType).ConfigureAwait(false);
                await this.TryUpdateLastCompletedAsync(relationship.RelationshipId, "failed", cancellationToken).ConfigureAwait(false);
                return false;
            }

            var executionContext = await this.CreateExecutionContextAsync(relationship, hostEntityId, executionCancellation.Token).ConfigureAwait(false);
            if (executionContext is null)
            {
                stopwatch.Stop();
                var contextMissMessage = $"tool {toolType}: could not build execution context (missing profile, tool, schedule, or relationship entity)";
                this.logger.LogError(
                    "Scheduled tool {Tool} could not run: {Reason}",
                    toolType, contextMissMessage);
                await this.TryCompleteAsync(handle, success: false, content: contextMissMessage, toolType).ConfigureAwait(false);
                await this.TryUpdateLastCompletedAsync(relationship.RelationshipId, "failed", cancellationToken).ConfigureAwait(false);
                return false;
            }

            var result = await tool!.ExecuteAsync(executionContext).ConfigureAwait(false);
            stopwatch.Stop();
            if (result.IsSuccess)
            {
                this.logger.LogInformation(
                    "Scheduled tool {Tool} completed in {Elapsed}. {Summary}",
                    toolType, stopwatch.Elapsed, result.ResultContent);
            }
            else
            {
                this.logger.LogError(
                    "Scheduled tool {Tool} failed in {Elapsed}: {Error}",
                    toolType, stopwatch.Elapsed, result.ErrorMessage);
            }

            await this.TryCompleteAsync(handle, success: result.IsSuccess, content: result.ResultContent ?? result.ErrorMessage, toolType).ConfigureAwait(false);
            await this.TryUpdateLastCompletedAsync(relationship.RelationshipId, result.IsSuccess ? "succeeded" : "failed", cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception)
        {
            stopwatch.Stop();
            this.logger.LogError(exception,
                "Scheduled tool {Tool} threw after {Elapsed}.", toolType, stopwatch.Elapsed);
            await this.TryCompleteAsync(handle, success: false, content: exception.Message, toolType).ConfigureAwait(false);
            await this.TryUpdateLastCompletedAsync(relationship.RelationshipId, "failed", cancellationToken).ConfigureAwait(false);
            throw;
        }
        finally
        {
            this.RemoveRunningExecution(relationship.RelationshipId);
        }
    }

    // Exception-safe completion write. A DAL failure here is logged but does not re-enter the outer
    // catch (which would double-write CompleteAsync and mask the original run outcome). See #1155.
    private async Task TryCompleteAsync(
        ToolExecutionResultHandle handle,
        bool success,
        string? content,
        string toolType)
    {
        try
        {
            // Use CancellationToken.None so that recording the outcome always succeeds even when
            // the outer token is cancelled (e.g. during shutdown). A result entity left in the
            // "running" state would otherwise appear as a phantom in-progress entry on next startup.
            await this.resultWriter.CompleteAsync(handle, success, content, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception completionException)
        {
            this.logger.LogError(
                completionException,
                "Failed to record completion for {Tool} run {Handle}.",
                toolType, handle.EntityId);
        }
    }

    // Mirror the completion status onto the tool-relationship entity so a stuck run is detectable
    // even if the per-run result entity is orphaned. See #1155.
    private async Task TryUpdateLastCompletedAsync(
        EntityId relationshipId,
        string status,
        CancellationToken cancellationToken)
    {
        try
        {
            await this.UpdateLastCompletedAsync(relationshipId, this.timeProvider.GetUtcNow(), status, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            this.logger.LogError(
                exception,
                "Failed to record last-completed on tool-relationship {Relationship}.",
                relationshipId);
        }
    }

    private async Task UpdateLastCompletedAsync(
        EntityId relationshipId,
        DateTimeOffset now,
        string status,
        CancellationToken cancellationToken)
    {
        var snapshot = await this.ReadEntitySnapshotAsync(relationshipId, cancellationToken).ConfigureAwait(false);
        if (snapshot?.Data is not { } data)
        {
            return;
        }

        var node = JsonNode.Parse(data.GetRawText())!.AsObject();
        node["last-completed"] = now.ToString("o", CultureInfo.InvariantCulture);
        node["last-status"] = status;
        var updated = JsonSerializer.SerializeToElement(node);

        await this.dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "scheduled-tools: mark last-completed" } },
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

    /// <summary>
    /// Reconciles orphaned per-run <c>tool-execution-result</c> entities left in <c>status: "running"</c>
    /// with no <c>end-time</c> — the residue of a prior process being terminated (or a completion-write
    /// having failed) between <see cref="ToolExecutionResultWriter.StartAsync"/> and
    /// <see cref="ToolExecutionResultWriter.CompleteAsync"/>. Each such entity is marked
    /// <c>status: "failed"</c> with an explanatory reconciliation message. Filters to results under the
    /// given host name components. See #1155.
    /// </summary>
    public async Task<int> ReconcileOrphanRunningResultsAsync(
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hostNameComponents);

        var queryResult = await this.dataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("orphan-running-results"),
                        Clause = new EntityTypeQueryClause
                        {
                            EntityTypeNames = new EntityTypeNameSet([ToolExecutionResultWriter.ToolExecutionResultEntityType]),
                        },
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);

        var reconciled = 0;
        foreach (var snapshot in queryResult.Batches.SelectMany(batch => batch.Entities))
        {
            if (snapshot.Data is not { } data)
            {
                continue;
            }

            if (!IsTopLevelRunningResultForHost(data, hostNameComponents))
            {
                continue;
            }

            if (await this.TryReconcileOrphanAsync(snapshot, data, hostNameComponents, cancellationToken).ConfigureAwait(false))
            {
                reconciled++;
            }
        }

        if (reconciled > 0)
        {
            this.logger.LogInformation(
                "Reconciled {Count} orphan running tool-execution-result entities as failed.", reconciled);
        }

        return reconciled;
    }

    private async Task<bool> TryReconcileOrphanAsync(
        EntitySnapshot snapshot,
        JsonElement data,
        IReadOnlyList<string> hostNameComponents,
        CancellationToken cancellationToken)
    {
        try
        {
            var node = JsonNode.Parse(data.GetRawText())!.AsObject();
            node["status"] = "failed";
            node["end-time"] = this.timeProvider.GetUtcNow().ToString("o", CultureInfo.InvariantCulture);

            // #1360: backfill the queryable host-label on legacy results that predate it, so the
            // reconciled run remains visible to host-filtered run-history queries.
            if (node["host-label"] is null)
            {
                node["host-label"] = string.Join(" / ", hostNameComponents);
            }

            const string reconciliationMessage = "run did not complete (process terminated or completion write failed); reconciled on startup";

            var contentObject = new JsonObject
            {
                ["default"] = new JsonObject
                {
                    ["mime-type"] = "text/plain",
                    ["content"] = new JsonObject
                    {
                        ["text"] = reconciliationMessage,
                    },
                },
            };
            node["content"] = contentObject;

            // The tool-execution-result becomes a note when it has content — mirror ToolExecutionResultWriter.
            if (node["entity-types"] is JsonArray entityTypes
                && !entityTypes.Any(t => (string?)t == "note"))
            {
                entityTypes.Add("note");
            }

            var updated = JsonSerializer.SerializeToElement(node);

            var updateResult = await this.dataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "scheduled-tools: reconcile orphan running result" } },
                    Changes =
                    [
                        new EntityChange
                        {
                            EntityId = snapshot.EntityId,
                            ConcurrencyTag = snapshot.ConcurrencyTag,
                            Data = updated,
                            EntityChangeMode = EntityChangeMode.Replace,
                        },
                    ],
                },
                cancellationToken).ConfigureAwait(false);

            return !updateResult.EntityResults.Any(r => r.UpdateState == UpdateState.Failed);
        }
        catch (Exception exception)
        {
            this.logger.LogError(
                exception,
                "Failed to reconcile orphan running tool-execution-result {Entity}.",
                snapshot.EntityId);
            return false;
        }
    }

    private static bool IsTopLevelRunningResultForHost(
        JsonElement data,
        IReadOnlyList<string> hostNameComponents)
    {
        // Only reconcile results whose status is "running" and which have no end-time recorded.
        if (!data.TryGetProperty("status", out var statusEl)
            || statusEl.ValueKind != JsonValueKind.String
            || !string.Equals(statusEl.GetString(), "running", StringComparison.Ordinal))
        {
            return false;
        }

        if (data.TryGetProperty("end-time", out var endEl) && endEl.ValueKind == JsonValueKind.String)
        {
            return false;
        }

        if (!data.TryGetProperty("names", out var namesEl) || namesEl.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var nameEl in namesEl.EnumerateArray())
        {
            if (nameEl.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            var components = nameEl.EnumerateArray()
                .Where(c => c.ValueKind == JsonValueKind.String)
                .Select(c => c.GetString()!)
                .ToArray();

            // Top-level runs live at [host..., "tool-executions", tool-name, start-timestamp].
            if (components.Length < hostNameComponents.Count + 3)
            {
                continue;
            }

            var executionsIndex = Array.IndexOf(components, ToolExecutionResultWriter.ToolExecutionsSegment);
            if (executionsIndex != hostNameComponents.Count)
            {
                continue;
            }

            // Match the host prefix exactly (case-sensitive; entity name components are stable strings).
            var matches = true;
            for (var i = 0; i < hostNameComponents.Count; i++)
            {
                if (!string.Equals(components[i], hostNameComponents[i], StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            // Only top-level runs (not child sub-tasks) have exactly [toolName, startTimestamp] after the segment.
            if (components.Length - (executionsIndex + 1) == 2)
            {
                return true;
            }
        }

        return false;
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
