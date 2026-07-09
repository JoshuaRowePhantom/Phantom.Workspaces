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
/// A built-in scheduled tool that refreshes the git metadata (branch, HEAD commit, remotes) for
/// all existing <c>git</c> and <c>git-worktree</c> entities that have a <c>path</c> property.
/// This keeps entities up to date between full scans — for example, when the HEAD of a known
/// repository moves.
/// </summary>
public sealed class GitWorkspaceUpdateTool : IWorkspaceTool
{
    private readonly Func<string, ILogger, GitMetadata?> metadataReader;
    private readonly ILogger<GitWorkspaceUpdateTool> logger;

    /// <param name="metadataReader">
    /// Reads git metadata for a repository path. Defaults to
    /// <see cref="GitRepositoryMetadataReader.TryReadMetadata"/>; overridable for testing.
    /// </param>
    /// <param name="logger">Logger for this tool; defaults to <see cref="NullLogger{T}.Instance"/>.</param>
    public GitWorkspaceUpdateTool(
        Func<string, ILogger, GitMetadata?>? metadataReader = null,
        ILogger<GitWorkspaceUpdateTool>? logger = null)
    {
        this.metadataReader = metadataReader ?? GitRepositoryMetadataReader.TryReadMetadata;
        this.logger = logger ?? NullLogger<GitWorkspaceUpdateTool>.Instance;
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

        var entities = queryResult.Batches.SelectMany(static b => b.Entities).ToList();
        var currentProfileNames = WorkspaceEntitySnapshotReader.GetEntityNames(context.CurrentComputerUserProfileEntity)
            .ToArray();

        var added = 0;
        var changed = 0;
        var unchanged = 0;
        var skipped = 0;

        foreach (var entity in entities)
        {
            context.CancellationToken.ThrowIfCancellationRequested();

            var path = WorkspaceEntitySnapshotReader.TryGetStringProperty(entity, "path");
            if (string.IsNullOrWhiteSpace(path))
            {
                this.logger.LogDebug("Skipping entity {EntityId}: no path property.", entity.EntityId);
                skipped++;
                continue;
            }

            var metadata = this.metadataReader(path, this.logger);
            if (metadata == null)
            {
                this.logger.LogDebug("Skipping entity {EntityId}: could not read metadata for path '{Path}'.", entity.EntityId, path);
                skipped++;
                continue;
            }

            var owningRepository = WorkspaceEntitySnapshotReader.TryGetStringProperty(entity, "owning-repository");
            var normalizedPath = NormalizeRepositoryPath(path);
            var deterministicId = DeterministicEntityId.Create("git-workspace", normalizedPath);

            var incomingData = GitWorkspaceEntityData.Build(
                path,
                currentProfileNames,
                metadata,
                owningRepository);

            // Check if git section would be unchanged
            if (IsEntityUnchanged(entity.Data, incomingData))
            {
                unchanged++;
                continue;
            }

            var updateResult = await WorkspaceToolEntityUtilities.UpsertEntityByDeterministicIdAsync(
                context.DataAccessLayer,
                deterministicId,
                incomingData,
                GitWorkspaceEntityData.MergePreservingUserEditableFields,
                "Refresh git workspace metadata.",
                context.CancellationToken);

            if (entity.Data is null)
            {
                added++;
            }
            else
            {
                changed++;
            }
        }

        this.logger.LogInformation(
            "Git workspace update: added {Added}, changed {Changed}, unchanged {Unchanged}; skipped {Skipped}.",
            added,
            changed,
            unchanged,
            skipped);

        return new WorkspaceToolExecutionResult
        {
            ResultContent = $"Added: {added}; changed: {changed}; unchanged: {unchanged}; skipped: {skipped}.",
        };
    }

    private static bool IsEntityUnchanged(JsonElement? existingData, JsonObject incomingData)
    {
        if (existingData is not { } existing)
        {
            return false;
        }

        // Compare all system-managed fields: path, owning-repository, git
        if (!JsonPropertyEquals(existing, incomingData, "path"))
        {
            return false;
        }

        if (!JsonPropertyEquals(existing, incomingData, "owning-repository"))
        {
            return false;
        }

        if (!JsonPropertyEquals(existing, incomingData, "git"))
        {
            return false;
        }

        return true;
    }

    private static bool JsonPropertyEquals(JsonElement existing, JsonObject incoming, string propertyName)
    {
        var hasExisting = existing.TryGetProperty(propertyName, out var existingValue);

        JsonNode? incomingValue = null;
        var hasIncoming = incoming.ContainsKey(propertyName);
        if (hasIncoming)
        {
            incomingValue = incoming[propertyName];
        }

        if (hasExisting != hasIncoming)
        {
            return false;
        }

        if (!hasExisting)
        {
            return true;
        }

        using var incomingDoc = JsonDocument.Parse(incomingValue!.ToJsonString());
        return JsonElement.DeepEquals(existingValue, incomingDoc.RootElement);
    }

    private static string NormalizeRepositoryPath(string repositoryPath)
    {
        return Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();
    }
}
