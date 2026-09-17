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
    private readonly Func<string, bool> directoryExists;
    private readonly ILogger<GitWorkspaceUpdateTool> logger;

    public GitWorkspaceUpdateTool(
        Func<string, ILogger, GitMetadata?>? metadataReader = null,
        ILogger<GitWorkspaceUpdateTool>? logger = null,
        Func<string, bool>? directoryExists = null)
    {
        this.metadataReader = metadataReader ?? GitRepositoryMetadataReader.TryReadMetadata;
        this.logger = logger ?? NullLogger<GitWorkspaceUpdateTool>.Instance;
        this.directoryExists = directoryExists ?? Directory.Exists;
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
        var foreign = 0;
        var errors = 0;

        foreach (var entity in entities)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

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
                var existsOnFilesystem = this.directoryExists(path);
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

                var entityChanged = !IsEntityUnchanged(entity.Data, incomingData);
                if (entityChanged)
                {
                    await UpdateEntityAsync(
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

                if (!existsOnFilesystem && previousExists is not false)
                {
                    await UpsertMissingRelationshipAsync(
                        context.DataAccessLayer,
                        entity.EntityId,
                        context.CancellationToken).ConfigureAwait(false);
                }
                else if (existsOnFilesystem && previousExists is false)
                {
                    await DeleteMissingRelationshipAsync(
                        context.DataAccessLayer,
                        entity.EntityId,
                        context.CancellationToken).ConfigureAwait(false);
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
            "Git workspace update: added {Added}, changed {Changed}, unchanged {Unchanged}; skipped {Skipped}; foreign {Foreign}; errors {Errors}.",
            added,
            changed,
            unchanged,
            skipped,
            foreign,
            errors);

        return new WorkspaceToolExecutionResult
        {
            ResultContent =
                $"Added: {added}; changed: {changed}; unchanged: {unchanged}; skipped: {skipped}; foreign: {foreign}; errors: {errors}.",
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

    private static async Task UpdateEntityAsync(
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
    }

    private static async Task UpsertMissingRelationshipAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId targetEntityId,
        CancellationToken cancellationToken)
    {
        var relationshipId = GetMissingRelationshipId(targetEntityId);
        var data = new JsonObject
        {
            ["entity-types"] = new JsonArray("entity", "not-interesting", "relationship"),
            ["participants"] = new JsonObject
            {
                ["target"] = targetEntityId.ToString(),
            },
            ["note"] = MissingWorkspaceNote,
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
            && JsonPropertyEquals(existing, incomingData, "exists-on-filesystem");
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
