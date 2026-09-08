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
            effectiveTrustProfile: new TrustProfile { NetworkCapabilities = [] });
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
            resolvedProfile =>
            {
                runtimeFactory.ProfileSeen = resolvedProfile;
                return runtimeFactory;
            }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(transport, "contained-profile");

        await using var session = await client.CreateSessionAsync(
            new SessionConfig { Model = "gpt-5" },
            ExecutorRoutingTestHarness.Ct());

        Assert.Equal("contained-profile", provider.ResolvedName);
        Assert.Same(profile, runtimeFactory.ProfileSeen);
        Assert.Equal(1, runtimeFactory.CallCount);
        Assert.Same(runtimeFactory.Connection, sdkFactory.Options!.Connection);
    }

    private sealed class RecordingProvider(TrustProfile profile) : ITrustProfileProvider
    {
        public string? ResolvedName { get; private set; }

        public ValueTask<TrustProfile> ResolveAsync(
            string profileName,
            CancellationToken cancellationToken = default)
        {
            ResolvedName = profileName;
            return ValueTask.FromResult(profile);
        }
    }

    private sealed class RecordingRuntimeFactory : ICopilotRuntimeConnectionFactory
    {
        public RuntimeConnection Connection { get; } =
            RuntimeConnection.ForStdio("phantom-copilot-wrapper.exe", ["--policy", "local"]);
        public TrustProfile? ProfileSeen { get; set; }
        public int CallCount { get; private set; }

        public Task<CopilotRuntimeConnectionSelection> CreateAsync(
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Task.FromResult(new CopilotRuntimeConnectionSelection(Connection, null));
        }
    }
}
