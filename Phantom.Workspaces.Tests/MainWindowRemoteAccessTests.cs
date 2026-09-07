using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Phantom.Workspaces.Configuration;
using Phantom.Workspaces.Services.DevTunnel;

namespace Phantom.Workspaces.Tests;

public sealed class MainWindowRemoteAccessTests
{
    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplyRemoteAccessChangeAsync_DisablingActiveDevTunnel_DisposesWithoutRestart()
    {
        var hosts = new List<TestDevTunnelHostService>();
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            configuration: CreateConfiguration(hostingEnabled: true));
        viewModel.DevTunnelHostServiceFactory = CreateHost;
        await viewModel.InitializeAsync();

        var changed = await viewModel.ApplyRemoteAccessChangeAsync(
            CreateRemoteHostingSettings(),
            CreateDevTunnelConfiguration(hostingEnabled: false));

        Assert.True(changed);
        var host = Assert.Single(hosts);
        Assert.True(host.Disposed);

        IDevTunnelHostService CreateHost()
        {
            var host = new TestDevTunnelHostService();
            hosts.Add(host);
            return host;
        }
    }

    [AvaloniaFact(Timeout = 15_000)]
    public async Task ApplyRemoteAccessChangeAsync_KeepingDevTunnelEnabled_DisposesAndRestartsHost()
    {
        var hosts = new List<TestDevTunnelHostService>();
        await using var viewModel = MainWindowIntegrationTests.CreateTestMainWindowViewModel(
            configuration: CreateConfiguration(hostingEnabled: true));
        viewModel.DevTunnelHostServiceFactory = CreateHost;
        await viewModel.InitializeAsync();

        var changed = await viewModel.ApplyRemoteAccessChangeAsync(
            CreateRemoteHostingSettings(),
            CreateDevTunnelConfiguration(hostingEnabled: true));

        Assert.True(changed);
        Assert.Equal(2, hosts.Count);
        Assert.True(hosts[0].Disposed);
        Assert.False(hosts[1].Disposed);
        Assert.Equal(1, hosts[1].StartCount);

        IDevTunnelHostService CreateHost()
        {
            var host = new TestDevTunnelHostService();
            hosts.Add(host);
            return host;
        }
    }

    private static WorkspacesConfiguration CreateConfiguration(bool hostingEnabled) =>
        new()
        {
            SkipStartupWorkspace = true,
            RemoteHosting = CreateRemoteHostingSettings(),
            DevTunnel = CreateDevTunnelConfiguration(hostingEnabled),
        };

    private static RemoteHostingSettings CreateRemoteHostingSettings() =>
        new()
        {
            Enabled = true,
            ListenUrls = ["http://127.0.0.1:0"],
        };

    private static DevTunnelConfiguration CreateDevTunnelConfiguration(bool hostingEnabled) =>
        new()
        {
            HostingEnabled = hostingEnabled,
            TunnelName = "runtime-toggle-test",
        };

    private sealed class TestDevTunnelHostService : IDevTunnelHostService
    {
        public DevTunnelHostStatus Status { get; } = DevTunnelHostStatus.Stopped;

        public event EventHandler<DevTunnelHostStatus>? StatusChanged
        {
            add { }
            remove { }
        }

        public int StartCount { get; private set; }

        public bool Disposed { get; private set; }

        public Task StartAsync(
            int localPort,
            string protocol,
            DevTunnelConfiguration configuration,
            CancellationToken cancellationToken = default)
        {
            this.StartCount++;
            return Task.CompletedTask;
        }

        public Task ReconfigureAsync(
            int localPort,
            string protocol,
            DevTunnelConfiguration configuration,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public ValueTask DisposeAsync()
        {
            this.Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
