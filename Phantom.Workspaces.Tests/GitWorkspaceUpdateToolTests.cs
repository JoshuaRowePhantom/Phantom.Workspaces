using System;
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

    private static async Task<EntityId> SeedGitEntityAsync(IDataAccessLayer dal, string path, string? existingGitJson = null)
    {
        var entityId = new EntityId(Guid.NewGuid());
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
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader);

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
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader);

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
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader, logger: logger);

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
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader);

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
        var tool = new GitWorkspaceUpdateTool(metadataReader: fakeReader);

        var result = await tool.ExecuteAsync(Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("changed: 1", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }
}
