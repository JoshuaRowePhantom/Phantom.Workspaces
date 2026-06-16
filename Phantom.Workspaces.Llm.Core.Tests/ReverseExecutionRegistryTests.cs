using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.AI;
using Phantom.Workspaces.Llm.Trust;
using Xunit;

namespace Phantom.Workspaces.Llm.Core.Tests;

public sealed class ReverseExecutionRegistryTests
{
    private sealed class FakeConnection : IReverseConnection
    {
        public FakeConnection(string clientInstanceId, int inFlight = 0)
        {
            this.ClientInstanceId = clientInstanceId;
            this.InFlightCount = inFlight;
        }

        public string ClientInstanceId { get; }

        public DateTimeOffset ConnectedAt { get; } = new(2026, 6, 16, 0, 0, 0, TimeSpan.Zero);

        public int InFlightCount { get; }

        public async IAsyncEnumerable<ChatResponseUpdate> ExecuteAsync(
            RemoteAgentRequest request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Yield();
            yield return new ChatResponseUpdate(ChatRole.Assistant, "ok");
        }
    }

    [Fact]
    public void Register_MakesInstanceConnected()
    {
        var registry = new ReverseExecutionRegistry();
        var connection = new FakeConnection("computer-a");

        registry.Register(connection);

        Assert.True(registry.IsConnected("computer-a"));
        Assert.True(registry.TryGetConnection("computer-a", out var resolved));
        Assert.Same(connection, resolved);
    }

    [Fact]
    public void Register_RaisesChangedEvent()
    {
        var registry = new ReverseExecutionRegistry();
        var raised = 0;
        registry.ConnectionsChanged += (_, _) => raised++;

        registry.Register(new FakeConnection("computer-a"));

        Assert.Equal(1, raised);
    }

    [Fact]
    public void Register_ReconnectSupersedesPriorConnection()
    {
        var registry = new ReverseExecutionRegistry();
        var first = new FakeConnection("computer-a");
        var second = new FakeConnection("computer-a");

        registry.Register(first);
        registry.Register(second);

        Assert.True(registry.TryGetConnection("computer-a", out var resolved));
        Assert.Same(second, resolved);

        // Unregistering the superseded (stale) connection must not remove the current one.
        registry.Unregister(first);
        Assert.True(registry.IsConnected("computer-a"));
    }

    [Fact]
    public void Unregister_RemovesCurrentConnection()
    {
        var registry = new ReverseExecutionRegistry();
        var connection = new FakeConnection("computer-a");
        registry.Register(connection);

        registry.Unregister(connection);

        Assert.False(registry.IsConnected("computer-a"));
    }

    [Fact]
    public void GetConnectedInstances_ReturnsStatusSnapshot()
    {
        var registry = new ReverseExecutionRegistry();
        registry.Register(new FakeConnection("computer-a", inFlight: 2));
        registry.Register(new FakeConnection("computer-b", inFlight: 0));

        var statuses = registry.GetConnectedInstances();

        Assert.Equal(2, statuses.Count);
        Assert.Equal(2, statuses.Single(s => s.ClientInstanceId == "computer-a").InFlightCount);
        Assert.Contains(statuses, s => s.ClientInstanceId == "computer-b");
    }

    [Fact]
    public void IsConnected_FalseForUnknownInstance()
    {
        var registry = new ReverseExecutionRegistry();
        Assert.False(registry.IsConnected("missing"));
    }
}
