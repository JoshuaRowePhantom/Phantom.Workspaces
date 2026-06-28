using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class GitWorkspaceScanToolTests : IDisposable
{
    private readonly string scanRoot;

    public GitWorkspaceScanToolTests()
    {
        this.scanRoot = Path.Combine(Path.GetTempPath(), "pw-git-scan-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.scanRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.scanRoot))
            {
                Directory.Delete(this.scanRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string MakeRepo(params string[] relativeSegments)
    {
        var repoPath = Path.Combine(new[] { this.scanRoot }.Concat(relativeSegments).ToArray());
        Directory.CreateDirectory(Path.Combine(repoPath, ".git"));
        return Path.GetFullPath(repoPath);
    }

    private WorkspaceToolExecutionContext Context(IDataAccessLayer dataAccessLayer) =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan", "scan-root": {{JsonSerializer.Serialize(this.scanRoot)}} }""");

    private static async Task<JsonElement[]> GitEntitiesAsync(IDataAccessLayer dataAccessLayer)
    {
        var result = await dataAccessLayer.QueryAsync(new QueryRequest
        {
            Clauses =
            [
                new TopLevelQueryClause
                {
                    ClauseIdentifier = new QueryClauseIdentifier("git"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["git"]) },
                },
            ],
        });
        return result.Batches.SelectMany(b => b.Entities).Select(e => e.Data!.Value).ToArray();
    }

    [Fact]
    public async Task Run_CreatesGitEntityPerRepository()
    {
        var repoA = this.MakeRepo("project-a");
        var repoB = this.MakeRepo("nested", "project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var paths = entities.Select(e => e.GetProperty("path").GetString()).ToHashSet();
        Assert.Equal(2, entities.Length);
        Assert.Contains(repoA, paths);
        Assert.Contains(repoB, paths);
    }

    [Fact]
    public async Task Run_DoesNotDescendIntoARepository()
    {
        var outer = this.MakeRepo("outer");
        // A nested repo inside an already-discovered repo must not be reported.
        Directory.CreateDirectory(Path.Combine(outer, "vendored", ".git"));
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        Assert.Equal(outer, single.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Run_IsIdempotent_AcrossRuns()
    {
        this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new GitWorkspaceScanTool();

        await tool.ExecuteAsync(this.Context(dataAccessLayer));
        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Single(await GitEntitiesAsync(dataAccessLayer));
    }

    [Fact]
    public async Task Run_NoRepositories_DoesNothing()
    {
        Directory.CreateDirectory(Path.Combine(this.scanRoot, "just-a-folder"));
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Empty(await GitEntitiesAsync(dataAccessLayer));
    }

    [Fact]
    public async Task Run_WithoutScanRoot_DefaultsToLocalDrives()
    {
        var repo = this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        // No scan-root configured; the tool falls back to the (here, fake) local fixed-drive roots.
        var tool = new GitWorkspaceScanTool(localFixedDriveRootsProvider: () => [this.scanRoot]);
        var context = WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            """{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan" }""");

        await tool.ExecuteAsync(context);

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        Assert.Equal(repo, single.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Run_WithScanRoots_ScansAllConfiguredRoots_Deduplicated()
    {
        var rootA = Path.Combine(this.scanRoot, "a");
        var rootB = Path.Combine(this.scanRoot, "b");
        Directory.CreateDirectory(rootA);
        Directory.CreateDirectory(rootB);
        var repoA = this.MakeRepo("a", "project-a");
        var repoB = this.MakeRepo("b", "project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        // Configure scan-roots (and overlapping scan-root) — the same repo must not be reported twice.
        var context = WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan", "scan-roots": [{{JsonSerializer.Serialize(rootA)}}, {{JsonSerializer.Serialize(rootB)}}], "scan-root": {{JsonSerializer.Serialize(rootA)}} }""");
        // The fake drive provider would return nothing, proving the configured roots take precedence.
        var tool = new GitWorkspaceScanTool(localFixedDriveRootsProvider: Array.Empty<string>);

        await tool.ExecuteAsync(context);

        var paths = (await GitEntitiesAsync(dataAccessLayer)).Select(e => e.GetProperty("path").GetString()).ToHashSet();
        Assert.Equal(2, paths.Count);
        Assert.Contains(repoA, paths);
        Assert.Contains(repoB, paths);
    }

    [Fact]
    public async Task Run_WithScanRoot_WhenPathDoesNotExist_LogsWarningAndProducesNoEntities()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var nonExistentPath = Path.Combine(this.scanRoot, "does-not-exist");
        // scan-root is configured but the path is absent; configured roots must be respected,
        // no fallback to local drives.
        var context = WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan", "scan-root": {{JsonSerializer.Serialize(nonExistentPath)}} }""");
        var tool = new GitWorkspaceScanTool(localFixedDriveRootsProvider: Array.Empty<string>, logger: logger);

        var result = await tool.ExecuteAsync(context);

        Assert.Empty(await GitEntitiesAsync(dataAccessLayer));
        Assert.NotNull(result.ResultContent);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task Run_WithScanRoots_WhenAllPathsDoNotExist_LogsWarningAndProducesNoEntities()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var nonExistentA = Path.Combine(this.scanRoot, "gone-a");
        var nonExistentB = Path.Combine(this.scanRoot, "gone-b");
        // All scan-roots are absent; configured roots must be respected, no fallback to local drives.
        var context = WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan", "scan-roots": [{{JsonSerializer.Serialize(nonExistentA)}}, {{JsonSerializer.Serialize(nonExistentB)}}] }""");
        var tool = new GitWorkspaceScanTool(localFixedDriveRootsProvider: Array.Empty<string>, logger: logger);

        var result = await tool.ExecuteAsync(context);

        Assert.Empty(await GitEntitiesAsync(dataAccessLayer));
        Assert.NotNull(result.ResultContent);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task ExecuteAsync_WhenDriveEnumerationFails_LogsWarning()
    {
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var driveEnumerationException = new IOException("simulated drive enumeration failure");
        IEnumerable<string> ThrowOnDriveEnum()
        {
            throw driveEnumerationException;
        }

        var dataAccessLayer = new InMemoryDataAccessLayer();
        // No scan-root configured — falls back to drive provider (which throws).
        var context = WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            """{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan" }""");
        var tool = new GitWorkspaceScanTool(localFixedDriveRootsProvider: ThrowOnDriveEnum, logger: logger);

        await tool.ExecuteAsync(context);

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Exception == driveEnumerationException);
    }

    [Fact]
    public async Task ExecuteAsync_ReturnsResultContentWithScanSummary()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new GitWorkspaceScanTool();

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("2", result.ResultContent, StringComparison.Ordinal);
        Assert.Contains(this.scanRoot, result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }
}

internal sealed class TestLogger<T> : ILogger<T>
{
    public sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    public List<LogEntry> Entries { get; } = [];

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        this.Entries.Add(new LogEntry(logLevel, exception, formatter(state, exception)));
    }
}
