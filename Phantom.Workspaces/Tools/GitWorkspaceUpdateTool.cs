using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Data;

namespace Phantom.Workspaces.Tools;

/// <summary>
/// Refreshes metadata and local-filesystem state for existing Git workspace entities owned by the
/// current computer-user profile.
/// </summary>
public sealed class GitWorkspaceUpdateTool : IWorkspaceTool
{
    private const string MissingWorkspaceNote =
        "This Git workspace does not exist on the filesystem.";

    private readonly Func<string, ILogger, GitMetadata?> metadataReader;
    private readonly Func<string, GitWorkspaceAvailability> availabilityReader;
    private readonly ILogger logger;

    public GitWorkspaceUpdateTool(
        Func<string, ILogger, GitMetadata?>? metadataReader = null,
        ILogger? logger = null,
        Func<string, bool>? directoryExists = null,
        Func<string, GitWorkspaceAvailability>? availabilityReader = null)
    {
        this.metadataReader = metadataReader ?? GitRepositoryMetadataReader.TryReadMetadata;
        this.logger = logger ?? NullLogger<GitWorkspaceUpdateTool>.Instance;
        this.availabilityReader = availabilityReader
            ?? (directoryExists is null
                ? GitWorkspacePath.Classify
                : path => directoryExists(path) ? GitWorkspaceAvailability.Exists : GitWorkspaceAvailability.ConfirmedMissing);
    }

    public string ToolType => "git-workspace-update";

