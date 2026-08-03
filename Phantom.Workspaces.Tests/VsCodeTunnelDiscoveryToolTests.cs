using System;
using System.Collections.Generic;
using System.ComponentModel;
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

    private sealed class FakeExecutionContextProvider : ICurrentExecutionContextProvider
    {
        public string ComputerName => "test-machine";
        public string UserName => "test-user";
        public string OperatingSystemName => "windows";
        public string HomeDirectoryPath => "C:/Users/test-user";
        public string EffectiveComputerName => this.ComputerName;
    }

    private sealed class FakeStatusResolver : IVsCodeTunnelStatusResolver
    {
        private readonly Func<string, CancellationToken, Task<VsCodeTunnelStatus?>> handler;
        public List<string> Invocations { get; } = new();

        public FakeStatusResolver(Func<string, CancellationToken, Task<VsCodeTunnelStatus?>> handler)
        {
            this.handler = handler;
        }

        public FakeStatusResolver(VsCodeTunnelStatus? status)
            : this((_, _) => Task.FromResult(status))
        {
        }

        public Task<VsCodeTunnelStatus?> GetTunnelStatusAsync(string cliPath, CancellationToken cancellationToken)
        {
            this.Invocations.Add(cliPath);
            return this.handler(cliPath, cancellationToken);
        }
    }

    private WorkspaceToolExecutionContext Context(
        IDataAccessLayer dataAccessLayer,
        string? cliPath = null)
    {
        var props = new List<string>
        {
            "\"entity-types\": [\"entity\", \"tool\"]",
            "\"tool-type\": \"vscode-tunnel-discovery\"",
        };

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

    // ---- New tests specified by #1201 --------------------------------------------------------

    [Fact]
    public async Task Discovery_RunningTunnel_UpsertsVsCodeTunnelEntityFromCliOutput()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus(
            TunnelName: "cli-reported-name",
            TunnelUrl: "https://vscode.dev/tunnel/cli-reported-name",
            IsConnected: true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.True(result.IsSuccess);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.NotNull(entity);
        Assert.Equal("cli-reported-name", entity!.Value.GetProperty("tunnel-name").GetString());
        Assert.Equal("https://vscode.dev/tunnel/cli-reported-name", entity.Value.GetProperty("tunnel-url").GetString());
        Assert.True(entity.Value.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Discovery_NoRunningTunnel_DoesNotUpsertEntity()
    {
        var resolver = new FakeStatusResolver((VsCodeTunnelStatus?)null);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.True(result.IsSuccess);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.Null(entity);
    }

    [Fact]
    public async Task Discovery_CliMissing_ReturnsFailureWithoutThrowing()
    {
        var resolver = new FakeStatusResolver((_, _) =>
            throw new Win32Exception("The system cannot find the file specified"));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("VS Code CLI", result.ErrorMessage);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.Null(entity);
    }

    [Fact]
    public async Task Discovery_DoesNotReadCodeTunnelJsonFile()
    {
        // Write a stale code_tunnel.json under an isolated USERPROFILE and prove the tool
        // ignores it — the entity's tunnel-name must come from the fake resolver output.
        var stalePath = Path.Combine(this.testRoot, ".vscode", "cli", "code_tunnel.json");
        Directory.CreateDirectory(Path.GetDirectoryName(stalePath)!);
        File.WriteAllText(stalePath, "{\"tunnel_name\":\"STALE-FROM-FILE\"}");

        var originalUserProfile = Environment.GetEnvironmentVariable("USERPROFILE");
        try
        {
            Environment.SetEnvironmentVariable("USERPROFILE", this.testRoot);

            var resolver = new FakeStatusResolver(new VsCodeTunnelStatus(
                TunnelName: "live-cli-name",
                TunnelUrl: "https://vscode.dev/tunnel/live-cli-name",
                IsConnected: true));
            var dataAccessLayer = new InMemoryDataAccessLayer();
            var tool = new VsCodeTunnelDiscoveryTool(
                new FakeExecutionContextProvider(),
                tunnelStatusResolver: resolver);

            var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

            Assert.True(result.IsSuccess);
            var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
            Assert.NotNull(entity);
            Assert.Equal("live-cli-name", entity!.Value.GetProperty("tunnel-name").GetString());
            Assert.DoesNotContain("STALE-FROM-FILE", entity.Value.GetProperty("tunnel-name").GetString());
        }
        finally
        {
            Environment.SetEnvironmentVariable("USERPROFILE", originalUserProfile);
        }
    }

    [Fact]
    public async Task Discovery_RunsCliOffCallingThread()
    {
        // The resolver invocation is awaited (not blocked). We confirm the tool returns before
        // the resolver's task completes when we control completion via a TaskCompletionSource,
        // and that ExecuteAsync itself yields to the resolver awaitable.
        var tcs = new TaskCompletionSource<VsCodeTunnelStatus?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FakeStatusResolver((_, _) => tcs.Task);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var executeTask = tool.ExecuteAsync(this.Context(dataAccessLayer));
        Assert.False(executeTask.IsCompleted);

        tcs.SetResult(new VsCodeTunnelStatus("late", "https://vscode.dev/tunnel/late", true));
        var result = await executeTask;
        Assert.True(result.IsSuccess);
    }

    // ---- Retained coverage (rewritten to use the resolver seam) -----------------------------

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_CliPathOverride()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus("my-desktop", "https://vscode.dev/tunnel/my-desktop", true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        await tool.ExecuteAsync(this.Context(dataAccessLayer, cliPath: "/custom/code"));

        Assert.Single(resolver.Invocations);
        Assert.Equal("/custom/code", resolver.Invocations[0]);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_DefaultCliPath_UsesLocatorResolvedPath()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus("my-desktop", "https://vscode.dev/tunnel/my-desktop", true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            defaultCliPathResolver: () => @"C:\fake\code.cmd");

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Single(resolver.Invocations);
        Assert.Equal(@"C:\fake\code.cmd", resolver.Invocations[0]);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_DeterministicEntityId()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus("my-desktop", "https://vscode.dev/tunnel/my-desktop", true));
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var dataAccessLayer1 = new InMemoryDataAccessLayer();
        await tool.ExecuteAsync(this.Context(dataAccessLayer1));
        var entity1 = await GetEntityByNameAsync(dataAccessLayer1, ExpectedEntityName);

        var dataAccessLayer2 = new InMemoryDataAccessLayer();
        await tool.ExecuteAsync(this.Context(dataAccessLayer2));
        var entity2 = await GetEntityByNameAsync(dataAccessLayer2, ExpectedEntityName);

        Assert.NotNull(entity1);
        Assert.NotNull(entity2);
        Assert.Equal(
            entity1!.Value.GetProperty("entity-id").GetString(),
            entity2!.Value.GetProperty("entity-id").GetString());
    }
}
