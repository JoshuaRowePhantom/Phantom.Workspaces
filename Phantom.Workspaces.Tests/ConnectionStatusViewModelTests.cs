using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Phantom.Workspaces.ViewModels;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class ConnectionStatusViewModelTests
{
    private sealed class FakeConnection : IReverseConnection
    {
        public FakeConnection(string clientInstanceId, DateTimeOffset connectedAt, int inFlight = 0)
        {
            this.ClientInstanceId = clientInstanceId;
            this.ConnectedAt = connectedAt;
            this.InFlightCount = inFlight;
        }

        public string ClientInstanceId { get; }
        public DateTimeOffset ConnectedAt { get; }
        public int InFlightCount { get; }

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }

        public Task<System.IO.Stream> OpenStreamAsync(TrustedStreamRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    [Fact]
    public void Inbound_ReflectsRegistry_LiveOnConnectAndDisconnect()
    {
        var registry = new ReverseExecutionRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasInboundConnections);

        var connection = new FakeConnection("computer-a", new DateTimeOffset(2026, 6, 16, 1, 0, 0, TimeSpan.Zero), inFlight: 1);
        registry.Register(connection);

        Assert.True(viewModel.HasInboundConnections);
        var inbound = Assert.Single(viewModel.Inbound);
        Assert.Equal("computer-a", inbound.ClientInstanceId);
        Assert.Equal(1, inbound.InFlightCount);

        registry.Unregister(connection);

        Assert.False(viewModel.HasInboundConnections);
        Assert.Empty(viewModel.Inbound);
    }

    [Fact]
    public void Inbound_OrdersByConnectedTime()
    {
        var registry = new ReverseExecutionRegistry();
        registry.Register(new FakeConnection("later", new DateTimeOffset(2026, 6, 16, 2, 0, 0, TimeSpan.Zero)));
        registry.Register(new FakeConnection("earlier", new DateTimeOffset(2026, 6, 16, 1, 0, 0, TimeSpan.Zero)));

        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.Equal(["earlier", "later"], viewModel.Inbound.Select(c => c.ClientInstanceId));
    }

    [Fact]
    public void Dispose_StopsTrackingRegistryChanges()
    {
        var registry = new ReverseExecutionRegistry();
        var viewModel = new ConnectionStatusViewModel(registry);
        viewModel.Dispose();

        registry.Register(new FakeConnection("computer-a", DateTimeOffset.UnixEpoch));

        Assert.Empty(viewModel.Inbound);
    }

    [Fact]
    public void AccessPoint_IsHiddenUntilSet_ThenExposedForCopying()
    {
        var registry = new ReverseExecutionRegistry();
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
        var registry = new ReverseExecutionRegistry();
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
        var registry = new ReverseExecutionRegistry();
        using var viewModel = new ConnectionStatusViewModel(registry);

        Assert.False(viewModel.HasDevTunnel);

        viewModel.SetTunnelName("phantom-workspaces-playspace");

        Assert.True(viewModel.HasDevTunnel);
        Assert.Equal("phantom-workspaces-playspace", viewModel.TunnelName);
    }

    [Fact]
    public void DevTunnelStatus_HostingPublishesAccessPoint_AndNoProblem()
    {
        var registry = new ReverseExecutionRegistry();
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
        var registry = new ReverseExecutionRegistry();
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
}
