namespace Phantom.Workspaces.Llm.Remote;

public sealed class RemoteAgentStatusTimeoutException : TimeoutException
{
    public RemoteAgentStatusTimeoutException(string stage)
        : base("Remote session status timed out.")
    {
        this.Stage = stage;
    }

    public string Stage { get; }
}
