using System.Text.Json;

namespace Phantom.Workspaces.Transport;

/// <summary>
/// Registry for transport factories. Routes connection requests to registered factories.
/// </summary>
public interface ITransportFactoryRegistry
{
    /// <summary>
    /// Registers a transport factory.
    /// </summary>
    /// <param name="factory">The factory to register.</param>
    void Register(ITransportFactory factory);

    /// <summary>
    /// Attempts to connect to a transport using registered factories.
    /// </summary>
    /// <param name="connectionDescriptor">Connection descriptor JSON.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A connected transport.</returns>
    /// <exception cref="TransportException">Thrown when no factory handles the descriptor.</exception>
    Task<ITransport> ConnectToAsync(JsonElement connectionDescriptor, CancellationToken ct = default);
}
