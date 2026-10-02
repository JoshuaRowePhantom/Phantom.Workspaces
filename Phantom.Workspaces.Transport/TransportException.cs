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

    public TransportException(string message, TransportErrorDetails details)
        : base($"{message}{Environment.NewLine}{details.Format()}")
    {
        this.RemoteError = details;
    }

    /// <summary>The worker's diagnostic cause; its stack trace is remote text, not this exception's stack.</summary>
    public TransportErrorDetails? RemoteError { get; }
}
