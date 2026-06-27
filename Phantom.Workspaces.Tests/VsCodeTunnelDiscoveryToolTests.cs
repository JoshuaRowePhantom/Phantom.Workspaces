using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.Data.Offline;
using Phantom.Workspaces.Tools;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class VsCodeTunnelDiscoveryToolTests : IDisposable
{
    private readonly string testRoot;

    public VsCodeTunnelDiscoveryToolTests()
    {
        this.testRoot = Path.Combine(Path.GetTempPath(), "pw-vscode-tunnel-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(this.testRoot);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(this.testRoot))
            {
                Directory.Delete(this.testRoot, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    private string WriteTunnelJson(string tunnelName)
    {
        var path = Path.Combine(this.testRoot, "code_tunnel.json");
        File.WriteAllText(path, $"{{\"tunnel_name\":\"{tunnelName}\"}}");
        return path;
    }

    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";
        public string UserName => "test-user";
        public string OperatingSystemName => "windows";
        public string HomeDirectoryPath => "C:/Users/test-user";
        public string EffectiveComputerName => this.ComputerName;
    }

    private WorkspaceToolExecutionContext Context(
        IDataAccessLayer dataAccessLayer,
        string? tunnelJsonPath = null,
        string? cliPath = null)
    {
        var props = new System.Collections.Generic.List<string>
        {
            "\"entity-types\": [\"entity\", \"tool\"]",
            "\"tool-type\": \"vscode-tunnel-discovery\"",
        };

        if (tunnelJsonPath is not null)
        {
            props.Add($"\"{VsCodeTunnelDiscoveryTool.TunnelJsonPathProperty}\": {JsonSerializer.Serialize(tunnelJsonPath)}");
        }

        if (cliPath is not null)
        {
            props.Add($"\"{VsCodeTunnelDiscoveryTool.CliPathProperty}\": {JsonSerializer.Serialize(cliPath)}");
        }

        var toolJson = "{" + string.Join(", ", props) + "}";
        return WorkspaceToolExecutionContextTestFactory.Create(dataAccessLayer, toolJson);
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

    private static readonly EntityName ExpectedEntityName = new(
        "computer-user-profiles", "users", "username", "test-user",
        "computers", "hostname", "test-machine", "vscode-tunnel");

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_ActiveTunnel_UpsertEntityWithCorrectUrl()
    {
        var tunnelJsonPath = this.WriteTunnelJson("my-desktop");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            (_, _) => Task.FromResult(0));

        await tool.ExecuteAsync(this.Context(dataAccessLayer, tunnelJsonPath: tunnelJsonPath));

        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.NotNull(entity);
        Assert.Contains("vscode-tunnel", entity!.Value.GetProperty("entity-types").EnumerateArray().Select(t => t.GetString()));
        Assert.Equal("my-desktop", entity.Value.GetProperty("tunnel-name").GetString());
        Assert.Equal("https://vscode.dev/tunnel/my-desktop", entity.Value.GetProperty("tunnel-url").GetString());
        Assert.True(entity.Value.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_NoTunnel_NoEntityChanges()
    {
        var noJsonPath = Path.Combine(this.testRoot, "nonexistent.json");
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            (_, _) => Task.FromResult(0));

        await tool.ExecuteAsync(this.Context(dataAccessLayer, tunnelJsonPath: noJsonPath));

        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.Null(entity);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_CliPathOverride()
    {
        var tunnelJsonPath = this.WriteTunnelJson("my-desktop");
        string? capturedCliPath = null;
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            (cliPath, _) =>
            {
                capturedCliPath = cliPath;
                return Task.FromResult(0);
            });

        await tool.ExecuteAsync(this.Context(dataAccessLayer, tunnelJsonPath: tunnelJsonPath, cliPath: "/custom/code"));

        Assert.Equal("/custom/code", capturedCliPath);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.NotNull(entity);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_DeterministicEntityId()
    {
        var tunnelJsonPath = this.WriteTunnelJson("my-desktop");
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            (_, _) => Task.FromResult(0));

        var dataAccessLayer1 = new InMemoryDataAccessLayer();
        await tool.ExecuteAsync(this.Context(dataAccessLayer1, tunnelJsonPath: tunnelJsonPath));
        var entity1 = await GetEntityByNameAsync(dataAccessLayer1, ExpectedEntityName);

        var dataAccessLayer2 = new InMemoryDataAccessLayer();
        await tool.ExecuteAsync(this.Context(dataAccessLayer2, tunnelJsonPath: tunnelJsonPath));
        var entity2 = await GetEntityByNameAsync(dataAccessLayer2, ExpectedEntityName);

        Assert.NotNull(entity1);
        Assert.NotNull(entity2);
        Assert.Equal(
            entity1!.Value.GetProperty("entity-id").GetString(),
            entity2!.Value.GetProperty("entity-id").GetString());
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_DefaultCliPath_UsesLocatorResolvedPath()
    {
        var tunnelJsonPath = this.WriteTunnelJson("my-desktop");
        string? capturedCliPath = null;
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            (cliPath, _) =>
            {
                capturedCliPath = cliPath;
                return Task.FromResult(0);
            },
            defaultCliPathResolver: () => @"C:\fake\code.cmd");

        await tool.ExecuteAsync(this.Context(dataAccessLayer, tunnelJsonPath: tunnelJsonPath));

        Assert.Equal(@"C:\fake\code.cmd", capturedCliPath);
    }
}
