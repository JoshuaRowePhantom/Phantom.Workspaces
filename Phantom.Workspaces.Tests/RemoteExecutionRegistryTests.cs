using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
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
    public void SyncFromReverseRegistry_RegistersExecutorWhenEndpointAnnounced()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(reverseRegistry);

        reverseRegistry.Register(new FakeConnectionWithEndpoint("computer-a", "https://computer-a.example/"));

        Assert.True(registry.IsRegistered("computer-a"));
        Assert.True(registry.TryGetExecutor("computer-a", out var executor));
        Assert.True(executor.CanExecute("computer-a"));
    }

    [Fact]
    public void SyncFromReverseRegistry_DoesNotRegister_WhenNoEndpointAnnounced()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(reverseRegistry);

        reverseRegistry.Register(new FakeConnectionNoEndpoint("computer-a"));

        Assert.False(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void SyncFromReverseRegistry_RemovesExecutorWhenConnectionDisconnects()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(reverseRegistry);

        var connection = new FakeConnectionWithEndpoint("computer-a", "https://computer-a.example/");
        reverseRegistry.Register(connection);
        Assert.True(registry.IsRegistered("computer-a"));

        reverseRegistry.Unregister(connection);

        Assert.False(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void SyncFromReverseRegistry_DoesNotRemoveManuallyRegisteredExecutor()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.Register("computer-a", "https://computer-a.example/");
        registry.SyncFrom(reverseRegistry);

        // Sync from an empty reverse registry — manually registered executor must survive.
        Assert.True(registry.IsRegistered("computer-a"));
    }

    [Fact]
    public void Dispose_UnsubscribesFromReverseRegistry()
    {
        var reverseRegistry = new ReverseExecutionRegistry();
        var registry = new RemoteExecutionRegistry();
        registry.SyncFrom(reverseRegistry);
        registry.Dispose();

        // After disposal, reverse registry changes should not affect the registry.
        reverseRegistry.Register(new FakeConnectionWithEndpoint("computer-a", "https://computer-a.example/"));

        Assert.False(registry.IsRegistered("computer-a"));
    }

    private sealed class FakeConnectionWithEndpoint : IReverseConnection
    {
        public FakeConnectionWithEndpoint(string clientInstanceId, string endpoint)
        {
            this.ClientInstanceId = clientInstanceId;
            this.AnnouncedEndpoint = endpoint;
        }

        public string ClientInstanceId { get; }
        public string? AnnouncedEndpoint { get; }
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UnixEpoch;
        public int InFlightCount => 0;

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }

    private sealed class FakeConnectionNoEndpoint : IReverseConnection
    {
        public FakeConnectionNoEndpoint(string clientInstanceId)
        {
            this.ClientInstanceId = clientInstanceId;
        }

        public string ClientInstanceId { get; }
        public string? AnnouncedEndpoint => null;
        public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.UnixEpoch;
        public int InFlightCount => 0;

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield break;
        }
    }
}