    public async Task<WorkspaceToolExecutionResult> ExecuteAsync(WorkspaceToolExecutionContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var queryResult = await context.DataAccessLayer.QueryAsync(
            new QueryRequest
            {
                Clauses =
                [
                    new TopLevelQueryClause
                    {
                        ClauseIdentifier = new QueryClauseIdentifier("git-entities"),
                        Clause = new OrQueryClause
                        {
                            Clauses =
                            [
                                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["git"]) },
                                new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["git-worktree"]) },
                            ],
                        },
                    },
                ],
            },
            context.CancellationToken).ConfigureAwait(false);

        var entities = queryResult.Batches.SelectMany(static batch => batch.Entities).ToList();
        var currentProfileNames = WorkspaceEntitySnapshotReader
            .GetEntityNames(context.CurrentComputerUserProfileEntity)
            .ToArray();
        var currentProfileId = context.CurrentComputerUserProfileEntity.EntityId;

        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var skipped = 0;
        var unknown = 0;
        var foreign = 0;
        var errors = 0;

        foreach (var queriedEntity in entities)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var entity = await WorkspaceToolEntityUtilities.TryGetEntityByIdAsync(
                context.DataAccessLayer, queriedEntity.EntityId, context.CancellationToken);
            if (entity?.Data is null)
            {
                continue;
            }

            var ownerId = WorkspaceEntitySnapshotReader.TryGetStringProperty(
                entity,
                "computer-user-profile-id");
            if (!string.IsNullOrWhiteSpace(ownerId)
                && !string.Equals(ownerId, currentProfileId.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                foreign++;
                continue;
            }

            var path = WorkspaceEntitySnapshotReader.TryGetStringProperty(entity, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                this.logger.LogDebug("Skipping entity {EntityId}: no path property.", entity.EntityId);
                skipped++;
                continue;
            }

            try
            {
                var availability = this.availabilityReader(path);
                if (availability == GitWorkspaceAvailability.ConfirmedMissing)
                {
                    availability = this.availabilityReader(path);
                }

                if (availability == GitWorkspaceAvailability.Unknown)
                {
                    this.logger.LogWarning("Preserving Git workspace {EntityId}: path availability is unknown.", entity.EntityId);
                    skipped++;
                    unknown++;
                    continue;
                }

                var existsOnFilesystem = availability == GitWorkspaceAvailability.Exists;
                var recycled = GitWorkspacePath.IsRecycled(path);
                var previousExists = TryGetExistsOnFilesystem(entity.Data);
                var metadata = existsOnFilesystem
                    ? this.metadataReader(path, this.logger)
                    : null;

                var owningRepository = WorkspaceEntitySnapshotReader.TryGetStringProperty(
                    entity,
                    "owning-repository");
                var incomingData = GitWorkspaceEntityData.Build(
                    path,
                    currentProfileNames,
                    metadata,
                    existsOnFilesystem,
                    owningRepository,
                    currentProfileId);

                PreserveExistingStructuralData(entity.Data, incomingData, preserveGit: metadata is null);
                var desiredReason = recycled ? "recycled" : existsOnFilesystem ? "visible" : "missing";
                var previousReason = WorkspaceEntitySnapshotReader.TryGetStringProperty(entity, "git-workspace-hidden-reason");
                var pending = WorkspaceEntitySnapshotReader.TryGetStringProperty(entity, "git-workspace-visibility-intent");
                var needsRelationshipReconciliation = pending is not null
                    || desiredReason == "visible"
                    || (desiredReason == "missing" && previousExists is not false)
                    || (desiredReason == "recycled" && previousReason != "recycled");
                if (needsRelationshipReconciliation)
                {
                    incomingData["git-workspace-visibility-intent"] = desiredReason;
                }
                if (desiredReason != "visible")
                {
                    incomingData["git-workspace-hidden-reason"] = desiredReason;
                }
                else
                {
                    incomingData.Remove("git-workspace-hidden-reason");
                }

                var relationship = await WorkspaceToolEntityUtilities.TryGetEntityByIdAsync(
                    context.DataAccessLayer, GetMissingRelationshipId(entity.EntityId), context.CancellationToken);
                if (desiredReason == "visible" && relationship?.Data is null && pending is null)
                {
                    incomingData.Remove("git-workspace-visibility-intent");
                    needsRelationshipReconciliation = false;
                }

                if (this.availabilityReader(path) != availability)
                {
                    this.logger.LogWarning("Preserving Git workspace {EntityId}: availability changed before commit.", entity.EntityId);
                    skipped++;
                    continue;
                }

                var entityChanged = !IsEntityUnchanged(entity.Data, incomingData);
                if (entityChanged)
                {
                    entity = await UpdateEntityAsync(
                        context.DataAccessLayer,
                        entity,
                        incomingData,
                        "Refresh Git workspace metadata and filesystem state.",
                        context.CancellationToken).ConfigureAwait(false);
                    if (entity.Data is null)
                    {
                        added++;
                    }
                    else
                    {
                        changed++;
                    }
                }
                else
                {
                    unchanged++;
                }

                if (needsRelationshipReconciliation)
                {
                    var current = await WorkspaceToolEntityUtilities.TryGetEntityByIdAsync(
                        context.DataAccessLayer, entity.EntityId, context.CancellationToken);
                    if (current?.ConcurrencyTag != entity.ConcurrencyTag)
                    {
                        this.logger.LogWarning("Deferring visibility reconciliation for {EntityId}: workspace changed.", entity.EntityId);
                        skipped++;
                        continue;
                    }

                    var revalidated = this.availabilityReader(path);
                    if (revalidated != availability)
                    {
                        this.logger.LogWarning("Deferring visibility reconciliation for {EntityId}: availability changed.", entity.EntityId);
                        skipped++;
                        continue;
                    }

                    if (desiredReason == "visible")
                    {
                        await DeleteMissingRelationshipAsync(
                            context.DataAccessLayer, entity.EntityId, context.CancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await UpsertMissingRelationshipAsync(
                            context.DataAccessLayer, entity.EntityId, recycled, context.CancellationToken).ConfigureAwait(false);
                    }

                    incomingData.Remove("git-workspace-visibility-intent");
                    await UpdateEntityAsync(context.DataAccessLayer, entity, incomingData,
                        "Complete Git workspace visibility reconciliation.", context.CancellationToken).ConfigureAwait(false);
                }

                if (existsOnFilesystem && metadata is null)
                {
                    this.logger.LogDebug(
                        "Could not read metadata for existing path '{Path}' on entity {EntityId}.",
                        path,
                        entity.EntityId);
                    skipped++;
                }
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidOperationException exception)
            {
                errors++;
                this.logger.LogError(
                    exception,
                    "Failed to update Git workspace entity {EntityId}.",
                    entity.EntityId);
            }
        }

        this.logger.LogInformation(
            "Git workspace update: added {Added}, changed {Changed}, unchanged {Unchanged}; skipped {Skipped}; unknown availability {Unknown}; foreign {Foreign}; errors {Errors}.",
            added,
            changed,
            unchanged,
            skipped,
            unknown,
            foreign,
            errors);

        return new WorkspaceToolExecutionResult
        {
            ResultContent =
                $"Added: {added}; changed: {changed}; unchanged: {unchanged}; skipped: {skipped}; unknown availability: {unknown}; foreign: {foreign}; errors: {errors}.",
        };
    }

    private static bool? TryGetExistsOnFilesystem(JsonElement? data)
    {
        if (data is { } element
            && element.TryGetProperty("exists-on-filesystem", out var exists)
            && exists.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            return exists.GetBoolean();
        }

        return null;
    }

    private static void PreserveExistingStructuralData(
        JsonElement? existingData,
        JsonObject incomingData,
        bool preserveGit)
    {
        if (existingData is not { } existing)
        {
            return;
        }

        CopyProperty(existing, incomingData, "names");
        CopyProperty(existing, incomingData, "display-name");
        foreach (var property in existing.EnumerateObject())
        {
            if (!incomingData.ContainsKey(property.Name))
            {
                incomingData[property.Name] = JsonNode.Parse(property.Value.GetRawText());
            }
        }
        if (preserveGit)
        {
            CopyProperty(existing, incomingData, "git");
        }
    }

    private static void CopyProperty(
        JsonElement source,
        JsonObject target,
        string propertyName)
    {
        if (source.TryGetProperty(propertyName, out var value))
        {
            target[propertyName] = JsonNode.Parse(value.GetRawText());
        }
    }

    private static async Task<EntitySnapshot> UpdateEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntitySnapshot entity,
        JsonObject data,
        string comment,
        CancellationToken cancellationToken)
    {
        data["entity-id"] = entity.EntityId.ToString();
        using var document = JsonDocument.Parse(data.ToJsonString());
        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = comment },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entity.EntityId,
                        ConcurrencyTag = entity.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        EnsureUpdateSucceeded(result, entity.EntityId);
        return result.EntityResults.Single(item => item.RequestedEntityId == entity.EntityId).CurrentEntity!;
    }

    private static async Task UpsertMissingRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId targetEntityId,
        bool recycled,
        CancellationToken cancellationToken)
    {
        var relationshipId = GetMissingRelationshipId(targetEntityId);
        var note = recycled ? "This Git workspace is in the Recycle Bin." : MissingWorkspaceNote;
        var existing = await WorkspaceToolEntityUtilities.TryGetEntityByIdAsync(
            dataAccessLayer, relationshipId, cancellationToken);
        if (existing?.Data is { } existingData
            && existingData.TryGetProperty("note", out var previousNote)
            && previousNote.GetString() == note)
        {
            return;
        }
        var data = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "not-interesting", "relationship"),
            ["participants"] = new JsonObject
            {
                ["target"] = targetEntityId.ToString(),
            },
            ["note"] = note,
        };
        _ = await WorkspaceToolEntityUtilities.UpsertEntityByDeterministicIdAsync(
            dataAccessLayer,
            relationshipId,
            data,
            static (_, incoming) => incoming,
            "Hide a Git workspace that is missing from the filesystem.",
            cancellationToken).ConfigureAwait(false);
    }

    private static async Task DeleteMissingRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId targetEntityId,
        CancellationToken cancellationToken)
    {
        var relationshipId = GetMissingRelationshipId(targetEntityId);
        var existing = await WorkspaceToolEntityUtilities.TryGetEntityByIdAsync(
            dataAccessLayer,
            relationshipId,
            cancellationToken).ConfigureAwait(false);
        if (existing?.Data is null)
        {
            return;
        }

        var result = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown
                    {
                        Text = "Restore a Git workspace that exists on the filesystem.",
                    },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = relationshipId,
                        ConcurrencyTag = existing.ConcurrencyTag,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = null,
                    },
                ],
            },
            cancellationToken).ConfigureAwait(false);
        EnsureUpdateSucceeded(result, relationshipId);
    }

    private static EntityId GetMissingRelationshipId(EntityId targetEntityId)
        => DeterministicEntityId.Create("git-workspace-missing", targetEntityId.ToString());

    private static void EnsureUpdateSucceeded(UpdateResult result, EntityId entityId)
    {
        var entityResult = result.EntityResults.Single(item => item.RequestedEntityId == entityId);
        if (entityResult.UpdateState != UpdateState.Failed && entityResult.Errors.Count == 0)
        {
            return;
        }

        var errors = string.Join(
            "; ",
            entityResult.Errors.Select(static error => error.Message));
        throw new InvalidOperationException($"Entity update failed for {entityId}: {errors}");
    }

    private static bool IsEntityUnchanged(JsonElement? existingData, JsonObject incomingData)
    {
        if (existingData is not { } existing)
        {
            return false;
        }

        return JsonPropertyEquals(existing, incomingData, "path")
            && JsonPropertyEquals(existing, incomingData, "owning-repository")
            && JsonPropertyEquals(existing, incomingData, "git")
            && JsonPropertyEquals(existing, incomingData, "computer-user-profile-id")
            && JsonPropertyEquals(existing, incomingData, "exists-on-filesystem")
            && JsonPropertyEquals(existing, incomingData, "git-workspace-hidden-reason")
            && JsonPropertyEquals(existing, incomingData, "git-workspace-visibility-intent");
    }

    private static bool JsonPropertyEquals(
        JsonElement existing,
        JsonObject incoming,
        string propertyName)
    {
        var hasExisting = existing.TryGetProperty(propertyName, out var existingValue);
        var hasIncoming = incoming.TryGetPropertyValue(propertyName, out var incomingValue);
        if (hasExisting != hasIncoming)
        {
            return false;
        }

        if (!hasExisting)
        {
            return true;
        }

        using var incomingDocument = JsonDocument.Parse(incomingValue!.ToJsonString());
        return JsonElement.DeepEquals(existingValue, incomingDocument.RootElement);
    }
}
