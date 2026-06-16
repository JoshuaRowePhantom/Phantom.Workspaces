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
}
