using System.Linq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Data;
using Phantom.Workspaces.ViewModels;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowReverseHttpRegistrationTests
{
    private static readonly EntityId LocalProfileId = new("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void BuildReverseHttpHubFactories_PublicHttpDnsEndpoint_ReportsDegradedPersistedReachabilityWithoutDroppingOutboundRegistration()
    {
        var factory = Assert.Single(MainWindowViewModel.BuildReverseHttpHubFactories(
            new WebRepositorySource("http://hub.example"),
            LocalProfileId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));

        Assert.Equal("http://hub.example", factory.HubUrl);
        Assert.Equal("endpoint.public-http-host", factory.LastReachabilityPublicationStatus?.ReasonCode);
        Assert.Equal("descriptor.hub-urls[]", factory.LastReachabilityPublicationStatus?.Field);
        Assert.Equal("false", factory.LastReachabilityPublicationStatus?.Persisted);
    }

    [Fact]
    public void BuildReverseHttpHubFactories_SeparateAdvertisedEndpoint_DoesNotChangeOutboundConnection()
    {
        var factory = Assert.Single(MainWindowViewModel.BuildReverseHttpHubFactories(
            new WebRepositorySource("http://hub.example"),
            LocalProfileId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance,
            "https://hub.example"));
        Assert.Equal("http://hub.example", factory.HubUrl);
        Assert.Equal("https://hub.example/", factory.AdvertisedHubUrl);
    }

    [Fact]
    public void BuildReverseHttpHubFactories_WhenRepositorySourceIsWeb_RegistersReverseHttpWithHost()
    {
        var hubFactories = MainWindowViewModel.BuildReverseHttpHubFactories(
            new WebRepositorySource("http://localhost:5282"),
            LocalProfileId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        var factory = Assert.Single(hubFactories);
        Assert.Equal("http://localhost:5282", factory.HubUrl);
        Assert.Equal(LocalProfileId.ToString(), factory.EntityId);
    }

    [Fact]
    public void BuildReverseHttpHubFactories_WhenRepositorySourceIsMongoDb_DoesNotRegisterReverseHttp()
    {
        var hubFactories = MainWindowViewModel.BuildReverseHttpHubFactories(
            new MongoDbRepositorySource("container", "root"),
            LocalProfileId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.Empty(hubFactories);
    }

    [Fact]
    public void BuildReverseHttpHubFactories_WhenRepositorySourceIsUnknown_DoesNotRegisterReverseHttp()
    {
        var hubFactories = MainWindowViewModel.BuildReverseHttpHubFactories(
            new UnknownRepositorySource(),
            LocalProfileId,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance);

        Assert.Empty(hubFactories);
    }

    [Fact]
    public void BuildReverseHttpHubFactories_UsesLocalProfileEntityId_AsRegistrationIdentity()
    {
        var localProfile = new EntityId("1db33c5d-2d87-1974-34ef-06027314717f");

        var factory = Assert.Single(MainWindowViewModel.BuildReverseHttpHubFactories(
            new WebRepositorySource("http://localhost:5282"),
            localProfile,
            Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance));

        // The client registers itself under its own local profile id, which is what the host records
        // in its Inbound Connections registry.
        Assert.Equal(localProfile.ToString(), factory.EntityId);
    }
}
