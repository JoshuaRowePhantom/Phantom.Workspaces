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
    private readonly ILogger<GitWorkspaceUpdateTool> logger;

    public GitWorkspaceUpdateTool(ILogger<GitWorkspaceUpdateTool>? logger = null)
    {
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

        var changes = new List<EntityChange>();
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

            var metadata = GitRepositoryMetadataReader.TryReadMetadata(path, this.logger);
            if (metadata == null)
            {
                this.logger.LogDebug("Skipping entity {EntityId}: could not read metadata for path '{Path}'.", entity.EntityId, path);
                skipped++;
                continue;
            }

            var entityNode = JsonNode.Parse(entity.Data!.Value.GetRawText())!.AsObject();
            var gitObject = new JsonObject();

            if (!string.IsNullOrWhiteSpace(metadata.BranchName))
            {
                gitObject["branch"] = metadata.BranchName;
            }

            if (!string.IsNullOrWhiteSpace(metadata.HeadCommitHash))
            {
                gitObject["head-commit"] = metadata.HeadCommitHash;
            }

            if (!string.IsNullOrWhiteSpace(metadata.OriginRemoteUrl))
            {
                gitObject["remotes"] = new JsonArray(
                    new JsonObject
                    {
                        ["name"] = "origin",
                        ["url"] = metadata.OriginRemoteUrl,
                    });
            }

            if (gitObject.Count > 0)
            {
                entityNode["git"] = gitObject;
            }
            else
            {
                entityNode.Remove("git");
            }

            using var updatedDocument = JsonDocument.Parse(entityNode.ToJsonString());
            changes.Add(new EntityChange
            {
                EntityId = entity.EntityId,
                ConcurrencyTag = entity.ConcurrencyTag,
                EntityChangeMode = EntityChangeMode.Replace,
                Data = updatedDocument.RootElement.Clone(),
            });
        }

        if (changes.Count > 0)
        {
            await context.DataAccessLayer.UpdateAsync(
                new UpdateRequest
                {
                    UpdateMetadata = new UpdateMetadata
                    {
                        Comment = new Markdown { Text = "Refresh git workspace metadata." },
                    },
                    Changes = changes,
                },
                context.CancellationToken).ConfigureAwait(false);
        }

        var updated = changes.Count;
        this.logger.LogInformation(
            "Git workspace update: refreshed {Updated} {Entity}; skipped {Skipped}.",
            updated,
            updated == 1 ? "entity" : "entities",
            skipped);

        return new WorkspaceToolExecutionResult
        {
            ResultContent = $"Updated {updated} {(updated == 1 ? "entity" : "entities")}; skipped {skipped}.",
        };
    }
}
