using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
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

    private ScheduledToolContext Context(IDataAccessLayer dataAccessLayer)
    {
        using var toolEntity = JsonDocument.Parse(
            $$"""{ "type": "git-workspace-scan", "scan-root": {{JsonSerializer.Serialize(this.scanRoot)}} }""");
        return new ScheduledToolContext
        {
            ToolEntity = toolEntity.RootElement.Clone(),
            TargetEntityIds = [],
            DataAccessLayer = dataAccessLayer,
        };
    }

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

        await new GitWorkspaceScanTool().RunAsync(this.Context(dataAccessLayer), default);

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

        await new GitWorkspaceScanTool().RunAsync(this.Context(dataAccessLayer), default);

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

        await tool.RunAsync(this.Context(dataAccessLayer), default);
        await tool.RunAsync(this.Context(dataAccessLayer), default);

        Assert.Single(await GitEntitiesAsync(dataAccessLayer));
    }

    [Fact]
    public async Task Run_NoRepositories_DoesNothing()
    {
        Directory.CreateDirectory(Path.Combine(this.scanRoot, "just-a-folder"));
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new GitWorkspaceScanTool().RunAsync(this.Context(dataAccessLayer), default);

        Assert.Empty(await GitEntitiesAsync(dataAccessLayer));
    }
}
