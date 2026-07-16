using Phantom.Workspaces.Transport.ReverseHttp;

namespace Phantom.Workspaces.Transport.Tests;

public sealed class ReverseConnectionStatusRegistryTests
{
    [Fact]
    public void OnRegistered_NewClient_AppearsInSnapshotOrderedByConnectedAt()
    {
        var registry = new ReverseConnectionStatusRegistry();
        var earlier = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var later = earlier.AddMinutes(5);

        registry.OnRegistered("machine-b", later);
        registry.OnRegistered("machine-a", earlier);

        var snapshot = registry.GetConnectedInstances();

        Assert.Equal(2, snapshot.Count);
        Assert.Equal("machine-a", snapshot[0].ClientInstanceId);
        Assert.Equal(earlier, snapshot[0].ConnectedAt);
        Assert.Equal("machine-b", snapshot[1].ClientInstanceId);
        Assert.Equal(0, snapshot[0].InFlightCount);
    }

    [Fact]
    public void OnUnregistered_KnownClient_RemovedFromSnapshot()
    {
        var registry = new ReverseConnectionStatusRegistry();
        registry.OnRegistered("machine-a", DateTimeOffset.UtcNow);

        registry.OnUnregistered("machine-a");

        Assert.Empty(registry.GetConnectedInstances());
    }

    [Fact]
    public void OnInFlightChanged_KnownClient_UpdatesInFlightCount()
    {
        var registry = new ReverseConnectionStatusRegistry();
        var connectedAt = DateTimeOffset.UtcNow;
        registry.OnRegistered("machine-a", connectedAt);

        registry.OnInFlightChanged("machine-a", 3);

        var status = Assert.Single(registry.GetConnectedInstances());
        Assert.Equal(3, status.InFlightCount);
        Assert.Equal(connectedAt, status.ConnectedAt);
    }

    [Fact]
    public void OnInFlightChanged_UnknownClient_NoEntryAddedOrChanged()
    {
        var registry = new ReverseConnectionStatusRegistry();
        var raised = 0;
        registry.ConnectionsChanged += (_, _) => raised++;

        registry.OnInFlightChanged("ghost", 5);

        Assert.Empty(registry.GetConnectedInstances());
        Assert.Equal(0, raised);
    }

    [Fact]
    public void AnyChange_RaisesConnectionsChanged()
    {
        var registry = new ReverseConnectionStatusRegistry();
        var raised = 0;
        registry.ConnectionsChanged += (_, _) => raised++;

        registry.OnRegistered("machine-a", DateTimeOffset.UtcNow);
        registry.OnInFlightChanged("machine-a", 1);
        registry.OnUnregistered("machine-a");

        Assert.Equal(3, raised);
    }
}
