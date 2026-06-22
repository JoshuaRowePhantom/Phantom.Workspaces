using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Phantom.Workspaces;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
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

    private WorkspaceToolExecutionContext Context(IDataAccessLayer dataAccessLayer) =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["tool"], "tool-type": "copilot-session-discovery", "session-state-root": {{JsonSerializer.Serialize(this.sessionStateRoot)}}, "mcp-config-path": {{JsonSerializer.Serialize(Path.Combine(this.sessionStateRoot, "nonexistent-mcp-config.json"))}} }""");

    private WorkspaceToolExecutionContext ContextWithMcpConfig(IDataAccessLayer dataAccessLayer, string mcpConfigPath) =>
        WorkspaceToolExecutionContextTestFactory.Create(
            dataAccessLayer,
            $$"""{ "entity-types": ["tool"], "tool-type": "copilot-session-discovery", "session-state-root": {{JsonSerializer.Serialize(this.sessionStateRoot)}}, "mcp-config-path": {{JsonSerializer.Serialize(mcpConfigPath)}} }""");

    private string WriteMcpConfig(string json)
    {
        var path = Path.Combine(this.sessionStateRoot, "mcp-config-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";

        public string UserName => "test-user";

        public string OperatingSystemName => "windows";

        public string HomeDirectoryPath => "C:/Users/test-user";
    }

    private static async Task<JsonElement?> GetEntityByNameAsync(IDataAccessLayer dataAccessLayer, EntityName entityName)
    {
        var result = await dataAccessLayer.GetAsync(new GetRequest
        {
            Entities = [new GetEntityRequest { EntityName = entityName }],
            Timestamps = [null],
        });
        return result.Batches.SelectMany(batch => batch.Entities).FirstOrDefault()?.Data;
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

        await new CopilotSessionDiscoveryTool().ExecuteAsync(this.Context(dataAccessLayer));

        foreach (var sessionId in new[] { first, second })
        {
            var entity = await GetEntityAsync(dataAccessLayer, sessionId);
            Assert.NotNull(entity);
            Assert.Equal("agent-definition", entity!.Value.GetProperty("entity-types")[0].GetString());
            var name = entity.Value.GetProperty("names")[0].EnumerateArray().Select(c => c.GetString()).ToArray();
            Assert.Equal("copilot", name[0]);
            Assert.Equal("sessions", name[1]);
            Assert.Equal(sessionId.ToString(), name[2]);
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

        await tool.ExecuteAsync(this.Context(dataAccessLayer));
        await tool.ExecuteAsync(this.Context(dataAccessLayer));

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

        await new CopilotSessionDiscoveryTool().ExecuteAsync(this.Context(dataAccessLayer));

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

        await new CopilotSessionDiscoveryTool().ExecuteAsync(this.Context(repository.DataAccessLayer));

        var entity = await GetEntityAsync(repository.DataAccessLayer, sessionId);
        Assert.NotNull(entity);
        Assert.Equal("agent-definition", entity!.Value.GetProperty("entity-types")[0].GetString());
    }

    private static readonly EntityName MachineMcpServerName = new(
        "computer-user-profiles", "users", "username", "test-user",
        "computers", "hostname", "test-machine", "copilot", "mcp-servers", "github");

    [Fact]
    public async Task Run_DiscoversRemoteMcpServer_AsMcpServerEntityUnderMachineProfile()
    {
        var mcpConfigPath = this.WriteMcpConfig(
            """
            {
              "mcpServers": {
                "github": { "url": "https://api.githubcopilot.com/mcp/" }
              }
            }
            """);
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new CopilotSessionDiscoveryTool(new FakeExecutionContextProvider())
            .ExecuteAsync(this.ContextWithMcpConfig(dataAccessLayer, mcpConfigPath));

        var entity = await GetEntityByNameAsync(dataAccessLayer, MachineMcpServerName);
        Assert.NotNull(entity);
        Assert.Equal("mcp-server", entity!.Value.GetProperty("entity-types")[0].GetString());
        var mcpServer = entity.Value.GetProperty("mcp-server");
        Assert.Equal("github", mcpServer.GetProperty("serverName").GetString());
        Assert.Equal("https://api.githubcopilot.com/mcp/", mcpServer.GetProperty("connection").GetProperty("endpoint").GetString());
    }

    [Fact]
    public async Task Run_DiscoversStdioMcpServer_BuildsStdioEndpoint()
    {
        var mcpConfigPath = this.WriteMcpConfig(
            """
            {
              "mcpServers": {
                "github": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-github"] }
              }
            }
            """);
        var dataAccessLayer = new InMemoryDataAccessLayer();

        await new CopilotSessionDiscoveryTool(new FakeExecutionContextProvider())
            .ExecuteAsync(this.ContextWithMcpConfig(dataAccessLayer, mcpConfigPath));

        var entity = await GetEntityByNameAsync(dataAccessLayer, MachineMcpServerName);
        Assert.NotNull(entity);
        var endpoint = entity!.Value.GetProperty("mcp-server").GetProperty("connection").GetProperty("endpoint").GetString();
        Assert.StartsWith("stdio://?command=npx", endpoint);
        Assert.Contains("arg=-y", endpoint);
        Assert.Contains(Uri.EscapeDataString("@modelcontextprotocol/server-github"), endpoint);
    }

    [Fact]
    public async Task Run_McpServerDiscovery_IsIdempotent()
    {
        var mcpConfigPath = this.WriteMcpConfig(
            """
            {
              "mcpServers": {
                "github": { "url": "https://api.githubcopilot.com/mcp/" }
              }
            }
            """);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new CopilotSessionDiscoveryTool(new FakeExecutionContextProvider());

        await tool.ExecuteAsync(this.ContextWithMcpConfig(dataAccessLayer, mcpConfigPath));
        await tool.ExecuteAsync(this.ContextWithMcpConfig(dataAccessLayer, mcpConfigPath));

        var export = await dataAccessLayer.ExportAsync(new ExportRequest());
        var distinctEntities = export.ChangeBatches.SelectMany(b => b.Entities).Select(e => e.EntityId).Distinct().Count();
        Assert.Equal(1, distinctEntities);
    }
}
