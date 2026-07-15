using System.Text.Json;

namespace Phantom.Workspaces.Transport.ReverseHttp;

public sealed class ReverseHttpClientTransportRegistry : ITransportFactoryRegistry, IAsyncDisposable
{
    private readonly List<ReverseHttpClientTransportFactory> factories = [];

    public IReadOnlyList<ReverseHttpClientTransportFactory> Factories => this.factories;

    public void Register(ITransportFactory factory)
    {
        if (factory is not ReverseHttpClientTransportFactory reverseFactory)
        {
            throw new ArgumentException("Reverse HTTP client registry only accepts reverse HTTP client factories.", nameof(factory));
        }

        this.factories.Add(reverseFactory);
    }

    public async Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default)
    {
        foreach (var factory in this.factories)
        {
            var transport = await factory.ConnectToAsync(connectionDescriptor, ct).ConfigureAwait(false);
            if (transport is not null)
            {
                return transport;
            }
        }

        throw new TransportException("No reverse HTTP client transport factory handled the descriptor.");
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var factory in this.factories)
        {
            await factory.DisposeAsync().ConfigureAwait(false);
        }

        this.factories.Clear();
    }
}
