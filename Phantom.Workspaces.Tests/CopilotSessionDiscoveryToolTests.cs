using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.ScheduledTools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class CopilotSessionDiscoveryToolTests : IDisposable
{
    private readonly string sessionStateRoot;

    public CopilotSessionDiscoveryToolTests()
    {
        this.sessionStateRoot = Path.Combine(Path.GetTempPath(), "pw-copilot-sessions-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.sessionStateRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.sessionStateRoot))
            {
                Directory.Delete(this.sessionStateRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private Guid AddSession()
    {
        var sessionId = Guid.NewGuid();
        Directory.CreateDirectory(Path.Combine(this.sessionStateRoot, sessionId.ToString()));
        return sessionId;
    }

    private ScheduledToolContext Context(IDataAccessLayer dataAccessLayer)
    {
        using var toolEntity = JsonDocument.Parse(
            $$"""{ "type": "copilot-session-discovery", "session-state-root": {{JsonSerializer.Serialize(this.sessionStateRoot)}} }""");
        return new ScheduledToolContext
        {
            ToolEntity = toolEntity.RootElement.Clone(),
            TargetEntityIds = [],
            DataAccessLayer = dataAccessLayer,
        };
    }

    private static async Task<JsonElement?> GetEntityAsync(IDataAccessLayer dataAccessLayer, Guid entityId)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityId = new EntityId(entityId) }],
            Timestamps = [null],
        });
        return result.Batches.SelectMany(batch => batch.Entities).FirstOrDefault(e => e.EntityId == new EntityId(entityId))?.Data;
    }

    [Fact]
    public async Task Run_CreatesAgentDefinitionPerSession_KeyedBySessionGuid()
    {
        var first = this.AddSession();
        var second = this.AddSession();
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new CopilotSessionDiscoveryTool().RunAsync(this.Context(dataAccessLayer), default);

        foreach (var sessionId in new[] { first, second })
        {
            var entity = await GetEntityAsync(dataAccessLayer, sessionId);
            Assert.NotNull(entity);
            Assert.Equal("agent-definition", entity!.Value.GetProperty("entity-types")[0].GetString());
            var name = entity.Value.GetProperty("names")[0].EnumerateArray().Select(c => c.GetString()).ToArray();
            Assert.Equal("copilot-sessions", name[0]);
            Assert.Equal(sessionId.ToString(), name[1]);
            Assert.Equal("github-copilot", entity.Value.GetProperty("definition").GetProperty("model").GetProperty("provider").GetString());
            Assert.Equal(sessionId.ToString(), entity.Value.GetProperty("definition").GetProperty("metadata").GetProperty("copilot-session-id").GetString());
        }
    }

    [Fact]
    public async Task Run_IsIdempotent_AcrossRuns()
    {
        this.AddSession();
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new CopilotSessionDiscoveryTool();

        await tool.RunAsync(this.Context(dataAccessLayer), default);
        await tool.RunAsync(this.Context(dataAccessLayer), default);

        var export = await dataAccessLayer.ExportAsync(new ExportRequest());
        var agentDefinitions = export.ChangeBatches
            .SelectMany(b => b.Entities)
            .Select(e => e.EntityId)
            .Distinct()
            .Count();
        Assert.Equal(1, agentDefinitions);
    }

    [Fact]
    public async Task Run_SkipsNonGuidDirectories()
    {
        Directory.CreateDirectory(Path.Combine(this.sessionStateRoot, "not-a-session"));
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new CopilotSessionDiscoveryTool().RunAsync(this.Context(dataAccessLayer), default);

        var export = await dataAccessLayer.ExportAsync(new ExportRequest());
        Assert.Empty(export.ChangeBatches.SelectMany(b => b.Entities));
    }

    [Fact]
    public async Task Run_ProducesAgentDefinitionThatValidatesAgainstSchema()
    {
        var sessionId = this.AddSession();

        // EntityRepository for an in-memory source builds the schema-validating pipeline and seeds the
        // schemas, so a produced agent-definition that does not conform would fail the update.
        var repository = await EntityRepository.CreateAsync(new UnknownRepositorySource());

        await new CopilotSessionDiscoveryTool().RunAsync(this.Context(repository.DataAccessLayer), default);

        var entity = await GetEntityAsync(repository.DataAccessLayer, sessionId);
        Assert.NotNull(entity);
        Assert.Equal("agent-definition", entity!.Value.GetProperty("entity-types")[0].GetString());
    }
}
