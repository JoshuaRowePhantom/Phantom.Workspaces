using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using AgentSchema;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;

namespace Phantom.Workspaces.Llm.Core.Tests;

/// <summary>
/// Additional coverage for <see cref="McpTransportFactory"/> under an <see cref="AgentExecutionTrustContext"/>
/// (issue #1477): stdio launches always go through the executor (null policy → ordinary branch,
/// compiled policy → MXC branch) and constrained HTTP/SSE is refused outright with an
/// unsupported-policy error because there is no child process to sandbox.
/// </summary>
public sealed class McpTransportFactoryTrustContextTests
{
    private static string ComSpecPath =>
        Environment.GetEnvironmentVariable("ComSpec")
        ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static McpTool StdioTool(string command = "cmd") => new()
    {
        ServerName = "srv",
        Connection = new AnonymousConnection { Endpoint = $"stdio://?command={command}" },
    };

    private static McpTool HttpTool() => new()
    {
        ServerName = "srv",
        Connection = new AnonymousConnection { Endpoint = "https://example.test/mcp" },
    };

    private static AgentExecutionTrustContext UnconstrainedContext()
        => new(new TrustProfile(), new MxcTrustProfilePolicyCompiler(new FakeMxcPolicyHost()));

    private static AgentExecutionTrustContext ConstrainedContext()
        => new(
            new TrustProfile { NetworkCapabilities = [] },
            new MxcTrustProfilePolicyCompiler(new FakeMxcPolicyHost()));

    [Fact]
    public async Task CreateStdioTransport_WithoutOptionalServices_UsesPhantomExecutor()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var transport = await McpTransportFactory.CreateMcpTransportAsync(
            StdioTool(Uri.EscapeDataString(ComSpecPath)),
            services: null,
            NullLoggerFactory.Instance,
            CancellationToken.None);

        Assert.IsType<ProcessExecutorBackedClientTransport>(transport);
    }

    [Fact]
    public async Task CreateStdioTransport_UnconstrainedProfile_UsesExecutorWithNullPolicy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var executor = new RecordingProcessExecutor();
        var trustContext = UnconstrainedContext();

        var transport = await McpTransportFactory.CreateMcpTransportAsync(
            StdioTool(Uri.EscapeDataString(ComSpecPath)),
            services: null,
            NullLoggerFactory.Instance,
            CancellationToken.None,
            clientIdOverride: null,
            trustContext: trustContext,
            processExecutor: executor);

        Assert.IsType<ProcessExecutorBackedClientTransport>(transport);
        // Force lazy stdio-request construction via BuildProcessExecutionRequest through the
        // internal helper so we can assert the policy switch without launching.
        var request = McpTransportFactory.BuildProcessExecutionRequest(
            McpTransportFactory.BuildStdioTransportOptions(
                new Uri($"stdio://?command={Uri.EscapeDataString(ComSpecPath)}"), "srv"),
            trustContext);
        Assert.Null(request.MxcPolicy);
    }

    [Fact]
    public void CreateStdioTransport_ConstrainedProfile_UsesExecutorWithMxcPolicy()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var trustContext = ConstrainedContext();

        var request = McpTransportFactory.BuildProcessExecutionRequest(
            McpTransportFactory.BuildStdioTransportOptions(
                new Uri($"stdio://?command={Uri.EscapeDataString(ComSpecPath)}"), "srv"),
            trustContext);

        Assert.NotNull(request.MxcPolicy);
        Assert.Equal(MxcProcessPolicy.CurrentSchemaVersion, request.MxcPolicy!.SchemaVersion);
    }

    [Fact]
    public async Task CreateHttpTransport_ConstrainedProfile_ReturnsUnsupportedPolicy()
    {
        var trustContext = ConstrainedContext();
        var executor = new RecordingProcessExecutor();

        var ex = await Assert.ThrowsAsync<McpUnsupportedPolicyException>(
            () => McpTransportFactory.CreateMcpTransportAsync(
                HttpTool(),
                services: null,
                NullLoggerFactory.Instance,
                CancellationToken.None,
                clientIdOverride: null,
                trustContext: trustContext,
                processExecutor: executor));

        Assert.Contains("cannot be constrained", ex.Message);
        Assert.Equal(0, executor.StartCount);
    }

    private sealed class RecordingProcessExecutor : IProcessExecutor
    {
        public int StartCount { get; private set; }
        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            throw new NotSupportedException("Test executor: launch not exercised.");
        }
    }
}
