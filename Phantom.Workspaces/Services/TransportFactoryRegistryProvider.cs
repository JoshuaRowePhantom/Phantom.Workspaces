using Phantom.Workspaces.Transport;

namespace Phantom.Workspaces.Services;

public interface ITransportFactoryRegistryProvider
{
    ITransportFactoryRegistry? Registry { get; }
}

public sealed class TransportFactoryRegistryProvider : ITransportFactoryRegistryProvider
{
    private ITransportFactoryRegistry? registry;

    public TransportFactoryRegistryProvider(ITransportFactoryRegistry? registry = null)
    {
        this.registry = registry;
    }

    public ITransportFactoryRegistry? Registry => Volatile.Read(ref this.registry);

    public void Publish(ITransportFactoryRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(registry);
        Interlocked.Exchange(ref this.registry, registry);
    }
}
