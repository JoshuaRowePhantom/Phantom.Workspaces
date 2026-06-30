using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public interface IRunningAgentChatTable
{
    Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<AgentChat>> factory);

    /// <summary>
    /// Raised (from a background thread) whenever a session is added to or removed from the table.
    /// Subscribers must dispatch UI updates to the UI thread.
    /// </summary>
    event EventHandler? SessionsChanged;

    /// <summary>The number of active sessions currently registered in the table.</summary>
    int SessionCount { get; }
}
