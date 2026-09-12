using GitHub.Copilot;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class RemoteModelHostTests
{
    [Fact]
    public async Task RemoteModel_InlineTrustProfile_FailsClosed()
    {
        var options = new AgentSchema.ModelOptions
        {
            AdditionalProperties = new Dictionary<string, object>
            {
                ["executor"] = "model-host",
            },
        };
        await using var client = new CopilotSdkChatClient(
            "gpt-5",
            "GitHub Copilot",
            gitHubToken: null,
            loggerFactory: null,
            modelOptions: options,
            executionTrustContext: new AgentExecutionTrustContext(
                new TrustProfile { NetworkCapabilities = [] },
                new RecordingCompiler()));
        var (transport, _) = ExecutorRoutingTestHarness.BuildHostTransport();
        await using var ownedTransport = transport;
        client.ConfigureExecutorRouting(
            ExecutorRoutingTestHarness.RemoteModelBindings("model-host"),
            new ExecutorRoutingTestHarness.RecordingTransportFactoryRegistry(transport));

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await client.ResolveRemoteClientForTestAsync());

        Assert.Contains("named trust-profile", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoteModel_ConstrainedProfile_CompilesAndCreatesEnvelopeOnRemoteHost()
    {
        using var files = new RuntimeFiles();
        var profile = new TrustProfile { NetworkCapabilities = [] };
        var provider = new RecordingProvider(profile);
        var compiler = new RecordingCompiler();
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var runtimeFactory = new CopilotRuntimeConnectionFactory(
            new CopilotLaunchPolicyStore(files.LaunchRoot, TimeProvider.System),
            files.BaseDirectory,
            "win-x64");
        var listeners = new Phantom.Workspaces.Transport.TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(
            sdkFactory,
            provider,
            compiler,
            runtimeFactory));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            new AgentExecutionTrustProfileReference(
                "trust-profile",
                "contained-profile",
                "17"));

        await using var session = await client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-5" },
            ExecutorRoutingTestHarness.Ct());

        Assert.Equal("contained-profile", provider.ResolvedName);
        Assert.Equal(1, compiler.CallCount);
        Assert.NotNull(sdkFactory.Options!.Connection);
        Assert.Single(Directory.EnumerateFiles(
            files.LaunchRoot,
            "policy.json",
            SearchOption.AllDirectories));
    }

    private sealed class RecordingProvider(TrustProfile profile) : IRemoteTrustProfileResolver
    {
        public string? ResolvedName { get; private set; }

        public Task<RemoteTrustProfileResolution?> ResolveAsync(
            string profileName,
            CancellationToken cancellationToken)
        {
            ResolvedName = profileName;
            return Task.FromResult<RemoteTrustProfileResolution?>(
                new(profile, "17"));
        }
    }

    private sealed class RecordingCompiler : ITrustProfileProcessPolicyCompiler
    {
        public int CallCount { get; private set; }

        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
        {
            CallCount++;
            return new(true, CopilotRuntimeConnectionFactoryTests.CreatePolicy(), []);
        }
    }

    private sealed class RuntimeFiles : IDisposable
    {
        public RuntimeFiles()
        {
            BaseDirectory = Path.Combine(
                Environment.CurrentDirectory,
                "TestResults",
                $"remote-copilot-runtime-{Guid.NewGuid():N}");
            var native = Directory.CreateDirectory(
                Path.Combine(BaseDirectory, "runtimes", "win-x64", "native"));
            File.WriteAllText(Path.Combine(native.FullName, "copilot.exe"), "cli");
            File.WriteAllText(
                Path.Combine(native.FullName, CopilotRuntimeConnectionFactory.WrapperFileName),
                "wrapper");
            LaunchRoot = Path.Combine(BaseDirectory, "launch");
        }

        public string BaseDirectory { get; }
        public string LaunchRoot { get; }

        public void Dispose() => Directory.Delete(BaseDirectory, recursive: true);
    }
}
