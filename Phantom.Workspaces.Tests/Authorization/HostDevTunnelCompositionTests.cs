using System.IO;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Llm.Interfaces.Access;
using Phantom.Workspaces.Services;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests.Authorization;

public sealed class HostDevTunnelCompositionTests
{
    [AvaloniaFact]
    public async Task MainWindowHostFactoryForwardsExactBrokerAuthenticationAndLiveScope()
    {
        var broker = Mock.Of<IAccessBroker>();
        var service = Mock.Of<IDevTunnelHostService>();
        var authentication = new RemoteAuthentication(RemoteAuthentication.EntraScheme);
        var scope = AccessPresentationScope.Bootstrap;
        Func<AccessPresentationScope> presentation = () => scope;
        var calls = 0;
        await using var cache = new AgentPersistenceStoreCache(broker, presentation);
        var services = new ApplicationServices(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(), cache, NullLoggerFactory.Instance,
            accessBroker: broker, accessPresentationScope: presentation)
        {
            DevTunnelHostServiceFactory = (actualAuthentication, actualBroker, actualPresentation) =>
            {
                Assert.Same(authentication, actualAuthentication);
                Assert.Same(broker, actualBroker);
                Assert.Same(presentation, actualPresentation);
                Assert.Equal(scope, actualPresentation());
                calls++;
                return service;
            },
        };
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            profileStore: CreateProfileStore(),
            applicationServices: services,
            configuration: new WorkspacesConfiguration
            {
                SkipStartupWorkspace = true,
                DevTunnel = new DevTunnelConfiguration { Authentication = authentication },
            });
        await viewModel.InitializeAsync();

        Assert.Same(service, viewModel.DevTunnelHostServiceFactory());
        scope = AccessPresentationScope.Runtime;
        Assert.Same(service, viewModel.DevTunnelHostServiceFactory());
        Assert.Equal(2, calls);
    }

    [AvaloniaFact]
    public async Task MainWindowHostFactoryWithoutComposedCapabilityFailsClosed()
    {
        var broker = Mock.Of<IAccessBroker>();
        await using var cache = new AgentPersistenceStoreCache(broker);
        var services = new ApplicationServices(
            MainWindowIntegrationTests.CreateTestRunningAgentChatTable(), cache, NullLoggerFactory.Instance,
            accessBroker: broker);
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            profileStore: CreateProfileStore(), applicationServices: services);
        await viewModel.InitializeAsync();

        var failure = Assert.Throws<InvalidOperationException>(() => viewModel.DevTunnelHostServiceFactory());
        Assert.Equal("Dev tunnel hosting access is not configured.", failure.Message);
        Mock.Get(broker).VerifyNoOtherCalls();
    }

    private static ProfileStore CreateProfileStore() =>
        new(Path.Combine(Environment.CurrentDirectory, ".nonexistent-host-devtunnel",
            Guid.NewGuid().ToString("N"), "profile.json"));
}
