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
        var profile = new TrustProfile { NetworkCapabilities = [] };
        var provider = new RecordingProvider(profile);
        var sdkFactory = new ExecutorRoutingTestHarness.RecordingClientFactory();
        var runtimeFactory = new RecordingRuntimeFactory();
        var listeners = new Phantom.Workspaces.Transport.TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(
            sdkFactory,
            provider,
            new RecordingCompiler(),
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
        Assert.Equal(1, runtimeFactory.CallCount);
        Assert.Equal("contained-profile", runtimeFactory.ContextSeen!.RemoteReference!.Id);
        Assert.Same(runtimeFactory.Connection, sdkFactory.Options!.Connection);
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
        public TrustProfileProcessPolicyCompilation Compile(TrustProfile effectiveProfile)
            => new(false, null, []);
    }

    private sealed class RecordingRuntimeFactory : ICopilotRuntimeConnectionFactory
    {
        public RuntimeConnection Connection { get; } =
            RuntimeConnection.ForStdio("phantom-copilot-wrapper.exe", ["--policy", "local"]);
        public AgentExecutionTrustContext? ContextSeen { get; private set; }
        public int CallCount { get; private set; }

        public async Task<CopilotRuntimeConnectionLease> CreateConnectionAsync(
            AgentExecutionTrustContext trustContext,
            string? cliPath,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            ContextSeen = trustContext;
            await trustContext.GetCompilationAsync(cancellationToken);
            return new CopilotRuntimeConnectionLease(Connection, null);
        }
    }
}
