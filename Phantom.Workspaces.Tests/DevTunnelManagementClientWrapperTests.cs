using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.DevTunnels.Contracts;
using Microsoft.DevTunnels.Management;
using Moq;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelManagementClientWrapperTests
{
    private const string Marker = DevTunnelNaming.WorkspacesMarkerLabel;

    [Fact]
    public async Task EnsureTunnelAsync_WhenNoneExists_CreatesTunnelWithMarkerAndNameLabels_NotCustomName()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        Tunnel? createdTunnel = null;
        management
            .Setup(client => client.CreateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Tunnel, TunnelRequestOptions, CancellationToken>((tunnel, _, _) => createdTunnel = tunnel)
            .ReturnsAsync((Tunnel tunnel, TunnelRequestOptions _, CancellationToken _) =>
            {
                tunnel.TunnelId = "tunnel-new";
                return tunnel;
            });
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var descriptor = await wrapper.EnsureTunnelAsync(tunnelId: null, tunnelName: "my-tunnel", TestContext.Current.CancellationToken);

        Assert.Equal("tunnel-new", descriptor.TunnelId);
        Assert.Equal("my-tunnel", descriptor.TunnelName);
        Assert.NotNull(createdTunnel);
        Assert.Null(createdTunnel!.Name); // never sets the SDK custom Name (would 403 on most accounts)
        Assert.Contains(Marker, createdTunnel.Labels!);
        Assert.Contains("my-tunnel", createdTunnel.Labels!);
    }

    [Fact]
    public async Task EnsureTunnelAsync_ReusesExistingTunnelMatchingNameLabel_WithoutCreating()
    {
        var existing = new Tunnel { TunnelId = "tunnel-existing", Labels = [Marker, "my-tunnel"] };
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var descriptor = await wrapper.EnsureTunnelAsync(tunnelId: null, tunnelName: "my-tunnel", TestContext.Current.CancellationToken);

        Assert.Equal("tunnel-existing", descriptor.TunnelId);
        management.Verify(
            client => client.CreateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureTunnelAsync_Auto_ReusesSingleMarkerTunnel()
    {
        var existing = new Tunnel { TunnelId = "tunnel-auto", Labels = [Marker] };
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([existing]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var descriptor = await wrapper.EnsureTunnelAsync(tunnelId: null, tunnelName: "auto", TestContext.Current.CancellationToken);

        Assert.Equal("tunnel-auto", descriptor.TunnelId);
        management.Verify(
            client => client.CreateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task EnsureTunnelAsync_Auto_WhenMultipleMarkerTunnels_Throws()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Tunnel { TunnelId = "tunnel-a", Labels = [Marker] },
                new Tunnel { TunnelId = "tunnel-b", Labels = [Marker] },
            ]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => wrapper.EnsureTunnelAsync(tunnelId: null, tunnelName: "auto", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task EnsureTunnelAsync_IgnoresNonWorkspacesTunnels_WhenMatching()
    {
        var foreignTunnel = new Tunnel { TunnelId = "foreign", Labels = ["my-tunnel"] }; // has name label but not marker
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([foreignTunnel]);
        management
            .Setup(client => client.CreateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((Tunnel tunnel, TunnelRequestOptions _, CancellationToken _) =>
            {
                tunnel.TunnelId = "tunnel-new";
                return tunnel;
            });
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var descriptor = await wrapper.EnsureTunnelAsync(tunnelId: null, tunnelName: "my-tunnel", TestContext.Current.CancellationToken);

        // The foreign tunnel (no marker) is not reused; a new Workspaces tunnel is created.
        Assert.Equal("tunnel-new", descriptor.TunnelId);
    }

    [Fact]
    public async Task SetSingleForwardedPortAsync_RemovesStalePorts_AndCreatesTargetPort()
    {
        var tunnel = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker] };
        var management = CreateManagementWithTunnel(tunnel, out var wrapper);
        management
            .Setup(client => client.ListTunnelPortsAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new TunnelPort { PortNumber = 9000 },
                new TunnelPort { PortNumber = 5280 },
            ]);
        var deletedPorts = new List<ushort>();
        management
            .Setup(client => client.DeleteTunnelPortAsync(It.IsAny<Tunnel>(), It.IsAny<ushort>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Tunnel, ushort, TunnelRequestOptions, CancellationToken>((_, port, _, _) => deletedPorts.Add(port))
            .ReturnsAsync(true);
        TunnelPort? createdPort = null;
        management
            .Setup(client => client.CreateOrUpdateTunnelPortAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelPort>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Tunnel, TunnelPort, TunnelRequestOptions, CancellationToken>((_, port, _, _) => createdPort = port)
            .ReturnsAsync((Tunnel _, TunnelPort port, TunnelRequestOptions _, CancellationToken _) => port);

        await wrapper.SetSingleForwardedPortAsync("tunnel-1", localPort: 5280, protocol: "https", TestContext.Current.CancellationToken);

        Assert.Equal([(ushort)9000], deletedPorts); // only the non-matching port removed
        Assert.NotNull(createdPort);
        Assert.Equal(5280, createdPort!.PortNumber);
        Assert.Equal("https", createdPort.Protocol);
    }

    [Fact]
    public async Task ApplyAccessModeAsync_DoesNotSendPortsOrEndpoints_OnTunnelUpdate()
    {
        // A tunnel fetched with IncludePorts carries Ports/Endpoints; updating it with those present is
        // rejected by the service ("Batch update of ports is not supported"), so they must be cleared.
        var tunnel = new Tunnel
        {
            TunnelId = "tunnel-1",
            Labels = [Marker],
            Ports = [new TunnelPort { PortNumber = 5280 }],
            Endpoints = [new TunnelRelayTunnelEndpoint()],
        };
        var management = CreateManagementWithTunnel(tunnel, out var wrapper);
        Tunnel? updatedTunnel = null;
        management
            .Setup(client => client.UpdateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Tunnel, TunnelRequestOptions, CancellationToken>((updated, _, _) => updatedTunnel = updated)
            .ReturnsAsync((Tunnel updated, TunnelRequestOptions _, CancellationToken _) => updated);

        await wrapper.ApplyAccessModeAsync("tunnel-1", DevTunnelAccessMode.Private, TestContext.Current.CancellationToken);

        Assert.NotNull(updatedTunnel);
        Assert.Null(updatedTunnel!.Ports);
        Assert.Null(updatedTunnel.Endpoints);
        Assert.NotNull(updatedTunnel.AccessControl);
        Assert.Empty(updatedTunnel.AccessControl!.Entries!);
    }

    [Fact]
    public async Task ApplyAccessModeAsync_Anonymous_AddsAnonymousConnectEntry()
    {
        var tunnel = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker] };
        var management = CreateManagementWithTunnel(tunnel, out var wrapper);
        Tunnel? updatedTunnel = null;
        management
            .Setup(client => client.UpdateTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .Callback<Tunnel, TunnelRequestOptions, CancellationToken>((updated, _, _) => updatedTunnel = updated)
            .ReturnsAsync((Tunnel updated, TunnelRequestOptions _, CancellationToken _) => updated);

        await wrapper.ApplyAccessModeAsync("tunnel-1", DevTunnelAccessMode.Anonymous, TestContext.Current.CancellationToken);

        var entry = Assert.Single(updatedTunnel!.AccessControl!.Entries!);
        Assert.Equal(TunnelAccessControlEntryType.Anonymous, entry.Type);
        Assert.Contains(TunnelAccessScopes.Connect, entry.Scopes!);
    }

    [Fact]
    public async Task LookupByNameAsync_ReturnsTunnelMatchingNameLabel_AmongWorkspacesTunnels()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Tunnel { TunnelId = "other", ClusterId = "usw2", Labels = [Marker, "other-tunnel"], Ports = [new TunnelPort { PortNumber = 1111 }] },
                new Tunnel { TunnelId = "wanted", ClusterId = "usw2", Labels = [Marker, "my-tunnel"], Ports = [new TunnelPort { PortNumber = 5280 }] },
            ]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var result = await ((IDevTunnelLookupClient)wrapper).LookupByNameAsync("my-tunnel", TestContext.Current.CancellationToken);

        Assert.Equal("wanted", result.TunnelId);
        Assert.Equal("usw2", result.ClusterId);
        Assert.Equal([5280], result.ForwardedPorts);
    }

    [Fact]
    public async Task LookupByNameAsync_WhenNotFound_Throws()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IDevTunnelLookupClient)wrapper).LookupByNameAsync("missing", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverSingleAsync_ReturnsTheSingleWorkspacesTunnel()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Tunnel { TunnelId = "foreign", ClusterId = "usw2", Labels = ["something-else"], Ports = [new TunnelPort { PortNumber = 1 }] },
                new Tunnel { TunnelId = "ours", ClusterId = "usw2", Labels = [Marker], Ports = [new TunnelPort { PortNumber = 5280 }] },
            ]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        var result = await ((IDevTunnelLookupClient)wrapper).DiscoverSingleAsync(TestContext.Current.CancellationToken);

        Assert.Equal("ours", result.TunnelId);
        Assert.Equal([5280], result.ForwardedPorts);
    }

    [Fact]
    public async Task DiscoverSingleAsync_WhenNone_Throws()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([new Tunnel { TunnelId = "foreign", Labels = ["other"] }]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IDevTunnelLookupClient)wrapper).DiscoverSingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task DiscoverSingleAsync_WhenMultiple_Throws()
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync(
            [
                new Tunnel { TunnelId = "a", Labels = [Marker] },
                new Tunnel { TunnelId = "b", Labels = [Marker] },
            ]);
        var wrapper = new DevTunnelManagementClientWrapper(management.Object);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ((IDevTunnelLookupClient)wrapper).DiscoverSingleAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConnectReadyTunnelAsync_FetchesFreshTunnelWithNonNullPorts()
    {
        var tunnel = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker] };
        var management = CreateManagementWithTunnel(tunnel, out var wrapper);
        // The service returns the tunnel without a Ports collection; the relay host requires non-null Ports.
        var refreshed = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker], Ports = null };
        management
            .Setup(client => client.GetTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        var result = await wrapper.GetConnectReadyTunnelAsync("tunnel-1", TestContext.Current.CancellationToken);

        Assert.NotNull(result.Ports);
        Assert.Same(refreshed, result);
        management.Verify(
            client => client.GetTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task GetConnectReadyTunnelAsync_PreservesReturnedPorts()
    {
        var tunnel = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker] };
        var management = CreateManagementWithTunnel(tunnel, out var wrapper);
        var refreshed = new Tunnel { TunnelId = "tunnel-1", Labels = [Marker], Ports = [new TunnelPort { PortNumber = 5280 }] };
        management
            .Setup(client => client.GetTunnelAsync(It.IsAny<Tunnel>(), It.IsAny<TunnelRequestOptions>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(refreshed);

        var result = await wrapper.GetConnectReadyTunnelAsync("tunnel-1", TestContext.Current.CancellationToken);

        var port = Assert.Single(result.Ports!);
        Assert.Equal(5280, port.PortNumber);
    }

    private static Mock<ITunnelManagementClient> CreateManagementWithTunnel(Tunnel tunnel, out DevTunnelManagementClientWrapper wrapper)
    {
        var management = new Mock<ITunnelManagementClient>(MockBehavior.Strict);
        management
            .Setup(client => client.ListTunnelsAsync(null, null, It.IsAny<TunnelRequestOptions>(), true, It.IsAny<CancellationToken>()))
            .ReturnsAsync([tunnel]);
        wrapper = new DevTunnelManagementClientWrapper(management.Object);
        // Ensure the wrapper holds the tunnel so port/access operations can run against it.
        wrapper.EnsureTunnelAsync(tunnel.TunnelId, tunnelName: null).GetAwaiter().GetResult();
        return management;
    }
}
