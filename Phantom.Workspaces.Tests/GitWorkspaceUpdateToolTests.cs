using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorkspaceUpdateToolTests
{
    private static WorkspaceToolExecutionContext Context(IDataAccessLayer dataAccessLayer) =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            """{ "entity-id": "00000000-0000-0000-0001-000000000001", "entity-types": ["entity", "tool"], "tool-type": "git-workspace-update" }""");

    private static async Task<IDataAccessLayer> CreateProductionStyleDataAccessLayerAsync()
    {
        var underlying = new InMemoryDataAccessLayer();
        var dal = ValidatedDataAccessLayerFactory.Create(underlying);
        var errors = await new SchemaPopulator(dal).Populate();
        Assert.Empty(errors);
        return dal;
    }

    private static async Task<EntityId> SeedGitEntityAsync(IDataAccessLayer dal, string path, string? existingGitJson = null)
    {
        // Use deterministic ID based on path (matching the new implementation)
        var normalizedPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).ToLowerInvariant();
        var entityId = DeterministicEntityId.Create("git-workspace", normalizedPath);
        var gitSection = existingGitJson is not null ? $@", ""git"": {existingGitJson}" : string.Empty;
        var json = $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git"],
              "names": [["git", "{{path}}"]],
              "display-name": {"default": "repo"},
              "path": "{{path}}"
              {{gitSection}}
            }
            """;
        await dal.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes = [new EntityChange
            {
                EntityChangeMode = EntityChangeMode.Replace,
                Data = JsonDocument.Parse(json).RootElement.Clone(),
            }],
        }, TestContext.Current.CancellationToken);
        return entityId;
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoGitEntities_ReturnsEmptySummary()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("0", result.ResultContent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGitMetadataPresent_ResultContentReportsChanged()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedGitEntityAsync(dataAccessLayer, "/repo/path");
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "main", HeadCommitHash = "abc123" };
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, directoryExists: _ => true);

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGitMetadataUnchanged_ResultContentReportsUnchanged()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedGitEntityAsync(dataAccessLayer, "/repo/path");
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "main", HeadCommitHash = "abc123" };
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, directoryExists: _ => true);

        await tool.ExecuteAsync(Context(dataAccessLayer)); // first run — populates git section

        var result = await tool.ExecuteAsync(Context(dataAccessLayer)); // second run — same metadata

        Assert.NotNull(result.ResultContent);
        Assert.Contains("unchanged: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenGitMetadataUnchanged_LogsUnchangedCount()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedGitEntityAsync(dataAccessLayer, "/repo/path");
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "main", HeadCommitHash = "abc123" };
        var logger = new TestLogger<GitWorkspaceUpdateTool>();
        var tool = new GitWorkspaceUpdateTool(
            metadataReader: fakeReader,
            logger: logger,
            directoryExists: _ => true);

        await tool.ExecuteAsync(Context(dataAccessLayer)); // first run
        logger.Entries.Clear();
        await tool.ExecuteAsync(Context(dataAccessLayer)); // second run — unchanged

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("unchanged", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("1", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_WhenGitMetadataChanges_ResultContentReportsChanged()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        await SeedGitEntityAsync(dataAccessLayer, "/repo/path");
        var commit = "initial";
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "main", HeadCommitHash = commit };
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, directoryExists: _ => true);

        await tool.ExecuteAsync(Context(dataAccessLayer)); // first run with "initial"
        commit = "updated-commit";
        var result = await tool.ExecuteAsync(Context(dataAccessLayer)); // second run with different HEAD

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_WhenEntityHasNoPath_CountsAsSkipped()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var json = """
            {
              "entity-id": "00000000-0000-0000-0002-000000000001",
              "entity-types": ["entity", "git"],
              "names": [["git", "no-path-entity"]],
              "display-name": {"default": "no-path"}
            }
            """;
        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes = [new EntityChange
            {
                EntityChangeMode = EntityChangeMode.Replace,
                Data = JsonDocument.Parse(json).RootElement.Clone(),
            }],
        }, TestContext.Current.CancellationToken);
        var tool = new GitWorkspaceUpdateTool();

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("skipped: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }
    [Fact]
    public async Task ExecuteAsync_WhenGitWorktreeEntity_ResultContentReportsChanged()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var entityId = new EntityId(Guid.NewGuid());
        var json = $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git-worktree"],
              "names": [["git-worktrees", "/repo/path"]],
              "display-name": {"default": "repo"},
              "path": "/repo/path"
            }
            """;
        await dataAccessLayer.UpdateAsync(new UpdateRequest
        {
            UpdateMetadata = new UpdateMetadata { Comment = new Markdown { Text = "seed" } },
            Changes = [new EntityChange
            {
                EntityChangeMode = EntityChangeMode.Replace,
                Data = JsonDocument.Parse(json).RootElement.Clone(),
            }],
        }, TestContext.Current.CancellationToken);
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "main", HeadCommitHash = "abc123" };
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, directoryExists: _ => true);

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_AgainstFullDalStack_CompletesSuccessfully()
    {
        var dataAccessLayer = await CreateProductionStyleDataAccessLayerAsync();
        await SeedGitEntityAsync(dataAccessLayer, "/repo/test-path");
        Func<string, ILogger, GitMetadata?> fakeReader = (_, _) =>
            new GitMetadata { BranchName = "develop", HeadCommitHash = "def456" };
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, directoryExists: _ => true);

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_MissingThenRestored_AgainstFullDalStack_ReconcilesSystemHideRelationship()
    {
        var dataAccessLayer = await CreateProductionStyleDataAccessLayerAsync();
        var entityId = new EntityId(Guid.NewGuid());
        using var document = JsonDocument.Parse(
            $$"""
            {
              "entity-id": "{{entityId}}",
              "entity-types": ["entity", "git-worktree", "filesystem-path"],
              "names": [["git-worktrees", "C:/missing/full-dal-worktree"]],
              "display-name": {"default": "full-dal-worktree"},
              "path": "C:/missing/full-dal-worktree",
              "exists-on-filesystem": true,
              "git": {"branch": "preserved"}
            }
            """);
        var seedResult = await dataAccessLayer.UpdateAsync(
            new UpdateRequest
            {
                UpdateMetadata = new UpdateMetadata
                {
                    Comment = new Markdown { Text = "Seed full-DAL missing worktree." },
                },
                Changes =
                [
                    new EntityChange
                    {
                        EntityId = entityId,
                        EntityChangeMode = EntityChangeMode.Replace,
                        Data = document.RootElement.Clone(),
                    },
                ],
            },
            TestContext.Current.CancellationToken);
        Assert.DoesNotContain(seedResult.EntityResults, static item => item.UpdateState == UpdateState.Failed);
        var context = Context(dataAccessLayer);
        var missingTool = new GitWorkspaceUpdateTool(directoryExists: _ => false);

        await missingTool.ExecuteAsync(context);

        var missingEntity = await GetEntityAsync(dataAccessLayer, entityId);
        Assert.False(missingEntity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        Assert.Equal(
            "preserved",
            missingEntity.Data.Value.GetProperty("git").GetProperty("branch").GetString());
        var relationshipId = DeterministicEntityId.Create(
            "git-workspace-missing",
            entityId.ToString());
        var missingRelationship = await GetEntityAsync(dataAccessLayer, relationshipId);
        Assert.Equal(
            "This Git workspace does not exist on the filesystem.",
            missingRelationship!.Data!.Value.GetProperty("note").GetString());

        var restoredTool = new GitWorkspaceUpdateTool(
            metadataReader: (_, _) => new GitMetadata
            {
                BranchName = "restored",
                HeadCommitHash = "abc123",
            },
            directoryExists: _ => true);
        await restoredTool.ExecuteAsync(context);

        var restoredEntity = await GetEntityAsync(dataAccessLayer, entityId);
        Assert.True(restoredEntity!.Data!.Value.GetProperty("exists-on-filesystem").GetBoolean());
        Assert.Equal(
            "restored",
            restoredEntity.Data.Value.GetProperty("git").GetProperty("branch").GetString());
        var removedRelationship = await GetEntityAsync(dataAccessLayer, relationshipId);
        Assert.True(removedRelationship is null || removedRelationship.Data is null);
    }

    private static async Task<EntitySnapshot?> GetEntityAsync(
        IDataAccessLayer dataAccessLayer,
        EntityId entityId)
    {
        var result = await dataAccessLayer.GetAsync(
            new GetRequest
            {
                Entities = [new GetEntityRequest { EntityId = entityId }],
            },
            TestContext.Current.CancellationToken);
        return result.Batches.SelectMany(static batch => batch.Entities).FirstOrDefault();
    }
}
