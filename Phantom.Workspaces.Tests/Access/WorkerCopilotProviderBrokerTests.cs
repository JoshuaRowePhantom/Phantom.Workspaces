using System.IO;
using System.Text.Json;
using GitHub.Copilot;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Copilot;
using Phantom.Workspaces.Llm.Core.Transport.Chat;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Llm.Interfaces.Authorization;
using Phantom.Workspaces.Services.Access;
using Phantom.Workspaces.Transport;
using Phantom.Workspaces.Transport.Local;

namespace Phantom.Workspaces.Tests.Access;

public sealed class WorkerCopilotProviderBrokerTests : IDisposable
{
    private readonly string directory = Path.Combine(
        AppContext.BaseDirectory,
        "worker-provider-broker-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WorkerProvider_RealHostBroker_CommitsReleaseBeforeMaterialAndSdkThenBlocksForgottenRelease()
    {
        var source = new Source();
        await using var executor = new StoredSecretAccessExecutor(source);
        var (broker, registry) = StoredSecretAccessExecutorTests.Create(executor, Path.Combine(this.directory, "release-memory.json"));
        await using var ownedBroker = broker;
        await using var ownedRegistry = registry;
        var sdk = new ClientFactory();
        var rows = new TaskCompletionSource<UserAuthorizationSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        registry.Changed += (_, _) =>
        {
            var pending = registry.GetSnapshot().FirstOrDefault(row => row.State == UserAuthorizationState.Pending);
            if (pending is not null)
                rows.TrySetResult(pending);
        };
        var listeners = new TransportRegistry();
        listeners.Register(new CopilotClientTransportListener(new AgentServices
        {
            AccessBroker = broker,
            AccessRequestor = new AccessRequestor
            {
                Identifier = CredentialIdentityFactory.CreateRequestorIdentifier(
                    AccessRequestorKind.Application,
                    "worker-host-profile"),
                Kind = AccessRequestorKind.Application,
                SafeDisplayName = "Test worker",
            },
            AccessLineage = new CredentialUseLineage
            {
                Session = CredentialIdentityFactory.CreateLineageIdentifier("host-session", "worker-host-session"),
            },
            RemoteCopilotProviderCatalog = new Catalog(),
            CopilotClientFactory = sdk,
        }));
        await using var transport = new LocalTransport(listeners);
        await using var client = new CopilotClientOverTransport(
            transport,
            providerReference: "worker");
        var creation = client.CreateSessionAsync(new SessionConfig { Model = "model" }, TestToken());
        var row = await rows.Task.WaitAsync(TestToken());

        Assert.Equal(0, source.Reads);
        Assert.Equal(0, sdk.Creations);
        Assert.Equal(CredentialSourceKind.Environment, Assert.Single(row.SourceOptions).Kind);
        Assert.DoesNotContain("worker-private", JsonSerializer.Serialize(row), StringComparison.Ordinal);
        await registry.ActAsync(new ApplyUserAuthorizationAction
        {
            RequestIdentifier = row.Identifier,
            ExpectedRevision = row.Revision,
            Memory = row.MemoryOptions.Single(option => option.Scope == ReleaseMemoryScopeKind.AllUses).Identifier,
            Source = row.SelectedSource,
        }, TestToken());
        await using var session = await creation;
        Assert.Equal(1, source.Reads);
        Assert.Equal(1, sdk.Creations);
        Assert.Equal("worker-private-material", sdk.Client.Provider!.ApiKey);
        Assert.Equal("https://worker-private.invalid/v1", sdk.Client.Provider.BaseUrl);
        var memory = Assert.Single(await broker.Policy.ListAsync(TestToken()));
        await broker.Policy.ForgetAsync(memory.Identifier, TestToken());

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SendAndWaitAsync(new MessageOptions { Prompt = "safe" }, null, TestToken()));
        Assert.Equal(0, sdk.Client.Session.Sends);
        Assert.Equal(1, source.Reads);
        Assert.DoesNotContain("worker-private", failure.ToString(), StringComparison.Ordinal);
    }

    private static CancellationToken TestToken() => new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token;

    public void Dispose()
    {
        if (Directory.Exists(this.directory))
            Directory.Delete(this.directory, recursive: true);
    }

    private sealed class Source : IStoredSecretAccessSource
    {
        public int Reads { get; private set; }
        public ValueTask<bool> ExistsAsync(
            string name,
            CredentialSourceKind source,
            CancellationToken cancellationToken) => ValueTask.FromResult(true);
        public ValueTask<char[]?> ReadAsync(
            string name,
            CredentialSourceKind source,
            CancellationToken cancellationToken)
        {
            Assert.Equal("WORKER_KEY", name);
            Assert.Equal(CredentialSourceKind.Environment, source);
            this.Reads++;
            return ValueTask.FromResult<char[]?>("worker-private-material".ToCharArray());
        }
    }

    private sealed class Catalog : IRemoteCopilotProviderCatalog
    {
        public Task<RemoteCopilotProviderMetadata?> GetMetadataAsync(
            string providerReference,
            CancellationToken cancellationToken) => Task.FromResult<RemoteCopilotProviderMetadata?>(new()
            {
                Type = "openai",
                BaseUrl = "https://worker-private.invalid/v1",
                EnvironmentKeyName = "WORKER_KEY",
            });
    }

    private sealed class ClientFactory : ICopilotClientFactory
    {
        public Client Client { get; } = new();
        public int Creations { get; private set; }
        public ICopilotClient Create(CopilotClientOptions options)
        {
            this.Creations++;
            return this.Client;
        }
    }

    private sealed class Client : ICopilotClient
    {
        public Session Session { get; } = new();
        public ProviderConfig? Provider { get; private set; }
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([]);
        public Task<ICopilotSession> CreateSessionAsync(SessionConfig config, CancellationToken cancellationToken)
        {
            this.Provider = config.Provider;
            return Task.FromResult<ICopilotSession>(this.Session);
        }
        public Task<ICopilotSession> ResumeSessionAsync(
            string sessionId,
            ResumeSessionConfig config,
            CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class Session : ICopilotSession
    {
        public string SessionId => "worker-session";
        public int Sends { get; private set; }
        public IDisposable Subscribe(Action<SessionEvent> handler) => new Subscription();
        public Task<AssistantMessageEvent?> SendAndWaitAsync(
            MessageOptions options,
            TimeSpan? timeout,
            CancellationToken cancellationToken)
        {
            this.Sends++;
            return Task.FromResult<AssistantMessageEvent?>(null);
        }
        public Task SendAsync(MessageOptions options, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task AbortAsync(CancellationToken cancellationToken) => Task.CompletedTask;
        public Task SetModelAsync(string modelId, CancellationToken cancellationToken) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        private sealed class Subscription : IDisposable
        {
            public void Dispose() { }
        }
    }
}
