namespace Phantom.Workspaces.Llm;

/// <summary>
/// Thrown when a Copilot SDK sub-agent fails, carrying the error message from the
/// <c>SubagentFailedEvent</c>.
/// </summary>
public sealed class AgentSubagentFailedException : Exception
{
    public AgentSubagentFailedException(string? message)
        : base(message ?? "The sub-agent failed.")
    {
    }
}
