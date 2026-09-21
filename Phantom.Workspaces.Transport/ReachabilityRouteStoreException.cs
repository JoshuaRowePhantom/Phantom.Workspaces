namespace Phantom.Workspaces.Transport;

public sealed class ReachabilityRouteStoreException : Exception
{
    public ReachabilityRouteStoreException(string message)
        : base(message)
    {
    }

    public ReachabilityRouteStoreException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
