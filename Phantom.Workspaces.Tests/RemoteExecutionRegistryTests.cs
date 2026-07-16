using System;
using Phantom.Workspaces.Transport.ReverseHttp;
using Phantom.Workspaces.Trust;
using Xunit;

namespace Phantom.Workspaces.Tests;

public sealed class RemoteExecutionRegistryTests
{
    [Fact]
    public void Register_AddsExecutorForClientInstance()
    {
        var registry = new RemoteExecutionRegistry();

        registry.Register("computer-a", "https://computer-a.example/");

        Assert.True(registry.IsRegistered("computer-a"));
        Assert.True(registry.TryGetExecutor("computer-a", out var executor));
        Assert.Equal("computer-a", executor.ClientInstance);
    }

    [Fact]
    public void Register_ReplacesExistingExecutor()
    {
        var registry = new RemoteExecutionRegistry();
        registry.Register("computer-a", "https://old.example/");
        registry.Register("computer-a", "https://new.example/");

        Assert.True(registry.TryGetExecutor("computer-a", out var executor));
        Assert.True(executor.CanExecute("computer-a"));
    }

    [Fact]
    public void Register_RaisesExecutorsChanged()
    {
        var registry = new RemoteExecutionRegistry();
        var raised = 0;
        registry.ExecutorsChanged += (_, _) => raised++;

        registry.Register("computer-a", "https://computer-a.example/");

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Unregister_RemovesExecutorForClientInstance()
    {
        var registry = new RemoteExecutionRegistry();
        registry.Register("computer-a", "https://computer-a.example/");

        registry.Unregister("computer-a");

        Assert.False(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void Unregister_NoOp_WhenNotRegistered()
    {
        var registry = new RemoteExecutionRegistry();
        var raised = 0;
        registry.ExecutorsChanged += (_, _) => raised++;

        registry.Unregister("not-registered");

        Assert.Equal(0, raised);
    }

    [Fact]
    public void TryGetExecutor_ReturnsFalse_WhenNotRegistered()
    {
        var registry = new RemoteExecutionRegistry();

        Assert.False(registry.TryGetExecutor("computer-a", out _));
    }

    [Fact]
    public void SyncFromStatusRegistry_RegistersExecutorWhenEndpointAnnounced()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(statusRegistry);

        statusRegistry.OnRegistered("computer-a", DateTimeOffset.UnixEpoch, "https://computer-a.example/");

        Assert.True(registry.IsRegistered("computer-a"));
        Assert.True(registry.TryGetExecutor("computer-a", out var executor));
        Assert.True(executor.CanExecute("computer-a"));
    }

    [Fact]
    public void SyncFromStatusRegistry_DoesNotRegister_WhenNoEndpointAnnounced()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(statusRegistry);

        statusRegistry.OnRegistered("computer-a", DateTimeOffset.UnixEpoch);

        Assert.False(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void SyncFromStatusRegistry_RemovesExecutorWhenConnectionDisconnects()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(statusRegistry);

        statusRegistry.OnRegistered("computer-a", DateTimeOffset.UnixEpoch, "https://computer-a.example/");
        Assert.True(registry.IsRegistered("computer-a"));

        statusRegistry.OnUnregistered("computer-a");

        Assert.False(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void SyncFromStatusRegistry_DoesNotRemoveManuallyRegisteredExecutor()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.Register("computer-a", "https://computer-a.example/");
        registry.SyncFrom(statusRegistry);

        // Sync from an empty status registry — manually registered executor must survive.
        Assert.True(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void Dispose_UnsubscribesFromStatusRegistry()
    {
        var statusRegistry = new ReverseConnectionStatusRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(statusRegistry);
        registry.Dispose();

        // After disposal, status registry changes should not affect the registry.
        statusRegistry.OnRegistered("computer-a", DateTimeOffset.UnixEpoch, "https://computer-a.example/");

        Assert.False(registry.IsRegistered("computer-a"));
    }
}
