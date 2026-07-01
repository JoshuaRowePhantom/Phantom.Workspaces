using System.Collections.ObjectModel;
using Phantom.Workspaces.Llm;

namespace Phantom.Workspaces.Services;

public interface IRunningAgentChatTable
{
    /// <summary>
    /// Acquires (or joins) a running agent-chat session identified by <paramref name="sessionKey"/>.
    /// If no session exists for the key, <paramref name="factory"/> is called once to create it.
    /// Dispose the returned lease when done; the underlying <see cref="AgentChat"/> is disposed when
    /// the last lease is released.
    /// </summary>
    Task<RunningAgentChatLease> AcquireAsync(string sessionKey, Func<Task<AgentChat>> factory, string entityName = "", string? entityId = null);

    /// <summary>
    /// The live collection of active sessions. Raises <see cref="System.Collections.Specialized.INotifyCollectionChanged.CollectionChanged"/>
    /// from a background thread; subscribers that update UI must dispatch to the UI thread.
    /// </summary>
    ObservableCollection<RunningAgentChat> RunningSessions { get; }
}
