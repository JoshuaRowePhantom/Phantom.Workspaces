using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Tests;

public sealed class ApplicationServicesConstructionTests
{
    [Fact]
    public void ApplicationServices_DefaultConstruction_ProvidesSecretServices()
    {
        var services = new ApplicationServices(
            null!,
            new AgentPersistenceStoreCache());

        Assert.NotNull(services.SecretProvider);
        Assert.NotNull(services.CredentialPicker);
        Assert.IsAssignableFrom<ISecretProvider>(services.SecretProvider);
        Assert.IsAssignableFrom<ICredentialPicker>(services.CredentialPicker);
        Assert.IsAssignableFrom<IAllowedSecretsStore>(services.AllowedSecretsStore);
        Assert.IsAssignableFrom<IPlatformSecretStore>(services.PlatformSecretStore);
    }

    [Fact]
    public void ApplicationServices_ExplicitSecretServices_AreExposed()
    {
        var secretProvider = new FakeSecretProvider();
        var credentialPicker = new NullCredentialPicker();
        var allowedSecretsStore = new FakeAllowedSecretsStore();
        var platformSecretStore = new NullPlatformSecretStore();

        var services = new ApplicationServices(
            null!,
            new AgentPersistenceStoreCache(),
            secretProvider: secretProvider,
            credentialPicker: credentialPicker,
            allowedSecretsStore: allowedSecretsStore,
            platformSecretStore: platformSecretStore);

        Assert.Same(secretProvider, services.SecretProvider);
        Assert.Same(credentialPicker, services.CredentialPicker);
        Assert.Same(allowedSecretsStore, services.AllowedSecretsStore);
        Assert.Same(platformSecretStore, services.PlatformSecretStore);
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public Task<RequestSecretsResult?> RequestSecretsAsync(
            IReadOnlyList<SecretRequest> requests,
            CancellationToken cancellationToken)
            => Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult([], []));
    }

    private sealed class FakeAllowedSecretsStore : IAllowedSecretsStore
    {
        public Task<MemorizedSecret?> TryGetAsync(string hash, CancellationToken ct)
            => Task.FromResult<MemorizedSecret?>(null);

        public Task PutAsync(string hash, MemorizedSecret record, CancellationToken ct)
            => Task.CompletedTask;

        public Task DeleteAsync(string hash, CancellationToken ct)
            => Task.CompletedTask;

        public Task<IReadOnlyDictionary<string, MemorizedSecret>> LoadAllAsync(CancellationToken ct)
            => Task.FromResult<IReadOnlyDictionary<string, MemorizedSecret>>(new Dictionary<string, MemorizedSecret>());
    }
}
