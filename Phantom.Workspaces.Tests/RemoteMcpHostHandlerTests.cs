using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Tests;

/// <summary>
/// Unit tests for the production remote MCP host (issue #1438). The handler serves inbound
/// <c>{"type":"mcp","connection":{...}}</c> channels by opening the requested MCP server locally and
/// bridging it back; an unrecognised connection is declined (null) so the listener does not host it.
/// The full live-server bridge is exercised end-to-end in <c>McpToolContextProviderRoutingTests</c>.
/// </summary>
public sealed class RemoteMcpHostHandlerTests
{
    [Fact]
    public async Task OpenAsync_UnknownConnection_ReturnsNull()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        // An `mcp` request whose connection carries no endpoint is not a hostable server.
        var handle = await handler.OpenAsync(Json("""{"type":"mcp","connection":{}}"""), channel, Ct());

        Assert.Null(handle);
    }

    [Fact]
    public async Task OpenAsync_NonMcpRequest_ReturnsNull()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        var handle = await handler.OpenAsync(Json("""{"type":"chat-client"}"""), channel, Ct());

        Assert.Null(handle);
    }

    [Fact]
    public async Task OpenAsync_HttpConnection_HostsServer()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        // A valid HTTP connection descriptor yields a live host session (the server connection itself
        // is lazy). Disposing the handle tears down the bridge without surfacing connection errors.
        var handle = await handler.OpenAsync(
            Json("""{"type":"mcp","connection":{"server-name":"remote","endpoint":"http://127.0.0.1:59999/mcp","transport":"streamable"}}"""),
            channel,
            Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_StdioConnection_HostsServer()
    {
        var handler = new RemoteMcpHostHandler(new Phantom.Workspaces.Llm.AgentServices
        {
            ProcessExecutor = new RecordingProcessExecutor(),
        });
        await using var channel = new StubMessageChannel();
        var command = Uri.EscapeDataString(ComSpecPath);

        // A stdio connection descriptor selects the stdio branch of the shared factory
        // (McpTransportFactory.IsStdioEndpoint/CreateStdioTransport) rather than the HTTP branch.
        // Construction of the stdio transport is synchronous and the child process launch is lazy, so
        // the host session is live immediately; disposing tears the bridge down without a round-trip.
        var handle = await handler.OpenAsync(
            Json($"{{\"type\":\"mcp\",\"connection\":{{\"server-name\":\"remote-stdio\",\"endpoint\":\"stdio://?command={command}\"}}}}"),
            channel,
            Ct());

        Assert.NotNull(handle);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_DisposesOnChannelClose()
    {
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        var handle = await handler.OpenAsync(
            Json("""{"type":"mcp","connection":{"server-name":"remote","endpoint":"http://127.0.0.1:59999/mcp"}}"""),
            channel,
            Ct());

        Assert.NotNull(handle);

        // Disposing twice is safe and never hangs (the relay is cancelled on the first disposal).
        await handle!.DisposeAsync();
        await handle.DisposeAsync();
    }

    [Fact]
    public async Task OpenAsync_CompiledPolicyOverWire_IsRejected()
    {
        // #1477: the launch host, not the caller, compiles policy. A request whose connection
        // carries a compiled-policy property must fail closed before opening anything.
        var handler = new RemoteMcpHostHandler();
        await using var channel = new StubMessageChannel();

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.OpenAsync(
                Json("""{"type":"mcp","connection":{"server-name":"srv","endpoint":"stdio://?command=cmd","compiled-policy":{}}}"""),
                channel,
                Ct()));
    }

    [Fact]
    public async Task RemoteStdio_ProfileReference_ResolvesAndCompilesOnLaunchHost()
    {
        // #1477: when the wire carries a trust-profile-ref, THIS host resolves it locally, composes
        // the effective profile, and threads the resulting AgentExecutionTrustContext into the
        // shared transport factory. Successful resolution reaches the transport open path.
        var resolver = new FakeRemoteTrustProfileResolver(
            new Phantom.Workspaces.Llm.Trust.RemoteTrustProfileResolution(
                new Phantom.Workspaces.Llm.Trust.TrustProfile(),
                Revision: "rev-1"));
        var compiler = new RecordingPolicyCompiler();
        var executor = new RecordingProcessExecutor();
        var services = new Phantom.Workspaces.Llm.AgentServices
        {
            TrustProfileResolver = resolver,
            TrustProfilePolicyCompiler = compiler,
            ProcessExecutor = executor,
        };
        var handler = new RemoteMcpHostHandler(services);
        await using var channel = new StubMessageChannel();
        var command = Uri.EscapeDataString(ComSpecPath);

        var handle = await handler.OpenAsync(
            Json($"{{\"type\":\"mcp\",\"connection\":{{\"server-name\":\"srv\",\"endpoint\":\"stdio://?command={command}\",\"trust-profile-ref\":\"my-profile\",\"trust-profile-revision\":\"rev-1\"}}}}"),
            channel,
            Ct());

        Assert.NotNull(handle);
        Assert.Equal(1, resolver.ResolveCalls);
        var launched = await executor.Started.Task.WaitAsync(Ct());
        Assert.Equal(1, compiler.CompileCalls);
        Assert.NotNull(launched.MxcPolicy);
        await handle!.DisposeAsync();
    }

    [Fact]
    public async Task RemoteStdio_StaleTrustProfileRevision_RejectsLaunch()
    {
        // A resolved profile whose revision no longer matches the caller-observed revision must
        // fail closed before any process is opened.
        var resolver = new FakeRemoteTrustProfileResolver(
            new Phantom.Workspaces.Llm.Trust.RemoteTrustProfileResolution(
                new Phantom.Workspaces.Llm.Trust.TrustProfile(),
                Revision: "rev-999"));
        var services = new Phantom.Workspaces.Llm.AgentServices
        {
            TrustProfileResolver = resolver,
            TrustProfilePolicyCompiler =
                new Phantom.Workspaces.Llm.Trust.MxcTrustProfilePolicyCompiler(),
        };
        var handler = new RemoteMcpHostHandler(services);
        await using var channel = new StubMessageChannel();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.OpenAsync(
                Json("""{"type":"mcp","connection":{"server-name":"srv","endpoint":"stdio://?command=my-server","trust-profile-ref":"my-profile","trust-profile-revision":"rev-1"}}"""),
                channel,
                Ct()));

        Assert.Contains("revision", ex.Message);
    }

    [Fact]
    public async Task RemoteStdio_MissingTrustProfileRevision_RejectsLaunch()
    {
        var handler = new RemoteMcpHostHandler(new Phantom.Workspaces.Llm.AgentServices
        {
            TrustProfileResolver = new FakeRemoteTrustProfileResolver(
                new RemoteTrustProfileResolution(new TrustProfile(), Revision: "current")),
            TrustProfilePolicyCompiler = new RecordingPolicyCompiler(),
            ProcessExecutor = new RecordingProcessExecutor(),
        });
        await using var channel = new StubMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await handler.OpenAsync(
                Json("""{"type":"mcp","connection":{"server-name":"srv","endpoint":"stdio://?command=cmd","trust-profile-ref":"my-profile"}}"""),
                channel,
                Ct()));

        Assert.Contains("expected revision", exception.Message, StringComparison.Ordinal);
    }

    private sealed class FakeRemoteTrustProfileResolver
        : Phantom.Workspaces.Llm.Trust.IRemoteTrustProfileResolver
    {
        private readonly Phantom.Workspaces.Llm.Trust.RemoteTrustProfileResolution? result;
        public int ResolveCalls { get; private set; }
        public FakeRemoteTrustProfileResolver(Phantom.Workspaces.Llm.Trust.RemoteTrustProfileResolution? result)
        {
            this.result = result;
        }

        public Task<Phantom.Workspaces.Llm.Trust.RemoteTrustProfileResolution?> ResolveAsync(
            string profileReference, CancellationToken cancellationToken)
        {
            ResolveCalls++;
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingPolicyCompiler : ITrustProfileProcessPolicyCompiler
    {
        public int CompileCalls { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CompileCalls++;
            var policy = new MxcProcessPolicy(
                MxcProcessPolicy.CurrentSchemaVersion,
                [], [], [],
                new Dictionary<string, string>(),
                new MxcProcessContainment(
                    MxcContainmentBackend.ProcessContainer,
                    LeastPrivilege: true,
                    LearningMode: false,
                    PermissiveMode: false));
            return new TrustProfileProcessPolicyCompilation(true, policy, []);
        }
    }

    private sealed class RecordingProcessExecutor : IProcessExecutor
    {
        public TaskCompletionSource<ProcessExecutionRequest> Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            Started.TrySetResult(request);
            return new StubProcessHandle();
        }
    }

    private sealed class StubProcessHandle : IProcessHandle
    {
        private readonly TaskCompletionSource<ProcessExitResult> exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream();
        public Stream StandardError { get; } = new MemoryStream();
        public ProcessLaunchInfo LaunchInfo { get; } = new(1234, true, []);
        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default)
            => exit.Task.WaitAsync(cancellationToken);
        public void Kill() => exit.TrySetResult(new ProcessExitResult(-1, false, null));
        public ValueTask DisposeAsync()
        {
            exit.TrySetResult(new ProcessExitResult(0, false, null));
            return ValueTask.CompletedTask;
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static string ComSpecPath =>
        Environment.GetEnvironmentVariable("ComSpec")
        ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");

    private static CancellationToken Ct() => new CancellationTokenSource(TimeSpan.FromSeconds(10)).Token;

    private sealed class StubMessageChannel : IMessageChannel
    {
        private readonly Channel<JsonElement> reader = Channel.CreateUnbounded<JsonElement>();
        private readonly Channel<JsonElement> writer = Channel.CreateUnbounded<JsonElement>();

        public ChannelReader<JsonElement> Reader => this.reader.Reader;

        public ChannelWriter<JsonElement> Writer => this.writer.Writer;

        public ValueTask DisposeAsync()
        {
            this.reader.Writer.TryComplete();
            this.writer.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
