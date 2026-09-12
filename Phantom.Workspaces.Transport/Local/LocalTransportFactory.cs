using System.Text.Json;

namespace Phantom.Workspaces.Transport.Local;

public sealed class LocalTransportFactory : ITransportFactory
{
    private readonly TransportRegistry registry;
    private readonly Action<IMessageChannel>? authenticateServerChannel;

    public LocalTransportFactory(
        TransportRegistry registry,
        Action<IMessageChannel>? authenticateServerChannel = null)
    {
        this.registry = registry ?? throw new ArgumentNullException(nameof(registry));
        this.authenticateServerChannel = authenticateServerChannel;
    }

    public Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        if (!connectionDescriptor.TryGetProperty("type", out var type)
            || !string.Equals(type.GetString(), "local", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult<ITransport?>(null);
        }

        return Task.FromResult<ITransport?>(
            new LocalTransport(this.registry, this.authenticateServerChannel));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
