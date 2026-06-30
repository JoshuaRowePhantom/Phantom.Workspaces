using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
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
                    ClauseIdentifier = new QueryClauseIdentifier("git-worktree"),
                    Clause = new EntityTypeQueryClause { EntityTypeNames = new EntityTypeNameSet(["git-worktree"]) },
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

    [Fact]
    public async Task ExecuteAsync_BeforeEachScanRoot_LogsInformationWithPath()
    {
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains(this.scanRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_WhenRepositoryFound_LogsDebugWithRepoPath()
    {
        var repo = this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Debug
            && e.Message.Contains(repo, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_AfterEachScanRoot_LogsInformationSummaryWithCountAndPath()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("2", StringComparison.Ordinal)
            && e.Message.Contains(this.scanRoot, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Run_CreatesGitEntities_AgainstFullDalStack()
    {
        var repo = this.MakeRepo("project-a");
        var dataAccessLayer = await CreateProductionStyleDataAccessLayerAsync();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        Assert.Equal(repo, single.GetProperty("path").GetString());
    }

    [Fact]
    public async Task Run_WhenAllEntitiesFailValidation_LogsWarning()
    {
        this.MakeRepo("project-a");
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);
        var dataAccessLayer = new AlwaysFailingDataAccessLayer();

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("rejected", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task ExecuteAsync_FirstRun_ResultContentReportsAddedCount()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new GitWorkspaceScanTool();

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.Contains("added: 2", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_SecondRunSameRepos_ResultContentReportsUnchangedCount()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new GitWorkspaceScanTool();
        await tool.ExecuteAsync(this.Context(dataAccessLayer)); // first run

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer)); // second run — identical

        Assert.NotNull(result.ResultContent);
        Assert.Contains("unchanged: 2", result.ResultContent, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ExecuteAsync_FirstRun_LogsAddedCount()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("added", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExecuteAsync_SecondRunSameRepos_LogsUnchangedCount()
    {
        this.MakeRepo("project-a");
        this.MakeRepo("project-b");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var logger = new TestLogger<GitWorkspaceScanTool>();
        var tool = new GitWorkspaceScanTool(logger: logger);
        await tool.ExecuteAsync(this.Context(dataAccessLayer)); // first run

        logger.Entries.Clear();
        await tool.ExecuteAsync(this.Context(dataAccessLayer)); // second run — unchanged

        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Information
            && e.Message.Contains("unchanged", StringComparison.OrdinalIgnoreCase)
            && e.Message.Contains("2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Run_CreatesGitWorktreeEntityType()
    {
        this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        var entityTypes = single.GetProperty("entity-types").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("git-worktree", entityTypes);
        Assert.DoesNotContain("git", entityTypes);
    }

    [Fact]
    public async Task Run_SetsNamesToGitWorktreesNamespace()
    {
        var repo = this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        var firstNameArray = single.GetProperty("names").EnumerateArray().First();
        var components = firstNameArray.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal("git-worktrees", components[0]);
        Assert.Equal(repo, components[1]);
    }

    [Fact]
    public async Task Run_WhenProfileHasComputerUserProfilesName_IncludesProfileNameEntry()
    {
        this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var context = this.ContextWithProfile(dataAccessLayer, ["computer-user-profiles", "users", "test-user", "computers", "test-computer"]);

        await new GitWorkspaceScanTool().ExecuteAsync(context);

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        var allNameArrays = single.GetProperty("names").EnumerateArray().ToArray();
        var profileEntry = allNameArrays.FirstOrDefault(n =>
            n.EnumerateArray().First().GetString() == "computer-user-profiles");
        Assert.True(profileEntry.ValueKind == System.Text.Json.JsonValueKind.Array, "Expected a profile name entry");
        var components = profileEntry.EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "computer-user-profiles", "users", "test-user", "computers", "test-computer" }, components!);
    }

    [Fact]
    public async Task Run_WhenProfileHasNoComputerUserProfilesName_OmitsProfileNameEntry()
    {
        this.MakeRepo("project-a");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        // Default context has a placeholder profile with name ["placeholder"] -- not computer-user-profiles

        await new GitWorkspaceScanTool().ExecuteAsync(this.Context(dataAccessLayer));

        var entities = await GitEntitiesAsync(dataAccessLayer);
        var single = Assert.Single(entities);
        var allNameArrays = single.GetProperty("names").EnumerateArray().ToArray();
        Assert.Single(allNameArrays); // only the primary git-worktrees name
    }

    private WorkspaceToolExecutionContext ContextWithProfile(IDataAccessLayer dataAccessLayer, string[] profileNameComponents)
    {
        var namesJson = string.Join(", ", profileNameComponents.Select(c => JsonSerializer.Serialize(c)));
        var profileJson = $$"""
            {
              "entity-id": "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
              "entity-types": ["entity", "user-computer-profile"],
              "names": [[{{namesJson}}]]
            }
            """;
        var profileSnapshot = WorkspaceToolExecutionContextTestFactory.CreateSnapshot(profileJson);
        var toolJson = $$"""{ "entity-types": ["entity", "tool"], "tool-type": "git-workspace-scan", "scan-root": {{JsonSerializer.Serialize(this.scanRoot)}} }""";
        return WorkspaceToolExecutionContextTestFactory.Create(dataAccessLayer, toolJson, profileSnapshot);
    }

    private static async Task<IDataAccessLayer> CreateProductionStyleDataAccessLayerAsync()
    {
        var underlying = new InMemoryDataAccessLayer();
        var dal = new ReferentialIntegrityDataAccessLayer(underlying);
        var errors = await new SchemaPopulator(dal).Populate();
        Assert.Empty(errors);
        return dal;
    }
}

internal sealed class AlwaysFailingDataAccessLayer : IDataAccessLayer
{
    public Task<UpdateResult> UpdateAsync(UpdateRequest request, CancellationToken cancellationToken = default)
    {
        var results = request.Changes.Select(change => new EntityUpdateResult
        {
            UpdateState = UpdateState.Failed,
            RequestedEntityId = change.EntityId ?? default,
            ResultingEntityId = change.EntityId ?? default,
            ConcurrencyMatchState = ConcurrencyMatchState.NotMatched,
            Errors = [],
        }).ToArray();
        return Task.FromResult(new UpdateResult { EntityResults = results });
    }

    public Task<GetResult> GetAsync(GetRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GetResult { Batches = [] });

    public Task<QueryResult> QueryAsync(QueryRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new QueryResult { Batches = [] });

    public Task<GetHistoryResult> GetHistoryAsync(GetHistoryRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GetHistoryResult { History = [] });

    [Obsolete]
    public Task<ExportResult> ExportAsync(ExportRequest request, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    public Task<GetChangedEntitiesResult> GetChangedEntitiesAsync(GetChangedEntitiesRequest request, CancellationToken cancellationToken = default)
        => Task.FromResult(new GetChangedEntitiesResult { Entities = [] });
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


