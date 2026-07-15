using System.Text.Json;

namespace Phantom.Workspaces.Transport.Local;

public sealed class LocalTransportFactory : ITransportFactory
{
    private readonly TransportRegistry registry;

    public LocalTransportFactory(TransportRegistry registry)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "local", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ITransport?>(null);
        }

        return Task.FromResult<ITransport?>(new LocalTransport(this.registry));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
