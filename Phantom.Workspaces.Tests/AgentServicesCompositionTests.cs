using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Llm;
using Phantom.Workspaces.Llm.Secrets;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.Secrets;

namespace Phantom.Workspaces.Tests;

// Issue #1403: AgentServicesComposition is the single composition root that produces the complete
// AgentServices bundle for a launch. This test proves the bundle carries every service every launch
// path needs, so no path can silently drop one (the root cause of #1401 and #1402).
public sealed class AgentServicesCompositionTests
{
    private static ApplicationServices CreateApplicationServices(object mcpOAuthOptions)
        => new(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(),
            new AgentPersistenceStoreCache(),
            credentialPicker: new NullCredentialPicker(),
            allowedSecretsStore: new AllowedSecretsStore(new AllowedSecretsStoreConfiguration()),
            platformSecretStore: new NullPlatformSecretStore(),
            mcpOAuthOptions: mcpOAuthOptions);

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_Compose_ProducesCompleteBundle()
    {
        var mcpOAuthOptions = new object();
        var applicationServices = CreateApplicationServices(mcpOAuthOptions);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            applicationServices: applicationServices);
        await viewModel.InitializeAsync();

        var services = await AgentServicesComposition.ComposeSessionServicesAsync(
            viewModel,
            AgentPersistenceStoreFactory.CreateInMemory());

        // Every service the launch paths depend on is present in the one bundle.
        Assert.NotNull(services.SecretProvider);
        Assert.Same(applicationServices.SecretProvider, services.SecretProvider);
        Assert.NotNull(services.McpOAuthOptions);
        Assert.Same(mcpOAuthOptions, services.McpOAuthOptions);
        Assert.NotNull(services.ToolsetFactory);
        Assert.NotNull(services.ToolResourceFactory);
        Assert.NotNull(services.AccountUpsertService);
        Assert.NotNull(services.CurrentSessionContext);
        Assert.NotNull(services.AgentPersistenceStoreOverride);
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task AgentServicesComposition_ComposeHostServices_CarriesSecretProviderAndMcpOAuthOptions()
    {
        var secretProvider = new object();
        var mcpOAuthOptions = new object();

        var services = AgentServicesComposition.ComposeHostServices(secretProvider, mcpOAuthOptions);

        Assert.Same(secretProvider, services.SecretProvider);
        Assert.Same(mcpOAuthOptions, services.McpOAuthOptions);
        await Task.CompletedTask;
    }
}
