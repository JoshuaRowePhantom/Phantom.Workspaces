using System;
using System.Linq;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ConnectionStatusViewModelTests
{
    [Fact]
    public void Inbound_SourcedFromReverseConnectionStatusRegistry_ReflectsSnapshot()
    {
        var registry = new ReverseConnectionStatusRegistry();
        registry.OnRegistered("computer-a", new DateTimeOffset(2026, 6, 16, 1, 0, 0, TimeSpan.Zero));
        registry.OnInFlightChanged("computer-a", 1);

        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.True(viewModel.HasInboundConnections);
        var inbound = Assert.Single(viewModel.Inbound);
        Assert.Equal("computer-a", inbound.ClientInstanceId);
        Assert.Equal(1, inbound.InFlightCount);
    }

    [Fact]
    public void ConnectionsChanged_RefreshesInboundCollection()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasInboundConnections);

        registry.OnRegistered("computer-a", new DateTimeOffset(2026, 6, 16, 1, 0, 0, TimeSpan.Zero));

        Assert.True(viewModel.HasInboundConnections);
        Assert.Equal("computer-a", Assert.Single(viewModel.Inbound).ClientInstanceId);

        registry.OnUnregistered("computer-a");

        Assert.False(viewModel.HasInboundConnections);
        Assert.Empty(viewModel.Inbound);
    }

    [Fact]
    public void Inbound_OrdersByConnectedTime()
    {
        var registry = new ReverseConnectionStatusRegistry();
        registry.OnRegistered("later", new DateTimeOffset(2026, 6, 16, 2, 0, 0, TimeSpan.Zero));
        registry.OnRegistered("earlier", new DateTimeOffset(2026, 6, 16, 1, 0, 0, TimeSpan.Zero));

        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.Equal(["earlier", "later"], viewModel.Inbound.Select(c => c.ClientInstanceId));
    }

    [Fact]
    public void Dispose_StopsTrackingRegistryChanges()
    {
        var registry = new ReverseConnectionStatusRegistry();
        var viewModel = new ConnectionStatusViewModel(registry);
        viewModel.Dispose();

        registry.OnRegistered("computer-a", DateTimeOffset.UnixEpoch);

        Assert.Empty(viewModel.Inbound);
    }

    [Fact]
    public void AccessPoint_IsHiddenUntilSet_ThenExposedForCopying()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasAccessPoint);
        Assert.Null(viewModel.AccessPoint);

        viewModel.SetAccessPoint("http://localhost:5280");

        Assert.True(viewModel.HasAccessPoint);
        Assert.Equal("http://localhost:5280", viewModel.AccessPoint);

        viewModel.SetAccessPoint(null);

        Assert.False(viewModel.HasAccessPoint);
    }

    [Fact]
    public void LocalAccessPoint_IsShownIndependentlyOfDevTunnelAccessPoint()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasLocalAccessPoint);

        viewModel.SetLocalAccessPoint("http://localhost:5280");

        Assert.True(viewModel.HasLocalAccessPoint);
        Assert.Equal("http://localhost:5280", viewModel.LocalAccessPoint);
        // The local access point is distinct from the dev tunnel (public) access point.
        Assert.False(viewModel.HasAccessPoint);
    }

    [Fact]
    public void TunnelName_DrivesHasDevTunnel()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasDevTunnel);

        viewModel.SetTunnelName("phantom-workspaces-playspace");

        Assert.True(viewModel.HasDevTunnel);
        Assert.Equal("phantom-workspaces-playspace", viewModel.TunnelName);
    }

    [Fact]
    public void DevTunnelStatus_HostingPublishesAccessPoint_AndNoProblem()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        viewModel.SetDevTunnelStatus(
            Services.DevTunnel.DevTunnelHostState.Hosting,
            "https://abc-5280.usw2.devtunnels.ms/",
            lastError: null);

        Assert.Equal("Hosting", viewModel.DevTunnelStatusText);
        Assert.True(viewModel.HasAccessPoint);
        Assert.Equal("https://abc-5280.usw2.devtunnels.ms/", viewModel.AccessPoint);
        Assert.False(viewModel.HasProblem);
        Assert.Null(viewModel.ProblemText);
    }

    [Fact]
    public void DevTunnelStatus_ErrorAndReconnecting_FlagProblemWithText()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        viewModel.SetDevTunnelStatus(
            Services.DevTunnel.DevTunnelHostState.Error,
            accessPointUrl: null,
            lastError: "Request forbidden.");

        Assert.Equal("Error", viewModel.DevTunnelStatusText);
        Assert.True(viewModel.HasProblem);
        Assert.Equal("Request forbidden.", viewModel.ProblemText);

        viewModel.SetDevTunnelStatus(
            Services.DevTunnel.DevTunnelHostState.Reconnecting,
            accessPointUrl: null,
            lastError: null);

        Assert.True(viewModel.HasProblem);
        Assert.Equal("Reconnecting…", viewModel.DevTunnelStatusText);

        viewModel.SetDevTunnelStatus(
            Services.DevTunnel.DevTunnelHostState.Hosting,
            "https://abc-5280.usw2.devtunnels.ms/",
            lastError: null);

        Assert.False(viewModel.HasProblem);
    }

    [Fact]
    public void ConnectionStatusViewModel_RecordClientConnectivityError_AppendsToRecentErrorsAndFlagsProblem()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasRecentErrors);
        Assert.False(viewModel.HasProblem);

        viewModel.RecordClientConnectivityError(new InvalidOperationException("relay answered 404"));

        Assert.True(viewModel.HasRecentErrors);
        Assert.True(viewModel.HasProblem);
        var recorded = Assert.Single(viewModel.RecentErrors);
        Assert.Equal("relay answered 404", recorded.Message);
        Assert.Equal("relay answered 404", viewModel.ProblemText);
    }

    [Fact]
    public void ConnectionStatusViewModel_RecentErrors_AreBoundedAndOrderedNewestFirst()
    {
        var registry = new ReverseConnectionStatusRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        for (var i = 0; i < 25; i++)
        {
            viewModel.RecordClientConnectivityError(new InvalidOperationException($"error {i}"));
        }

        // Bounded to the most recent 20, newest first.
        Assert.Equal(20, viewModel.RecentErrors.Count);
        Assert.Equal("error 24", viewModel.RecentErrors[0].Message);
        Assert.Equal("error 5", viewModel.RecentErrors[^1].Message);
    }
}
