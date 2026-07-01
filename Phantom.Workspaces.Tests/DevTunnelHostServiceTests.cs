using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class DevTunnelHostServiceTests
{
    [Fact]
    public async Task StartAsync_EnsuresTunnel_ForwardsSinglePort_AndPublishesHostingStatus()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"));
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);
        var observed = new List<DevTunnelHostState>();
        service.StatusChanged += (_, status) => observed.Add(status.State);

        var configuration = new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private };
        await service.StartAsync(localPort: 5280, protocol: "https", configuration, TestContext.Current.CancellationToken);

        Assert.Equal(DevTunnelHostState.Hosting, service.Status.State);
        Assert.Equal("https://tunnel-123-5280.devtunnels.ms/", service.Status.AccessPointUrl);
        Assert.Equal("tunnel-123", service.Status.TunnelId);
        Assert.Equal(DevTunnelAccessMode.Private, service.Status.AccessMode);
        Assert.True(relayHost.IsRunning);
        Assert.Equal(5280, managementClient.ForwardedPort);
        Assert.Equal("https", managementClient.ForwardedProtocol);
        Assert.Equal(DevTunnelAccessMode.Private, managementClient.AppliedAccessMode);
        Assert.Equal([DevTunnelHostState.Starting, DevTunnelHostState.Hosting], observed);
    }

    [Fact]
    public async Task StartAsync_WithHttpProtocol_ForwardsHttpToManagementClient()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-456", "http-tunnel"));
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        var configuration = new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private };
        await service.StartAsync(localPort: 5280, protocol: "http", configuration, TestContext.Current.CancellationToken);

        Assert.Equal("http", managementClient.ForwardedProtocol);
        Assert.Equal(DevTunnelHostState.Hosting, service.Status.State);
    }

    [Fact]
    public async Task StartAsync_WhenManagementFails_SetsErrorStatusAndRethrows()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"))
        {
            EnsureTunnelException = new InvalidOperationException("sign-in required"),
        };
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        var configuration = new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private };
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => service.StartAsync(localPort: 5280, protocol: "http", configuration, TestContext.Current.CancellationToken));

        Assert.Equal("sign-in required", exception.Message);
        Assert.Equal(DevTunnelHostState.Error, service.Status.State);
        Assert.Equal("sign-in required", service.Status.LastError);
        Assert.False(relayHost.IsRunning);
    }

    [Fact]
    public async Task ReconfigureAsync_RestartsHostKeepingTunnelIdentity()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"));
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        await service.StartAsync(localPort: 5280, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private }, TestContext.Current.CancellationToken);
        await service.ReconfigureAsync(localPort: 6000, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Anonymous }, TestContext.Current.CancellationToken);

        Assert.Equal(DevTunnelHostState.Hosting, service.Status.State);
        Assert.Equal("tunnel-123", service.Status.TunnelId);
        Assert.Equal(DevTunnelAccessMode.Anonymous, service.Status.AccessMode);
        Assert.Equal(6000, managementClient.ForwardedPort);
        Assert.Equal(2, managementClient.EnsureCallCount);
        Assert.Equal(1, relayHost.StopCount);
    }

    [Fact]
    public async Task StartAsync_PrivateMode_StatusIncludesConnectTokenFromManagementClient()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"))
        {
            ConnectToken = "api-issued-connect-token",
        };
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        await service.StartAsync(localPort: 5280, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private }, TestContext.Current.CancellationToken);

        Assert.Equal("api-issued-connect-token", service.Status.ConnectToken);
    }

    [Fact]
    public async Task StartAsync_AnonymousMode_StatusConnectTokenIsNull()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"))
        {
            ConnectToken = null,  // Anonymous: management client returns null
        };
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        await service.StartAsync(localPort: 5280, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Anonymous }, TestContext.Current.CancellationToken);

        Assert.Null(service.Status.ConnectToken);
    }

    [Fact]
    public async Task StopAsync_ClearsConnectToken()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"))
        {
            ConnectToken = "api-issued-connect-token",
        };
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        await service.StartAsync(localPort: 5280, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private }, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Null(service.Status.ConnectToken);
    }

    [Fact]
    public async Task StopAsync_StopsRelayHostAndReportsStopped()
    {
        var managementClient = new FakeManagementClient(new DevTunnelDescriptor("tunnel-123", "my-tunnel"));
        var relayHost = new FakeRelayHost();
        var service = new DevTunnelHostService(managementClient, relayHost);

        await service.StartAsync(localPort: 5280, protocol: "https", new DevTunnelConfiguration { AccessMode = DevTunnelAccessMode.Private }, TestContext.Current.CancellationToken);
        await service.StopAsync(TestContext.Current.CancellationToken);

        Assert.Equal(DevTunnelHostState.Stopped, service.Status.State);
        Assert.False(relayHost.IsRunning);
    }

    private sealed class FakeManagementClient(DevTunnelDescriptor descriptor) : IDevTunnelManagementClient
    {
        public Exception? EnsureTunnelException { get; init; }

        public int EnsureCallCount { get; private set; }

        public int? ForwardedPort { get; private set; }

        public string? ForwardedProtocol { get; private set; }

        public DevTunnelAccessMode? AppliedAccessMode { get; private set; }

        /// <summary>The connect token returned by <see cref="ApplyAccessModeAsync"/>; null by default (Anonymous).</summary>
        public string? ConnectToken { get; init; }

        public Task<DevTunnelDescriptor> EnsureTunnelAsync(string? tunnelId, string? tunnelName, CancellationToken cancellationToken = default)
        {
            this.EnsureCallCount++;
            if (this.EnsureTunnelException is not null)
            {
                throw this.EnsureTunnelException;
            }

            return Task.FromResult(descriptor);
        }

        public Task SetSingleForwardedPortAsync(string tunnelId, int localPort, string protocol, CancellationToken cancellationToken = default)
        {
            this.ForwardedPort = localPort;
            this.ForwardedProtocol = protocol;
            return Task.CompletedTask;
        }

        public Task<string?> ApplyAccessModeAsync(string tunnelId, DevTunnelAccessMode accessMode, CancellationToken cancellationToken = default)
        {
            this.AppliedAccessMode = accessMode;
            return Task.FromResult(this.ConnectToken);
        }

        public Task<string> GetAccessPointUrlAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default)
        {
            return Task.FromResult($"https://{tunnelId}-{localPort}.devtunnels.ms/");
        }
    }

    private sealed class FakeRelayHost : IDevTunnelRelayHost
    {
        public bool IsRunning { get; private set; }

        public int StopCount { get; private set; }

        public Task StartAsync(string tunnelId, int localPort, CancellationToken cancellationToken = default)
        {
            this.IsRunning = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken = default)
        {
            this.IsRunning = false;
            this.StopCount++;
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            this.IsRunning = false;
            return ValueTask.CompletedTask;
        }
    }
}
