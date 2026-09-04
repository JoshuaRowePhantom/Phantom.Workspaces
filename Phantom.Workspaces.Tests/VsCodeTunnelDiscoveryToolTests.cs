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
        private readonly Func<string, CancellationToken, Task<VsCodeTunnelResolution>> handler;
        public List<string> Invocations { get; } = new();

        public FakeStatusResolver(Func<string, CancellationToken, Task<VsCodeTunnelResolution>> handler)
        {
            this.handler = handler;
        }

        public FakeStatusResolver(VsCodeTunnelStatus? status)
            : this((_, _) => Task.FromResult(new VsCodeTunnelResolution(
                status,
                new VsCodeCliResult(0, string.Empty, string.Empty),
                CliLaunchError: null)))
        {
        }

        public FakeStatusResolver(VsCodeTunnelResolution resolution)
            : this((_, _) => Task.FromResult(resolution))
        {
        }

        public Task<VsCodeTunnelResolution> ResolveAsync(string cliPath, CancellationToken cancellationToken)
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

        // #1355: a not-found discovery is a failure, and still upserts no stale entity.
        Assert.False(result.IsSuccess);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.Null(entity);
    }

    [Fact]
    public async Task Discovery_NoRunningTunnel_ReturnsFailure()
    {
        var resolver = new FakeStatusResolver((VsCodeTunnelStatus?)null);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("no tunnel running", result.ErrorMessage);
    }

    [Fact]
    public async Task Discovery_RunningTunnelFound_ReturnsSuccess()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus(
            TunnelName: "found-name",
            TunnelUrl: "https://vscode.dev/tunnel/found-name",
            IsConnected: true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.True(result.IsSuccess);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.NotNull(entity);
        Assert.Equal("found-name", entity!.Value.GetProperty("tunnel-name").GetString());
    }

    [Fact]
    public async Task Discovery_CliMissing_ReturnsFailureWithoutThrowing()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelResolution(
            Status: null,
            CliResult: null,
            CliLaunchError: "The system cannot find the file specified"));
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
        var tcs = new TaskCompletionSource<VsCodeTunnelResolution>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = new FakeStatusResolver((_, _) => tcs.Task);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var executeTask = tool.ExecuteAsync(this.Context(dataAccessLayer));
        Assert.False(executeTask.IsCompleted);

        tcs.SetResult(new VsCodeTunnelResolution(
            new VsCodeTunnelStatus("late", "https://vscode.dev/tunnel/late", true),
            new VsCodeCliResult(0, string.Empty, string.Empty),
            CliLaunchError: null));
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

    // ---- #1206: logging + reporting ---------------------------------------------------------

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_TunnelStatusNonZeroExit_FailureResultContainsCliOutput()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelResolution(
            Status: null,
            CliResult: new VsCodeCliResult(2, "some stdout marker", "some stderr marker"),
            CliLaunchError: null));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("some stdout marker", result.ErrorMessage);
        Assert.Contains("some stderr marker", result.ErrorMessage);
        Assert.Contains("exit 2", result.ErrorMessage);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_TunnelStatusInvoked_RoutesThroughSharedInvoker()
    {
        // Prove the tool dispatches "tunnel status" through the shared VsCodeCliInvoker
        // (via VsCodeTunnelStatusResolver) rather than a bare ProcessRunner.RunProcessAsync
        // call. We construct a real resolver + invoker; the invoker's processRunner captures
        // every invocation so we can assert routing.
        var invocations = new List<string>();
        var invoker = new VsCodeCliInvoker(
            notificationService: null,
            logger: null,
            processRunner: (parameters, ct) =>
            {
                invocations.Add(string.Join(" ", parameters.Arguments));
                return Task.FromResult(new ProcessResult(0, "no tunnel", string.Empty, "no tunnel"));
            });
        var resolver = new VsCodeTunnelStatusResolver(invoker: invoker);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            defaultCliPathResolver: () => "code");

        await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.NotEmpty(invocations);
        Assert.Contains(invocations, args => args.Contains("tunnel") && args.Contains("status"));
    }

    // ---- #1239: discovery tool reports resolved tunnel status -------------------------------

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_TunnelDiscovered_ResultContentSummarizesStatus()
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
        Assert.NotNull(result.ResultContent);
        Assert.Contains("cli-reported-name", result.ResultContent);
        Assert.Contains("https://vscode.dev/tunnel/cli-reported-name", result.ResultContent);
        Assert.Contains("True", result.ResultContent);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_TunnelDiscovered_LogsInformationSummary()
    {
        var logger = new TestLogger<VsCodeTunnelDiscoveryTool>();
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus(
            TunnelName: "cli-reported-name",
            TunnelUrl: "https://vscode.dev/tunnel/cli-reported-name",
            IsConnected: true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            logger: logger);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information
                && e.Message.Contains("cli-reported-name")
                && e.Message == result.ResultContent);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_NoRunningTunnel_ResultContentSaysNoTunnel()
    {
        var logger = new TestLogger<VsCodeTunnelDiscoveryTool>();
        var resolver = new FakeStatusResolver((VsCodeTunnelStatus?)null);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            logger: logger);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        // #1355: no tunnel is a failure; the informative message is surfaced via ErrorMessage
        // and still logged at Information.
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("no tunnel running", result.ErrorMessage);
        Assert.Contains(
            logger.Entries,
            e => e.Level == LogLevel.Information && e.Message == result.ErrorMessage);
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_TunnelDiscovered_ResultContentExcludesRawCliOutput()
    {
        var logger = new TestLogger<VsCodeTunnelDiscoveryTool>();
        var resolver = new FakeStatusResolver(new VsCodeTunnelResolution(
            Status: new VsCodeTunnelStatus(
                TunnelName: "cli-reported-name",
                TunnelUrl: "https://vscode.dev/tunnel/cli-reported-name",
                IsConnected: true),
            CliResult: new VsCodeCliResult(0, "RAW-STDOUT-MARKER", "RAW-STDERR-MARKER"),
            CliLaunchError: null));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            logger: logger);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.NotNull(result.ResultContent);
        Assert.DoesNotContain("RAW-STDOUT-MARKER", result.ResultContent);
        Assert.DoesNotContain("RAW-STDERR-MARKER", result.ResultContent);
        Assert.DoesNotContain(
            logger.Entries,
            e => e.Message.Contains("RAW-STDOUT-MARKER") || e.Message.Contains("RAW-STDERR-MARKER"));
    }

    [Fact]
    public async Task VsCodeTunnelDiscoveryTool_ResolverThrows_FailureResultUnchanged()
    {
        var logger = new TestLogger<VsCodeTunnelDiscoveryTool>();
        var resolver = new FakeStatusResolver((_, _) =>
            Task.FromException<VsCodeTunnelResolution>(new InvalidOperationException("boom")));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver,
            logger: logger);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.False(result.IsSuccess);
        Assert.Null(result.ResultContent);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Information);
    }

    // ---- #1359: schema-correct running/not-running via the shared resolver ------------------

    [Fact]
    public async Task Discovery_TunnelConnected_UpsertsTunnelEntityAndReportsSuccess()
    {
        var resolver = new FakeStatusResolver(new VsCodeTunnelStatus(
            TunnelName: "daemon",
            TunnelUrl: "https://vscode.dev/tunnel/daemon",
            IsConnected: true));
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        Assert.True(result.IsSuccess);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.NotNull(entity);
        Assert.Equal("daemon", entity!.Value.GetProperty("tunnel-name").GetString());
        Assert.Equal("https://vscode.dev/tunnel/daemon", entity.Value.GetProperty("tunnel-url").GetString());
        Assert.True(entity.Value.GetProperty("active").GetBoolean());
    }

    [Fact]
    public async Task Discovery_TunnelNull_ReportsNotFound()
    {
        var resolver = new FakeStatusResolver((VsCodeTunnelStatus?)null);
        var dataAccessLayer = new InMemoryDataAccessLayer();
        var tool = new VsCodeTunnelDiscoveryTool(
            new FakeExecutionContextProvider(),
            tunnelStatusResolver: resolver);

        var result = await tool.ExecuteAsync(this.Context(dataAccessLayer));

        // A `{"tunnel":null,...}` status is reported as no tunnel found (aligned with #1355),
        // not as a discovered tunnel.
        Assert.False(result.IsSuccess);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("no tunnel running", result.ErrorMessage);
        var entity = await GetEntityByNameAsync(dataAccessLayer, ExpectedEntityName);
        Assert.Null(entity);
    }
}
