namespace Phantom.Workspaces.Llm.Remote;

public sealed class RemoteAgentSessionException : Exception
{
    private RemoteAgentSessionException(RemoteAgentOperationError error)
        : base(error.Message)
    {
        this.Code = error.Code;
        this.Operation = error.Operation;
        this.IsRetryable = error.IsRetryable;
        this.CorrelationId = error.CorrelationId;
    }

    public string Code { get; }
    public string Operation { get; }
    public bool IsRetryable { get; }
    public Guid CorrelationId { get; }

    internal static RemoteAgentSessionException FromWire(RemoteAgentOperationError error)
        => new(error);
}
