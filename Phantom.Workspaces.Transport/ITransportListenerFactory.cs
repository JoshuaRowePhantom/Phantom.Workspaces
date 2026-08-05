namespace Phantom.Workspaces.Transport;

/// <summary>
/// Creates <see cref="ITransportListener"/> instances. Provides a single composition-time seam so a
/// whole listener/channel stack can be wrapped uniformly (for example with logging) via one call,
/// mirroring the <see cref="ITransportFactory"/> pattern.
/// </summary>
public interface ITransportListenerFactory
{
    /// <summary>Creates a new listener.</summary>
    ITransportListener CreateListener();
}
