namespace Phantom.Workspaces.Transport;

/// <summary>
/// Registry for transport listeners. Routes incoming channel/stream requests to registered listeners.
/// </summary>
public interface ITransportRegistry
{
    /// <summary>
    /// Registers a transport listener.
    /// </summary>
    /// <param name="listener">The listener to register.</param>
    void Register(ITransportListener listener);
}
