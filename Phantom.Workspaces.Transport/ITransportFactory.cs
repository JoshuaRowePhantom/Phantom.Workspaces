using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Creates ITransport instances from connection descriptors.
/// </summary>
public interface ITransportFactory : IAsyncDisposable
{
    /// <summary>
    /// Attempts to connect to a transport using the given connection descriptor.
    /// </summary>
    /// <param name="connectionDescriptor">Connection descriptor JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An ITransport if this factory handles the descriptor; null otherwise.</returns>
    Task<ITransport?> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default);
}
