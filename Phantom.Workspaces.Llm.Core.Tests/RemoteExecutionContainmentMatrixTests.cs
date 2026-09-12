using System.Text.Json;
using AgentSchema;
using GitHub.Copilot;
using Microsoft.Extensions.Logging.Abstractions;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Manifest;
using Phantom.Workspaces.Llm.Core.Transport;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Mcp;
using Phantom.Workspaces.Llm.Processes;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class RemoteExecutionContainmentMatrixTests
{
    private const string AgentHost = "agent-host";
    private const string ComponentHost = "component-host";

    [Fact]
    public async Task ExecutionMatrix_ContainmentNotRequired_UsesOrdinaryExecutorBranch()
    {
        var system = new RecordingSystemProcessFactory
        {
            Process = new FakeProcessBackend(),
        };
        var sandbox = new FakeSandboxRunner();
        var executor = new ProcessExecutor(system, sandbox);

        await using var handle = await executor.StartAsync(
            new ProcessExecutionRequest("ordinary.exe"));

        Assert.NotNull(system.Request);
        Assert.Equal(0, sandbox.SpawnCount);
        Assert.False(handle.LaunchInfo.IsContained);
    }

    [Fact]
    public async Task ExecutionMatrix_ContainmentRequired_UsesMxcExecutorBranch()
    {
        var system = new RecordingSystemProcessFactory();
        var sandbox = new FakeSandboxRunner();
        var executor = new ProcessExecutor(system, sandbox);

        await using var handle = await executor.StartAsync(
            new ProcessExecutionRequest("contained.exe")
            {
                MxcPolicy = CreatePolicy(),
            });

        Assert.Null(system.Request);
        Assert.Equal(1, sandbox.SpawnCount);
        Assert.True(handle.LaunchInfo.IsContained);
    }

    [Fact]
    public void ExecutionMatrix_RemoteBoundary_ContainsNoCompiledPolicyOrPolicyPath()
    {
        var request = CopilotSessionTransportFrames.BuildConnectionRequest(
            new AgentExecutionTrustProfileReference(
                "trust-profile",
                "restricted",
                "revision-17"));
        var json = request.GetRawText();

        Assert.Equal("restricted", request.GetProperty("trust-profile").GetString());
        Assert.Equal("revision-17", request.GetProperty("expected-trust-profile-revision").GetString());
        Assert.DoesNotContain("compiled", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("policy-path", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".json", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExecutionMatrix_GuiAndEntityTools_UseApplicationAuthorizationNotMxcClaim()
    {
        var bindings = CreateBindings(remoteAgent: true, remoteComponent: true);
        var topology = bindings.ToTopology();

        Assert.True(topology.ResolvesLocally(ExecutorTarget.GuiLocal));
        Assert.False(topology.ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.Equal(
            TrustProfile.LocalClientInstance,
            topology.Resolve(ExecutorTarget.GuiLocal));
    }

    [Fact]
    public async Task SplitClient_RemoteCopilot_LocalAgentChat_PreservesDistinctTopology()
    {
        var bindings = CreateBindings(remoteAgent: false, remoteComponent: true);
        var (transport, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var ownedTransport = transport;
        var registry = new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport);
        await using var client = ExecutorRoutingTestHarness.CreateClient("model");
        client.ConfigureExecutorRouting(bindings, registry);

        var remoteClient = await client.ResolveRemoteClientForTestAsync();

        Assert.True(bindings.ToTopology().ResolvesLocally(ExecutorTarget.AgentExecutor));
        Assert.IsType<CopilotClientOverTransport>(remoteClient);
        Assert.Equal(ComponentHost, registry.LastDescriptor!.Value
            .GetProperty(ExecutorBindings.EntityIdPropertyName).GetString());
    }

    [Fact]
    public async Task ContainedCopilot_RemoteOwner_CreatesWrapperEnvelopeOnlyOnCopilotHost()
    {
        using var files = new MatrixRuntimeFiles();
        var resolver = new RecordingResolver();
        var compiler = new RecordingCompiler(requiresContainment: true);
        var runtimeFactory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.RemoteLaunchRoot, TimeProvider.System),
            files.RemoteBaseDirectory,
            "win-x64");
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(
            sdkFactory,
            resolver,
            compiler,
            runtimeFactory));
        await using var transport = new Phantom.Workspaces.Transport.Local.LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            new AgentExecutionTrustProfileReference("trust-profile", "restricted", "revision-17"));

        await using var session = await client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-5" },
            CancellationToken.None);

        Assert.Equal(1, resolver.ResolveCount);
        Assert.Equal(1, compiler.CompileCount);
        Assert.True(Directory.EnumerateFiles(
            files.RemoteLaunchRoot,
            "policy.json",
            SearchOption.AllDirectories).Any());
        Assert.False(Directory.Exists(files.CallerLaunchRoot));
    }

    [Fact]
    public async Task ContainedStdioMcp_RemoteExecutor_OwnsProcessTransportOnToolHost()
    {
        var executor = new OwnedProcessExecutor();
        var transport = new ProcessExecutorBackedClientTransport(
            "remote-tool",
            new ProcessExecutionRequest("tool.exe") { MxcPolicy = CreatePolicy() },
            executor,
            NullLoggerFactory.Instance);

        var connected = await transport.ConnectAsync();
        await connected.DisposeAsync();

        Assert.Equal(1, executor.StartCount);
        Assert.NotNull(executor.Request!.MxcPolicy);
        Assert.Equal(1, executor.Handle.DisposeCount);
    }

    [Fact]
    public async Task ContainmentCompileFails_ReturnsSanitizedErrorAndDoesNotLaunch()
    {
        var clientFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listener = new CopilotClientTransportListener(
            clientFactory,
            new RecordingResolver(),
            new RecordingCompiler(
                requiresContainment: true,
                failMessage: @"C:\secret\policy.json TOKEN=value --unsafe stderr"),
            new CompilerInvokingRuntimeFactory());
        await using var channel = new MatrixMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(
                TrustRequest(),
                channel,
                CancellationToken.None));

        Assert.Equal("Remote Copilot launch was denied by host policy.", exception.Message);
        Assert.Equal(0, clientFactory.CreateCount);
    }

    [Theory]
    [InlineData("""{"type":"copilot-sdk-session","trust-profile":"restricted"}""")]
    [InlineData("""{"type":"copilot-sdk-session","expected-trust-profile-revision":"17"}""")]
    [InlineData("""{"type":"copilot-sdk-session","trust-profile":42,"expected-trust-profile-revision":"17"}""")]
    [InlineData("""{"type":"copilot-sdk-session","trust-profile":"restricted","expected-trust-profile-revision":17}""")]
    [InlineData("""{"type":"copilot-sdk-session","trust-profile":" ","expected-trust-profile-revision":"17"}""")]
    public async Task RemoteCopilot_MalformedTrustIntent_FailsClosedBeforeClientCreation(string json)
    {
        var clientFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listener = new CopilotClientTransportListener(
            clientFactory,
            new RecordingResolver(),
            new RecordingCompiler(requiresContainment: false),
            new CompilerInvokingRuntimeFactory());
        await using var channel = new MatrixMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(
                JsonDocument.Parse(json).RootElement.Clone(),
                channel,
                CancellationToken.None));

        Assert.Equal("Remote Copilot launch was denied by host policy.", exception.Message);
        Assert.Equal(0, clientFactory.CreateCount);
    }

    [Fact]
    public async Task RemoteCopilot_ClientStartFailure_IsSanitizedAndDisposesSelection()
    {
        using var files = new MatrixRuntimeFiles();
        var handoff = Path.Combine(files.RemoteLaunchRoot, "unconsumed.json");
        Directory.CreateDirectory(files.RemoteLaunchRoot);
        await File.WriteAllTextAsync(handoff, "sensitive handoff");
        var clientFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        clientFactory.Client.StartException =
            new InvalidOperationException(@"C:\secret\wrapper.exe --token sensitive stderr");
        var listener = new CopilotClientTransportListener(
            clientFactory,
            new RecordingResolver(),
            new RecordingCompiler(requiresContainment: false),
            new FixedRuntimeFactory(handoff));
        await using var channel = new MatrixMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(
                TrustRequest(),
                channel,
                CancellationToken.None));

        Assert.Equal("Remote Copilot launch was denied by host policy.", exception.Message);
        Assert.DoesNotContain("secret", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(1, clientFactory.Client.DisposeCount);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public async Task RemoteCopilot_FinalTransportRelease_DisposesHostClientAndPolicyLease()
    {
        using var files = new MatrixRuntimeFiles();
        var handoff = Path.Combine(files.RemoteLaunchRoot, "active.json");
        Directory.CreateDirectory(files.RemoteLaunchRoot);
        await File.WriteAllTextAsync(handoff, "active policy");
        var clientFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(
            clientFactory,
            new RecordingResolver(),
            new RecordingCompiler(requiresContainment: false),
            new FixedRuntimeFactory(handoff)));
        await using var transport =
            new Phantom.Workspaces.Transport.Local.LocalTransport(listeners);
        var remoteClient = new CopilotClientOverTransport(
            transport,
            new AgentExecutionTrustProfileReference(
                "trust-profile",
                "restricted",
                "revision-17"));

        await using (var session = await remoteClient.CreateSessionAsync(
                         new SessionConfig { Model = "gpt-5" },
                         CancellationToken.None))
        {
            Assert.True(File.Exists(handoff));
        }
        await remoteClient.DisposeAsync();

        Assert.Equal(1, clientFactory.Client.DisposeCount);
        Assert.False(File.Exists(handoff));
    }

    [Fact]
    public async Task MxcLaunchFails_DoesNotFallbackUncontained()
    {
        var system = new RecordingSystemProcessFactory();
        var sandbox = new FakeSandboxRunner
        {
            SpawnException = new InvalidOperationException("MXC launch failed"),
        };
        var executor = new ProcessExecutor(system, sandbox);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.StartAsync(
                new ProcessExecutionRequest("tool.exe")
                {
                    MxcPolicy = CreatePolicy(),
                }));

        Assert.Equal(1, sandbox.SpawnCount);
        Assert.Null(system.Request);
    }

    [Fact]
    public async Task ConstrainedHttpOrSse_ReturnsUnsupportedPolicy()
    {
        var context = new AgentExecutionTrustContext(
            new TrustProfile { NetworkCapabilities = [] },
            new RecordingCompiler(requiresContainment: true));
        var executor = new OwnedProcessExecutor();
        var tool = new McpTool
        {
            ServerName = "remote-http",
            Connection = new AnonymousConnection
            {
                Endpoint = "https://example.test/mcp",
            },
        };

        await Assert.ThrowsAsync<McpUnsupportedPolicyException>(
            () => McpTransportFactory.CreateMcpTransportAsync(
                tool,
                services: null,
                NullLoggerFactory.Instance,
                CancellationToken.None,
                trustContext: context,
                processExecutor: executor));

        Assert.Equal(0, executor.StartCount);
    }

    [Fact]
    public async Task RemoteError_PolicyPathEnvironmentArgvAndStderr_AreAbsent()
    {
        const string secret = @"C:\private\policy.json API_TOKEN=secret --password hunter2 stderr-data";
        var listener = new CopilotClientTransportListener(
            new ExecutorRoutingTestHarness.RecordingClientFactory(),
            new RecordingResolver(),
            new RecordingCompiler(requiresContainment: true, failMessage: secret),
            new CompilerInvokingRuntimeFactory());
        await using var channel = new MatrixMessageChannel();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => listener.OnChannelOpenAsync(
                TrustRequest(),
                channel,
                CancellationToken.None));

        Assert.DoesNotContain("policy.json", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("API_TOKEN", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("--password", exception.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stderr-data", exception.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static ExecutorBindings CreateBindings(bool remoteAgent, bool remoteComponent)
    {
        var session = remoteAgent ? RemoteDescriptor(AgentHost) : ExecutorBindings.LocalDescriptor();
        var model = remoteComponent ? RemoteDescriptor(ComponentHost) : ExecutorBindings.LocalDescriptor();
        return new ExecutorBindings
        {
            SessionExecutor = session,
            Bindings = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            {
                ["model"] = model,
            },
        };
    }

    private static JsonElement RemoteDescriptor(string entityId) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string>
        {
            [ExecutorBindings.TypePropertyName] = "user-computer-profile",
            [ExecutorBindings.EntityIdPropertyName] = entityId,
        });

    private static JsonElement TrustRequest() =>
        CopilotSessionTransportFrames.BuildConnectionRequest(
            new AgentExecutionTrustProfileReference(
                "trust-profile",
                "restricted",
                "revision-17"));

    private static TrustProfileProcessPolicyCompilation CreateCompilation(
        bool requiresContainment) =>
        new(
            requiresContainment,
            requiresContainment ? CreatePolicy() : null,
            []);

    private static MxcProcessPolicy CreatePolicy() =>
        CopilotRuntimeConnectionFactoryTests.CreatePolicy();

    private sealed class FixedRuntimeFactory(string handoff) : ICopilotRuntimeConnectionFactory
    {
        public Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
            AgentExecutionTrustContext trustContext,
            string? cliPath,
            CancellationToken cancellationToken = default)
            => Task.FromResult(
                new CopilotRuntimeConnectionLease(
                    RuntimeConnection.ForStdio("wrapper.exe", []),
                    new CopilotLaunchPolicyLease(handoff)));
    }

    private sealed class RecordingResolver : IRemoteTrustProfileResolver
    {
        public int ResolveCount { get; private set; }

        public Task<RemoteTrustProfileResolution?> ResolveAsync(
            string profileReference,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return Task.FromResult<RemoteTrustProfileResolution?>(
                new(new TrustProfile { NetworkCapabilities = [] }, "revision-17"));
        }
    }

    private sealed class RecordingCompiler(
        bool requiresContainment,
        string? failMessage = null) : ITrustProfileProcessPolicyCompiler
    {
        public int CompileCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CompileCount++;
            if (failMessage is not null)
                throw new InvalidOperationException(failMessage);
            return CreateCompilation(requiresContainment);
        }
    }

    private sealed class CompilerInvokingRuntimeFactory : ICopilotRuntimeConnectionFactory
    {
        public async Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
            AgentExecutionTrustContext trustContext,
            string? cliPath,
            CancellationToken cancellationToken = default)
        {
            await trustContext.GetCompilationAsync(cancellationToken);
            throw new InvalidOperationException("Compilation unexpectedly succeeded.");
        }
    }

    private sealed class OwnedProcessExecutor : IProcessExecutor
    {
        public int StartCount { get; private set; }
        public ProcessExecutionRequest? Request { get; private set; }
        public OwnedProcessHandle Handle { get; } = new();

        public IProcessHandle Start(ProcessExecutionRequest request)
        {
            StartCount++;
            Request = request;
            return Handle;
        }
    }

    private sealed class OwnedProcessHandle : IProcessHandle
    {
        private readonly TaskCompletionSource<ProcessExitResult> exit =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int DisposeCount { get; private set; }
        public Stream StandardInput { get; } = new MemoryStream();
        public Stream StandardOutput { get; } = new MemoryStream();
        public Stream StandardError { get; } = new MemoryStream();
        public ProcessLaunchInfo LaunchInfo { get; } = new()
        {
            ProcessId = 42,
            IsContained = true,
            Warnings = [],
            PathCategory = ProcessPathCategory.CallerProvided,
            LaunchMechanism = ProcessLaunchMechanism.MxcSpawn,
            CreationStatusAvailable = true,
            CreateProcessSucceeded = true,
            CreateProcessWin32Error = null,
            SdkSpawnSucceeded = true,
            JobConfigured = true,
            JobAssigned = true,
            ResumeSucceeded = true,
        };

        public Task<ProcessExitResult> WaitAsync(CancellationToken cancellationToken = default) =>
            exit.Task.WaitAsync(cancellationToken);

        public void Kill() => exit.TrySetResult(ProcessExitResult.Create(0, false, null));

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            exit.TrySetResult(ProcessExitResult.Create(0, false, null));
            StandardInput.Dispose();
            StandardOutput.Dispose();
            StandardError.Dispose();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MatrixMessageChannel : IMessageChannel
    {
        private readonly System.Threading.Channels.Channel<JsonElement> incoming =
            System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();
        private readonly System.Threading.Channels.Channel<JsonElement> outgoing =
            System.Threading.Channels.Channel.CreateUnbounded<JsonElement>();

        public System.Threading.Channels.ChannelReader<JsonElement> Reader => incoming.Reader;
        public System.Threading.Channels.ChannelWriter<JsonElement> Writer => outgoing.Writer;

        public ValueTask DisposeAsync()
        {
            incoming.Writer.TryComplete();
            outgoing.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class MatrixRuntimeFiles : IDisposable
    {
        public MatrixRuntimeFiles()
        {
            Root = Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"remote-containment-{Guid.NewGuid():N}");
            RemoteBaseDirectory = Path.Combine(Root, "remote-host");
            CallerLaunchRoot = Path.Combine(Root, "caller-host", "launch");
            RemoteLaunchRoot = Path.Combine(RemoteBaseDirectory, "launch");
            var native = Path.Combine(RemoteBaseDirectory, "runtimes", "win-x64", "native");
            Directory.CreateDirectory(native);
            File.WriteAllText(Path.Combine(native, "copilot.exe"), "cli");
            File.WriteAllText(
                Path.Combine(native, CopilotRuntimeConnectionFactory.WrapperFileName),
                "wrapper");
        }

        public string Root { get; }
        public string RemoteBaseDirectory { get; }
        public string CallerLaunchRoot { get; }
        public string RemoteLaunchRoot { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}
