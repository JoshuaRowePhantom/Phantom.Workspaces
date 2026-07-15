using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Iterating implementation of ITransportFactoryRegistry.
/// </summary>
public sealed class TransportFactoryRegistry : ITransportFactoryRegistry
{
    private readonly List<ITransportFactory> _factories = [];

    /// <inheritdoc />
    public void Register(ITransportFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);
        _factories.Add(factory);
    }

    /// <inheritdoc />
    public async Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        foreach (var factory in _factories)
        {
            var transport = await factory.ConnectToAsync(connectionDescriptor, ct);
            if (transport is not null)
            {
                return transport;
            }
        }

        throw new TransportException($"No registered factory can handle the connection descriptor.");
    }
}
