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
    }

    [Fact]
    public void ApplicationServices_ExplicitSecretServices_AreExposed()
    {
        var secretProvider = new FakeSecretProvider();
        var credentialPicker = new NullCredentialPicker();

        var services = new ApplicationServices(
            null!,
            new AgentPersistenceStoreCache(),
            secretProvider: secretProvider,
            credentialPicker: credentialPicker);

        Assert.Same(secretProvider, services.SecretProvider);
        Assert.Same(credentialPicker, services.CredentialPicker);
    }

    private sealed class FakeSecretProvider : ISecretProvider
    {
        public Task<RequestSecretsResult?> RequestSecretsAsync(
            IReadOnlyList<SecretRequest> requests,
            CancellationToken cancellationToken)
            => Task.FromResult<RequestSecretsResult?>(new RequestSecretsResult([], []));
    }
}
