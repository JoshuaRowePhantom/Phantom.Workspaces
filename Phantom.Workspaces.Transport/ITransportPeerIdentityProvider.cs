using System.Runtime.CompilerServices;

namespace Phantom.Workspaces.Transport;

internal interface ITransportPeerIdentityProvider
{
    TransportPeerIdentity GetRequiredIdentity(IMessageChannel channel);
}

public sealed class TransportPeerIdentityProvider : ITransportPeerIdentityProvider
{
    private readonly ConditionalWeakTable<IMessageChannel, TransportPeerIdentity> identities = new();

    public void SetIdentity(IMessageChannel channel, TransportPeerIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(identity);
        this.identities.Remove(channel);
        this.identities.Add(channel, identity);
    }

    public TransportPeerIdentity GetRequiredIdentity(IMessageChannel channel)
        => this.identities.TryGetValue(channel, out var identity)
            ? identity
            : throw new UnauthorizedAccessException("The transport peer is not authenticated.");
}
