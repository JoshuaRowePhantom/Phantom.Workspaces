namespace Phantom.Workspaces.Transport;

/// <summary>
/// Exception thrown for transport-layer errors.
/// </summary>
public class TransportException : Exception
{
    public TransportException()
    {
    }

    public TransportException(string message) : base(message)
    {
    }

    public TransportException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
