namespace Phantom.Workspaces.Llm.Remote;

public sealed class RemoteAgentStatusException : Exception
{
    public RemoteAgentStatusException(string stage, string reasonCode)
        : base("Remote session status failed.")
    {
        this.Stage = stage;
        this.ReasonCode = reasonCode;
    }

    public string Stage { get; }

    public string ReasonCode { get; }
}
